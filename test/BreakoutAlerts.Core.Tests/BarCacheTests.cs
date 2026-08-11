using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Caching;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BreakoutAlerts.Core.Tests;

/// <summary>
/// Tests for the local bar cache and the provider decorator that fills it.
/// </summary>
/// <remarks>
/// The cache exists because the gateway serves only about three days. The risk it introduces
/// is serving a partial day as though it were whole, which would produce a wrong opening
/// range or a wrong previous-session high while looking entirely plausible - so most of what
/// is pinned here is about what must NOT be cached.
/// </remarks>
public sealed class BarCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ba-cache-" + Guid.NewGuid().ToString("N"));

    private JsonBarCache CreateCache() => new(_dir, NullLogger<JsonBarCache>.Instance);

    /// <summary>A bar at a given time of day on a given session, in exchange time.</summary>
    private static Bar BarAt(DateOnly session, int hour, int minute)
    {
        var naive = session.ToDateTime(new TimeOnly(hour, minute));
        var offset = MarketSession.ExchangeTimeZone.GetUtcOffset(naive);
        return new Bar(new DateTimeOffset(naive, offset), 100m, 101m, 99m, 100m, 1_000);
    }

    private static DateOnly Today => MarketSession.SessionDate(DateTimeOffset.Now);

    [Fact]
    public async Task StoresAndReadsBackASession()
    {
        var cache = CreateCache();
        var session = Today.AddDays(-5);
        var bars = new[] { BarAt(session, 9, 30), BarAt(session, 9, 35) };

        await cache.StoreSessionAsync("AAA", 5, session, bars);
        var read = await cache.GetSessionAsync("AAA", 5, session);

        Assert.Equal(2, read.Count);
        Assert.Equal(bars[0].Timestamp, read[0].Timestamp);
    }

    [Fact]
    public async Task AMissingSessionIsAnEmptyResultNotAFailure()
    {
        var cache = CreateCache();
        Assert.Empty(await cache.GetSessionAsync("AAA", 5, Today.AddDays(-3)));
    }

    [Fact]
    public async Task AnUnreadableFileIsTreatedAsAMiss()
    {
        var cache = CreateCache();
        var session = Today.AddDays(-2);

        await cache.StoreSessionAsync("AAA", 5, session, new[] { BarAt(session, 9, 30) });

        var path = Path.Combine(_dir, "AAA_5m", $"{session:yyyy-MM-dd}.json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        // A corrupt cache file must never take down a scan. The provider can still serve
        // recent sessions, and losing a session of history is a far better outcome than an
        // exception propagating out of a performance optimisation.
        Assert.Empty(await cache.GetSessionAsync("AAA", 5, session));
    }

    [Fact]
    public async Task SessionsAreListedNewestFirst()
    {
        var cache = CreateCache();

        foreach (var offset in new[] { -1, -3, -2 })
        {
            var s = Today.AddDays(offset);
            await cache.StoreSessionAsync("AAA", 5, s, new[] { BarAt(s, 9, 30) });
        }

        var sessions = await cache.GetStoredSessionsAsync("AAA", 5);

        Assert.Equal([Today.AddDays(-1), Today.AddDays(-2), Today.AddDays(-3)], sessions);
    }

    [Fact]
    public async Task TimeframesAreStoredSeparately()
    {
        var cache = CreateCache();
        var session = Today.AddDays(-1);

        await cache.StoreSessionAsync("AAA", 5, session, new[] { BarAt(session, 9, 30) });

        // A 5-minute session must never be served as a 1-minute one. Sharing a key would
        // silently return bars of the wrong size, which every downstream calculation assumes.
        Assert.Empty(await cache.GetSessionAsync("AAA", 1, session));
    }

    // ---- Decorator ---------------------------------------------------------

    private static IMarketDataProvider ProviderReturning(params Bar[] bars)
    {
        var inner = Substitute.For<IMarketDataProvider>();
        inner.DataSource.Returns("test");
        inner.GetBarsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>(bars));
        return inner;
    }

    private CachingMarketDataProvider Decorate(IMarketDataProvider inner) =>
        new(inner, CreateCache(), NullLogger<CachingMarketDataProvider>.Instance);

    [Fact]
    public async Task TheCurrentSessionIsNeverCached()
    {
        var cache = CreateCache();
        var inner = ProviderReturning(BarAt(Today, 9, 30), BarAt(Today, 9, 35));
        var provider = new CachingMarketDataProvider(inner, cache, NullLogger<CachingMarketDataProvider>.Instance);

        await provider.GetBarsAsync("AAA", 5, 800);

        // Today is still forming - its high, its low and its bar count all change as the day
        // goes on. A cached copy would freeze a partial day and later serve it as a whole
        // one, producing a wrong opening range from data that stopped at lunchtime.
        Assert.Empty(await cache.GetSessionAsync("AAA", 5, Today));
    }

    [Fact]
    public async Task CompletedSessionsAreCached()
    {
        var cache = CreateCache();
        var yesterday = Today.AddDays(-1);
        var inner = ProviderReturning(BarAt(yesterday, 9, 30), BarAt(Today, 9, 30));
        var provider = new CachingMarketDataProvider(inner, cache, NullLogger<CachingMarketDataProvider>.Instance);

        await provider.GetBarsAsync("AAA", 5, 800);

        Assert.Single(await cache.GetSessionAsync("AAA", 5, yesterday));
    }

    [Fact]
    public async Task OlderSessionsAreServedFromCacheWhenTheGatewayCannotReachThem()
    {
        var cache = CreateCache();
        var old = Today.AddDays(-9);

        await cache.StoreSessionAsync("AAA", 5, old, new[] { BarAt(old, 9, 30), BarAt(old, 9, 35) });

        // The gateway serves only today - exactly what it does for anything past its window.
        var inner = ProviderReturning(BarAt(Today, 9, 30));
        var provider = new CachingMarketDataProvider(inner, cache, NullLogger<CachingMarketDataProvider>.Instance);

        var bars = await provider.GetBarsAsync("AAA", 5, 800);

        // This is the whole point: charting an alert older than the gateway's window, and
        // any strategy that reasons about a previous session.
        Assert.Equal(3, bars.Count);
        Assert.Equal(old, MarketSession.SessionDate(bars[0].Timestamp));
    }

    [Fact]
    public async Task LiveBarsWinOverCachedOnesForTheSameTimestamp()
    {
        var cache = CreateCache();
        var yesterday = Today.AddDays(-1);

        var stale = new Bar(BarAt(yesterday, 9, 30).Timestamp, 1m, 1m, 1m, 1m, 1);
        await cache.StoreSessionAsync("AAA", 5, yesterday, new[] { stale });

        var fresh = BarAt(yesterday, 9, 30);
        var inner = ProviderReturning(fresh);
        var provider = new CachingMarketDataProvider(inner, cache, NullLogger<CachingMarketDataProvider>.Instance);

        var bars = await provider.GetBarsAsync("AAA", 5, 800);

        // A session the gateway still serves is the more authoritative copy - it may carry a
        // correction made after the cache was written.
        Assert.Equal(100m, Assert.Single(bars).Close);
    }

    [Fact]
    public async Task ResultsStayInAscendingTimeOrder()
    {
        var cache = CreateCache();
        var old = Today.AddDays(-8);

        await cache.StoreSessionAsync("AAA", 5, old, new[] { BarAt(old, 9, 35), BarAt(old, 9, 30) });

        var inner = ProviderReturning(BarAt(Today, 9, 30));
        var provider = new CachingMarketDataProvider(inner, cache, NullLogger<CachingMarketDataProvider>.Instance);

        var bars = await provider.GetBarsAsync("AAA", 5, 800);

        Assert.Equal(bars.OrderBy(b => b.Timestamp).Select(b => b.Timestamp), bars.Select(b => b.Timestamp));
    }

    [Fact]
    public async Task TheRequestedCountIsRespected()
    {
        var cache = CreateCache();
        var old = Today.AddDays(-7);

        var first = BarAt(old, 9, 30);
        await cache.StoreSessionAsync("AAA", 5, old,
            Enumerable.Range(0, 10)
                .Select(i => first with { Timestamp = first.Timestamp.AddMinutes(i * 5) })
                .ToList());

        var inner = ProviderReturning(BarAt(Today, 9, 30));
        var provider = new CachingMarketDataProvider(inner, cache, NullLogger<CachingMarketDataProvider>.Instance);

        var bars = await provider.GetBarsAsync("AAA", 5, 4);

        // Trimmed from the OLDEST end, so the most recent bars always survive - the opposite
        // choice would discard exactly the data every caller actually wants.
        Assert.Equal(4, bars.Count);
        Assert.Equal(Today, MarketSession.SessionDate(bars[^1].Timestamp));
    }

    [Fact]
    public void DataSourceIsPassedThroughUnchanged()
    {
        // Alert provenance is stamped from this. A decorator that reported its own name would
        // mark every alert as coming from the cache rather than from the feed behind it.
        Assert.Equal("test", Decorate(ProviderReturning()).DataSource);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup only.
        }
    }
}
