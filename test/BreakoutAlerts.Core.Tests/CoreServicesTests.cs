using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Alerts;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;
using BreakoutAlerts.Core.Watchlist;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BreakoutAlerts.Core.Tests;

/// <summary>Tests for the watchlist store.</summary>
public sealed class JsonWatchlistServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ba-wl-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "watchlist.json");

    private JsonWatchlistService Create() => new(Path_, NullLogger<JsonWatchlistService>.Instance);

    [Fact]
    public async Task FirstLoadSeedsDefaultsAndPersistsThem()
    {
        var service = Create();
        await service.LoadAsync();

        Assert.NotEmpty(service.Items);
        Assert.True(File.Exists(Path_));
    }

    [Fact]
    public async Task AddNormalisesToUpperCaseAndPersists()
    {
        var service = Create();
        await service.LoadAsync();

        Assert.True(await service.AddAsync("  spy  "));
        Assert.Contains(service.Items, e => e.Ticker == "SPY");

        // A second service over the same file must see it - persistence, not just memory.
        var reloaded = Create();
        await reloaded.LoadAsync();
        Assert.Contains(reloaded.Items, e => e.Ticker == "SPY");
    }

    [Fact]
    public async Task DuplicateAddIsRejectedCaseInsensitively()
    {
        var service = Create();
        await service.LoadAsync();
        await service.AddAsync("SPY");

        Assert.False(await service.AddAsync("spy"));
        Assert.Single(service.Items, e => e.Ticker == "SPY");
    }

    [Fact]
    public async Task BlankSymbolsAreRejected()
    {
        var service = Create();
        await service.LoadAsync();

        Assert.False(await service.AddAsync(""));
        Assert.False(await service.AddAsync("   "));
    }

    [Fact]
    public async Task RemoveWorksCaseInsensitivelyAndReportsMisses()
    {
        var service = Create();
        await service.LoadAsync();
        await service.AddAsync("SPY");

        Assert.True(await service.RemoveAsync("spy"));
        Assert.False(service.Contains("SPY"));
        Assert.False(await service.RemoveAsync("NOTTHERE"));
    }

    [Fact]
    public async Task ChangedFiresOnAddAndRemove()
    {
        var service = Create();
        await service.LoadAsync();

        var count = 0;
        service.Changed += (_, _) => count++;

        await service.AddAsync("SPY");
        await service.RemoveAsync("SPY");

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CorruptFileFallsBackToSeedRatherThanFailing()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path_, "{ this is not valid json");

        var service = Create();
        await service.LoadAsync();

        // Leaving the scanner with nothing to scan would be worse than losing the list.
        Assert.NotEmpty(service.Items);
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
        catch (IOException)
        {
        }
    }
}

/// <summary>Tests for the strategy registry.</summary>
public sealed class StrategyRegistryTests
{
    private static IPriceStrategy Stub(string id)
    {
        var strategy = Substitute.For<IPriceStrategy>();
        strategy.Id.Returns(id);
        return strategy;
    }

    [Fact]
    public void RegisteredStrategiesStartEnabled()
    {
        var registry = new StrategyRegistry();
        registry.Register(Stub("A"));

        // A strategy that silently never fires until separately switched on is a
        // surprising and invisible default.
        Assert.True(registry.IsEnabled("A"));
        Assert.Single(registry.Active);
    }

    [Fact]
    public void DisablingRemovesFromActiveButNotFromAll()
    {
        var registry = new StrategyRegistry();
        registry.Register(Stub("A"));

        Assert.True(registry.SetEnabled("A", false));
        Assert.Empty(registry.Active);
        Assert.Single(registry.All);
    }

    [Fact]
    public void SetEnabledReportsUnknownIds()
    {
        var registry = new StrategyRegistry();
        Assert.False(registry.SetEnabled("MISSING", true));
    }

    [Fact]
    public void RegisteringDuplicateIdReplacesRatherThanDuplicates()
    {
        var registry = new StrategyRegistry();
        registry.Register(Stub("A"));
        registry.Register(Stub("A"));

        Assert.Single(registry.All);
    }

    [Fact]
    public void ActiveSetChangedFiresOnRegisterAndOnRealToggles()
    {
        var registry = new StrategyRegistry();
        var count = 0;
        registry.ActiveSetChanged += (_, _) => count++;

        registry.Register(Stub("A"));
        registry.SetEnabled("A", false);

        // Re-setting the same value must not fire - it would cause needless scanner
        // reconfiguration on every checkbox repaint.
        registry.SetEnabled("A", false);

        Assert.Equal(2, count);
    }

    [Fact]
    public void EmptyIdIsRejected()
    {
        var registry = new StrategyRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(Stub("  ")));
    }
}

/// <summary>Tests for the alert notification channel.</summary>
public sealed class AlertNotificationServiceTests
{
    private static AlertRecord Sample() => new()
    {
        Ticker = "TEST",
        Strategy = "ORB_Breakout",
        AlertPath = "PATH-1",
        Direction = "LONG",
        Timestamp = DateTimeOffset.Now
    };

    [Fact]
    public async Task PersistsBeforeNotifying()
    {
        var store = Substitute.For<IAlertStore>();
        var service = new AlertNotificationService(store, NullLogger<AlertNotificationService>.Instance);

        var persistedFirst = false;
        service.AlertRaised += (_, _) =>
            persistedFirst = store.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAlertStore.AppendAsync));

        await service.PublishAsync(Sample());

        // Reversed, a crash between notify and persist leaves a signal the user acted on
        // but the backtest log has no record of.
        Assert.True(persistedFirst);
    }

    [Fact]
    public async Task OneThrowingSubscriberDoesNotBlockTheOthers()
    {
        var store = Substitute.For<IAlertStore>();
        var service = new AlertNotificationService(store, NullLogger<AlertNotificationService>.Instance);

        var secondRan = false;
        service.AlertRaised += (_, _) => throw new InvalidOperationException("boom");
        service.AlertRaised += (_, _) => secondRan = true;

        await service.PublishAsync(Sample());

        Assert.True(secondRan);
    }

    [Fact]
    public async Task PublishingWithNoSubscribersStillPersists()
    {
        var store = Substitute.For<IAlertStore>();
        var service = new AlertNotificationService(store, NullLogger<AlertNotificationService>.Instance);

        await service.PublishAsync(Sample());

        await store.Received(1).AppendAsync(Arg.Any<AlertRecord>(), Arg.Any<CancellationToken>());
    }
}
