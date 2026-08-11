using BreakoutAlerts.Core.Charting;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;
using ScottPlot;

// ScottPlot defines its own Bar (a bar-chart element) and Color, both of which collide with
// this project's price Bar and with System.Windows.Media.Color. Aliased rather than
// qualified at each use, to keep the drawing code readable.
using PriceBar = BreakoutAlerts.Core.Models.Bar;
using PlotColor = ScottPlot.Color;

namespace BreakoutAlerts.App.Services;

/// <summary>
/// Everything needed to draw one chart, with no WPF or ScottPlot control types.
/// </summary>
/// <remarks>
/// Exists so the drawing can be exercised without a window. See
/// <see cref="ChartRenderer"/> for why that matters.
/// </remarks>
/// <param name="Ticker">Symbol being charted.</param>
/// <param name="Bars">Session bars, ascending.</param>
/// <param name="TimeframeMinutes">Bar size, used for candle width.</param>
/// <param name="OrbHigh">Locked opening range high, if known.</param>
/// <param name="OrbLow">Locked opening range low, if known.</param>
/// <param name="PremarketHigh">Premarket high, if any.</param>
/// <param name="PremarketLow">Premarket low, if any.</param>
/// <param name="Alerts">Every alert on this ticker, oldest first.</param>
/// <param name="Focus">The alert that opened the window, drawn larger.</param>
public sealed record ChartRenderModel(
    string Ticker,
    IReadOnlyList<PriceBar> Bars,
    int TimeframeMinutes,
    decimal? OrbHigh,
    decimal? OrbLow,
    decimal? PremarketHigh,
    decimal? PremarketLow,
    IReadOnlyList<AlertRecord> Alerts,
    AlertRecord? Focus);

/// <summary>
/// Draws the session chart onto a ScottPlot plot: candles, the opening range band,
/// premarket levels and alert flags.
/// </summary>
/// <remarks>
/// <b>Deliberately free of WPF and of ScottPlot's WPF control.</b> It takes a bare
/// <see cref="Plot"/>, so the identical code path can be driven headlessly and saved to a
/// PNG. That is not academic: this application's windows cannot be screenshotted from the
/// build environment, so rendering to a file is the only way to actually see whether the
/// chart draws correctly rather than merely compiles. The app exposes a
/// <c>--render-sample</c> switch that does exactly that.
/// </remarks>
public static class ChartRenderer
{
    private const string OrbHex = "#29B6F6";
    private const string UpHex = "#26D07C";
    private const string DownHex = "#FF5C6C";

    /// <summary>Plot background, reused as the plate behind labels drawn over candles.</summary>
    private const string PanelHex = "#0F1115";

    /// <summary>Clears the plot and draws the model onto it.</summary>
    public static void Render(Plot plot, ChartRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(plot);
        ArgumentNullException.ThrowIfNull(model);

        plot.Clear();
        ApplyTheme(plot);

        if (model.Bars.Count == 0)
        {
            return;
        }

        // Bands first so they sit behind the candles - painted on top they would wash out the
        // very bodies the window exists to judge distance from.
        DrawPremarketShading(plot, model);
        DrawOpeningRange(plot, model);
        DrawPremarketLevels(plot, model);
        DrawCandles(plot, model);
        DrawMarketOpen(plot, model);
        DrawAlertFlags(plot, model);

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.AutoScale();
    }

    private static void ApplyTheme(Plot plot)
    {
        plot.FigureBackground.Color = PlotColor.FromHex("#161920");
        plot.DataBackground.Color = PlotColor.FromHex("#12151B");

        // Brighter than the app's secondary text. On screen the axis labels were sitting at
        // #98A0B0 against a near-black plot and were genuinely hard to read at a glance -
        // which defeats the purpose of a window opened to check a time quickly.
        plot.Axes.Color(PlotColor.FromHex("#C6CDDA"));
        plot.Grid.MajorLineColor = PlotColor.FromHex("#1E222B");
    }

