using BreakoutAlerts.Core.Trading;

namespace BreakoutAlerts.Core.Abstractions;

/// <summary>
/// Order routing to the broker.
/// </summary>
/// <remarks>
/// <b>Every method here is called from an explicit user action.</b> Nothing in this
/// application places, amends or cancels an order on its own initiative, and no automatic
/// caller of this interface should be added without that being a deliberate, separately
/// reviewed decision. The scanner brings a setup to the user's attention; the user decides.
///
/// <para>Separate from <see cref="IMarketDataProvider"/> on purpose. They speak to different
/// sockets with different failure modes, and a quote-side fault must never be able to
/// disturb the path that moves money.</para>
/// </remarks>
public interface ITradingService
{
    /// <summary>Whether the trading channel is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Whether the trade password has been accepted this session.</summary>
    /// <remarks>
    /// Required for live orders and not for paper ones, which is what lets the whole flow be
    /// rehearsed on a paper account without the password ever being typed.
    /// </remarks>
    bool IsUnlocked { get; }

    /// <summary>Raised when connection or unlock state changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Raised when the broker reports a change to any order. Background thread.</summary>
    event EventHandler<TradeOrder>? OrderUpdated;

    /// <summary>Connects the trading channel. Safe to call when already connected.</summary>
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists accounts that can trade US securities.
    /// </summary>
    /// <remarks>
    /// Filtered to US securities deliberately. A live gateway also exposes crypto, futures
    /// and prediction-market accounts; showing those in a picker for an options order invites
    /// selecting one that cannot possibly accept it, and the resulting broker rejection would
    /// be far less clear than simply not offering them.
    /// </remarks>
    Task<IReadOnlyList<TradeAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Unlocks trading for this session.</summary>
    /// <param name="tradePassword">
    /// The account's trade password. Used and discarded - implementations must not retain it.
    /// </param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>
    /// The broker's own answer, not a bare true/false. A refused unlock has several distinct
    /// causes that the user has to tell apart - a wrong password, a password belonging to a
    /// different brokerage entity, an account with no trade password set - and only the
    /// broker's message distinguishes them.
    /// </returns>
    Task<UnlockResult> UnlockAsync(string tradePassword, CancellationToken cancellationToken = default);

    /// <summary>Submits an order. Only ever from a confirmed user action.</summary>
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels a working order.</summary>
    Task<OrderResult> CancelOrderAsync(TradeAccount account, ulong orderId, CancellationToken cancellationToken = default);

    /// <summary>Amends a working order's price and quantity.</summary>
    Task<OrderResult> ModifyOrderAsync(
        TradeAccount account,
        ulong orderId,
        int quantity,
        decimal limitPrice,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches the current day's orders for an account.</summary>
    Task<IReadOnlyList<TradeOrder>> GetOrdersAsync(TradeAccount account, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of an unlock attempt.</summary>
/// <param name="Success">Whether the broker accepted the password.</param>
/// <param name="Message">
/// The broker's reason for refusing, passed through unedited. Never contains the password.
/// </param>
/// <remarks>
/// A separate type from <see cref="OrderResult"/> because there is no order id here and
/// borrowing one that carries a permanently-null field would be a small lie in a place where
/// the types are the documentation.
/// </remarks>
public sealed record UnlockResult(bool Success, string? Message)
{
    /// <summary>A successful unlock.</summary>
    public static UnlockResult Ok() => new(true, null);

    /// <summary>A refusal, carrying the broker's reason.</summary>
    public static UnlockResult Fail(string message) => new(false, message);
}

/// <summary>Outcome of an order operation.</summary>
/// <param name="Success">Whether the broker accepted it.</param>
/// <param name="OrderId">Broker order id, when one was assigned.</param>
/// <param name="Message">Failure reason, or a confirmation.</param>
/// <remarks>
/// A result type rather than exceptions for rejections. A rejected order is an ordinary,
/// expected outcome - insufficient buying power, a closed market, a bad limit - and the user
/// needs to read the reason, not see a stack trace. Exceptions stay for genuine faults like a
/// dropped connection.
/// </remarks>
public sealed record OrderResult(bool Success, ulong? OrderId, string? Message)
{
    /// <summary>A successful result.</summary>
    public static OrderResult Ok(ulong orderId, string? message = null) => new(true, orderId, message);

    /// <summary>A failed result.</summary>
    public static OrderResult Fail(string message) => new(false, null, message);
}
