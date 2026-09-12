using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;

namespace BreakoutAlerts.Core.Tests;

/// <summary>
/// Behavioural tests for ZEBRA.
/// </summary>
/// <remarks>
/// The rules that are easy to implement backwards are the ones pinned hardest here: that the
/// zone comes from the previous session's regular hours and nothing else, that the overnight
/// test reads BODIES while the trigger reads WICKS, and that a touch of a level means
/// "spent" before the open and "traded" after it. Every one of those would produce a
/// plausible-looking alert at the wrong price rather than an error.
/// </remarks>
public sealed class ZebraStrategyTests
{
    private const int ZoneTimeframe = 30;

    private static readonly DateTime Yesterday = new(2026, 8, 3);
    private static readonly DateTime Today = new(2026, 8, 4);

    /// <summary>Builds an Eastern-time instant on a given day.</summary>
    private static DateTimeOffset At(DateTime day, int hour, int minute)
    {
        var local = day.Add(new TimeSpan(hour, minute, 0));
        return new DateTimeOffset(local, MarketSession.ExchangeTimeZone.GetUtcOffset(local));
    }

    private static Bar Bar(DateTime day, int hour, int minute,
        decimal open, decimal high, decimal low, decimal close) =>
        new(At(day, hour, minute), open, high, low, close, 100_000);

    /// <summary>
    /// A 30-minute series giving a clean 99.00-101.00 zone with an untouched night.
    /// </summary>
    /// <remarks>
    /// The overnight bars sit well inside the zone by default, so both sides start armed and
    /// a test that wants a side killed has to say so. Defaults that arm everything make the
    /// invalidation tests explicit rather than accidental.
    /// </remarks>
    private static List<Bar> ZoneSeries()
    {
        return
        [
            // Previous session, regular hours. High 101.00, low 99.00.
            Bar(Yesterday, 9, 30, 100m, 101.00m, 99.00m, 100m),
            Bar(Yesterday, 12, 0, 100m, 100.50m, 99.50m, 100m),
            Bar(Yesterday, 15, 30, 100m, 100.80m, 99.20m, 100m),

            // After hours, then the true overnight block, then premarket. All inside.
            Bar(Yesterday, 16, 30, 100m, 100.40m, 99.60m, 100m),
            Bar(Yesterday, 20, 0, 100m, 100.30m, 99.70m, 100m),
            Bar(Today, 2, 0, 100m, 100.30m, 99.70m, 100m),
            Bar(Today, 8, 0, 100m, 100.40m, 99.60m, 100m)
        ];
    }

    private static Dictionary<int, IReadOnlyList<Bar>> Zone(List<Bar>? series = null) =>
        new() { [ZoneTimeframe] = series ?? ZoneSeries() };

    private static ZebraStrategy Strategy(bool repeatTaps = false)
    {
        var strategy = new ZebraStrategy();
        strategy.Configure(new Dictionary<string, double>
        {
            [ZebraStrategy.ParamTimeframe] = 5,
            [ZebraStrategy.ParamZoneTimeframe] = ZoneTimeframe,
            [ZebraStrategy.ParamRepeatTaps] = repeatTaps ? 1 : 0
        });
        return strategy;
    }

    // ---- The zone -----------------------------------------------------------

