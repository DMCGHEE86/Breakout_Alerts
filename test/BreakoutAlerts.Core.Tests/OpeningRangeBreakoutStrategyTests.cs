using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;

namespace BreakoutAlerts.Core.Tests;

/// <summary>
/// Behavioural tests for the ORB strategy port.
/// </summary>
/// <remarks>
/// These assert the rules that were expensive to get right in the Pine original: that
/// wicks never trigger, that invalidation is checked before the trigger logic, that PATH-3
/// is suppressed when the premarket level sits inside the range, and that re-scanning the
/// same bars does not re-fire. Each of those is a bug that would look like working software
/// right up until it produced a wrong trade.
/// </remarks>
public sealed class OpeningRangeBreakoutStrategyTests
{
    private static readonly DateTime SessionDay = new(2026, 8, 3);

    /// <summary>Builds an Eastern-time instant on the test session date.</summary>
    private static DateTimeOffset At(int hour, int minute)
    {
        var local = SessionDay.Add(new TimeSpan(hour, minute, 0));
        return new DateTimeOffset(local, MarketSession.ExchangeTimeZone.GetUtcOffset(local));
    }

    private static Bar Bar(int hour, int minute, decimal open, decimal high, decimal low, decimal close) =>
        new(At(hour, minute), open, high, low, close, 100_000);

    /// <summary>
    /// A minimal session: premarket, a 09:30-09:45 range of 99.00-101.00, nothing after.
    /// </summary>
    /// <remarks>
    /// Premarket defaults to sitting INSIDE the opening range, so PATH-3 stays suppressed
    /// unless a test deliberately widens it. That makes the suppression the default and
    /// forces tests that want PATH-3 to say so explicitly.
    /// </remarks>
    private static List<Bar> BaseSession(decimal premarketHigh = 100.50m, decimal premarketLow = 99.50m)
    {
        return
        [
            Bar(8, 0, 100m, premarketHigh, premarketLow, 100m),
            Bar(9, 30, 100m, 101.00m, 99.00m, 100m),
            Bar(9, 35, 100m, 100.80m, 99.20m, 100m),
            Bar(9, 40, 100m, 100.90m, 99.10m, 100m)
        ];
    }

    [Fact]
    public void LocksRangeFromOpeningWindowOnly()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();

        // A 09:50 bar far outside the range must not widen it - only 09:30-09:45 counts.
        bars.Add(Bar(9, 50, 100m, 105m, 95m, 101.50m));

        var signals = strategy.Evaluate("TEST", bars);

