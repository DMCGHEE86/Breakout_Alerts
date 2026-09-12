using System.Collections.Concurrent;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Strategies;

/// <summary>
/// ZEBRA - yesterday's high and low, tapped and rejected. A port of "The KGU Tap and Trap".
/// </summary>
/// <remarks>
/// Mean reversion at a known level, and deliberately the opposite of
/// <see cref="OpeningRangeBreakoutStrategy"/>: one trades continuation through a level, this
/// one trades failure at it. A second momentum strategy would mostly restate the first.
///
/// <para>Three rules, in order:</para>
/// <list type="number">
/// <item><b>The zone</b> is the previous session's regular-hours high and low. Regular hours
/// only - if the zone included extended hours, then asking whether extended hours stayed
/// inside it would be asking whether a range contains itself.</item>
/// <item><b>The overnight test.</b> Between the previous close and this open, every 30-minute
/// <i>body</i> must have stayed inside the zone. A body that tapped or crossed a level kills
/// that side for the day - the premise being that a level which has already been tested no
/// longer has orders resting on it.</item>
/// <item><b>The trigger.</b> During regular hours, a bar that reaches a surviving level and
/// closes back inside the zone is the signal: puts at the high, calls at the low.</item>
/// </list>
///
/// <para><b>The overnight test and the trigger are the same event read differently</b> - a
/// touch of the level. Before the open it means the level is spent; after the open it means
/// the trap has sprung. That is not a contradiction, but it is the thing most likely to be
/// implemented backwards, so it is stated here rather than left to be inferred.</para>
///
/// <para><b>Bodies for the overnight test, wicks for the trigger.</b> Both come from the
/// source material and both matter. Overnight, a wick through a level on thin volume is noise
/// and should not kill a setup; during the session, a wick through the level and a close back
/// inside <i>is</i> the rejection being traded.</para>
///
/// <para>The 30-minute series is requested from the gateway rather than aggregated from the
/// scanner's 5-minute bars - see <see cref="IPriceStrategy.AdditionalTimeframes"/> for why
/// that distinction is load-bearing here.</para>
/// </remarks>
public sealed class ZebraStrategy : IPriceStrategy
{
    /// <summary>Parameter key for the evaluation timeframe.</summary>
    public const string ParamTimeframe = "triggerTimeframeMinutes";

    /// <summary>Parameter key for the timeframe the zone and overnight test are read on.</summary>
    public const string ParamZoneTimeframe = "zoneTimeframeMinutes";

    /// <summary>Parameter key for repeated taps on the same side.</summary>
    public const string ParamRepeatTaps = "allowRepeatTaps";

    private readonly ConcurrentDictionary<string, SessionState> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, double> _config = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the strategy with default parameters applied.</summary>
    public ZebraStrategy()
    {
        Configure(new Dictionary<string, double>());
    }

    /// <inheritdoc />
    /// <remarks>
    /// Written into every alert record, so it is permanent in a way the display name is not.
    /// </remarks>
    public string Id => "ZEBRA";

    /// <inheritdoc />
    public string DisplayName => "ZEBRA - Yesterday's High/Low";

    /// <inheritdoc />
    public string Description =>
        "Marks the previous session's regular-hours high and low. A side stays armed only if " +
        "every 30-minute body stayed inside the zone overnight. Fires when price then reaches " +
        "a surviving level during the session and closes back inside - puts at the high, " +
        "calls at the low.";

    /// <inheritdoc />
    public IReadOnlyList<StrategyParameterDescriptor> Parameters { get; } =
    [
        new StrategyParameterDescriptor(
            ParamTimeframe,
            "Trigger timeframe (minutes)",
            "Bar size the tap is evaluated on during the session.",
            DefaultValue: 5, Minimum: 1, Maximum: 15, Kind: StrategyParameterKind.Integer),

        new StrategyParameterDescriptor(
            ParamZoneTimeframe,
            "Zone timeframe (minutes)",
            "Bar size the zone and the overnight body test are read on. The source material specifies 30, and a smaller size invalidates far more setups because smaller bodies reach further.",
            DefaultValue: 30, Minimum: 5, Maximum: 60, Kind: StrategyParameterKind.Integer),

        new StrategyParameterDescriptor(
            ParamRepeatTaps,
            "Allow repeated taps",
            "On: every rejection at a level fires another alert. Off: one alert per side per day, on the first test - which is the one the setup is about.",
            DefaultValue: 0, Minimum: 0, Maximum: 1, Kind: StrategyParameterKind.Toggle)
    ];

