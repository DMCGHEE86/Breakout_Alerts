using BreakoutAlerts.Core.Trading;
using Microsoft.Extensions.Logging.Abstractions;

namespace BreakoutAlerts.Core.Tests;

/// <summary>Tests for the stop price check on an order request.</summary>
public sealed class StopValidationTests
{
    private static OrderRequest Request(decimal? limit, decimal? stop, OrderSide side = OrderSide.Buy) => new()
    {
        Account = new TradeAccount(1, TradeEnvironment.Paper, "PAPER", 1000m, 1000m, IsUsable: true),
        ContractCode = "AAPL260828C305000",
        Side = side,
        Quantity = 1,
        Pricing = OrderPricing.Limit,
        LimitPrice = limit,
        StopLossPrice = stop
    };

    [Fact]
    public void AcceptsAStopBelowTheEntry()
    {
        Assert.Null(Request(limit: 5.00m, stop: 4.00m).Validate());
    }

    [Fact]
    public void RejectsAStopAtOrAboveTheEntryOnABuy()
    {
        // A stop above a long entry sits on the wrong side of the position - it protects
        // nothing and would exit immediately. The broker accepts it, so the mistake would only
        // surface as a surprise fill.
        Assert.NotNull(Request(limit: 5.00m, stop: 5.50m).Validate());
        Assert.NotNull(Request(limit: 5.00m, stop: 5.00m).Validate());
    }

    [Fact]
    public void RejectsAZeroOrNegativeStop()
    {
        Assert.NotNull(Request(limit: 5.00m, stop: 0m).Validate());
        Assert.NotNull(Request(limit: 5.00m, stop: -1m).Validate());
    }

    [Fact]
    public void NoStopIsPerfectlyValid()
    {
        Assert.Null(Request(limit: 5.00m, stop: null).Validate());
    }
}

/// <summary>
/// Tests for the durable record pairing an entry with its protective stop.
/// </summary>
/// <remarks>
/// The broker accepts both orders at once, so protection rests there and survives this
/// application closing. What it does <b>not</b> do is link them: the API has no parent/child
/// field, so cancelling an entry leaves the stop resting, and a stop with no position can open
/// a short. This record is what lets the application close that gap, which is why it has to
/// survive a restart.
/// </remarks>
public sealed class OrderLinkStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ba-links-" + Guid.NewGuid().ToString("N"));

    private OrderLinkStore CreateStore() =>
        new(Path.Combine(_dir, "order-links.json"), NullLogger<OrderLinkStore>.Instance);

    private static OrderLink Link(ulong entryId, ulong? stopId = null, string? failure = null) => new()
    {
        EntryOrderId = entryId,
        StopOrderId = stopId,
        AccountId = 99,
        Environment = "PAPER",
        ContractCode = "AAPL260828C305000",
        StopPrice = 4.50m,
        Quantity = 5,
        CreatedAt = DateTimeOffset.Now,
        Failure = failure
    };

    [Fact]
    public async Task SurvivesARestart()
    {
        await CreateStore().SaveAsync(Link(1, stopId: 500));

        // A separate instance, as a relaunched application would be. Without this the pairing
        // is lost, and cancelling the entry later would leave the stop resting.
        var reopened = Assert.Single(await CreateStore().GetAllAsync());

        Assert.Equal(1ul, reopened.EntryOrderId);
        Assert.Equal(500ul, reopened.StopOrderId);
    }

    [Fact]
    public async Task SavingTheSameEntryReplacesRatherThanDuplicates()
    {
        var store = CreateStore();

        await store.SaveAsync(Link(1));
        await store.SaveAsync(Link(1, stopId: 500));

        Assert.Equal(500ul, Assert.Single(await store.GetAllAsync()).StopOrderId);
    }

    [Fact]
    public async Task RemovesOnlyTheRequestedEntry()
    {
        var store = CreateStore();

        await store.SaveAsync(Link(1));
        await store.SaveAsync(Link(2));
        await store.RemoveAsync(1);

        Assert.Equal(2ul, Assert.Single(await store.GetAllAsync()).EntryOrderId);
    }

    [Fact]
    public async Task FailedStopsAreKeptNotDiscarded()
    {
        var store = CreateStore();
        await store.SaveAsync(Link(1, failure: "rejected"));

        // A failed stop means an unprotected entry. Dropping the record would remove the only
        // evidence that the user needs to go and look.
        Assert.False(Assert.Single(await store.GetAllAsync()).IsProtected);
    }

    [Fact]
    public async Task AnUnreadableFileDoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "order-links.json"), "{ not json");

        Assert.Empty(await CreateStore().GetAllAsync());
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(500ul, null, true)]
    [InlineData(500ul, "rejected", false)]
    public void IsProtectedOnlyWhenAStopRestsWithoutFailure(ulong? stopId, string? failure, bool expected)
    {
        Assert.Equal(expected, Link(1, stopId, failure).IsProtected);
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
