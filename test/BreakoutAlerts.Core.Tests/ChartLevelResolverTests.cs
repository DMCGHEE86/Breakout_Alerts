using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Charting;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;

namespace BreakoutAlerts.Core.Tests;

/// <summary>
/// Tests for the chart's level resolution.
/// </summary>
/// <remarks>
/// This is the logic behind "the chart drew no levels", which has happened once already and
/// is invisible in a rendered PNG - a chart missing its band looks like a chart of a quiet
/// session. Every case here is a way that can happen without anything throwing.
/// </remarks>
public sealed class ChartLevelResolverTests
{
    private static AlertRecord Alert(string strategy, Dictionary<string, decimal?> levels) =>
        new()
        {
            Ticker = "TEST",
            Strategy = strategy,
            AlertPath = "PATH-1",
            Direction = "LONG",
            Timestamp = DateTimeOffset.Now,
            Levels = levels
        };

    [Fact]
    public void OrbLevelsResolveToABandAndTwoLines()
    {
        var alert = Alert("ORB_Breakout", new Dictionary<string, decimal?>
        {
            ["orb_high"] = 101m,
            ["orb_low"] = 99m,
            ["premarket_high"] = 102m,
            ["premarket_low"] = 98m
        });

        var (bands, lines) = ChartLevelResolver.Resolve(alert, [new OpeningRangeBreakoutStrategy()]);

        var band = Assert.Single(bands);
        Assert.Equal("ORB", band.Label);
        Assert.Equal(101m, band.Upper);
        Assert.Equal(99m, band.Lower);

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Value == 102m && l.Tone == LevelTone.Positive);
        Assert.Contains(lines, l => l.Value == 98m && l.Tone == LevelTone.Negative);
    }

    [Fact]
    public void ZebraLevelsResolveToItsZoneBand()
    {
        var alert = Alert("ZEBRA", new Dictionary<string, decimal?>
        {
            ["zone_high"] = 105m,
            ["zone_low"] = 95m
        });

        var (bands, lines) = ChartLevelResolver.Resolve(alert, [new ZebraStrategy()]);

        var band = Assert.Single(bands);
        Assert.Equal("YDAY", band.Label);
        Assert.Equal(105m, band.Upper);
        Assert.Equal(95m, band.Lower);

        // The overnight body extremes are carried on the alert but deliberately not drawn -
        // they are diagnostics, not levels price reacts to.
        Assert.Empty(lines);
    }

    [Fact]
    public void EachStrategyGetsOnlyItsOwnLevels()
    {
        // Both strategies registered, as they are at runtime. Resolving by the alert's
        // strategy id is what stops a ZEBRA alert being drawn with ORB's band.
        var alert = Alert("ZEBRA", new Dictionary<string, decimal?>
        {
            ["zone_high"] = 105m,
            ["zone_low"] = 95m,
            ["orb_high"] = 101m,
            ["orb_low"] = 99m
        });

        var (bands, _) = ChartLevelResolver.Resolve(
            alert, [new OpeningRangeBreakoutStrategy(), new ZebraStrategy()]);

        var band = Assert.Single(bands);
        Assert.Equal("YDAY", band.Label);
    }

    [Fact]
    public void AnUnknownStrategyDrawsNothingRatherThanGuessing()
    {
        var alert = Alert("SOME_OLD_STRATEGY", new Dictionary<string, decimal?>
        {
            ["orb_high"] = 101m,
            ["orb_low"] = 99m
        });

        var (bands, lines) = ChartLevelResolver.Resolve(alert, [new ZebraStrategy()]);

        Assert.Empty(bands);
        Assert.Empty(lines);
    }

    [Fact]
    public void AMissingBandEdgeSkipsTheBandRatherThanDrawingItAtZero()
    {
        // An alert written before the second level existed. Drawing the band with a zero edge
        // would put a rectangle across the whole chart and it would look like data.
        var alert = Alert("ORB_Breakout", new Dictionary<string, decimal?> { ["orb_high"] = 101m });

        var (bands, _) = ChartLevelResolver.Resolve(alert, [new OpeningRangeBreakoutStrategy()]);

        Assert.Empty(bands);
    }

    [Fact]
    public void ANullLevelIsTreatedAsAbsent()
    {
        // The map holds decimal?, and null means "this strategy had no premarket high today"
        // - which must not become a line at 0.00.
        var alert = Alert("ORB_Breakout", new Dictionary<string, decimal?>
        {
            ["orb_high"] = 101m,
            ["orb_low"] = 99m,
            ["premarket_high"] = null,
            ["premarket_low"] = null
        });

        var (bands, lines) = ChartLevelResolver.Resolve(alert, [new OpeningRangeBreakoutStrategy()]);

        Assert.Single(bands);
        Assert.Empty(lines);
    }

    [Fact]
    public void BandEdgesAreOrderedEvenIfTheRecordIsInverted()
    {
        var alert = Alert("ZEBRA", new Dictionary<string, decimal?>
        {
            ["zone_high"] = 95m,
            ["zone_low"] = 105m
        });

        var band = Assert.Single(ChartLevelResolver.Resolve(alert, [new ZebraStrategy()]).Bands);

        Assert.Equal(105m, band.Upper);
        Assert.Equal(95m, band.Lower);
    }

    [Fact]
    public void NoFocusAlertDrawsNothing()
    {
        var (bands, lines) = ChartLevelResolver.Resolve(null, [new ZebraStrategy()]);

        Assert.Empty(bands);
        Assert.Empty(lines);
    }

    [Fact]
    public void AnEmptyRegistryDrawsNothingWithoutThrowing()
    {
        // The case that produced a levelless chart before: the render path ran with no
        // strategies available to consult.
        var alert = Alert("ZEBRA", new Dictionary<string, decimal?> { ["zone_high"] = 105m, ["zone_low"] = 95m });

        var (bands, _) = ChartLevelResolver.Resolve(alert, []);

        Assert.Empty(bands);
    }

    [Fact]
    public void StrategyIdMatchingIsCaseInsensitive()
    {
        var alert = Alert("zebra", new Dictionary<string, decimal?> { ["zone_high"] = 105m, ["zone_low"] = 95m });

        Assert.Single(ChartLevelResolver.Resolve(alert, [new ZebraStrategy()]).Bands);
    }
}