    /// <inheritdoc />
    public IReadOnlyList<int> AdditionalTimeframes => [(int)_config[ParamZoneTimeframe]];

    /// <inheritdoc />
    /// <remarks>
    /// The zone is a band, and the overnight body extremes are deliberately not drawn. They
    /// are diagnostic - how close the night came to killing this side - and putting them on
    /// the chart as lines would suggest they are levels price reacts to, which they are not.
    /// </remarks>
    public IReadOnlyList<Charting.LevelDisplay> LevelDisplays { get; } =
    [
        Charting.LevelDisplay.Band("YDAY", "zone_high", "zone_low")
    ];

    /// <inheritdoc />
    public void Configure(IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        _config.Clear();

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
    /// <remarks>
    /// Produces nothing on its own. The zone comes from a second bar series, and without it
    /// there is no level to test - so this forwards to the real overload with an empty set
    /// rather than falling back to something approximate.
    /// </remarks>
    public IReadOnlyList<StrategySignal> Evaluate(string ticker, IReadOnlyList<Bar> bars) =>
        Evaluate(ticker, bars, new Dictionary<int, IReadOnlyList<Bar>>());

    /// <inheritdoc />
    public IReadOnlyList<StrategySignal> Evaluate(
        string ticker,
        IReadOnlyList<Bar> bars,
        IReadOnlyDictionary<int, IReadOnlyList<Bar>> additionalBars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(additionalBars);

        if (bars.Count == 0)
        {
            return [];
        }

        var state = _sessions.GetOrAdd(ticker, _ => new SessionState());
        var signals = new List<StrategySignal>();

        lock (state.Gate)
        {
            foreach (var bar in bars)
            {
                if (state.LastProcessedBar is { } last && bar.Timestamp <= last)
                {
                    continue;
                }

                ProcessBar(bar, state, ticker, additionalBars, signals);
                state.LastProcessedBar = bar.Timestamp;
            }
        }

        return signals;
    }

    /// <summary>Advances the state machine by exactly one bar.</summary>
    private void ProcessBar(
        Bar bar, SessionState state, string ticker,
        IReadOnlyDictionary<int, IReadOnlyList<Bar>> additionalBars,
        List<StrategySignal> signals)
    {
        var sessionDate = MarketSession.SessionDate(bar.Timestamp);

        if (state.SessionDate != sessionDate)
        {
            state.ResetForNewSession(sessionDate);
        }

        // The trigger is a regular-session event. Overnight and premarket bars advance the
        // dedup marker and nothing else - the zone they would be tested against is built from
        // them, and a level cannot be tested by the same bars that defined it.
        if (!MarketSession.IsRegularSession(bar.Timestamp))
        {
            return;
        }

        if (!state.ZoneResolved)
        {
            state.ZoneResolved = true;
            ResolveZone(state, sessionDate, additionalBars);
        }

        if (state.ZoneHigh is not { } zoneHigh || state.ZoneLow is not { } zoneLow)
        {
            return;
        }

        var repeatTaps = _config[ParamRepeatTaps] >= 0.5;

        // The bar that opens the session, identified by its own timestamp rather than by a
        // "have I seen one yet" flag. The flag version is wrong whenever history begins
        // mid-session: an 11:00 bar would be treated as the open and judged for gapping.
        var isOpeningBar = MarketSession.TimeOfDay(bar.Timestamp) == MarketSession.RegularOpen;

        EvaluateHigh(ticker, bar, state, zoneHigh, zoneLow, repeatTaps, isOpeningBar, signals);
        EvaluateLow(ticker, bar, state, zoneHigh, zoneLow, repeatTaps, isOpeningBar, signals);
    }

    /// <summary>
    /// Builds the zone and decides which sides survived the night.
    /// </summary>
    /// <remarks>
    /// Done once per ticker per session, from the bar list rather than accumulated as bars
    /// stream past. The zone describes the <i>previous</i> session, so streaming would make it
    /// depend on the application having been running yesterday - and it would be absent
    /// exactly when the app was restarted in the morning, which is most mornings.
    /// </remarks>
    private void ResolveZone(
        SessionState state, DateOnly today,
        IReadOnlyDictionary<int, IReadOnlyList<Bar>> additionalBars)
    {
        var zoneTimeframe = (int)_config[ParamZoneTimeframe];

        // Absent, not empty - see IPriceStrategy.AdditionalTimeframes. No series means the
        // gateway gave us nothing, and a level guessed from the trigger timeframe would be a
        // different level wearing the same name.
        if (!additionalBars.TryGetValue(zoneTimeframe, out var zoneBars) || zoneBars.Count == 0)
        {
            return;
        }

        // "Yesterday" is the most recent session present in the data, not a calendar
        // subtraction. That handles weekends, holidays and half-days without a market
        // calendar, and is correct on a Tuesday after a holiday Monday where any fixed offset
        // is not.
        DateOnly? previous = null;
        foreach (var bar in zoneBars)
        {
            var date = MarketSession.SessionDate(bar.Timestamp);
            if (date < today && (previous is null || date > previous))
            {
                previous = date;
            }
        }

        if (previous is not { } previousDate)
        {
            return;
        }

        decimal? high = null;
        decimal? low = null;

        foreach (var bar in zoneBars)
        {
            if (MarketSession.SessionDate(bar.Timestamp) != previousDate
                || !MarketSession.IsRegularSession(bar.Timestamp))
            {
                continue;
            }

            high = high is { } h ? Math.Max(h, bar.High) : bar.High;
            low = low is { } l ? Math.Min(l, bar.Low) : bar.Low;
        }

        if (high is not { } zoneHigh || low is not { } zoneLow)
        {
            return;
        }

        state.ZoneHigh = zoneHigh;
        state.ZoneLow = zoneLow;
        state.PreviousSessionDate = previousDate;

        // ---- The overnight test ----
        // Window: the previous session's close through this session's open. Expressed as
        // date-plus-time-of-day comparisons rather than as constructed boundary instants,
        // which would mean choosing a UTC offset for each date and being an hour wrong twice
        // a year.
        foreach (var bar in zoneBars)
        {
            var date = MarketSession.SessionDate(bar.Timestamp);
            var time = MarketSession.TimeOfDay(bar.Timestamp);

            var isOvernight =
                (date == previousDate && time >= MarketSession.RegularClose) ||
                (date == today && time < MarketSession.RegularOpen);

            if (!isOvernight)
            {
                continue;
            }

            state.OvernightBodyHigh = state.OvernightBodyHigh is { } obh
                ? Math.Max(obh, bar.BodyHigh)
                : bar.BodyHigh;

            state.OvernightBodyLow = state.OvernightBodyLow is { } obl
                ? Math.Min(obl, bar.BodyLow)
                : bar.BodyLow;

            // Inclusive: the rule is "tapped into OR broke past", so touching counts.
            if (bar.BodyHigh >= zoneHigh)
            {
                state.HighSideAlive = false;
            }

            if (bar.BodyLow <= zoneLow)
            {
                state.LowSideAlive = false;
            }
        }
    }

    /// <summary>Upper level: a rejection here is a short, traded with puts.</summary>
    private void EvaluateHigh(
        string ticker, Bar bar, SessionState state,
        decimal zoneHigh, decimal zoneLow, bool repeatTaps, bool isOpeningBar,
        List<StrategySignal> signals)
    {
        if (!state.HighSideAlive)
        {
            return;
        }

        // ---- Invalidation, checked before the trigger ----
        // A close above the level means it broke rather than held. A later move back down
        // through it is a different setup - a breakdown after a breakout - and firing this
        // alert on it would describe the wrong trade.
        if (bar.Close > zoneHigh)
        {
            state.HighSideAlive = false;
            return;
        }

        // Price already through the level when the session opened. There is no tap to wait
        // for: the level was cleared while nobody was looking, and the zone is behind price
        // rather than in front of it.
        if (isOpeningBar && bar.Open > zoneHigh)
        {
            state.HighSideAlive = false;
            return;
        }

        if (!repeatTaps && state.HighTapFired)
        {
            return;
        }

        // The tap. A wick counts - "touches or goes beyond the line and rejects" - and the
        // close must come back inside the zone for the rejection to have happened at all.
        if (bar.High >= zoneHigh && bar.Close < zoneHigh)
        {
            state.HighTapFired = true;
            signals.Add(BuildSignal(ticker, bar, state, "TAP-HIGH", TradeDirection.Short, zoneHigh, zoneLow));
        }
    }

    /// <summary>Lower level: a rejection here is a long, traded with calls.</summary>
    private void EvaluateLow(
        string ticker, Bar bar, SessionState state,
        decimal zoneHigh, decimal zoneLow, bool repeatTaps, bool isOpeningBar,
        List<StrategySignal> signals)
    {
        if (!state.LowSideAlive)
        {
            return;
        }

        if (bar.Close < zoneLow)
        {
            state.LowSideAlive = false;
            return;
        }

        if (isOpeningBar && bar.Open < zoneLow)
        {
            state.LowSideAlive = false;
            return;
        }

        if (!repeatTaps && state.LowTapFired)
        {
            return;
        }

        if (bar.Low <= zoneLow && bar.Close > zoneLow)
        {
            state.LowTapFired = true;
            signals.Add(BuildSignal(ticker, bar, state, "TAP-LOW", TradeDirection.Long, zoneHigh, zoneLow));
        }
    }

    private StrategySignal BuildSignal(
        string ticker, Bar bar, SessionState state,
        string path, TradeDirection direction,
        decimal zoneHigh, decimal zoneLow) =>
        new StrategySignal(
            ticker,
            Id,
            path,
            direction,
            bar.Close,
            // Bar timestamps are OPEN times. Alerts carry the moment the signal became
            // knowable, which is the close - and the chart locates the flag by that.
            bar.Timestamp.AddMinutes(_config[ParamTimeframe]),
            new Dictionary<string, decimal?>
            {
                ["zone_high"] = zoneHigh,
                ["zone_low"] = zoneLow,
                // How close the night came to killing this side. A body that stopped a cent
                // short is worth seeing next to the alert.
                ["overnight_body_high"] = state.OvernightBodyHigh,
                ["overnight_body_low"] = state.OvernightBodyLow
            });

    /// <summary>Per-ticker, per-session mutable state.</summary>
    private sealed class SessionState
    {
        public Lock Gate { get; } = new();

        public DateOnly SessionDate;
        public DateTimeOffset? LastProcessedBar;

        public bool ZoneResolved;
        public decimal? ZoneHigh;
        public decimal? ZoneLow;
        public DateOnly? PreviousSessionDate;

        public decimal? OvernightBodyHigh;
        public decimal? OvernightBodyLow;

        // Both start alive. The overnight test can only take a side away, never grant one -
        // so a missing overnight window leaves both armed, which is the permissive failure.
        // Worth knowing: it is the direction that produces a trade rather than suppresses one.
        public bool HighSideAlive = true;
        public bool LowSideAlive = true;

        public bool HighTapFired;
        public bool LowTapFired;

        /// <summary>Clears everything except the bar-dedup marker, which is monotonic.</summary>
        public void ResetForNewSession(DateOnly date)
        {
            SessionDate = date;
            ZoneResolved = false;
            ZoneHigh = ZoneLow = null;
            PreviousSessionDate = null;
            OvernightBodyHigh = OvernightBodyLow = null;
            HighSideAlive = LowSideAlive = true;
            HighTapFired = LowTapFired = false;
        }
    }
}
