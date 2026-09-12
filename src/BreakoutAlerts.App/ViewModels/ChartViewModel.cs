using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.ViewModels;

/// <summary>
/// Data behind the chart popup: one snapshot of a ticker's session, taken when the window
/// opens.
/// </summary>
/// <remarks>
/// <b>Deliberately static.</b> The window is opened to answer one question - how far did
/// this candle close from the opening range - and then closed. It does not subscribe to
/// updates, which removes an entire class of problems: no dispatcher marshalling, no
/// redraw thrash, no risk of the chart quietly diverging from the alert that opened it.
/// Reopening gives a fresh snapshot.
///
/// <para>Bars come from the same <see cref="IMarketDataProvider"/> the scanner uses, so
/// the candles drawn here are the candles the strategy evaluated. If they came from
/// separate sources the chart could eventually show a breakout the scanner never fired on,
/// and neither would be trustworthy.</para>
///
/// <para>Holds no ScottPlot types. Rendering lives in the window's code-behind, so this
/// stays plain testable data.</para>
/// </remarks>
public sealed partial class ChartViewModel : ObservableObject
{
    private readonly IMarketDataProvider _marketData;
    private readonly IStrategyRegistry _registry;
    private readonly ILogger<ChartViewModel> _logger;

    /// <summary>
    /// Bars requested before filtering to the alert's session.
    /// </summary>
    /// <remarks>
    /// Enough to reach back through everything the provider will serve, because the request
    /// cannot ask for a specific day - it returns the most recent N bars and the session is
    /// selected from them here. An extended-hours session is 192 bars at 5 minutes, so this
    /// covers roughly four days.
    /// </remarks>
    private const int BarCount = 800;

    /// <summary>
    /// How much premarket lead-in the chart shows.
    /// </summary>
    /// <remarks>
    /// The full premarket session is 04:00-09:30, which is 5½ of the 12 hours to the close -
    /// nearly half the width spent on the part that is only context, leaving the session being
    /// traded squeezed into the remainder. That directly works against a window whose whole
    /// purpose is a quick read.
    ///
    /// <para>Trimming the bars costs nothing that matters: the premarket high and low are
    /// drawn as horizontal levels and stay visible whether or not the bar that set them is on
    /// screen. 90 minutes keeps the active part of the premarket, where the levels that
    /// actually get tested are usually formed.</para>
    /// </remarks>
    private static readonly TimeOnly ChartWindowStart = new(8, 0);

    /// <summary>True once the bar is at or past the chart's 08:00 start.</summary>
    private static bool IsAfterChartStart(DateTimeOffset instant) =>
        TimeOnly.FromDateTime(MarketSession.ToExchangeTime(instant).DateTime) >= ChartWindowStart;

    /// <summary>True while the bar is before the regular close.</summary>
    private static bool IsBeforeChartEnd(DateTimeOffset instant) =>
        TimeOnly.FromDateTime(MarketSession.ToExchangeTime(instant).DateTime) < MarketSession.RegularClose;

    /// <summary>Symbol being charted.</summary>
    [ObservableProperty]
    private string _ticker = string.Empty;

    /// <summary>Bar size in minutes.</summary>
    [ObservableProperty]
    private int _timeframeMinutes = 5;

    /// <summary>True while the snapshot is being fetched.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Set when the snapshot could not be taken, for display in the window.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Set when the focused alert falls outside the bars on screen.
    /// </summary>
    /// <remarks>
    /// Flags outside the plotted window are not drawn, because a marker with no candle
    /// under it reads as a phantom signal. But silently dropping the very alert the user
    /// double-clicked would be worse - they would get a chart with nothing marked on it and
    /// no reason why. This says so explicitly.
    /// </remarks>
    [ObservableProperty]
    private string? _outOfRangeNotice;

    /// <summary>Bars for the charted session, ascending.</summary>
    public IReadOnlyList<Bar> Bars { get; private set; } = [];

    /// <summary>Every alert fired on this ticker, oldest first.</summary>
    /// <remarks>
    /// All of them, not just the one double-clicked. PATH-1 then the PATH-2 retest then
    /// PATH-3 tells the story of the move; a single flag just says "something happened".
    /// </remarks>
    public IReadOnlyList<AlertRecord> Alerts { get; private set; } = [];

    /// <summary>The alert that opened the window, highlighted among the rest.</summary>
    public AlertRecord? FocusAlert { get; private set; }

    /// <summary>
    /// The focus alert's levels, named and valued for the header readout.
    /// </summary>
    /// <remarks>
    /// A list rather than named ORB and premarket properties. The window used to bind four
    /// fixed fields, which meant a ZEBRA alert would have shown four blanks under headings
    /// describing levels it does not have. What the levels are called is the strategy's
    /// business, and it already says so.
    /// </remarks>
    public IReadOnlyList<LevelReadout> Levels { get; private set; } = [];

    /// <summary>One labelled level for the chart header.</summary>
    /// <param name="Label">e.g. "ORB" or "YDAY".</param>
    /// <param name="Text">Formatted value, or a range for a band.</param>
    public sealed record LevelReadout(string Label, string Text);

    /// <summary>Projects this snapshot into the shape the renderer draws from.</summary>
    public Services.ChartRenderModel ToRenderModel()
    {
        var (bands, lines) = Core.Charting.ChartLevelResolver.Resolve(FocusAlert, _registry.All);

        return new Services.ChartRenderModel(
            Ticker,
            Bars,
            TimeframeMinutes,
            bands,
            lines,
            Alerts,
            FocusAlert);
    }

