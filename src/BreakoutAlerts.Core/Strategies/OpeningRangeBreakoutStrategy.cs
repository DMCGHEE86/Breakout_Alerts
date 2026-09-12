using System.Collections.Concurrent;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Strategies;

/// <summary>
/// 15-minute Opening Range Breakout. A C# port of the Pine Script indicator in the sibling
/// <c>Indicators</c> project.
/// </summary>
/// <remarks>
/// Three signal paths:
/// <list type="bullet">
/// <item><b>PATH-1</b> - the first candle body to close outside the locked 09:30-09:45 range.</item>
/// <item><b>PATH-2</b> - a retest of the broken level followed by a reversal candle back in
/// the breakout direction, with the setup invalidated if any body re-enters the range.</item>
/// <item><b>PATH-3</b> - a close beyond the premarket level, fired only when that level lies
/// OUTSIDE the opening range. When the premarket high sits inside the range, clearing the
/// ORB high already cleared it, so the alert would carry no information PATH-1 did not.</item>
/// </list>
///
/// <para><b>State is keyed per ticker.</b> One instance is evaluated against every watched
/// symbol, so a bare field holding "the current ORB high" would pass single-symbol testing
/// and silently cross-contaminate a real scan. All mutable state lives in
/// <see cref="_sessions"/>.</para>
///
/// <para><b>Bars are processed once.</b> The scanner passes overlapping ranges on each
/// cycle, so each ticker records the timestamp of the last bar it consumed. Without this a
/// single breakout would re-fire on every scan for the rest of the day.</para>
///
/// <para><b>Parity with the Pine script is deliberate.</b> The rule ordering, the inclusive
/// comparisons on invalidation, and the supported timeframe set are all carried over
/// unchanged so the two implementations can be run over the same session and compared
/// signal for signal - the cheapest correctness test available for a port.</para>
/// </remarks>
public sealed class OpeningRangeBreakoutStrategy : IPriceStrategy
{
    /// <summary>Parameter key for the evaluation timeframe.</summary>
    public const string ParamTimeframe = "triggerTimeframeMinutes";

    /// <summary>Parameter key for repeated PATH-2 signals.</summary>
    public const string ParamRepeatPath2 = "allowRepeatPath2";

    /// <summary>Parameter key for the premarket confirmation signal.</summary>
    public const string ParamPremarketConfirm = "enablePremarketConfirm";

    /// <summary>Parameter key for the minimum opening range width filter.</summary>
    public const string ParamMinRangePercent = "minRangePercent";

    private readonly ConcurrentDictionary<string, SessionState> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, double> _config = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the strategy with default parameters applied.</summary>
    public OpeningRangeBreakoutStrategy()
    {
        Configure(new Dictionary<string, double>());
    }

    /// <inheritdoc />
    public string Id => "ORB_Breakout";

    /// <inheritdoc />
    public string DisplayName => "Opening Range Breakout (15m)";

    /// <inheritdoc />
    public string Description =>
        "Locks the 09:30-09:45 ET range, then fires on the first body close outside it (PATH-1), " +
        "on a retest-and-reversal in the breakout direction (PATH-2), and on a close beyond a " +
        "premarket level that sits outside the range (PATH-3).";

    /// <inheritdoc />
    public IReadOnlyList<StrategyParameterDescriptor> Parameters { get; } =
    [
        new StrategyParameterDescriptor(
            ParamTimeframe,
            "Trigger timeframe (minutes)",
            "Bar size breakouts are evaluated on. Only 1, 3, 5 and 15 are permitted - any other size produces a bar straddling the 09:45 lock, which would silently widen the range.",
            DefaultValue: 5, Minimum: 1, Maximum: 15, Kind: StrategyParameterKind.Integer),

        new StrategyParameterDescriptor(
            ParamRepeatPath2,
            "Allow repeated PATH-2 signals",
            "On: every fresh retest-and-reverse fires another alert. Off: PATH-2 fires at most once per direction per day.",
            DefaultValue: 0, Minimum: 0, Maximum: 1, Kind: StrategyParameterKind.Toggle),

        new StrategyParameterDescriptor(
            ParamPremarketConfirm,
            "Premarket confirmation (PATH-3)",
            "Fires when price closes beyond a premarket level that sits outside the opening range. Suppressed when the premarket level is inside the range, where it would duplicate PATH-1.",
            DefaultValue: 1, Minimum: 0, Maximum: 1, Kind: StrategyParameterKind.Toggle),

        new StrategyParameterDescriptor(
            ParamMinRangePercent,
            "Minimum range width",
            "Skips symbols whose opening range is narrower than this share of price, filtering out names where a breakout is meaningless noise.",
            DefaultValue: 0.002, Minimum: 0, Maximum: 0.05, Kind: StrategyParameterKind.Percent)
    ];

