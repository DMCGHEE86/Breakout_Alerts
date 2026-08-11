using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Configuration;
using BreakoutAlerts.Core.Scanning;
using BreakoutAlerts.Core.Strategies;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.Services;

/// <summary>
/// Drives <see cref="ScannerEngine"/> on a timer for the lifetime of the application.
/// </summary>
/// <remarks>
/// Thin by design. All the scanning logic lives in the engine, which is a plain class in
/// Core and can be exercised directly from a test; this wrapper only owns the loop and the
/// application lifetime hook. Splitting it that way is what keeps the scanner testable
/// without spinning up a host.
/// </remarks>
public sealed class ScannerHostedService : BackgroundService
{
    /// <summary>How long the first cycle waits for the gateway before giving up on it.</summary>
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(15);

    private readonly ScannerEngine _engine;
    private readonly IWatchlistService _watchlist;
    private readonly IStrategyRegistry _registry;
    private readonly IMarketDataProvider _marketData;
    private readonly ILogger<ScannerHostedService> _logger;

    /// <summary>
    /// Interval between scan cycles.
    /// </summary>
    /// <remarks>
    /// Short relative to a real deployment because the mock feed replays the session at
    /// accelerated speed - a 5-minute cycle against a 30x clock would skip most of the day.
    /// Against a live gateway this becomes bar-close aligned instead of a fixed interval.
    /// </remarks>
    private readonly TimeSpan _cycleInterval;

    /// <summary>Creates the service.</summary>
    public ScannerHostedService(
        ScannerEngine engine,
        IWatchlistService watchlist,
        IStrategyRegistry registry,
        IMarketDataProvider marketData,
        MarketDataOptions options,
        ILogger<ScannerHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _cycleInterval = TimeSpan.FromSeconds(Math.Max(1, options.ScanIntervalSeconds));
        engine.TimeframeMinutes = options.TimeframeMinutes;
        engine.AlertCutoff = options.ParsedAlertCutoff;
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _watchlist = watchlist ?? throw new ArgumentNullException(nameof(watchlist));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The watchlist has to be loaded before the first cycle, or the scanner starts with
        // nothing to scan and quietly does nothing.
        await _watchlist.LoadAsync(stoppingToken).ConfigureAwait(false);

        // Register the real strategy. Done here rather than in a ViewModel so the scanner
        // works whether or not the Strategies page has ever been opened.
        var strategy = new OpeningRangeBreakoutStrategy();
        strategy.Configure(new Dictionary<string, double>
        {
            [OpeningRangeBreakoutStrategy.ParamTimeframe] = _engine.TimeframeMinutes
        });
        _registry.Register(strategy);

        _logger.LogInformation("Scanner started over {Count} symbols", _watchlist.Items.Count);

        await WaitForDataAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _engine.RunCycleAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_cycleInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan cycle failed");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Scanner stopped");
    }

    /// <summary>Waits briefly for the data provider to come up before the first cycle.</summary>
    /// <remarks>
    /// Both this service and the gateway connector start together, and connecting takes a
    /// moment. Without this wait the first scan runs against a provider that is not connected
    /// yet, every symbol returns an empty bar list, and the cycle silently accomplishes
    /// nothing - so a freshly launched app sits blank for a full scan interval with no
    /// indication that anything is wrong. Observed live on 2026-08-04.
    ///
    /// <para>Bounded, and a timeout is not an error. If OpenD genuinely is not running the
    /// scanner still starts and cycles; each cycle is a no-op until the connection arrives,
    /// which is the intended disconnected behaviour rather than a reason to refuse to run.</para>
    /// </remarks>
    private async Task WaitForDataAsync(CancellationToken stoppingToken)
    {
        var deadline = DateTimeOffset.UtcNow + ConnectWait;

        while (!_marketData.IsConnected && DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!_marketData.IsConnected)
        {
            _logger.LogWarning(
                "Market data not connected after {Seconds}s - scanning anyway; cycles will " +
                "produce nothing until the connection is established",
                ConnectWait.TotalSeconds);
        }
    }
}
