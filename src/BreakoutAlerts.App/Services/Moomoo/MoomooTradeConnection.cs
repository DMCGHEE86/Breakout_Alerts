using System.Collections.Concurrent;
using BreakoutAlerts.Core.Configuration;
using Futu.OpenApi;
using Futu.OpenApi.Pb;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Single owner of the trading connection to the local moomoo OpenD gateway.
/// </summary>
/// <remarks>
/// <b>A separate socket from <see cref="MoomooConnection"/>, deliberately.</b> The SDK models
/// quotes and trading as different clients with different callback interfaces, and keeping
/// them apart means a quote-side fault - a bad subscription, a throttled history request -
/// cannot disturb the connection that places orders. It also means order routing can be left
/// entirely unconnected while the scanner runs, which is the normal state.
///
/// <para>Adapts the SDK's callback model into awaitable calls the same way the quote
/// connection does: a <see cref="TaskCompletionSource{TResult}"/> parked per serial number
/// and completed from the reply. Every wait is bounded, and a disconnect fails all
/// outstanding waiters rather than leaving an order request hanging - which matters far more
/// here, where the caller is a person waiting to know whether their order went in.</para>
///
/// <para><b>This class never places an order on its own initiative.</b> It exposes the
/// request; deciding to send one belongs to an explicit user action further up.</para>
/// </remarks>
public sealed partial class MoomooTradeConnection : FTSPI_Trd, FTSPI_Conn, IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(20);

    private readonly FTAPI_Trd _trd = new();
    private readonly MarketDataOptions _options;
    private readonly ILogger<MoomooTradeConnection> _logger;

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<object>> _pending = new();

    private TaskCompletionSource<bool>? _connectResult;
    private bool _disposed;

    /// <summary>True when the trading gateway is connected.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// True once the trade password has been accepted for this session.
    /// </summary>
    /// <remarks>
    /// Required before real orders. Simulated orders do not need it, which is what makes a
    /// paper account usable as a full rehearsal without the password ever being typed.
    /// </remarks>
    public bool IsUnlocked { get; private set; }

    /// <summary>Raised when the connection state changes. Background thread.</summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>Creates the wrapper. Does not connect.</summary>
    public MoomooTradeConnection(MarketDataOptions options, ILogger<MoomooTradeConnection> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Connects to the gateway's trading channel.</summary>
    /// <returns>False when OpenD is not running or refused the connection.</returns>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return true;
        }

        _connectResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        FTAPI.Init();
        _trd.SetClientInfo("BreakoutAlerts", 1);
        _trd.SetConnCallback(this);
        _trd.SetTrdCallback(this);
        _trd.InitConnect(_options.Host, _options.Port, false);

        var completed = await Task.WhenAny(
            _connectResult.Task,
            Task.Delay(ReplyTimeout, cancellationToken)).ConfigureAwait(false);

        if (completed != _connectResult.Task)
        {
            _logger.LogWarning("OpenD trade channel did not respond within {Timeout}s",
                ReplyTimeout.TotalSeconds);
            return false;
        }

        return _connectResult.Task.Result;
    }

    /// <summary>Issues a request and awaits its reply.</summary>
    internal async Task<TResponse> SendAsync<TResponse>(Func<uint> send, CancellationToken cancellationToken)
        where TResponse : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected to the OpenD trade channel.");
        }

        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serial = send();
        _pending[serial] = tcs;

        using var timeout = new CancellationTokenSource(ReplyTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var registration = linked.Token.Register(() =>
        {
            if (_pending.TryRemove(serial, out var abandoned))
            {
                abandoned.TrySetException(new TimeoutException(
                    $"OpenD did not reply to trade request {serial} within {ReplyTimeout.TotalSeconds}s."));
            }
        });

        var result = await tcs.Task.ConfigureAwait(false);

        return result as TResponse
            ?? throw new InvalidOperationException(
                $"OpenD returned {result.GetType().Name} where {typeof(TResponse).Name} was expected.");
    }

    private void Deliver(uint serial, object response)
    {
        if (_pending.TryRemove(serial, out var tcs))
        {
            tcs.TrySetResult(response);
        }
    }

    /// <inheritdoc />
    public void OnInitConnect(FTAPI_Conn client, long errCode, string desc)
    {
        var ok = errCode == 0;
        IsConnected = ok;

        if (ok)
        {
            _logger.LogInformation("Trade channel connected to OpenD at {Host}:{Port}",
                _options.Host, _options.Port);
        }
        else
        {
            _logger.LogWarning("Trade channel connection failed ({Code}): {Desc}", errCode, desc);
        }

        _connectResult?.TrySetResult(ok);
        ConnectionStateChanged?.Invoke(this, ok);
    }

    /// <inheritdoc />
    public void OnDisconnect(FTAPI_Conn client, long errCode)
    {
        IsConnected = false;

        // The unlock does not survive a reconnect. Leaving this true would let a real order
        // be attempted against a gateway that would reject it - or worse, let the UI claim
        // the account is unlocked when it is not.
        IsUnlocked = false;

        _logger.LogWarning("Trade channel disconnected ({Code})", errCode);

        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException("OpenD trade channel disconnected."));
            }
        }

        ConnectionStateChanged?.Invoke(this, false);
    }

    // ---- Replies this application consumes ----------------------------------

    /// <inheritdoc />
    public void OnReply_GetAccList(FTAPI_Conn c, uint serial, TrdGetAccList.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_GetFunds(FTAPI_Conn c, uint serial, TrdGetFunds.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_GetPositionList(FTAPI_Conn c, uint serial, TrdGetPositionList.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_GetOrderList(FTAPI_Conn c, uint serial, TrdGetOrderList.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_UnlockTrade(FTAPI_Conn c, uint serial, TrdUnlockTrade.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_PlaceOrder(FTAPI_Conn c, uint serial, TrdPlaceOrder.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_ModifyOrder(FTAPI_Conn c, uint serial, TrdModifyOrder.Response rsp) =>
        Deliver(serial, rsp);

    // ---- Push notifications --------------------------------------------------

    /// <summary>Raised when an order's status changes at the broker. Background thread.</summary>
    /// <remarks>
    /// Push rather than reply. An order's life continues after the request that created it
    /// returns - filled, partially filled, cancelled, rejected by the exchange minutes later
    /// - and none of that arrives through <c>PlaceOrder</c>. Anything that shows order state
    /// has to listen here or it will show "submitted" forever.
    /// </remarks>
    public event EventHandler<TrdCommon.Order>? OrderUpdated;

    /// <summary>Raised when a fill is reported. Background thread.</summary>
    public event EventHandler<TrdCommon.OrderFill>? OrderFilled;

    /// <inheritdoc />
    public void OnReply_SubAccPush(FTAPI_Conn c, uint serial, TrdSubAccPush.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_UpdateOrder(FTAPI_Conn c, uint serial, TrdUpdateOrder.Response rsp)
    {
        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            return;
        }

        var order = rsp.S2C.Order;
        _logger.LogInformation(
            "Order update {OrderId} {Code} status {Status} filled {Filled}/{Qty}",
            order.OrderID, order.Code, order.OrderStatus, order.FillQty, order.Qty);

        OrderUpdated?.Invoke(this, order);
    }

    /// <inheritdoc />
    public void OnReply_UpdateOrderFill(FTAPI_Conn c, uint serial, TrdUpdateOrderFill.Response rsp)
    {
        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            return;
        }

        var fill = rsp.S2C.OrderFill;
        _logger.LogInformation("Fill {FillId} {Code} {Qty} @ {Price}",
            fill.FillID, fill.Code, fill.Qty, fill.Price);

        OrderFilled?.Invoke(this, fill);
    }

    // ---- Request helpers -----------------------------------------------------

    /// <summary>
    /// Registers for account push, without which no order update ever arrives.
    /// </summary>
    /// <remarks>
    /// <b>The gateway pushes nothing to a connection that has not subscribed.</b> The
    /// <c>OnReply_UpdateOrder</c> and <c>OnReply_UpdateOrderFill</c> callbacks exist and look
    /// wired, but they are never invoked until this request is sent - so an order tracker
    /// would sit on "Submitted" forever while appearing to be live. A status display that
    /// silently freezes is worse than one that visibly needs refreshing.
    /// </remarks>
    public async Task<bool> SubscribeAccountPushAsync(
        IReadOnlyCollection<ulong> accountIds, CancellationToken cancellationToken = default)
    {
        if (accountIds.Count == 0 || !IsConnected)
        {
            return false;
        }

        var builder = TrdSubAccPush.C2S.CreateBuilder();
        foreach (var id in accountIds)
        {
            builder.AddAccIDList(id);
        }

        try
        {
            var rsp = await SendAsync<TrdSubAccPush.Response>(
                () => _trd.SubAccPush(TrdSubAccPush.Request.CreateBuilder().SetC2S(builder.Build()).Build()),
                cancellationToken).ConfigureAwait(false);

            var ok = rsp.RetType == (int)Common.RetType.RetType_Succeed;

            if (ok)
            {
                _logger.LogInformation("Subscribed to order push for {Count} accounts", accountIds.Count);
            }
            else
            {
                _logger.LogWarning("Order push subscription refused: {Msg}", rsp.RetMsg);
            }

            return ok;
        }
        catch (Exception ex)
        {
            // Not fatal. Orders still place and the tracker still refreshes on demand; only
            // automatic status updates are lost.
            _logger.LogWarning(ex, "Order push subscription failed");
            return false;
        }
    }

    /// <summary>A fresh packet id from the SDK.</summary>
    /// <remarks>
    /// The gateway uses this to reject a resend of the same order, which is what stops a
    /// retry from opening a second position. Minted by the SDK rather than by us so its
    /// connection id is correct. A caller that ever adds automatic retry must reuse an id
    /// rather than ask for a new one.
    /// </remarks>
    internal Common.PacketID NextPacketId() => _trd.NextPacketID();

    internal Task<TrdGetAccList.Response> GetAccountsAsync(TrdGetAccList.Request req, CancellationToken ct) =>
        SendAsync<TrdGetAccList.Response>(() => _trd.GetAccList(req), ct);

    internal Task<TrdGetFunds.Response> GetFundsAsync(TrdGetFunds.Request req, CancellationToken ct) =>
        SendAsync<TrdGetFunds.Response>(() => _trd.GetFunds(req), ct);

    internal Task<TrdGetPositionList.Response> GetPositionsAsync(TrdGetPositionList.Request req, CancellationToken ct) =>
        SendAsync<TrdGetPositionList.Response>(() => _trd.GetPositionList(req), ct);

    internal Task<TrdGetOrderList.Response> GetOrdersAsync(TrdGetOrderList.Request req, CancellationToken ct) =>
        SendAsync<TrdGetOrderList.Response>(() => _trd.GetOrderList(req), ct);

    internal Task<TrdModifyOrder.Response> ModifyOrderAsync(TrdModifyOrder.Request req, CancellationToken ct) =>
        SendAsync<TrdModifyOrder.Response>(() => _trd.ModifyOrder(req), ct);

    /// <summary>
    /// Submits an order. <b>Only ever called from an explicit, confirmed user action.</b>
    /// </summary>
    internal Task<TrdPlaceOrder.Response> PlaceOrderAsync(TrdPlaceOrder.Request req, CancellationToken ct) =>
        SendAsync<TrdPlaceOrder.Response>(() => _trd.PlaceOrder(req), ct);

    /// <summary>
    /// Unlocks trading for this session using the account's trade password.
    /// </summary>
    /// <remarks>
    /// The password is hashed to MD5 before transmission, as the gateway requires, and is
    /// never stored by this application - it is taken as a parameter, used, and dropped. A
    /// caller that wants "remember me" has to make that decision explicitly and visibly
    /// rather than inheriting it from here.
    /// </remarks>
    public async Task<bool> UnlockAsync(string tradePassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tradePassword);

        var md5 = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(tradePassword)))
            .ToLowerInvariant();

        var c2s = TrdUnlockTrade.C2S.CreateBuilder()
            .SetUnlock(true)
            .SetPwdMD5(md5)
            .Build();

        var rsp = await SendAsync<TrdUnlockTrade.Response>(
            () => _trd.UnlockTrade(TrdUnlockTrade.Request.CreateBuilder().SetC2S(c2s).Build()),
            cancellationToken).ConfigureAwait(false);

        IsUnlocked = rsp.RetType == (int)Common.RetType.RetType_Succeed;

        if (!IsUnlocked)
        {
            // The message is logged but not the password, obviously. A wrong password is a
            // normal user error, not an application fault.
            _logger.LogWarning("Trade unlock refused: {Msg}", rsp.RetMsg);
        }

        return IsUnlocked;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsConnected = false;
        IsUnlocked = false;
        _trd.Close();
    }
}
