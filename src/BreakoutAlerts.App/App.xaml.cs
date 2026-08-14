using System.IO;
using System.Windows;
using System.Windows.Threading;
using BreakoutAlerts.App.Services;
using BreakoutAlerts.App.ViewModels;
using BreakoutAlerts.App.Views;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.App.Services.Moomoo;
using BreakoutAlerts.Core.Alerts;
using BreakoutAlerts.Core.Caching;
using BreakoutAlerts.Core.Configuration;
using Microsoft.Extensions.Configuration;
using BreakoutAlerts.Core.Options;
using BreakoutAlerts.Core.Scanning;
using BreakoutAlerts.Core.Trading;
using BreakoutAlerts.Core.Watchlist;
using BreakoutAlerts.Core.Strategies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace BreakoutAlerts.App;

/// <summary>
/// Application entry point. Owns the dependency injection container, logging
/// configuration and the lifetime of background services.
/// </summary>
/// <remarks>
/// Built on the generic host rather than a hand-rolled service locator so that
/// <see cref="IHostedService"/> implementations - the mock feed today, the OpenD
/// connection manager from Phase 3 - get proper start and stop hooks tied to the
/// application lifetime. Without that, a background socket outlives the window and the
/// process fails to exit.
/// </remarks>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>
    /// Process-wide lock ensuring only one instance runs at a time.
    /// </summary>
    /// <remarks>
    /// Not a nicety. Two instances both scan the watchlist and both append to the same
    /// <c>alerts.jsonl</c>, so every signal is written twice - which silently corrupts the
    /// one artefact the whole system exists to produce, and makes any backtest run over it
    /// wrong in a way that looks like a strategy firing too often rather than like a
    /// duplicate-process bug. This was hit twice during development before being fixed.
    ///
    /// <para>Global\ scope rather than Local\ so it holds across terminal server sessions
    /// too. The mutex is released when the process exits, including on a crash.</para>
    /// </remarks>
    private static Mutex? _singleInstanceMutex;

    private const string SingleInstanceMutexName = @"Global\BreakoutAlerts.SingleInstance";

    /// <summary>
    /// Directory for user-writable data: logs and the alert history.
    /// </summary>
    /// <remarks>
    /// LocalApplicationData, not the install directory. A packaged app writing beside its
    /// own executable hits access-denied under Program Files, and the failure surfaces
    /// only after installation - never during development.
    /// </remarks>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BreakoutAlerts");

    /// <summary>Builds the host, wires services and shows the shell window.</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless render mode, checked before the single-instance lock so it can run while
        // the app is already open. See SampleChartRender for why this exists.
        var sampleIndex = Array.IndexOf(e.Args, "--render-sample");
        if (sampleIndex >= 0 && sampleIndex + 1 < e.Args.Length)
        {
            SampleChartRender.Run(e.Args[sampleIndex + 1]);
            Shutdown();
            return;
        }

        // Live adapter probe. Also outside the single-instance lock: it opens its own
        // connection, writes nothing to the alert log, and is most useful when run against
        // an app that is already up. See LiveDataProbe for why it exists.
        var probeIndex = Array.IndexOf(e.Args, "--probe-live");
        if (probeIndex >= 0 && probeIndex + 2 < e.Args.Length)
        {
            await LiveDataProbe.RunAsync(e.Args[probeIndex + 1], e.Args[probeIndex + 2]);
            Shutdown();
            return;
        }

        // Renders the chart popup for a real alert, headlessly. Same reasoning as the sample
        // render, except this one uses live data - which is where the defects that depend on
        // what the provider actually returns can be seen.
        var alertIndex = Array.IndexOf(e.Args, "--render-alert");
        if (alertIndex >= 0 && alertIndex + 2 < e.Args.Length)
        {
            await LiveChartRender.RunAsync(e.Args[alertIndex + 1], e.Args[alertIndex + 2]);
            Shutdown();
            return;
        }

        // Read-only trade inspection. Cannot place, modify or cancel anything - see TradeProbe.
        var tradeIndex = Array.IndexOf(e.Args, "--probe-trade");
        if (tradeIndex >= 0 && tradeIndex + 1 < e.Args.Length)
        {
            await TradeProbe.RunAsync(e.Args[tradeIndex + 1]);
            Shutdown();
            return;
        }

        // Claim the single-instance lock before anything else touches the data directory.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "BreakoutAlerts is already running.\n\n" +
                "Only one instance can run at a time - a second would scan the same watchlist " +
                "and write duplicate entries to the alert log.",
                "BreakoutAlerts",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Shutdown rather than Environment.Exit, so WPF tears down cleanly and OnExit
            // is not left holding a mutex it never acquired.
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        Directory.CreateDirectory(DataDirectory);

        // Serilog is configured before the host so that failures during host construction
        // are themselves logged rather than vanishing into a silent startup crash.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(DataDirectory, "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        // Unhandled exceptions on the UI thread would otherwise close the app with no
        // trace at all. Logging and marking handled keeps a bug from becoming a mystery.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration(cfg => cfg
                .SetBasePath(AppContext.BaseDirectory)
                // optional:true deliberately. A missing config file must land on the LIVE
                // provider via the options defaults, never leave the app unable to start
                // and never fall through to synthetic data.
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false))
            .ConfigureServices(ConfigureServices)
            .Build();

        await _host.StartAsync();

        var shell = _host.Services.GetRequiredService<ShellWindow>();
        shell.Show();
    }

    /// <summary>Registers every service the application resolves.</summary>
    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // ---- Core services -------------------------------------------------------
        // The alert log lives beside the app's other user data and is append-only.
        services.AddSingleton<IAlertStore>(sp => new JsonLinesAlertStore(
            Path.Combine(DataDirectory, "alerts.jsonl"),
            sp.GetRequiredService<ILogger<JsonLinesAlertStore>>()));

        services.AddSingleton<IAlertNotificationService, AlertNotificationService>();
        services.AddSingleton<IStrategyRegistry, StrategyRegistry>();

        // The watchlist is the scanner's universe, persisted beside the alert log.
        services.AddSingleton<IWatchlistService>(sp => new JsonWatchlistService(
            Path.Combine(DataDirectory, "watchlist.json"),
            sp.GetRequiredService<ILogger<JsonWatchlistService>>()));

        // ---- Scanning ------------------------------------------------------------
        // The engine is a plain class so a full cycle can be driven from a test; the
        // hosted service owns only the timing loop.
        services.AddSingleton<ScannerEngine>();
        services.AddHostedService<ScannerHostedService>();

        // ---- Market data ---------------------------------------------------------
        var marketData = new MarketDataOptions();
        context.Configuration.GetSection(MarketDataOptions.SectionName).Bind(marketData);
        services.AddSingleton(marketData);

        // Bars for completed sessions accumulate here, because the gateway only serves about
        // three days. Registered outside the provider branch so the synthetic path gets the
        // same treatment and the decorator is exercised in offline development too.
        services.AddSingleton<IBarCache>(sp => new JsonBarCache(
            Path.Combine(DataDirectory, "bars"),
            sp.GetRequiredService<ILogger<JsonBarCache>>()));

        if (marketData.IsSynthetic)
        {
            // Loud on purpose. An application that quietly serves invented prices while
            // looking exactly like it is serving real ones, in a tool used to size real
            // trades, is the most damaging failure this codebase could produce.
            Log.Warning(
                "SYNTHETIC MARKET DATA IS ACTIVE. Every price, bar and option quote is " +
                "generated locally and is NOT real. This mode exists for testing and " +
                "offline development only.");

            services.AddSingleton<MockMarketDataProvider>();
            services.AddSingleton<IMarketDataProvider>(sp => new CachingMarketDataProvider(
                sp.GetRequiredService<MockMarketDataProvider>(),
                sp.GetRequiredService<IBarCache>(),
                sp.GetRequiredService<ILogger<CachingMarketDataProvider>>()));
        }
        else
        {
            // Only on the live path. There is no gateway to start when running on generated
            // data, and offering the button there would be nonsense.
            services.AddSingleton<OpenDLauncher>();

            services.AddSingleton<MoomooConnection>();
            services.AddSingleton<MoomooMarketDataProvider>();

            // Wrapped rather than modified. The moomoo adapter carries three verified gateway
            // quirks and is the most expensive code here to re-verify; caching is an
            // orthogonal concern and stays outside it.
            services.AddSingleton<IMarketDataProvider>(sp => new CachingMarketDataProvider(
                sp.GetRequiredService<MoomooMarketDataProvider>(),
                sp.GetRequiredService<IBarCache>(),
                sp.GetRequiredService<ILogger<CachingMarketDataProvider>>()));

            services.AddHostedService<MoomooStartupService>();
        }

        // ---- Order routing -------------------------------------------------------
        // Registered whatever the data provider is, but note what is NOT here: no hosted
        // service, no timer, no subscription to alerts. Nothing connects the trading channel
        // or places an order except an explicit user action on the Trading page.
        services.AddSingleton(sp => new OrderAuditLog(
            Path.Combine(DataDirectory, "orders.jsonl"),
            sp.GetRequiredService<ILogger<OrderAuditLog>>()));

        // Pairings between an entry and its protective stop. The API cannot express the link, so it is tracked here.
        services.AddSingleton(sp => new OrderLinkStore(
            Path.Combine(DataDirectory, "order-links.json"),
            sp.GetRequiredService<ILogger<OrderLinkStore>>()));

        services.AddSingleton<MoomooTradeConnection>();
        services.AddSingleton<ITradingService, MoomooTradingService>();
        services.AddSingleton<TradingViewModel>();

        // Delta band comes from configuration so the rule can be tuned without a rebuild.
        services.AddSingleton<IOptionRanker>(_ => new DeltaTargetRanker
        {
            TargetDelta = marketData.OptionTargetDelta,
            MinimumDelta = marketData.OptionMinimumDelta,
            MaximumDelta = marketData.OptionMaximumDelta
        });

        // ---- ViewModels ----------------------------------------------------------
        // Singletons, not transients: each page keeps its accumulated state - scroll
        // position, the alert list, selected strategy - when navigated away from and back.
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ScannerViewModel>();
        services.AddSingleton<StrategiesViewModel>();

        // ---- Views ---------------------------------------------------------------
        services.AddSingleton<ShellWindow>();

        // Chart popups are transient - one window and one ViewModel per double-click, so
        // several charts can be open at once.
        services.AddTransient<ChartViewModel>();
        services.AddTransient<ChartWindow>();
        services.AddSingleton<Func<ChartWindow>>(sp => sp.GetRequiredService<ChartWindow>);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled exception on the UI thread");

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails were written to the log.",
            "BreakoutAlerts",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Marked handled so a single UI fault does not terminate a running scan session.
        e.Handled = true;
    }

    /// <summary>Stops background services and flushes logs before exit.</summary>
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Bounded shutdown: a hung background service must not leave a zombie process
            // holding the alert log open.
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();

        // Released last, so the slot is only freed once this instance has genuinely finished
        // writing to the alert log.
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}