    /// <summary>Dims the premarket portion so the regular session reads as the main event.</summary>
    /// <remarks>
    /// Premarket bars are kept - the premarket high and low are levels this chart draws, and
    /// PATH-3 fires against them - but they are context, not the subject. Undimmed they were
    /// visually indistinguishable from regular-session action, so the eye had no cue where the
    /// session it cares about actually begins.
    /// </remarks>
    private static void DrawPremarketShading(Plot plot, ChartRenderModel model)
    {
        var premarket = model.Bars.Where(b => MarketSession.IsPremarket(b.Timestamp)).ToList();
        if (premarket.Count == 0)
        {
            return;
        }

        // Bounded by the plotted data's own extremes. A rectangle spanning infinity would
        // make AutoScale useless.
        var low = (double)model.Bars.Min(b => b.Low);
        var high = (double)model.Bars.Max(b => b.High);

        var band = plot.Add.Rectangle(
            FirstBarX(model),
            premarket[^1].Timestamp.LocalDateTime.AddMinutes(model.TimeframeMinutes).ToOADate(),
            low,
            high);

        // Light rather than dark. A darker wash was invisible against an already near-black
        // plot; a faint light overlay separates the two regions at a glance without touching
        // candle colour.
        band.FillStyle.Color = PlotColor.FromHex("#8FA0C0").WithAlpha(0.10);
        band.LineStyle.Width = 0;
    }

    /// <summary>Marks 09:30, where the session being traded begins.</summary>
    private static void DrawMarketOpen(Plot plot, ChartRenderModel model)
    {
        // Found from the bars rather than constructed from a date, so it lands exactly on a
        // real bar boundary and cannot drift by a timezone or a rounding.
        var openBar = model.Bars.FirstOrDefault(b => MarketSession.IsRegularSession(b.Timestamp));
        if (openBar.Timestamp == default)
        {
            return;
        }

        var line = plot.Add.VerticalLine(openBar.Timestamp.LocalDateTime.ToOADate());
        line.LineColor = PlotColor.FromHex("#E8EBF2").WithAlpha(0.55);
        line.LineWidth = 1;
        line.LinePattern = LinePattern.Dashed;
        line.LabelText = "9:30 OPEN";
        line.LabelFontColor = PlotColor.FromHex("#E8EBF2");
        line.LabelBackgroundColor = PlotColor.FromHex(PanelHex);
        line.LabelFontSize = 10;
    }

    private static double FirstBarX(ChartRenderModel model) =>
        model.Bars.Count > 0 ? model.Bars[0].Timestamp.LocalDateTime.ToOADate() : 0;

    /// <summary>
    /// A small vertical offset in price units, used to lift labels clear of what they annotate.
    /// </summary>
    /// <remarks>
    /// Scaled to the session's own price span rather than a fixed number of dollars: the
    /// same code has to look right on a $70 stock and a $740 one, and a constant offset
    /// would be invisible on one and enormous on the other.
    /// </remarks>
    private static double LabelOffset(ChartRenderModel model)
    {
        if (model.Bars.Count == 0)
        {
            return 0;
        }

        var high = model.Bars.Max(b => (double)b.High);
        var low = model.Bars.Min(b => (double)b.Low);
        return (high - low) * 0.035;
    }

    private static void DrawCandles(Plot plot, ChartRenderModel model)
    {
        var ohlcs = model.Bars
            .Select(b => new OHLC
            {
                Open = (double)b.Open,
                High = (double)b.High,
                Low = (double)b.Low,
                Close = (double)b.Close,
                DateTime = b.Timestamp.LocalDateTime,
                TimeSpan = TimeSpan.FromMinutes(model.TimeframeMinutes)
            })
            .ToList();

        var candles = plot.Add.Candlestick(ohlcs);
        candles.RisingColor = PlotColor.FromHex(UpHex);
        candles.FallingColor = PlotColor.FromHex(DownHex);
    }

    private static void DrawOpeningRange(Plot plot, ChartRenderModel model)
    {
        if (model.OrbHigh is not { } high || model.OrbLow is not { } low)
        {
            return;
        }

        var band = plot.Add.Rectangle(
            FirstBarX(model),
            model.Bars[^1].Timestamp.LocalDateTime.ToOADate(),
            (double)low,
            (double)high);

        band.FillColor = PlotColor.FromHex(OrbHex).WithAlpha(0.10);
        band.LineColor = PlotColor.FromHex(OrbHex);
        band.LineWidth = 1.5f;

        AddLevelLabel(plot, model, (double)high, $"ORB {high:N2}", OrbHex, above: true);
        AddLevelLabel(plot, model, (double)low, $"ORB {low:N2}", OrbHex, above: false);
    }

