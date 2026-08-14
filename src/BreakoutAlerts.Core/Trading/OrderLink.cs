using System.Text.Json.Serialization;

namespace BreakoutAlerts.Core.Trading;

/// <summary>
/// Records that an entry order and a protective stop belong together.
/// </summary>
/// <remarks>
/// <b>The broker accepts both orders at once.</b> Measured against a live account: a stop-sell
/// rests happily before the position exists, alongside its unfilled entry. So both are sent at
/// submission and the stop is protected by the broker whether this application is running or
/// not.
///
/// <para><b>But the API cannot express the link.</b> <c>TrdPlaceOrder</c> has no parent, child
/// or attach field. moomoo's own ticket produces a genuine bracket - cancel the entry there and
/// the stop cancels with it - whereas two orders sent through OpenAPI are entirely independent.
/// Cancel the entry and the stop keeps resting, and a stop with no position can open a short if
/// margin allows.</para>
///
/// <para>This record is what closes that gap. It is persisted so the pairing survives a
/// restart, and it is the reason cancelling an entry can also cancel its stop.</para>
/// </remarks>
public sealed record OrderLink
{
    /// <summary>Broker id of the entry order.</summary>
    [JsonPropertyName("entry_order_id")]
    public required ulong EntryOrderId { get; init; }

    /// <summary>Broker id of the protective stop, or null if it could not be placed.</summary>
    [JsonPropertyName("stop_order_id")]
    public ulong? StopOrderId { get; init; }

    /// <summary>Account both orders were sent to.</summary>
    [JsonPropertyName("account_id")]
    public required ulong AccountId { get; init; }

    /// <summary>PAPER or LIVE, so a restart cannot act on the wrong environment.</summary>
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    /// <summary>Contract both orders are for.</summary>
    [JsonPropertyName("contract")]
    public required string ContractCode { get; init; }

    /// <summary>Stop trigger price.</summary>
    [JsonPropertyName("stop_price")]
    public required decimal StopPrice { get; init; }

    /// <summary>Contracts.</summary>
    [JsonPropertyName("quantity")]
    public required int Quantity { get; init; }

    /// <summary>When the pair was submitted.</summary>
    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Why the stop could not be placed, if it failed.</summary>
    /// <remarks>
    /// Kept rather than discarded. A failed stop means the entry is unprotected, and dropping
    /// the record would remove the only evidence that the user should go and look.
    /// </remarks>
    [JsonPropertyName("failure")]
    public string? Failure { get; init; }

    /// <summary>True when the stop is resting at the broker.</summary>
    [JsonIgnore]
    public bool IsProtected => StopOrderId is not null && Failure is null;
}
