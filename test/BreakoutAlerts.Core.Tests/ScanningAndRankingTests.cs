using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Options;
using BreakoutAlerts.Core.Scanning;
using BreakoutAlerts.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace BreakoutAlerts.Core.Tests;

/// <summary>Tests for the scan cycle.</summary>
public sealed class ScannerEngineTests
{
    private static (ScannerEngine Engine, IAlertNotificationService Notifications, IMarketDataProvider Data, IStrategyRegistry Registry)
        Build(IReadOnlyList<WatchlistEntry> watchlist, IPriceStrategy? strategy)
    {
        var wl = Substitute.For<IWatchlistService>();
        wl.Items.Returns(watchlist);

        var data = Substitute.For<IMarketDataProvider>();
        data.DataSource.Returns("test-provider");
        data.GetBarsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>(
                [new Bar(DateTimeOffset.Now, 100m, 101m, 99m, 100m, 1000)]));

        var registry = Substitute.For<IStrategyRegistry>();
        registry.Active.Returns(strategy is null ? [] : new List<IPriceStrategy> { strategy });

        var notifications = Substitute.For<IAlertNotificationService>();

        var engine = new ScannerEngine(wl, data, registry, notifications, NullLogger<ScannerEngine>.Instance);
        return (engine, notifications, data, registry);
    }

    /// <summary>A fake strategy that always reports the given signals.</summary>
    /// <remarks>
    /// <b>Both Evaluate overloads are configured, and both are needed.</b> A substitute does
    /// not inherit an interface's default implementation - it intercepts every member and
    /// returns null for any that was not set up - so configuring only the two-argument
    /// overload leaves the engine's actual call path returning null. Stubbing both keeps the
    /// fake honest about which one the engine uses.
    /// </remarks>
    private static IPriceStrategy StrategyReturning(params StrategySignal[] signals)
    {
        var strategy = Substitute.For<IPriceStrategy>();
        strategy.Id.Returns("ORB_Breakout");
        strategy.AdditionalTimeframes.Returns([]);
        strategy.Evaluate(Arg.Any<string>(), Arg.Any<IReadOnlyList<Bar>>()).Returns(signals);
        strategy.Evaluate(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Bar>>(),
                Arg.Any<IReadOnlyDictionary<int, IReadOnlyList<Bar>>>())
            .Returns(signals);
        return strategy;
    }

    private static StrategySignal Signal(string ticker, string path = "PATH-1") =>
        new(ticker, "ORB_Breakout", path, TradeDirection.Long, 100.5m, DateTimeOffset.Now,
            new Dictionary<string, decimal?> { ["orb_high"] = 100m, ["orb_low"] = 98m });

    [Fact]
    public async Task PublishesOneAlertPerSignal()
    {
        var (engine, notifications, _, _) = Build(
            [new WatchlistEntry("AAA", DateTimeOffset.Now)],
            StrategyReturning(Signal("AAA", "PATH-1"), Signal("AAA", "PATH-3")));

        await engine.RunCycleAsync();

        // Both must land - collapsing simultaneous signals would discard the stronger one.
        await notifications.Received(2).PublishAsync(Arg.Any<AlertRecord>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, engine.LastCycleSignalCount);
    }

    [Fact]
    public async Task MapsSignalContextOntoTheAlertRecord()
    {
        var (engine, notifications, _, _) = Build(
            [new WatchlistEntry("AAA", DateTimeOffset.Now)],
            StrategyReturning(Signal("AAA")));

        await engine.RunCycleAsync();

        await notifications.Received(1).PublishAsync(
            Arg.Is<AlertRecord>(a =>
                a != null
                && a.Ticker == "AAA"
                && a.AlertPath == "PATH-1"
                && a.Direction == "LONG"
                // Levels pass through whole. The engine does not know or name them, which is
                // what lets a new strategy report entirely different ones without an edit here.
                && a.Level("orb_high") == 100m
                && a.Level("orb_low") == 98m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StampsEveryAlertWithTheProviderThatSuppliedTheBars()
    {
        var (engine, notifications, _, _) = Build(
            [new WatchlistEntry("AAA", DateTimeOffset.Now)],
            StrategyReturning(Signal("AAA")));

        await engine.RunCycleAsync();

        // The alert log is the backtesting substrate. A generated alert that is
        // indistinguishable from a real one silently poisons everything computed from the
        // file, and the damage is undetectable after the fact - so provenance is recorded
        // at the moment of publication, taken from the provider itself rather than from
        // configuration that could drift out of step with it.
        await notifications.Received(1).PublishAsync(
            Arg.Is<AlertRecord>(a => a != null && a.DataSource == "test-provider"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Pacific Standard Time")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("Tokyo Standard Time")]
    [InlineData("UTC")]
    public void SessionDateIsIndependentOfTheMachineTimezone(string timeZoneId)
    {
        // 2026-08-06 15:00 ET is the same instant everywhere; only its wall-clock reading
        // differs. Rendered in Tokyo it is already the 7th locally, so anything that took a
        // local calendar date would classify it as a different session.
        var et = new DateTimeOffset(2026, 8, 6, 15, 0, 0, TimeSpan.FromHours(-4));
        var elsewhere = TimeZoneInfo.ConvertTime(et, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

        // Pins a property a reviewer flagged as a risk: SessionDate converts to exchange time
        // before taking the date, so the host's timezone cannot move a signal between
        // sessions. Cheap to hold, and it stops someone "fixing" a problem that is not there.
        Assert.Equal(new DateOnly(2026, 8, 6), MarketSession.SessionDate(elsewhere));
        Assert.Equal(MarketSession.SessionDate(et), MarketSession.SessionDate(elsewhere));
    }

    [Fact]
    public async Task SuppressesSignalsAfterTheCutoff()
    {
        var late = DateTimeOffset.Now.Date.AddHours(15).AddMinutes(30);
        var lateSignal = new StrategySignal("AAA", "ORB_Breakout", "PATH-1", TradeDirection.Long,
            100.5m, new DateTimeOffset(late, MarketSession.ExchangeTimeZone.GetUtcOffset(late)),
            new Dictionary<string, decimal?>());

        var (engine, notifications, _, _) = Build(
            [new WatchlistEntry("AAA", DateTimeOffset.Now)], StrategyReturning(lateSignal));

        engine.AlertCutoff = new TimeOnly(14, 0);

        await engine.RunCycleAsync();

        // An opening-range breakout is a claim about the day's direction. One made half an
        // hour before the close leaves no time to act on it, so it competes for attention
        // with signals that still matter rather than adding anything.
        await notifications.DidNotReceive().PublishAsync(Arg.Any<AlertRecord>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, engine.LastCycleSuppressedCount);
    }

    [Fact]
    public async Task PublishesSignalsBeforeTheCutoff()
    {
        var early = DateTimeOffset.Now.Date.AddHours(10);
        var earlySignal = new StrategySignal("AAA", "ORB_Breakout", "PATH-1", TradeDirection.Long,
            100.5m, new DateTimeOffset(early, MarketSession.ExchangeTimeZone.GetUtcOffset(early)),
            new Dictionary<string, decimal?>());

        var (engine, notifications, _, _) = Build(
            [new WatchlistEntry("AAA", DateTimeOffset.Now)], StrategyReturning(earlySignal));

        engine.AlertCutoff = new TimeOnly(14, 0);

        await engine.RunCycleAsync();

        await notifications.Received(1).PublishAsync(Arg.Any<AlertRecord>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, engine.LastCycleSuppressedCount);
    }

    [Fact]
    public async Task PublishesAllDayWhenNoCutoffIsSet()
    {
        var late = DateTimeOffset.Now.Date.AddHours(15).AddMinutes(30);
        var lateSignal = new StrategySignal("AAA", "ORB_Breakout", "PATH-1", TradeDirection.Long,
            100.5m, new DateTimeOffset(late, MarketSession.ExchangeTimeZone.GetUtcOffset(late)),
            new Dictionary<string, decimal?>());

        var (engine, notifications, _, _) = Build(
            [new WatchlistEntry("AAA", DateTimeOffset.Now)], StrategyReturning(lateSignal));

        engine.AlertCutoff = null;

        await engine.RunCycleAsync();

        await notifications.Received(1).PublishAsync(Arg.Any<AlertRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DoesNothingWithAnEmptyWatchlist()
    {
        var (engine, notifications, _, _) = Build([], StrategyReturning(Signal("AAA")));

        await engine.RunCycleAsync();

        await notifications.DidNotReceive().PublishAsync(Arg.Any<AlertRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DoesNothingWithNoActiveStrategies()
    {
        var (engine, notifications, _, _) = Build(
            [new WatchlistEntry("AAA", DateTimeOffset.Now)], strategy: null);

        await engine.RunCycleAsync();

        await notifications.DidNotReceive().PublishAsync(Arg.Any<AlertRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneFailingSymbolDoesNotAbortTheCycle()
    {
        var wl = Substitute.For<IWatchlistService>();
        wl.Items.Returns([
            new WatchlistEntry("BAD", DateTimeOffset.Now),
            new WatchlistEntry("GOOD", DateTimeOffset.Now)
        ]);

        var data = Substitute.For<IMarketDataProvider>();

        // Throws, not a Returns lambda that throws - NSubstitute evaluates the latter while
        // still resolving the call it should attach to, and loses track of it.
        data.GetBarsAsync("BAD", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("gateway blew up"));
        data.GetBarsAsync("GOOD", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>(
                [new Bar(DateTimeOffset.Now, 100m, 101m, 99m, 100m, 1000)]));

        // Built before the Returns() call, not inside it. Configuring one substitute while
        // NSubstitute is still resolving another's call loses track of which call the
        // Returns belongs to - the error it raises says exactly this.
        var strategy = StrategyReturning(Signal("GOOD"));

        var registry = Substitute.For<IStrategyRegistry>();
        registry.Active.Returns(new List<IPriceStrategy> { strategy });

        var notifications = Substitute.For<IAlertNotificationService>();
        var engine = new ScannerEngine(wl, data, registry, notifications, NullLogger<ScannerEngine>.Instance);

        await engine.RunCycleAsync();

        // A transient error on one name must not silently stop the rest being scanned -
        // that failure mode looks exactly like "no setups today".
        await notifications.Received(1).PublishAsync(Arg.Any<AlertRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SymbolsWithNoBarsAreSkipped()
    {
        var wl = Substitute.For<IWatchlistService>();
        wl.Items.Returns([new WatchlistEntry("AAA", DateTimeOffset.Now)]);

        var data = Substitute.For<IMarketDataProvider>();
        data.GetBarsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>([]));

        var strategy = StrategyReturning(Signal("AAA"));
        var registry = Substitute.For<IStrategyRegistry>();
        registry.Active.Returns(new List<IPriceStrategy> { strategy });

        var notifications = Substitute.For<IAlertNotificationService>();
        var engine = new ScannerEngine(wl, data, registry, notifications, NullLogger<ScannerEngine>.Instance);

        await engine.RunCycleAsync();

        strategy.DidNotReceive().Evaluate(Arg.Any<string>(), Arg.Any<IReadOnlyList<Bar>>());
    }
}

/// <summary>Tests for the placeholder option ranker.</summary>
public sealed class DeltaTargetRankerTests
{
    private static OptionQuote Quote(
        OptionRight right, decimal strike, double delta,
        decimal bid = 5m, decimal ask = 5.2m, long openInterest = 5000, int dte = 14) => new()
    {
        Underlying = "TEST",
        ContractId = $"TEST-{strike}-{right}",
        Right = right,
        Strike = strike,
        Expiry = DateOnly.FromDateTime(DateTime.Today.AddDays(dte)),
        Bid = bid,
        Ask = ask,
        Delta = delta,
        Theta = -0.1,
        ImpliedVolatility = 0.3,
        OpenInterest = openInterest,
        DaysToExpiry = dte
    };

    [Fact]
    public void LongSignalReturnsOnlyCallsByDefault()
    {
        var ranker = new DeltaTargetRanker();
        var chain = new List<OptionQuote>
        {
            Quote(OptionRight.Call, 100, 0.65),
            Quote(OptionRight.Put, 100, -0.65)
        };

        var ranked = ranker.Rank(chain, new OptionRankContext(TradeDirection.Long, 100m));

        Assert.All(ranked, r => Assert.Equal(OptionRight.Call, r.Quote.Right));
    }

    [Fact]
    public void ShowBothSidesDisablesTheDirectionFilter()
    {
        var ranker = new DeltaTargetRanker();
        var chain = new List<OptionQuote>
        {
            Quote(OptionRight.Call, 100, 0.65),
            Quote(OptionRight.Put, 100, -0.65)
        };

        var ranked = ranker.Rank(chain, new OptionRankContext(
            TradeDirection.Long, 100m, RestrictToDirection: false));

        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void ContractNearestTargetDeltaRanksHighest()
    {
        var ranker = new DeltaTargetRanker();
        var chain = new List<OptionQuote>
        {
            Quote(OptionRight.Call, 95, 0.69),
            Quote(OptionRight.Call, 100, 0.65),
            Quote(OptionRight.Call, 105, 0.61)
        };

        var ranked = ranker.Rank(chain, new OptionRankContext(TradeDirection.Long, 100m));

        Assert.Equal(100m, ranked[0].Quote.Strike);
    }

    [Fact]
    public void ResultsAreOrderedBestFirst()
    {
        var ranker = new DeltaTargetRanker();
        var chain = new List<OptionQuote>
        {
            Quote(OptionRight.Call, 95, 0.61),
            Quote(OptionRight.Call, 100, 0.70),
            Quote(OptionRight.Call, 105, 0.65),
            Quote(OptionRight.Call, 110, 0.63)
        };

        var ranked = ranker.Rank(chain, new OptionRankContext(TradeDirection.Long, 100m));

        // The dashboard highlights the FIRST row as the top pick, so best-first ordering is
        // load-bearing rather than a convenience. A change that returned the same contracts
        // unsorted would leave the pane confidently marking the wrong one, and nothing about
        // the display would look broken.
        Assert.Equal(
            ranked.Select(r => r.Score).OrderByDescending(s => s).ToList(),
            ranked.Select(r => r.Score).ToList());
    }

    [Fact]
    public void DeltaBandIsAHardFilterNotAPreference()
    {
        var ranker = new DeltaTargetRanker();
        var chain = new List<OptionQuote>
        {
            Quote(OptionRight.Call, 90, 0.85),   // above the band
            Quote(OptionRight.Call, 100, 0.65),  // in the band
            Quote(OptionRight.Call, 110, 0.30)   // below the band
        };

        var ranked = ranker.Rank(chain, new OptionRankContext(TradeDirection.Long, 100m));

        // A 0.30-delta contract is a different trade, not a worse version of this one.
        // Ranking it last rather than excluding it would put contracts in the list that
        // should never be considered.
        Assert.Single(ranked);
        Assert.Equal(100m, ranked[0].Quote.Strike);
    }

    [Fact]
    public void BandBoundariesAreInclusive()
    {
        var ranker = new DeltaTargetRanker();
        var chain = new List<OptionQuote>
        {
            Quote(OptionRight.Call, 100, 0.60),
            Quote(OptionRight.Call, 101, 0.70)
        };

        Assert.Equal(2, ranker.Rank(chain, new OptionRankContext(TradeDirection.Long, 100m)).Count);
    }

    [Fact]
    public void PutsAreJudgedOnAbsoluteDelta()
    {
        var ranker = new DeltaTargetRanker();

        // A -0.65 put is the mirror of a +0.65 call, not something outside the band.
        var chain = new List<OptionQuote> { Quote(OptionRight.Put, 100, -0.65) };

        var ranked = ranker.Rank(chain, new OptionRankContext(TradeDirection.Short, 100m));

        Assert.Single(ranked);
        Assert.Equal(1.0, ranked[0].Score, precision: 3);
    }

    [Fact]
    public void ScoreIsOneAtTargetAndFallsTowardTheBandEdge()
    {
        var ranker = new DeltaTargetRanker();
        var chain = new List<OptionQuote>
        {
            Quote(OptionRight.Call, 100, 0.65),
            Quote(OptionRight.Call, 101, 0.70)
        };

        var ranked = ranker.Rank(chain, new OptionRankContext(TradeDirection.Long, 100m));

        var atTarget = ranked.Single(r => r.Quote.Strike == 100m);
        var atEdge = ranked.Single(r => r.Quote.Strike == 101m);

        Assert.Equal(1.0, atTarget.Score, precision: 3);
        Assert.Equal(0.0, atEdge.Score, precision: 3);
    }

    [Fact]
    public void IlliquidAndUntradeableContractsAreExcluded()
    {
        var ranker = new DeltaTargetRanker();
        var chain = new List<OptionQuote>
        {
            Quote(OptionRight.Call, 100, 0.65, openInterest: 5),      // too thin
            Quote(OptionRight.Call, 101, 0.65, dte: 1),               // expiring too soon
            Quote(OptionRight.Call, 102, 0.65, bid: 0m, ask: 0m),     // no two-sided market
            Quote(OptionRight.Call, 103, 0.65)                        // the only keeper
        };

        var ranked = ranker.Rank(chain, new OptionRankContext(TradeDirection.Long, 100m));

        Assert.Single(ranked);
        Assert.Equal(103m, ranked[0].Quote.Strike);
    }

    [Fact]
    public void ContractsWithoutADeltaAreExcluded()
    {
        var ranker = new DeltaTargetRanker();
        var quote = Quote(OptionRight.Call, 100, 0.65) with { Delta = null };

        Assert.Empty(ranker.Rank([quote], new OptionRankContext(TradeDirection.Long, 100m)));
    }

    [Fact]
    public void ResultsAreCappedByMaxResults()
    {
        var ranker = new DeltaTargetRanker();
        var chain = Enumerable.Range(0, 40)
            .Select(i => Quote(OptionRight.Call, 100 + i, 0.65))
            .ToList();

        var ranked = ranker.Rank(chain, new OptionRankContext(TradeDirection.Long, 100m, MaxResults: 5));

        Assert.Equal(5, ranked.Count);
    }

    [Fact]
    public void EveryResultCarriesAReadableRationale()
    {
        var ranker = new DeltaTargetRanker();
        var ranked = ranker.Rank(
            [Quote(OptionRight.Call, 100, 0.65)],
            new OptionRankContext(TradeDirection.Long, 100m));

        // A ranked recommendation the user cannot interrogate is one they have to either
        // trust blindly or ignore.
        Assert.All(ranked, r => Assert.False(string.IsNullOrWhiteSpace(r.Rationale)));
    }

    [Fact]
    public void WideSpreadScoresBelowTightSpreadAtEqualDelta()
    {
        var ranker = new DeltaTargetRanker();
        var chain = new List<OptionQuote>
        {
            Quote(OptionRight.Call, 100, 0.65, bid: 5.00m, ask: 5.05m),
            Quote(OptionRight.Call, 101, 0.65, bid: 5.00m, ask: 7.00m)
        };

        var ranked = ranker.Rank(chain, new OptionRankContext(TradeDirection.Long, 100m));

        Assert.Equal(100m, ranked[0].Quote.Strike);
    }
}

/// <summary>Guards that the scanner only alerts on the current session.</summary>
public sealed class ScannerSessionFilterTests
{
    [Fact]
    public async Task HistoricalSignalsAreNotPublished()
    {
        var wl = Substitute.For<IWatchlistService>();
        wl.Items.Returns([new WatchlistEntry("AAA", DateTimeOffset.Now)]);

        var data = Substitute.For<IMarketDataProvider>();
        data.GetBarsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>(
                [new Bar(DateTimeOffset.Now, 100m, 101m, 99m, 100m, 1000)]));

        // A live provider returns several days of history, and the strategy evaluates each
        // day correctly - so without a filter the first scan after startup replays a week
        // of breakouts as though they had just fired.
        var stale = new StrategySignal("AAA", "ORB_Breakout", "PATH-1", TradeDirection.Long,
            100.5m, DateTimeOffset.Now.AddDays(-3), new Dictionary<string, decimal?>());
        var fresh = new StrategySignal("AAA", "ORB_Breakout", "PATH-1", TradeDirection.Long,
            100.5m, DateTimeOffset.Now, new Dictionary<string, decimal?>());

        var strategy = Substitute.For<IPriceStrategy>();
        strategy.Id.Returns("ORB_Breakout");
        strategy.AdditionalTimeframes.Returns([]);

        // Both overloads - a substitute does not inherit the interface's default
        // implementation. See StrategyReturning in ScannerEngineTests.
        strategy.Evaluate(Arg.Any<string>(), Arg.Any<IReadOnlyList<Bar>>())
            .Returns(new List<StrategySignal> { stale, fresh });
        strategy.Evaluate(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Bar>>(),
                Arg.Any<IReadOnlyDictionary<int, IReadOnlyList<Bar>>>())
            .Returns(new List<StrategySignal> { stale, fresh });

        var registry = Substitute.For<IStrategyRegistry>();
        registry.Active.Returns(new List<IPriceStrategy> { strategy });

        var notifications = Substitute.For<IAlertNotificationService>();
        var engine = new ScannerEngine(wl, data, registry, notifications, NullLogger<ScannerEngine>.Instance);

        await engine.RunCycleAsync();

        await notifications.Received(1).PublishAsync(Arg.Any<AlertRecord>(), Arg.Any<CancellationToken>());
    }
}