    [Fact]
    public void ZoneComesFromThePreviousRegularSessionOnly()
    {
        // The after-hours bar reaches 102.00, well above the regular-session high of 101.00.
        // If extended hours leaked into the zone, the tap below would not fire.
        var series = ZoneSeries();
        series.Add(Bar(Yesterday, 17, 0, 100m, 102.00m, 98.00m, 100m));

        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100m, 101.05m, 99.90m, 100.50m)], Zone(series));

        var signal = Assert.Single(signals);
        Assert.Equal(101.00m, signal.Context["zone_high"]);
        Assert.Equal(99.00m, signal.Context["zone_low"]);
    }

    [Fact]
    public void PreviousSessionIsFoundAcrossAWeekendGap()
    {
        // Friday's session, then Monday. A calendar subtraction would land on Sunday and find
        // no bars; taking the latest session present in the data lands on Friday.
        var friday = new DateTime(2026, 7, 31);
        var monday = new DateTime(2026, 8, 3);

        var series = new List<Bar>
        {
            Bar(friday, 9, 30, 100m, 101.00m, 99.00m, 100m),
            Bar(friday, 15, 30, 100m, 100.50m, 99.50m, 100m)
        };

        var signals = Strategy().Evaluate("TEST",
            [Bar(monday, 10, 0, 100m, 101.05m, 99.90m, 100.50m)], Zone(series));

        Assert.Equal(101.00m, Assert.Single(signals).Context["zone_high"]);
    }

    [Fact]
    public void NoPreviousSessionProducesNoSignal()
    {
        // Only today in the zone series. There is no previous session, so there is no level -
        // and inventing one from today's own bars would fire an alert about nothing.
        var series = new List<Bar> { Bar(Today, 9, 30, 100m, 101.00m, 99.00m, 100m) };

        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100m, 105m, 95m, 100.50m)], Zone(series));

        Assert.Empty(signals);
    }

    [Fact]
    public void AMissingZoneSeriesProducesNoSignal()
    {
        // The gateway gave us no 30-minute bars. Falling back to the trigger timeframe would
        // silently measure a different level and call it the same one.
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100m, 101.05m, 99.90m, 100.50m)],
            new Dictionary<int, IReadOnlyList<Bar>>());

        Assert.Empty(signals);
    }

    // ---- The overnight test -------------------------------------------------

    [Fact]
    public void AnOvernightBodyThroughTheHighKillsTheHighSide()
    {
        var series = ZoneSeries();
        series.Add(Bar(Today, 3, 0, 101.20m, 101.50m, 101.10m, 101.40m));

        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100m, 101.05m, 99.90m, 100.50m)], Zone(series));

        Assert.Empty(signals);
    }

    [Fact]
    public void AnOvernightWickThroughTheHighLeavesTheSideArmed()
    {
        // The distinction the whole filter rests on. A wick through the level on thin
        // overnight volume is noise; only a body counts as the level having been tested.
        var series = ZoneSeries();
        series.Add(Bar(Today, 3, 0, 100.50m, 101.80m, 100.40m, 100.60m));

        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100m, 101.05m, 99.90m, 100.50m)], Zone(series));

        Assert.Single(signals);
    }

    [Fact]
    public void AnOvernightBodyTouchingTheLevelExactlyKillsTheSide()
    {
        // "Tapped into OR broke past" - touching counts, so the comparison is inclusive.
        var series = ZoneSeries();
        series.Add(Bar(Today, 3, 0, 100.50m, 101.00m, 100.40m, 101.00m));

        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100m, 101.05m, 99.90m, 100.50m)], Zone(series));

        Assert.Empty(signals);
    }

    [Fact]
    public void TheOvernightWindowCoversTheTrueOvernightBlock()
    {
        // 02:00 is inside 16:00 -> 09:30 and must be tested. This is the block the gateway
        // only returns when the request asks for Session_ALL, and treating it as out of
        // window would arm a side the rules kill.
        var series = ZoneSeries();
        series.Add(Bar(Today, 2, 30, 98.50m, 98.90m, 98.40m, 98.60m));

        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100m, 100.10m, 98.95m, 99.50m)], Zone(series));

        Assert.Empty(signals);
    }

    [Fact]
    public void PreviousSessionRegularBarsDoNotCountAsOvernight()
    {
        // Yesterday's own regular-hours bars TOUCH the zone by definition - they are what
        // defined it. Counting them as overnight would kill both sides every single day.
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100m, 101.05m, 99.90m, 100.50m)], Zone());

        Assert.Single(signals);
    }

    // ---- The trigger --------------------------------------------------------

    [Fact]
    public void AWickTapOfTheHighClosingInsideFiresAShort()
    {
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m)], Zone());

        var signal = Assert.Single(signals);
        Assert.Equal("TAP-HIGH", signal.Path);
        Assert.Equal(TradeDirection.Short, signal.Direction);
    }

    [Fact]
    public void AWickTapOfTheLowClosingInsideFiresALong()
    {
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 99.80m, 99.90m, 98.95m, 99.50m)], Zone());

        var signal = Assert.Single(signals);
        Assert.Equal("TAP-LOW", signal.Path);
        Assert.Equal(TradeDirection.Long, signal.Direction);
    }

    [Fact]
    public void ACloseBeyondTheHighFiresNothingAndKillsTheSide()
    {
        // The level broke rather than held. A later move back down through it is a
        // breakdown-after-breakout, a different trade, and must not wear this alert's name.
        var signals = Strategy().Evaluate("TEST",
        [
            Bar(Today, 10, 0, 100.50m, 101.50m, 100.40m, 101.30m),
            Bar(Today, 10, 5, 101.30m, 101.40m, 100.20m, 100.50m)
        ], Zone());

        Assert.Empty(signals);
    }

    [Fact]
    public void ATouchThatDoesNotCloseBackInsideFiresNothing()
    {
        // Closing exactly ON the level is not closing back inside it.
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100.50m, 101.20m, 100.40m, 101.00m)], Zone());

        Assert.Empty(signals);
    }

    [Fact]
    public void ASecondTapIsSuppressedByDefault()
    {
        var signals = Strategy().Evaluate("TEST",
        [
            Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m),
            Bar(Today, 10, 5, 100.50m, 101.10m, 100.40m, 100.60m)
        ], Zone());

        Assert.Single(signals);
    }

    [Fact]
    public void RepeatedTapsFireWhenEnabled()
    {
        var signals = Strategy(repeatTaps: true).Evaluate("TEST",
        [
            Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m),
            Bar(Today, 10, 5, 100.50m, 101.10m, 100.40m, 100.60m)
        ], Zone());

        Assert.Equal(2, signals.Count);
    }

    [Fact]
    public void BothSidesCanFireOnTheSameDay()
    {
        var signals = Strategy().Evaluate("TEST",
        [
            Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m),
            Bar(Today, 11, 0, 99.80m, 99.90m, 98.95m, 99.50m)
        ], Zone());

        Assert.Equal(2, signals.Count);
        Assert.Contains(signals, s => s.Path == "TAP-HIGH");
        Assert.Contains(signals, s => s.Path == "TAP-LOW");
    }

    [Fact]
    public void AGapOpenAboveTheZoneKillsTheHighSide()
    {
        // Price opened through the level. There is no tap to wait for - the move happened
        // while the market was closed, and this bar falling back into the zone is entry, not
        // rejection.
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 9, 30, 101.50m, 101.60m, 100.20m, 100.80m)], Zone());

        Assert.Empty(signals);
    }

    [Fact]
    public void TheGapTestAppliesOnlyToTheOpeningBar()
    {
        // History beginning mid-session must not have its first bar judged as the open. An
        // 11:00 bar opening above the level would otherwise kill the side for the whole day.
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 11, 0, 100.20m, 101.05m, 100.10m, 100.50m)], Zone());

        Assert.Single(signals);
    }

    [Fact]
    public void PremarketTapsDoNotFire()
    {
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 9, 20, 100.20m, 101.05m, 100.10m, 100.50m)], Zone());

        Assert.Empty(signals);
    }

    [Fact]
    public void RescanningTheSameBarsDoesNotRefire()
    {
        var strategy = Strategy();
        var bars = new List<Bar> { Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m) };

        Assert.Single(strategy.Evaluate("TEST", bars, Zone()));
        Assert.Empty(strategy.Evaluate("TEST", bars, Zone()));
    }

    [Fact]
    public void StateIsKeptPerTicker()
    {
        var strategy = Strategy();
        var bars = new List<Bar> { Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m) };

        Assert.Single(strategy.Evaluate("AAA", bars, Zone()));
        Assert.Single(strategy.Evaluate("BBB", bars, Zone()));
    }

    // ---- The record ---------------------------------------------------------

    [Fact]
    public void SignalIsStampedWithTheBarCloseNotTheOpen()
    {
        // The regression that put every chart flag one candle to the right. Bars carry OPEN
        // times; an alert describes the moment the signal became knowable.
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m)], Zone());

        Assert.Equal(At(Today, 10, 5), Assert.Single(signals).TriggeredAt);
    }

    [Fact]
    public void ContextCarriesTheZoneAndTheOvernightExtremes()
    {
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m)], Zone());

        var context = Assert.Single(signals).Context;

        Assert.Equal(101.00m, context["zone_high"]);
        Assert.Equal(99.00m, context["zone_low"]);

        // Bodies, not wicks - the overnight bars all open and close at 100.00.
        Assert.Equal(100m, context["overnight_body_high"]);
        Assert.Equal(100m, context["overnight_body_low"]);
    }

    [Fact]
    public void TriggerPriceIsTheBarClose()
    {
        var signals = Strategy().Evaluate("TEST",
            [Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m)], Zone());

        Assert.Equal(100.50m, Assert.Single(signals).TriggerPrice);
    }

    // ---- Timezone -----------------------------------------------------------

    [Theory]
    [InlineData("Pacific Standard Time")]
    [InlineData("Tokyo Standard Time")]
    [InlineData("UTC")]
    public void ZoneIsIndependentOfTheOffsetTheBarsCarry(string timeZoneId)
    {
        // Same instants, rendered in another zone's offset - which is what a provider in a
        // different locale, or a cache file round-tripped through one, would hand over.
        // Tokyo is the case that breaks naive handling: 15:00 in New York is already the next
        // calendar day there, so anything reading the bar's own local date splits one session
        // across two and finds the wrong "previous" one.
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        static Bar Shift(Bar bar, TimeZoneInfo tz) =>
            new(TimeZoneInfo.ConvertTime(bar.Timestamp, tz),
                bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

        var series = ZoneSeries().Select(b => Shift(b, zone)).ToList();
        var trigger = Shift(Bar(Today, 10, 0, 100.20m, 101.05m, 100.10m, 100.50m), zone);

        var signals = Strategy().Evaluate("TEST", [trigger], Zone(series));

        var signal = Assert.Single(signals);
        Assert.Equal("TAP-HIGH", signal.Path);
        Assert.Equal(101.00m, signal.Context["zone_high"]);
        Assert.Equal(99.00m, signal.Context["zone_low"]);
    }

    // ---- The declared timeframe --------------------------------------------

    [Fact]
    public void TheZoneTimeframeIsDeclaredSoTheScannerFetchesIt()
    {
        Assert.Equal([ZoneTimeframe], Strategy().AdditionalTimeframes);
    }
}