        var path1 = Assert.Single(signals, s => s.Path == "PATH-1");
        Assert.Equal(101.00m, path1.Context["orb_high"]);
        Assert.Equal(99.00m, path1.Context["orb_low"]);
    }

    [Fact]
    public void Path1FiresOnFirstBodyCloseAboveRange()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();
        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));

        var signals = strategy.Evaluate("TEST", bars);

        var signal = Assert.Single(signals, s => s.Path == "PATH-1");
        Assert.Equal(TradeDirection.Long, signal.Direction);
        Assert.Equal(101.50m, signal.TriggerPrice);
    }

    [Fact]
    public void Path1IgnoresWickPiercingTheRange()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();

        // High pierces 101.00 but the body closes back inside. Wicks must never trigger.
        bars.Add(Bar(9, 50, 100.50m, 101.80m, 100.40m, 100.90m));

        var signals = strategy.Evaluate("TEST", bars);

        Assert.DoesNotContain(signals, s => s.Path == "PATH-1");
    }

    [Fact]
    public void Path1FiresOnlyOncePerDirection()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();
        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));
        bars.Add(Bar(9, 55, 101.50m, 102.60m, 101.40m, 102.50m));
        bars.Add(Bar(10, 0, 102.50m, 103.60m, 102.40m, 103.50m));

        var signals = strategy.Evaluate("TEST", bars);

        Assert.Single(signals, s => s.Path == "PATH-1" && s.Direction == TradeDirection.Long);
    }

    [Fact]
    public void Path1FiresIndependentlyForShortSide()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();
        bars.Add(Bar(9, 50, 99.50m, 99.60m, 98.40m, 98.50m));

        var signals = strategy.Evaluate("TEST", bars);

        var signal = Assert.Single(signals, s => s.Path == "PATH-1");
        Assert.Equal(TradeDirection.Short, signal.Direction);
    }

    [Fact]
    public void Path2FiresOnConfirmationAfterACounterDirectionRetest()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();

        // Break out above the range.
        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));
        // Retest: a RED candle pulling back, wick touching the level, body still above.
        bars.Add(Bar(9, 55, 101.50m, 101.55m, 100.90m, 101.10m));
        // Confirmation: a GREEN candle closing back beyond the level.
        bars.Add(Bar(10, 0, 101.10m, 101.90m, 101.05m, 101.80m));

        var signals = strategy.Evaluate("TEST", bars);

        Assert.Single(signals, s => s.Path == "PATH-2" && s.Direction == TradeDirection.Long);
    }

    [Fact]
    public void Path2DoesNotFireOnAWickTouchWithoutAPullbackCandle()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();

        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));

        // Green candles that graze the level on the way up. Under the old rule a bare wick
        // touch armed the setup and the very next green bar fired PATH-2 - during ordinary
        // chop, before any pullback had happened. A retest now requires a counter-direction
        // candle, so none of these should trigger.
        bars.Add(Bar(9, 55, 101.10m, 101.60m, 100.95m, 101.55m));
        bars.Add(Bar(10, 0, 101.55m, 101.95m, 100.98m, 101.90m));

        Assert.DoesNotContain(strategy.Evaluate("TEST", bars), s => s.Path == "PATH-2");
    }

    [Fact]
    public void Path2RequiresTheConfirmingCandleToCloseOutsideTheRange()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();

        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));
        // Red pullback candle - a valid retest.
        bars.Add(Bar(9, 55, 101.50m, 101.55m, 100.90m, 101.10m));
        // Green, but closes back INSIDE the range. Confirms nothing, and in fact re-enters,
        // so it invalidates rather than triggers.
        bars.Add(Bar(10, 0, 100.95m, 101.00m, 100.60m, 100.98m));

        Assert.DoesNotContain(strategy.Evaluate("TEST", bars), s => s.Path == "PATH-2");
    }

    [Fact]
    public void Path2FiresOnShortSideAfterAGreenRetest()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();

        // Break below the range.
        bars.Add(Bar(9, 50, 99.50m, 99.60m, 98.40m, 98.50m));
        // Retest: a GREEN candle pulling back up, wick touching the low, body still below.
        bars.Add(Bar(9, 55, 98.50m, 99.05m, 98.45m, 98.90m));
        // Confirmation: a RED candle closing back below the low.
        bars.Add(Bar(10, 0, 98.90m, 98.95m, 98.10m, 98.20m));

        var signals = strategy.Evaluate("TEST", bars);

        Assert.Single(signals, s => s.Path == "PATH-2" && s.Direction == TradeDirection.Short);
    }

    [Fact]
    public void Path2InvalidatedWhenBodyReEntersRange()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();

        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));
        // Body closes back inside the range - the setup is dead for the session.
        bars.Add(Bar(9, 55, 101.40m, 101.50m, 100.30m, 100.50m));
        // A later textbook retest-reversal must NOT fire, because PATH-1 cannot re-arm.
        bars.Add(Bar(10, 0, 101.20m, 101.90m, 100.95m, 101.80m));

        var signals = strategy.Evaluate("TEST", bars);

        Assert.DoesNotContain(signals, s => s.Path == "PATH-2");
    }

    [Fact]
    public void Path2InvalidationTreatsTouchAsReEntry()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();

        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));
        // Close lands exactly ON the range high. The rule is "touches or crosses", so this
        // invalidates - an exclusive comparison here would be a real behavioural difference.
        bars.Add(Bar(9, 55, 101.40m, 101.60m, 100.90m, 101.00m));
        bars.Add(Bar(10, 0, 101.20m, 101.90m, 100.95m, 101.80m));

        var signals = strategy.Evaluate("TEST", bars);

        Assert.DoesNotContain(signals, s => s.Path == "PATH-2");
    }

    [Fact]
    public void Path3FiresWhenPremarketHighSitsAboveRange()
    {
        var strategy = new OpeningRangeBreakoutStrategy();

        // Premarket high at 102.00, above the 101.00 range high - a genuine second barrier.
        var bars = BaseSession(premarketHigh: 102.00m, premarketLow: 99.50m);
        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));  // clears ORB only
        bars.Add(Bar(9, 55, 101.50m, 102.40m, 101.40m, 102.30m));  // clears premarket high

        var signals = strategy.Evaluate("TEST", bars);

        var path3 = Assert.Single(signals, s => s.Path == "PATH-3");
        Assert.Equal(TradeDirection.Long, path3.Direction);
        Assert.Equal(102.00m, path3.Context["premarket_high"]);
    }

    [Fact]
    public void Path3SuppressedWhenPremarketHighSitsInsideRange()
    {
        var strategy = new OpeningRangeBreakoutStrategy();

        // Premarket high at 100.50, inside the 99.00-101.00 range. Clearing the ORB high
        // already cleared it, so a PATH-3 here would duplicate PATH-1 rather than confirm it.
        var bars = BaseSession(premarketHigh: 100.50m, premarketLow: 99.50m);
        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));

        var signals = strategy.Evaluate("TEST", bars);

        Assert.Contains(signals, s => s.Path == "PATH-1");
        Assert.DoesNotContain(signals, s => s.Path == "PATH-3");
    }

    [Fact]
    public void Path3FiresOnShortSideBelowPremarketLow()
    {
        var strategy = new OpeningRangeBreakoutStrategy();

        var bars = BaseSession(premarketHigh: 100.50m, premarketLow: 98.00m);
        bars.Add(Bar(9, 50, 99.50m, 99.60m, 98.40m, 98.50m));   // clears ORB low only
        bars.Add(Bar(9, 55, 98.40m, 98.50m, 97.60m, 97.70m));   // clears premarket low

        var signals = strategy.Evaluate("TEST", bars);

        var path3 = Assert.Single(signals, s => s.Path == "PATH-3");
        Assert.Equal(TradeDirection.Short, path3.Direction);
    }

    [Fact]
    public void ReEvaluatingSameBarsProducesNoDuplicateSignals()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();
        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));

        var first = strategy.Evaluate("TEST", bars);
        var second = strategy.Evaluate("TEST", bars);
        var third = strategy.Evaluate("TEST", bars);

        // This is what stops one breakout alerting on every scan cycle for the rest of
        // the day - the single most likely way a working scanner becomes unusable.
        Assert.NotEmpty(first);
        Assert.Empty(second);
        Assert.Empty(third);
    }

    [Fact]
    public void IncrementalBarsAreProcessedOnlyOnce()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();

        strategy.Evaluate("TEST", bars);

        // Scanner passes an overlapping window on the next cycle, with one new bar.
        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));
        var signals = strategy.Evaluate("TEST", bars);

        Assert.Single(signals, s => s.Path == "PATH-1");
    }

    [Fact]
    public void StateIsIsolatedPerTicker()
    {
        var strategy = new OpeningRangeBreakoutStrategy();

        var breakoutBars = BaseSession();
        breakoutBars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));

        var quietBars = BaseSession();
        quietBars.Add(Bar(9, 50, 100.20m, 100.60m, 99.80m, 100.30m));

        var alpha = strategy.Evaluate("AAA", breakoutBars);
        var beta = strategy.Evaluate("BBB", quietBars);

        // One instance serves every symbol. Shared state here would leak AAA's breakout
        // into BBB - a bug that passes every single-symbol test.
        Assert.NotEmpty(alpha);
        Assert.Empty(beta);
    }

    [Fact]
    public void NewSessionResetsRangeAndFiredFlags()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();
        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));

        strategy.Evaluate("TEST", bars);

        // Next trading day, same shape. PATH-1 must be able to fire again.
        var nextDay = new List<Bar>();
        foreach (var bar in bars)
        {
            nextDay.Add(bar with { Timestamp = bar.Timestamp.AddDays(1) });
        }

        var signals = strategy.Evaluate("TEST", nextDay);

        Assert.Single(signals, s => s.Path == "PATH-1");
    }

    [Fact]
    public void NoSignalsWithoutAnOpeningRange()
    {
        var strategy = new OpeningRangeBreakoutStrategy();

        // History begins mid-session, so no 09:30-09:45 bars exist. Evaluating against a
        // range that was never measured would invent signals from nothing.
        List<Bar> bars =
        [
            Bar(11, 0, 100m, 101m, 99m, 100.50m),
            Bar(11, 5, 100.50m, 105m, 100m, 104m)
        ];

        Assert.Empty(strategy.Evaluate("TEST", bars));
    }

    [Fact]
    public void NarrowRangeIsFilteredOut()
    {
        var strategy = new OpeningRangeBreakoutStrategy();

        // Range of 0.02 on a 100 stock is 0.02% - well under the 0.2% default floor.
        List<Bar> bars =
        [
            Bar(9, 30, 100m, 100.01m, 99.99m, 100m),
            Bar(9, 50, 100m, 100.20m, 99.98m, 100.10m)
        ];

        Assert.Empty(strategy.Evaluate("TEST", bars));
    }

    [Fact]
    public void ResetClearsStateForASingleTicker()
    {
        var strategy = new OpeningRangeBreakoutStrategy();
        var bars = BaseSession();
        bars.Add(Bar(9, 50, 100.50m, 101.60m, 100.40m, 101.50m));

        Assert.NotEmpty(strategy.Evaluate("TEST", bars));
        Assert.Empty(strategy.Evaluate("TEST", bars));

        strategy.Reset("TEST");

        Assert.NotEmpty(strategy.Evaluate("TEST", bars));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    [InlineData(15, true)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(10, false)]
    public void OnlyTimeframesDividingFifteenAreSupported(int minutes, bool expected)
    {
        // Mirrors the Pine indicator's guard. A 2-minute bar straddles the 09:45 lock and
        // would silently widen the range.
        Assert.Equal(expected, MarketSession.IsSupportedTimeframe(minutes));
    }

    [Fact]
    public void SessionWindowsUseHalfOpenBoundaries()
    {
        // 09:30 is the first regular-session bar, NOT the last premarket one. Getting this
        // boundary wrong shifts the entire opening range by a bar.
        Assert.False(MarketSession.IsPremarket(At(9, 30)));
        Assert.True(MarketSession.IsPremarket(At(9, 29)));
        Assert.True(MarketSession.IsOpeningRange(At(9, 30)));
        Assert.False(MarketSession.IsOpeningRange(At(9, 45)));
        Assert.True(MarketSession.IsRegularSession(At(9, 45)));
        Assert.False(MarketSession.IsRegularSession(At(16, 0)));
    }
}
