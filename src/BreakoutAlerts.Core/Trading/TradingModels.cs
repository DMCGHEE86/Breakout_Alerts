using System.Text.Json.Serialization;

namespace BreakoutAlerts.Core.Trading;

/// <summary>Which broker environment an account belongs to.</summary>
/// <remarks>
/// Not a bool. <c>IsReal</c> reads identically whether it is true or false at a glance, and
/// this is the single most consequential distinction in the application - a named value
/// forces the reader to see which one they are looking at.
/// </remarks>
public enum TradeEnvironment
{
    /// <summary>Paper trading. The default everywhere.</summary>
    Paper = 0,

    /// <summary>Real money.</summary>
    Live = 1
}

/// <summary>Buy or sell.</summary>
public enum OrderSide
{
    /// <summary>Buy to open.</summary>
    Buy = 0,

    /// <summary>Sell to close.</summary>
    Sell = 1
}

/// <summary>How the order is priced.</summary>
public enum OrderPricing
{
    /// <summary>Limit at a stated price.</summary>
    Limit = 0,

    /// <summary>Market. Fills immediately at whatever is available.</summary>
    Market = 1
}

/// <summary>Where an order currently stands.</summary>
public enum OrderState
{
    /// <summary>Sent, not yet acknowledged as working.</summary>
    Submitted = 0,

    /// <summary>Live at the exchange, unfilled.</summary>
    Working = 1,

    /// <summary>Some quantity filled, some still working.</summary>
    PartiallyFilled = 2,

    /// <summary>Fully filled.</summary>
    Filled = 3,

    /// <summary>Cancelled, by the user or the broker.</summary>
    Cancelled = 4,

    /// <summary>Rejected. <see cref="TradeOrder.Message"/> carries the reason.</summary>
    Rejected = 5,

    /// <summary>Reported by the broker but not recognised by this application.</summary>
    /// <remarks>
    /// Deliberately present rather than folded into one of the others. An unmapped broker
    /// status shown as "Working" would be a lie, and shown as "Filled" a dangerous one -
    /// this says plainly that the state is unknown so the user checks the broker.
    /// </remarks>
    Unknown = 6
}

/// <summary>A tradable account exposed by the broker.</summary>
/// <param name="AccountId">Broker account identifier.</param>
/// <param name="Environment">Paper or live.</param>
/// <param name="Description">Human-readable label for the picker.</param>
/// <param name="Cash">Cash balance, in USD.</param>
/// <param name="BuyingPower">Available buying power, in USD.</param>
/// <param name="IsUsable">
/// False when the broker reports the account as anything other than normal status.
/// </param>
public sealed record TradeAccount(
    ulong AccountId,
    TradeEnvironment Environment,
    string Description,
    decimal Cash,
    decimal BuyingPower,
    bool IsUsable);

/// <summary>Everything needed to submit one order.</summary>
/// <remarks>
/// Quantity is supplied by the user, never computed. Position sizing is explicitly the
/// trader's decision here - the candidates pane exists to shorten the path to knowing what a
/// contract costs, not to decide how much of it to buy.
/// </remarks>
public sealed record OrderRequest
{
    /// <summary>Account the order is sent to.</summary>
    public required TradeAccount Account { get; init; }

    /// <summary>Broker contract code, e.g. "AAPL260807C305000".</summary>
    public required string ContractCode { get; init; }

    /// <summary>Buy or sell.</summary>
    public required OrderSide Side { get; init; }

    /// <summary>Number of contracts.</summary>
    public required int Quantity { get; init; }

    /// <summary>Limit or market.</summary>
    public required OrderPricing Pricing { get; init; }

    /// <summary>Limit price. Ignored for a market order.</summary>
    public decimal? LimitPrice { get; init; }

    /// <summary>Underlying symbol, carried for the audit record.</summary>
    public string? Underlying { get; init; }

    /// <summary>Alert this order was raised from, if any. Audit only.</summary>
    public string? SourceAlertIdentity { get; init; }

    /// <summary>
    /// Protective stop price, or null for no stop.
    /// </summary>
    /// <remarks>
    /// Not sent with the entry - the broker API has no bracket order, and a stop to sell would
    /// be rejected before a position exists. It is recorded as a <see cref="PendingStop"/> and
    /// submitted once the entry fills. See that type for why it is persisted.
    /// </remarks>
    public decimal? StopLossPrice { get; init; }

