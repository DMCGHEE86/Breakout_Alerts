using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;

namespace BreakoutAlerts.App.Services;

/// <summary>
/// Renders a representative chart straight to a PNG, with no window involved.
/// </summary>
/// <remarks>
/// Invoked by <c>BreakoutAlerts.App.exe --render-sample &lt;path.png&gt;</c>.
///
/// <para><b>Why this exists.</b> The chart popup cannot be screenshotted from the build
/// environment - this application's windows never reach the interactive desktop when
/// launched from an automated shell. Without a headless path, "the chart works" could only
/// ever mean "the chart compiles", and layout mistakes (a label anchored to the wrong
/// origin, a band painted over the candles, flags on the wrong side) would only surface
/// when a human happened to open the window.</para>
///
/// <para>It drives the real <see cref="ChartRenderer"/>, so what lands in the PNG is what
/// the popup draws. Also doubles as a quick way to check the visual after a palette change
/// without launching the app.</para>
/// </remarks>
public static class SampleChartRender
{
    /// <summary>Builds a synthetic session and writes the rendered chart to disk.</summary>
    /// <param name="path">Destination PNG path.</param>
    /// <returns>Zero on success.</returns>
    public static int Run(string path)
    {
        var model = BuildSample();

        var plot = new ScottPlot.Plot();
        ChartRenderer.Render(plot, model);
        plot.SavePng(path, 1200, 700);

        Console.WriteLine($"Rendered sample chart: {path}");
        Console.WriteLine($"  bars   : {model.Bars.Count}");
        Console.WriteLine($"  alerts : {model.Alerts.Count}");
        foreach (var band in model.Bands)
        {
            Console.WriteLine($"  {band.Label,-7}: {band.Lower:N2} - {band.Upper:N2}");
        }

        foreach (var line in model.Lines)
        {
            Console.WriteLine($"  {line.Label,-7}: {line.Value:N2}");
        }
        return 0;
    }

    /// <summary>
    /// A deterministic session with an opening range, a breakout, a retest and a premarket
    /// break - so every element the renderer can draw actually appears in the output.
    /// </summary>
    private static ChartRenderModel BuildSample()
    {
        var day = new DateTime(2026, 8, 3);
        var offset = MarketSession.ExchangeTimeZone.GetUtcOffset(day);
        DateTimeOffset At(int h, int m) => new(day.Add(new TimeSpan(h, m, 0)), offset);

        var bars = new List<Bar>();
        var rng = new Random(20260804);
        var price = 100m;

        // 09:30-16:00 on five-minute bars.
        for (var minutes = 0; minutes < 390; minutes += 5)
        {
            var time = At(9, 30).AddMinutes(minutes);

            // Flat through the opening range, then a push up, then a drift back - enough
            // shape to see a breakout and a retest against the band.
            var bias = minutes switch
            {
                < 15 => 0m,
                < 90 => 0.08m,
                < 150 => -0.05m,
                _ => 0.03m
            };

            var open = price;
            var close = Math.Round(open + bias + ((decimal)rng.NextDouble() - 0.5m) * 0.35m, 2);
            var wick = Math.Abs(close - open) * (decimal)(0.4 + rng.NextDouble());

            bars.Add(new Bar(
                time,
                open,
                Math.Round(Math.Max(open, close) + wick, 2),
                Math.Round(Math.Min(open, close) - wick, 2),
                close,
                rng.Next(50_000, 250_000)));

            price = close;
        }

        // Range measured from the bars that actually fall in the 09:30-09:45 window, so the
        // drawn band matches the data rather than being asserted independently.
        var rangeBars = bars.Where(b => MarketSession.IsOpeningRange(b.Timestamp)).ToList();
        var orbHigh = rangeBars.Max(b => b.High);
        var orbLow = rangeBars.Min(b => b.Low);

        // Premarket high above the range, so PATH-3 is legitimately drawable.
        var pmHigh = orbHigh + 1.20m;
        var pmLow = orbLow - 0.90m;

        AlertRecord Alert(string path, string direction, DateTimeOffset at, decimal trigger) => new()
        {
            Ticker = "DEMO",
            Strategy = "ORB_Breakout",
            AlertPath = path,
            Direction = direction,
            Levels = new Dictionary<string, decimal?>
            {
                ["orb_duration"] = 15,
                ["orb_high"] = orbHigh,
                ["orb_low"] = orbLow,
                ["premarket_high"] = pmHigh,
                ["premarket_low"] = pmLow
            },
            TriggerPrice = trigger,
            Timestamp = at
        };

        var breakoutBar = bars.First(b => b.Timestamp > At(9, 45) && b.Close > orbHigh);
        var retestBar = bars.First(b => b.Timestamp > breakoutBar.Timestamp.AddMinutes(20));
        // Bar is a struct, so FirstOrDefault yields a zeroed bar rather than null and ??
        // does not apply. Match explicitly and fall back to the last bar.
        var pmBreaks = bars.Where(b => b.Close > pmHigh).ToList();
        var pmBreakBar = pmBreaks.Count > 0 ? pmBreaks[0] : bars[^1];

        // Deliberately includes two alerts on the SAME bar and two one bar apart. Flags
        // firing together is routine - PATH-1 and PATH-3 both trigger on a close that
        // clears the range and a premarket level - and overprinted labels were the defect
        // this sample now has to reproduce.
        var crowdBar = bars.First(b => b.Timestamp > retestBar.Timestamp.AddMinutes(30));
        var crowdNext = bars.First(b => b.Timestamp > crowdBar.Timestamp);

        var alerts = new List<AlertRecord>
        {
            Alert("PATH-1", "LONG", breakoutBar.Timestamp, breakoutBar.Close),
            Alert("PATH-3", "LONG", breakoutBar.Timestamp, breakoutBar.Close),
            Alert("PATH-2", "LONG", retestBar.Timestamp, retestBar.Close),
            Alert("PATH-2", "SHORT", crowdBar.Timestamp, crowdBar.Close),
            Alert("PATH-1", "SHORT", crowdNext.Timestamp, crowdNext.Close),
            Alert("PATH-3", "LONG", pmBreakBar.Timestamp, pmBreakBar.Close),

            // Deliberately three hours PAST the final bar. Alerts persist across sessions,
            // so the list routinely contains signals from a run that got further than the
            // one on screen. Drawn, they float in empty space and read as phantom signals.
            // This one must NOT appear in the output.
            Alert("PATH-1", "SHORT", bars[^1].Timestamp.AddHours(3), bars[^1].Close)
        };

        return new ChartRenderModel(
            "DEMO",
            bars,
            TimeframeMinutes: 5,
            [new Core.Charting.ChartBand("ORB", orbHigh, orbLow)],
            [
                new Core.Charting.ChartLine("PM", pmHigh, Core.Charting.LevelTone.Positive),
                new Core.Charting.ChartLine("PM", pmLow, Core.Charting.LevelTone.Negative)
            ],
            alerts,
            // Focus the retest, so the emphasised-vs-plain flag styling is both visible.
            alerts[2]);
    }
}