    /// <summary>Rebuilds the header readout from the current focus alert.</summary>
    private void RefreshLevels()
    {
        var (bands, lines) = Core.Charting.ChartLevelResolver.Resolve(FocusAlert, _registry.All);

        var readouts = new List<LevelReadout>();
        readouts.AddRange(bands.Select(b => new LevelReadout(b.Label, $"{b.Lower:N2} – {b.Upper:N2}")));
        readouts.AddRange(lines.Select(l => new LevelReadout(l.Label, $"{l.Value:N2}")));

        Levels = readouts;
    }

    /// <summary>Creates the ViewModel.</summary>
    public ChartViewModel(
        IMarketDataProvider marketData,
        IStrategyRegistry registry,
        ILogger<ChartViewModel> logger)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Takes the snapshot for one alert.</summary>
    /// <param name="focus">The alert that was double-clicked.</param>
    /// <param name="allAlerts">Every alert currently held, filtered to this ticker here.</param>
    public async Task LoadAsync(AlertRecord focus, IEnumerable<AlertRecord> allAlerts)
    {
        ArgumentNullException.ThrowIfNull(focus);

        FocusAlert = focus;
        Ticker = focus.Ticker;
        IsLoading = true;
        ErrorMessage = null;
        OutOfRangeNotice = null;

        try
        {
            // Same ticker AND same session as the focused alert. Without the session test,
            // yesterday's signals travel with the list and land on today's candles at
            // whatever time of day they happen to match - a flag on a bar that never
            // triggered anything, which is indistinguishable from a real signal.
            var focusSession = MarketSession.SessionDate(focus.Timestamp);
            Alerts = allAlerts
                .Where(a => string.Equals(a.Ticker, focus.Ticker, StringComparison.OrdinalIgnoreCase)
                    && MarketSession.SessionDate(a.Timestamp) == focusSession)
                .OrderBy(a => a.Timestamp)
                .ToList();

            var fetched = await _marketData.GetBarsAsync(Ticker, TimeframeMinutes, BarCount);

            // Scoped to the session the alert fired in, not simply "the most recent bars".
            // The provider returns several days so the strategy has history to work from, and
            // drawing all of it squeezed the day in question into a sliver at the right-hand
            // edge with earlier sessions filling the plot - the opening range, the levels and
            // the flags all became unreadable, which defeats the entire purpose of a window
            // opened to judge how far a candle closed from the range.
            //
            // Filtering by session date rather than taking a trailing slice also fixes the
            // separate problem of an older alert being charted against today's price action:
            // the day drawn is always the day the signal fired.
            // Premarket through the regular close - 04:00 to 16:00 - rather than the whole
            // extended session. Premarket stays because PATH-3 fires against its levels and
            // they are drawn on the chart. After-hours goes because it is not traded off this
            // window and it distorts what the window is for: on 2026-08-04 NVDA ran four
            // points after 16:00, which rescaled the price axis and squeezed the entire
            // regular session - opening range included - into the lower third. The question
            // being asked is how far a candle closed from the range, so anything that
            // compresses that distance is actively harmful.
            //
            // Bars up to the last alert are kept regardless, so a signal that fires outside
            // those hours still has its candle underneath it rather than a bare flag.
            var lastAlertAt = Alerts.Count > 0 ? Alerts[^1].Timestamp : focus.Timestamp;

            // The alert clause EXTENDS the window forwards, it does not open it backwards. An
            // earlier version wrote "in window OR at/before the last alert", which matched
            // every bar earlier in the day - so the 08:00 start had no effect at all and the
            // chart still opened at 04:00.
            Bars = fetched
                .Where(b => MarketSession.SessionDate(b.Timestamp) == focusSession
                    && IsAfterChartStart(b.Timestamp)
                    && (IsBeforeChartEnd(b.Timestamp) || b.Timestamp <= lastAlertAt))
                .ToList();

            if (fetched.Count == 0)
            {
                // The mock only knows a fixed set of symbols, and the real provider will
                // fail for a delisted or mistyped one. Saying so beats an empty chart the
                // user has to interpret.
                ErrorMessage = $"No bars available for {Ticker}.";
            }
            else if (Bars.Count == 0)
            {
                // History does not reach back far enough. Distinct from having no data at
                // all, and the difference matters: nothing is wrong with the symbol.
                ErrorMessage =
                    $"No bars for {focusSession:MMM dd} - history only goes back to " +
                    $"{fetched[0].Timestamp.ToLocalTime():MMM dd}.";
            }

            if (Bars.Count > 0
                && (focus.Timestamp < Bars[0].Timestamp || focus.Timestamp > Bars[^1].Timestamp))
            {
                OutOfRangeNotice =
                    $"This alert fired at {focus.Timestamp.ToLocalTime():MMM dd HH:mm}, outside the bars shown " +
                    $"({Bars[0].Timestamp.ToLocalTime():HH:mm}-{Bars[^1].Timestamp.ToLocalTime():HH:mm}). No flag is drawn for it.";
            }

            _logger.LogDebug("Chart snapshot for {Ticker}: {Bars} bars, {Alerts} alerts",
                Ticker, Bars.Count, Alerts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chart data for {Ticker}", Ticker);
            ErrorMessage = $"Could not load {Ticker}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;

            // Levels are derived from FocusAlert, which is a plain property with no change
            // notification. The window's bindings evaluate once when the DataContext is set -
            // before LoadAsync has run - so without this they stay on their placeholders
            // forever, showing nothing while the chart beside them draws the very same levels
            // correctly.
            RefreshLevels();
            OnPropertyChanged(nameof(Levels));
        }
    }
}