    private static void DrawPremarketLevels(Plot plot, ChartRenderModel model)
    {
        if (model.PremarketHigh is { } pmHigh)
        {
            var line = plot.Add.HorizontalLine((double)pmHigh);
            line.Color = PlotColor.FromHex(UpHex);
            line.LineWidth = 1;
            line.LinePattern = LinePattern.Dotted;
            AddLevelLabel(plot, model, (double)pmHigh, $"PM {pmHigh:N2}", UpHex, above: true);
        }

        if (model.PremarketLow is { } pmLow)
        {
            var line = plot.Add.HorizontalLine((double)pmLow);
            line.Color = PlotColor.FromHex(DownHex);
            line.LineWidth = 1;
            line.LinePattern = LinePattern.Dotted;
            AddLevelLabel(plot, model, (double)pmLow, $"PM {pmLow:N2}", DownHex, above: false);
        }
    }

    /// <summary>Marks every alert on the ticker, with the focused one emphasised.</summary>
    /// <remarks>
    /// All of them, not just the one double-clicked: PATH-1, then the PATH-2 retest, then
    /// PATH-3 reads as the story of the move. A lone flag only says something happened.
    /// </remarks>
    private static void DrawAlertFlags(Plot plot, ChartRenderModel model)
    {
        var offset = LabelOffset(model);
        var span = model.Bars[^1].Timestamp.LocalDateTime.ToOADate() - FirstBarX(model);
        // Widened along with the plate. A label is now physically wider than the bar it sits
        // on, so flags several bars apart still overlap horizontally and need tiering.
        var crowdingWindow = span * 0.06;
        var placed = new List<double>();

        // Alerts on the SAME bar become ONE flag with a combined label. This is the common
        // case, not an edge case: a close that clears both the opening range and a
        // premarket level fires PATH-1 and PATH-3 together, at an identical timestamp.
        // Stacking cannot separate those - there is no horizontal distance to stack against
        // - and drawing them independently produced overprinted text reading "PATH-PATH-2".
        // Only alerts that fall inside the plotted window are drawn. One outside it has no
        // candle to mark and renders as a flag floating in empty space past the end of the
        // data - which reads as a phantom signal rather than as missing bars.
        //
        // This is not hypothetical: alerts.jsonl persists across application restarts, so
        // the list routinely contains alerts from a session that ran further than the one
        // currently loaded. Against a live feed the same thing happens with any alert older
        // than the bars on screen.
        // Every alert is resolved to the bar that actually fired it - see AlertBarLocator for
        // why an alert timestamp is not a bar timestamp. A -1 means the alert falls outside
        // the plotted bars and is dropped.
        //
        // Grouped by resolved BAR rather than by exact timestamp, so anything landing on one
        // candle merges into a single flag even if the timestamps differ slightly.
        var groups = model.Alerts
            .Where(a => a.TriggerPrice.HasValue)
            .Select(a => new
            {
                Alert = a,
                Index = AlertBarLocator.FindTriggerBar(model.Bars, a.Timestamp, model.TimeframeMinutes)
            })
            .Where(x => x.Index >= 0)
            .GroupBy(x => (x.Index, IsLong: string.Equals(x.Alert.Direction, "LONG", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(g => g.Key.Index)
            .ToList();

        foreach (var group in groups)
        {
            var isLong = group.Key.IsLong;
            var alerts = group.Select(g => g.Alert).ToList();
            var price = (double)alerts[0].TriggerPrice!.Value;
            var isFocus = alerts.Any(a => ReferenceEquals(a, model.Focus));
            var colour = PlotColor.FromHex(isLong ? UpHex : DownHex);

            // The bar's own timestamp is the x-position, so a flag can only ever sit on a
            // real candle.
            var bar = model.Bars[group.Key.Index];
            var x = bar.Timestamp.LocalDateTime.ToOADate();

            // Distinct, so a duplicate path on one bar does not repeat in the label.
            var label = string.Join(" ", alerts.Select(a => a.AlertPath).Distinct());

            // Flags sit clear of the candle, below for longs and above for shorts, so they
            // never obscure the body whose distance from the range is the whole point.
            var direction = isLong ? -1 : 1;

            // Anchored to the CANDLE's own high or low, not to the trigger price plus a
            // chart-scaled offset. The earlier version floated a flag 1.5 points above its
            // bar on a 7-point chart, landing it on the opening range line with no candle
            // anywhere near - which reads as the signal being on the wrong bar entirely.
            // Hanging it off the wick keeps flag and candle visually welded together.
            var anchor = isLong ? (double)bar.Low : (double)bar.High;

            // Stacking separates flags on nearby bars. The spacing has to clear a whole
            // label, not just a marker: once labels gained an opaque plate they became much
            // taller, and the previous 1.45 tier gap left PATH-1 and PATH-2 overprinting into
            // an unreadable smear whenever a retest confirmed a bar or two after the break -
            // which is exactly the sequence the window is opened to read.
            var tier = placed.Count(px => Math.Abs(px - x) < crowdingWindow);
            placed.Add(x);

            var markerY = anchor + (offset * direction * (0.55 + (tier * 2.6)));

            var marker = plot.Add.Marker(x, markerY);
            marker.MarkerStyle.Shape = isLong ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
            marker.MarkerStyle.Size = isFocus ? 26 : 16;
            marker.MarkerStyle.FillColor = colour;
            marker.MarkerStyle.LineColor = isFocus ? PlotColor.FromHex("#E8EBF2") : colour;
            marker.MarkerStyle.LineWidth = isFocus ? 2 : 0;

            var text = plot.Add.Text(label, x, markerY + (offset * 0.8 * direction));
            text.LabelFontColor = colour;
            text.LabelFontSize = isFocus ? 13 : 11;
            text.LabelBold = isFocus;

            // Opaque plate behind the text. Alert labels sit over the candles by necessity -
            // that is where their bar is - and coloured text on a dense candle field was
            // genuinely unreadable in the 2026-08-04 render. The plate costs a little of the
            // chart behind it and buys legibility, which is the point of the window.
            text.LabelStyle.BackgroundColor = PlotColor.FromHex(PanelHex).WithAlpha(0.85);
            text.LabelStyle.Padding = 2;

            // Alignment does two jobs.
            //
            // Near the right edge the label is pulled inward, because the ORB and premarket
            // level labels are pinned there and a late signal overprinted them.
            //
            // Otherwise consecutive tiers alternate sides. Vertical spacing alone could not
            // fix this: a label is wider than several bars, so two flags a few bars apart
            // overlap horizontally no matter how far apart they are stacked, and pushing the
            // gap wide enough to clear that would throw flags off the top of the plot.
            // Alternating gives roughly half a label of horizontal separation for free.
            var nearRightEdge = x > FirstBarX(model) + (span * 0.88);

            text.LabelAlignment = nearRightEdge ? Alignment.MiddleRight
                : tier % 2 == 1 ? Alignment.MiddleLeft
                : Alignment.MiddleRight;
        }
    }

    /// <summary>Places a price label at the right edge of the plotted data.</summary>
    /// <remarks>
    /// Right edge, not left, for two reasons. It follows the price-scale convention every
    /// charting package uses, and it keeps these labels away from alert flags - which
    /// cluster wherever the action is, and collided with the left-anchored versions.
    ///
    /// <para>Anchored to a real bar's x, never to 0. The axis is in OLE automation dates,
    /// where 0 is 30 December 1899 - a label there would stretch the axis across 127 years
    /// and squash the whole session into a single pixel column.</para>
    /// </remarks>
    private static void AddLevelLabel(Plot plot, ChartRenderModel model, double y, string label, string hex, bool above)
    {
        var nudge = LabelOffset(model) * 0.45 * (above ? 1 : -1);
        var x = model.Bars.Count > 0 ? model.Bars[^1].Timestamp.LocalDateTime.ToOADate() : 0;

        var text = plot.Add.Text(label, x, y + nudge);
        text.LabelFontColor = PlotColor.FromHex(hex);
        text.LabelFontSize = 11;
        text.LabelBold = true;
        text.LabelAlignment = Alignment.MiddleRight;
    }
}
