using BreakoutAlerts.Core.Configuration;

namespace BreakoutAlerts.Core.Tests;

/// <summary>
/// Guards the safety properties of market data configuration.
/// </summary>
/// <remarks>
/// These are not property round-trip tests. Each one pins a decision that, if it silently
/// reversed, would let the application serve generated prices while looking like it was
/// serving real ones.
/// </remarks>
public sealed class MarketDataOptionsTests
{
    [Fact]
    public void DefaultsToLiveDataNotSynthetic()
    {
        var options = new MarketDataOptions();

        // The safe state must be what you get by doing nothing. A missing, malformed or
        // deleted configuration file lands here.
        Assert.Equal(MarketDataProviderKind.Moomoo, options.Provider);
        Assert.False(options.IsSynthetic);
    }

    [Fact]
    public void SyntheticIsOnlyReachableByNamingItExplicitly()
    {
        var options = new MarketDataOptions { Provider = MarketDataProviderKind.SyntheticForTestingOnly };

        Assert.True(options.IsSynthetic);

        // The enum member is named for what it is so it cannot be set casually or mistaken
        // for a normal operating mode.
        Assert.Equal("SyntheticForTestingOnly", options.Provider.ToString());
    }

    [Fact]
    public void DefaultDeltaBandMatchesTheStatedRule()
    {
        var options = new MarketDataOptions();

        Assert.Equal(0.65, options.OptionTargetDelta, precision: 4);
        Assert.Equal(0.60, options.OptionMinimumDelta, precision: 4);
        Assert.Equal(0.70, options.OptionMaximumDelta, precision: 4);
    }

    [Fact]
    public void DefaultTimeframeDividesTheOpeningRangeEvenly()
    {
        var options = new MarketDataOptions();

        // Anything that does not divide 15 produces a bar straddling the 09:45 lock.
        Assert.Equal(0, 15 % options.TimeframeMinutes);
    }
}