    /// <inheritdoc />
    /// <remarks>
    /// The opening range is a band; the premarket levels are independent lines that can sit
    /// anywhere relative to it, so they are not a pair and must not be shaded between.
    /// </remarks>
    public IReadOnlyList<Charting.LevelDisplay> LevelDisplays { get; } =
    [
        Charting.LevelDisplay.Band("ORB", "orb_high", "orb_low"),
        Charting.LevelDisplay.Line("PM", "premarket_high", Charting.LevelTone.Positive),
        Charting.LevelDisplay.Line("PM", "premarket_low", Charting.LevelTone.Negative)
    ];

    /// <inheritdoc />
    public void Configure(IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        _config.Clear();

        // Seed defaults first, then overlay. A config written by an older build will not
        // contain parameters added since, and those must fall back rather than vanish.
        foreach (var descriptor in Parameters)
        {
            _config[descriptor.Key] = descriptor.DefaultValue;
        }

        foreach (var (key, value) in values)
        {
            _config[key] = value;
        }
    }

    /// <inheritdoc />
    public void Reset(string? ticker = null)
    {
        if (ticker is null)
        {
            _sessions.Clear();
        }
        else
        {
            _sessions.TryRemove(ticker, out _);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StrategySignal> Evaluate(string ticker, IReadOnlyList<Bar> bars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentNullException.ThrowIfNull(bars);

        if (bars.Count == 0)
        {
            return [];
        }

        var state = _sessions.GetOrAdd(ticker, _ => new SessionState());
        var signals = new List<StrategySignal>();

        // Locking per ticker rather than globally: two symbols can be evaluated
        // concurrently without contending, while a single symbol's state stays coherent.
        lock (state.Gate)
        {
            foreach (var bar in bars)
            {
                // Skip bars already consumed on an earlier scan cycle. This is what stops
                // one breakout re-firing every cycle for the rest of the session.
                if (state.LastProcessedBar is { } last && bar.Timestamp <= last)
                {
                    continue;
                }

                ProcessBar(ticker, bar, state, signals);
                state.LastProcessedBar = bar.Timestamp;
            }
        }

        return signals;
    }

    /// <summary>Advances the state machine by exactly one bar.</summary>
    private void ProcessBar(string ticker, Bar bar, SessionState state, List<StrategySignal> signals)
    {
        var sessionDate = MarketSession.SessionDate(bar.Timestamp);

        // A new trading date wipes everything. This is what stops yesterday's range and
        // yesterday's fired flags leaking into today.
        if (state.SessionDate != sessionDate)
        {
            state.ResetForNewSession(sessionDate);
        }

        // ---- Premarket accumulation (04:00-09:30) ----
        if (MarketSession.IsPremarket(bar.Timestamp))
        {
            state.PremarketHigh = state.PremarketHigh is { } ph ? Math.Max(ph, bar.High) : bar.High;
            state.PremarketLow = state.PremarketLow is { } pl ? Math.Min(pl, bar.Low) : bar.Low;
            return;
        }

        // ---- Opening range accumulation (09:30-09:45) ----
        if (MarketSession.IsOpeningRange(bar.Timestamp))
        {
            state.OrbHigh = state.OrbHigh is { } oh ? Math.Max(oh, bar.High) : bar.High;
            state.OrbLow = state.OrbLow is { } ol ? Math.Min(ol, bar.Low) : bar.Low;
            return;
        }

        // Everything past here requires an actual regular-session bar after the lock.
        if (!MarketSession.IsRegularSession(bar.Timestamp))
        {
            return;
        }

        if (state.OrbHigh is not { } orbHigh || state.OrbLow is not { } orbLow)
        {
            // No range formed - the chart's history began mid-session. Evaluating against
            // a range that was never measured would invent signals.
            return;
        }

        state.OrbLocked = true;

        // ---- Range width filter ----
        // A range narrower than the threshold is noise, and a "breakout" of it is
        // meaningless. Checked here rather than at accumulation time so the levels are
        // still recorded and visible even when signals are suppressed.
        var minRangePercent = (decimal)_config[ParamMinRangePercent];
        if (bar.Close > 0 && (orbHigh - orbLow) / bar.Close < minRangePercent)
        {
            return;
        }

        var repeatPath2 = _config[ParamRepeatPath2] >= 0.5;
        var premarketConfirm = _config[ParamPremarketConfirm] >= 0.5;

        EvaluateLong(ticker, bar, state, orbHigh, orbLow, repeatPath2, premarketConfirm, signals);
        EvaluateShort(ticker, bar, state, orbHigh, orbLow, repeatPath2, premarketConfirm, signals);
    }

    /// <summary>Long-side state machine. Mirrored exactly by <see cref="EvaluateShort"/>.</summary>
    private void EvaluateLong(
        string ticker, Bar bar, SessionState state,
        decimal orbHigh, decimal orbLow,
        bool repeatPath2, bool premarketConfirm,
        List<StrategySignal> signals)
    {
        // ---- Step 1: invalidation, checked FIRST ----
        // Ordering is load-bearing. Checking this before the trigger logic means a bar that
        // re-enters the range can never also be read as a valid retest. Inclusive
        // comparisons because the rule is "touches or crosses", and only the body counts.
        if (state.LongPath2Alive && (bar.Open <= orbHigh || bar.Close <= orbHigh))
        {
            state.LongPath2Alive = false;
            state.LongRetestSeen = false;
        }

        // ---- Step 2: PATH-1 ----
        var firedPath1 = false;
        if (!state.LongPath1Fired && bar.Close > orbHigh)
        {
            state.LongPath1Fired = true;
            state.LongPath2Alive = true;
            firedPath1 = true;
            signals.Add(BuildSignal(ticker, bar, state, "PATH-1", TradeDirection.Long, orbHigh, orbLow));
        }

        // ---- Steps 3 and 4: retest and PATH-2 ----
        // Gated on the PATH-1 signal not having fired on this very bar - the breakout bar
        // is not itself a retest of the level it just broke.
        if (state.LongPath2Alive && !firedPath1 && (repeatPath2 || !state.LongPath2Fired))
        {
            // A retest is a COUNTER-DIRECTION candle - bearish, for a long setup - whose
            // wick reaches back to the broken level while its body stays outside. Requiring
            // the pullback direction is what makes this a retest rather than any bar that
            // happens to graze the level: a bare wick touch armed the setup during ordinary
            // chop and fired PATH-2 on the first red bar of it, well before an actual
            // pullback had happened.
            if (bar.IsBearish && bar.Low <= orbHigh)
            {
                state.LongRetestSeen = true;
            }

            // The trigger is a WITH-DIRECTION candle closing beyond the level - the bar
            // that confirms continuation after the pullback was rejected. The explicit
            // close test states the requirement rather than leaving it implied by the
            // invalidation check above.
            if (state.LongRetestSeen && bar.IsBullish && bar.Close > orbHigh)
            {
                state.LongPath2Fired = true;
                state.LongRetestSeen = false;
                signals.Add(BuildSignal(ticker, bar, state, "PATH-2", TradeDirection.Long, orbHigh, orbLow));
            }
        }

        // ---- PATH-3: premarket confirmation ----
        // Only meaningful when the premarket high sits ABOVE the opening range high. If it
        // sits inside, clearing the ORB high already cleared it and this would duplicate
        // PATH-1 rather than confirm it.
        if (premarketConfirm
            && !state.LongPath3Fired
            && state.PremarketHigh is { } pmHigh
            && pmHigh > orbHigh
            && bar.Close > pmHigh)
        {
            state.LongPath3Fired = true;
            signals.Add(BuildSignal(ticker, bar, state, "PATH-3", TradeDirection.Long, orbHigh, orbLow));
        }
    }

    /// <summary>Short-side state machine. Every comparison is the long side inverted.</summary>
    private void EvaluateShort(
        string ticker, Bar bar, SessionState state,
        decimal orbHigh, decimal orbLow,
        bool repeatPath2, bool premarketConfirm,
        List<StrategySignal> signals)
    {
        if (state.ShortPath2Alive && (bar.Open >= orbLow || bar.Close >= orbLow))
        {
            state.ShortPath2Alive = false;
            state.ShortRetestSeen = false;
        }

        var firedPath1 = false;
        if (!state.ShortPath1Fired && bar.Close < orbLow)
        {
            state.ShortPath1Fired = true;
            state.ShortPath2Alive = true;
            firedPath1 = true;
            signals.Add(BuildSignal(ticker, bar, state, "PATH-1", TradeDirection.Short, orbHigh, orbLow));
        }

        if (state.ShortPath2Alive && !firedPath1 && (repeatPath2 || !state.ShortPath2Fired))
        {
            // Counter-direction pullback for a short setup is a bullish candle wicking back
            // up to the broken low.
            if (bar.IsBullish && bar.High >= orbLow)
            {
                state.ShortRetestSeen = true;
            }

            if (state.ShortRetestSeen && bar.IsBearish && bar.Close < orbLow)
            {
                state.ShortPath2Fired = true;
                state.ShortRetestSeen = false;
                signals.Add(BuildSignal(ticker, bar, state, "PATH-2", TradeDirection.Short, orbHigh, orbLow));
            }
        }

        if (premarketConfirm
            && !state.ShortPath3Fired
            && state.PremarketLow is { } pmLow
            && pmLow < orbLow
            && bar.Close < pmLow)
        {
            state.ShortPath3Fired = true;
            signals.Add(BuildSignal(ticker, bar, state, "PATH-3", TradeDirection.Short, orbHigh, orbLow));
        }
    }

    private StrategySignal BuildSignal(
        string ticker, Bar bar, SessionState state,
        string path, TradeDirection direction,
        decimal orbHigh, decimal orbLow) =>
        new(
            ticker,
            Id,
            path,
            direction,
            bar.Close,
            // Bar timestamps are OPEN times, so the close is one bar-length later. Alerts
            // must carry the moment the signal was actually knowable.
            bar.Timestamp.AddMinutes(_config[ParamTimeframe]),
            new Dictionary<string, decimal?>
            {
                ["orb_high"] = orbHigh,
                ["orb_low"] = orbLow,
                ["premarket_high"] = state.PremarketHigh,
                ["premarket_low"] = state.PremarketLow
            });

    /// <summary>Per-ticker, per-session mutable state.</summary>
    private sealed class SessionState
    {
        public Lock Gate { get; } = new();

        public DateOnly SessionDate;
        public DateTimeOffset? LastProcessedBar;

        public decimal? OrbHigh;
        public decimal? OrbLow;
        public bool OrbLocked;

        public decimal? PremarketHigh;
        public decimal? PremarketLow;

        public bool LongPath1Fired, ShortPath1Fired;
        public bool LongPath2Alive, ShortPath2Alive;
        public bool LongRetestSeen, ShortRetestSeen;
        public bool LongPath2Fired, ShortPath2Fired;
        public bool LongPath3Fired, ShortPath3Fired;

        /// <summary>Clears everything except the bar-dedup marker, which is monotonic.</summary>
        public void ResetForNewSession(DateOnly date)
        {
            SessionDate = date;
            OrbHigh = OrbLow = null;
            OrbLocked = false;
            PremarketHigh = PremarketLow = null;
            LongPath1Fired = ShortPath1Fired = false;
            LongPath2Alive = ShortPath2Alive = false;
            LongRetestSeen = ShortRetestSeen = false;
            LongPath2Fired = ShortPath2Fired = false;
            LongPath3Fired = ShortPath3Fired = false;
        }
    }
}