    /// <summary>Whether the request is internally consistent.</summary>
    /// <remarks>
    /// Checked before anything is sent. A limit order with no price would be rejected by the
    /// broker, but only after a round trip and with a message the user has to interpret;
    /// catching it here keeps the failure adjacent to the field that caused it.
    /// </remarks>
    public string? Validate()
    {
        if (Quantity <= 0)
        {
            return "Quantity must be at least 1 contract.";
        }

        if (string.IsNullOrWhiteSpace(ContractCode))
        {
            return "No contract selected.";
        }

        if (Pricing == OrderPricing.Limit && (LimitPrice is null || LimitPrice <= 0))
        {
            return "A limit order needs a limit price above zero.";
        }

        if (!Account.IsUsable)
        {
            return $"Account {Account.AccountId} is not reported as usable by the broker.";
        }

        if (StopLossPrice is { } stop)
        {
            if (stop <= 0)
            {
                return "A stop price must be above zero.";
            }

            // A buy protected by a stop ABOVE the entry would trigger immediately on any
            // downtick past it - or rather, would never protect anything, because the stop
            // sits on the wrong side of the position. Caught here because the broker would
            // accept it and the mistake would only show up as a surprise exit.
            if (Side == OrderSide.Buy && Pricing == OrderPricing.Limit
                && LimitPrice is { } limit && stop >= limit)
            {
                return $"Stop {stop:N2} must be below the entry limit {limit:N2}.";
            }
        }

        return null;
    }
}

/// <summary>An order as the broker currently reports it.</summary>
public sealed record TradeOrder
{
    /// <summary>Broker order id.</summary>
    public required ulong OrderId { get; init; }

    /// <summary>Contract code.</summary>
    public required string ContractCode { get; init; }

    /// <summary>Buy or sell.</summary>
    public required OrderSide Side { get; init; }

    /// <summary>Current state.</summary>
    public required OrderState State { get; init; }

    /// <summary>Contracts ordered.</summary>
    public required int Quantity { get; init; }

    /// <summary>Contracts filled so far.</summary>
    public required int FilledQuantity { get; init; }

    /// <summary>Order price, if it has one.</summary>
    public decimal? Price { get; init; }

    /// <summary>Average fill price, if anything has filled.</summary>
    public decimal? AverageFillPrice { get; init; }

    /// <summary>Broker message. Carries the reason on a rejection.</summary>
    public string? Message { get; init; }

    /// <summary>When the order was submitted.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }

    /// <summary>Account it belongs to.</summary>
    public required ulong AccountId { get; init; }

    /// <summary>Paper or live.</summary>
    public required TradeEnvironment Environment { get; init; }

    /// <summary>True while the order can still be cancelled or amended.</summary>
    public bool IsOpen => State is OrderState.Submitted or OrderState.Working or OrderState.PartiallyFilled;
}

/// <summary>One line in the order audit log.</summary>
/// <remarks>
/// Separate from <see cref="TradeOrder"/> and from the alert log, and append-only. This is
/// the record of what this application <b>asked the broker to do</b>, which is a different
/// question from what the broker ended up doing, and it is the only artefact that can answer
/// "did the software send that, or did I?" after the fact.
///
/// <para><see cref="Environment"/> is on every line and is not optional. A log where paper
/// and live orders are indistinguishable would be worse than no log at all.</para>
/// </remarks>
public sealed record OrderAuditRecord
{
    /// <summary>What happened: "SUBMIT", "CANCEL", "MODIFY", "REJECTED".</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>When.</summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Account targeted.</summary>
    [JsonPropertyName("account_id")]
    public required ulong AccountId { get; init; }

    /// <summary>PAPER or LIVE.</summary>
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    /// <summary>Contract code.</summary>
    [JsonPropertyName("contract")]
    public required string ContractCode { get; init; }

    /// <summary>BUY or SELL.</summary>
    [JsonPropertyName("side")]
    public required string Side { get; init; }

    /// <summary>Contracts.</summary>
    [JsonPropertyName("quantity")]
    public required int Quantity { get; init; }

    /// <summary>LIMIT or MARKET.</summary>
    [JsonPropertyName("pricing")]
    public required string Pricing { get; init; }

    /// <summary>Limit price, if any.</summary>
    [JsonPropertyName("limit_price")]
    public decimal? LimitPrice { get; init; }

    /// <summary>Broker order id once known.</summary>
    [JsonPropertyName("order_id")]
    public ulong? OrderId { get; init; }

    /// <summary>Alert the order was raised from, if any.</summary>
    [JsonPropertyName("source_alert")]
    public string? SourceAlertIdentity { get; init; }

    /// <summary>Broker response or failure reason.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }
}
