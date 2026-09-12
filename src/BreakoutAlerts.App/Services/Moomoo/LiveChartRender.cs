using System.IO;
using BreakoutAlerts.App.ViewModels;
using BreakoutAlerts.Core.Alerts;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Renders the chart popup for a real alert straight to a PNG, using live data and no window.
/// Invoked by <c>BreakoutAlerts.App.exe --render-alert TICKER out.png</c>.
/// </summary>
/// <remarks>
/// <see cref="SampleChartRender"/> proves the renderer draws a hand-built session correctly.
/// It cannot catch anything that depends on what the provider actually returns - and the
/// defect that prompted this did exactly that: the popup drew every bar the provider served,
/// which after the live adapter landed meant several days of extended-hours data with the
/// session of interest squeezed into a sliver at the right-hand edge.
///
/// <para>So this drives the real <see cref="ChartViewModel"/> against the real provider,
/// seeded from a real record in <c>alerts.jsonl</c>. What lands in the PNG is what the popup
/// would show for that alert, which is the only way to check the fix without a screen.</para>
/// </remarks>
public static class LiveChartRender
{
    /// <summary>Renders the most recent alert for a ticker.</summary>
    public static async Task<int> RunAsync(string ticker, string outputPath)
    {
        var logPath = Path.Combine(App.DataDirectory, "alerts.jsonl");
        if (!File.Exists(logPath))
        {
            Console.WriteLine($"No alert log at {logPath}");
            return 1;
        }

        // Read through the store, not by deserializing lines here. A second parsing path
        // silently skipped the store's legacy-level migration, and the symptom was a chart
        // that drew candles perfectly with no opening range and no premarket lines - because
        // every level had quietly come back empty.
        var store = new JsonLinesAlertStore(logPath, NullLogger<JsonLinesAlertStore>.Instance);

        AlertRecord? focus = null;
        var all = new List<AlertRecord>();

        await foreach (var record in store.ReadAllAsync().ConfigureAwait(false))
        {
            all.Add(record);

            if (string.Equals(record.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
            {
                focus = record;
            }
        }

        if (focus is null)
        {
            Console.WriteLine($"No alert for {ticker} in {logPath}");
            return 1;
        }

        var options = new Core.Configuration.MarketDataOptions();
        using var connection = new MoomooConnection(options, NullLogger<MoomooConnection>.Instance);

        if (!await connection.ConnectAsync().ConfigureAwait(false))
        {
            Console.WriteLine($"Could not connect to OpenD at {options.Host}:{options.Port}");
            return 1;
        }

        using var provider = new MoomooMarketDataProvider(
            connection, options, NullLogger<MoomooMarketDataProvider>.Instance);

        // The same registry contents the running app has. The renderer resolves an alert's
        // levels through the strategy that declared them, so a probe with an empty registry
        // would draw no levels and look exactly like the bug this switch exists to catch.
        var registry = new Core.Strategies.StrategyRegistry();
        registry.Register(new Core.Strategies.OpeningRangeBreakoutStrategy());
        registry.Register(new Core.Strategies.ZebraStrategy());

        var vm = new ChartViewModel(provider, registry, NullLogger<ChartViewModel>.Instance)
        {
            TimeframeMinutes = options.TimeframeMinutes
        };

        await vm.LoadAsync(focus, all).ConfigureAwait(false);

        var model = vm.ToRenderModel();
        var plot = new ScottPlot.Plot();
        ChartRenderer.Render(plot, model);
        plot.SavePng(outputPath, 1200, 700);

        Console.WriteLine($"Rendered {ticker} chart: {outputPath}");
        Console.WriteLine($"  focus alert : {focus.AlertPath} {focus.Direction} at {focus.Timestamp:yyyy-MM-dd HH:mm zzz}");
        Console.WriteLine($"  session     : {MarketSession.SessionDate(focus.Timestamp):yyyy-MM-dd}");
        Console.WriteLine($"  bars drawn  : {model.Bars.Count}");

        if (model.Bars.Count > 0)
        {
            Console.WriteLine($"  bar range   : {model.Bars[0].Timestamp:MM-dd HH:mm} .. {model.Bars[^1].Timestamp:MM-dd HH:mm}");
            var days = model.Bars.Select(b => MarketSession.SessionDate(b.Timestamp)).Distinct().Count();
            Console.WriteLine($"  sessions    : {days}{(days == 1 ? "  OK" : "  <-- MORE THAN ONE DAY")}");
        }

        Console.WriteLine($"  alerts      : {model.Alerts.Count}");
        Console.WriteLine($"  strategy    : {focus.Strategy}");

        // Printed because "the chart drew no levels" has two causes that look identical in a
        // PNG: the alert carried none, or the strategy declared none. Naming the counts
        // separates them without opening the image.
        Console.WriteLine($"  bands       : {model.Bands.Count}");
        foreach (var band in model.Bands)
        {
            Console.WriteLine($"      {band.Label,-6} {band.Lower:N2} - {band.Upper:N2}");
        }

        Console.WriteLine($"  lines       : {model.Lines.Count}");
        foreach (var line in model.Lines)
        {
            Console.WriteLine($"      {line.Label,-6} {line.Value:N2}");
        }

        if (model.Bands.Count == 0 && model.Lines.Count == 0)
        {
            Console.WriteLine("  <-- NO LEVELS DRAWN. Check the alert's levels and the strategy's LevelDisplays.");
        }

        if (vm.ErrorMessage is not null)
        {
            Console.WriteLine($"  ERROR       : {vm.ErrorMessage}");
        }

        if (vm.OutOfRangeNotice is not null)
        {
            Console.WriteLine($"  NOTICE      : {vm.OutOfRangeNotice}");
        }

        return 0;
    }
}
