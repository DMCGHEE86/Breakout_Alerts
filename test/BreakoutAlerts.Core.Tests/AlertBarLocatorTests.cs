using BreakoutAlerts.Core.Charting;
using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Tests;

/// <summary>
/// Tests for resolving an alert back to the bar that fired it.
/// </summary>
/// <remarks>
/// This arithmetic has been wrong twice on screen, which is the reason it was pulled out of
/// the renderer and into Core. Both live failures are pinned below: a flag drawn one candle
/// to the right, and the last signal of a session vanishing entirely.
/// </remarks>
public sealed class AlertBarLocatorTests
{
    private const int Timeframe = 5;

    private static readonly DateTimeOffset Open = new(2026, 8, 4, 9, 30, 0, TimeSpan.FromHours(-4));

    /// <summary>Bars stamped with their OPEN times, five minutes apart.</summary>
    private static List<Bar> Bars(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Bar(Open.AddMinutes(i * Timeframe), 100m, 101m, 99m, 100m, 1_000))
            .ToList();

    /// <summary>The close time of bar <paramref name="index"/>, which is what an alert carries.</summary>
    private static DateTimeOffset CloseOf(int index) => Open.AddMinutes((index + 1) * Timeframe);

    [Fact]
    public void ResolvesToTheBarThatClosed_NotTheNextOne()
    {
        var bars = Bars(10);

        // The alert for bar 3 is stamped 09:50 - which is also bar 4's OPEN. Treating that
        // timestamp as a bar timestamp put every flag one candle to the right, onto a bar
        // that did nothing, and made the breakout distance read from the wrong candle.
        Assert.Equal(3, AlertBarLocator.FindTriggerBar(bars, CloseOf(3), Timeframe));
    }

    [Fact]
    public void FindsAnAlertOnTheFinalBar()
    {
        var bars = Bars(10);

        // The last bar's alert sits one bar-length past the last bar's open. Comparing the
        // raw timestamp against the plotted range discarded it, so the closing signal of
        // every session silently lost its flag.
        Assert.Equal(9, AlertBarLocator.FindTriggerBar(bars, CloseOf(9), Timeframe));
    }

    [Fact]
    public void FindsAnAlertOnTheFirstBar()
    {
        Assert.Equal(0, AlertBarLocator.FindTriggerBar(Bars(10), CloseOf(0), Timeframe));
    }

    [Fact]
    public void RejectsAnAlertFromBeforeThePlottedBars()
    {
        // The alert log persists across restarts, so older sessions routinely travel with the
        // list. Snapping these to the nearest bar would paint a flag onto an unrelated candle.
        Assert.Equal(-1, AlertBarLocator.FindTriggerBar(Bars(10), CloseOf(0).AddDays(-1), Timeframe));
    }

    [Fact]
    public void RejectsAnAlertFromAfterThePlottedBars()
    {
        Assert.Equal(-1, AlertBarLocator.FindTriggerBar(Bars(10), CloseOf(40), Timeframe));
    }

    [Fact]
    public void ReturnsNotFoundForAnEmptyChart()
    {
        Assert.Equal(-1, AlertBarLocator.FindTriggerBar([], CloseOf(3), Timeframe));
    }

    [Fact]
    public void SnapsToTheNearestBarWhenTheTimeframeDoesNotLineUp()
    {
        var bars = Bars(10);

        // A chart drawn at a different timeframe from the one that fired the alert must still
        // land on a real candle rather than in a gap between two.
        var offBy = CloseOf(3).AddMinutes(1);

        Assert.Equal(3, AlertBarLocator.FindTriggerBar(bars, offBy, Timeframe));
    }

    [Fact]
    public void SimultaneousAlertsResolveToTheSameBar()
    {
        var bars = Bars(10);

        // PATH-1 and PATH-3 fire together whenever one close clears both the opening range
        // and a premarket level. They must collapse onto a single flag, not two overprinted
        // ones, and that only works if they resolve identically.
        var at = CloseOf(6);

        Assert.Equal(
            AlertBarLocator.FindTriggerBar(bars, at, Timeframe),
            AlertBarLocator.FindTriggerBar(bars, at, Timeframe));
        Assert.Equal(6, AlertBarLocator.FindTriggerBar(bars, at, Timeframe));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(15)]
    public void HoldsForEverySupportedTimeframe(int minutes)
    {
        var bars = Enumerable.Range(0, 8)
            .Select(i => new Bar(Open.AddMinutes(i * minutes), 100m, 101m, 99m, 100m, 1_000))
            .ToList();

        var closeOfBar5 = Open.AddMinutes(6 * minutes);

        Assert.Equal(5, AlertBarLocator.FindTriggerBar(bars, closeOfBar5, minutes));
    }

    [Fact]
    public void TriggerBarOpenIsOneBarBeforeTheAlert()
    {
        var alertAt = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.FromHours(-4));

        Assert.Equal(
            new DateTimeOffset(2026, 8, 4, 9, 55, 0, TimeSpan.FromHours(-4)),
            AlertBarLocator.TriggerBarOpen(alertAt, Timeframe));
    }
}
