using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Trading;
using Futu.OpenApi.Pb;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Order routing through the moomoo OpenD trading channel.
/// </summary>
/// <remarks>
/// <b>Nothing here runs on a timer or reacts to a signal.</b> Every method is reached from an
/// explicit, confirmed user action. That is the safety property this class is built around,
/// and it is worth restating at the top of the file because it would be a small edit to break
/// it and a very expensive one to notice.
///
/// <para>Two behaviours were measured against a live gateway on 2026-08-05 and are handled
/// here rather than discovered later:</para>
/// <list type="number">
/// <item><b>Currency is a required parameter</b> on funds and positions - but the gateway only
/// says so for some accounts. Worse, the values differ: one account reported total assets of
/// 28,309 with no currency set and 3,609 with USD. Buying power shown from an unset request is
/// simply a wrong number, and nothing about it looks wrong.</item>
/// <item><b>A gateway exposes accounts that cannot trade options at all</b> - crypto and
/// prediction-market accounts sit in the same list as US securities accounts. They are
/// filtered out rather than offered, because a picker that lets you send an options order to
/// a crypto account is a trap.</item>
/// </list>
/// </remarks>
public sealed class MoomooTradingService : ITradingService
{
    private readonly MoomooTradeConnection _connection;
    private readonly OrderAuditLog _audit;
    private readonly ILogger<MoomooTradingService> _logger;

    /// <inheritdoc />
    public bool IsConnected => _connection.IsConnected;

    /// <inheritdoc />
    public bool IsUnlocked => _connection.IsUnlocked;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public event EventHandler<TradeOrder>? OrderUpdated;

    /// <summary>Creates the service.</summary>
    public MoomooTradingService(
        MoomooTradeConnection connection,
        OrderAuditLog audit,
        ILogger<MoomooTradingService> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connection.ConnectionStateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _connection.OrderUpdated += (_, order) => OrderUpdated?.Invoke(this, MapOrder(order));
    }

    /// <inheritdoc />
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) =>
        _connection.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> UnlockAsync(string tradePassword, CancellationToken cancellationToken = default)
    {
        var ok = await _connection.UnlockAsync(tradePassword, cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TradeAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return [];
        }

        var rsp = await _connection
            .GetAccountsAsync(
                TrdGetAccList.Request.CreateBuilder()
                    .SetC2S(TrdGetAccList.C2S.CreateBuilder()
                        .SetUserID(0)
                        // Defaults to FALSE, and with it unset the gateway omits general
                        // securities accounts entirely - a user with separate cash and margin
                        // accounts sees only one of them, with nothing to indicate the other
                        // exists. Sending an order to the wrong one of a cash/margin pair is
                        // not a recoverable mistake, so both have to be offered by name.
                        .SetNeedGeneralSecAccount(true)
                        .Build())
                    .Build(),
                cancellationToken)
            .ConfigureAwait(false);

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            _logger.LogWarning("Account list failed: {Msg}", rsp.RetMsg);
            return [];
        }

        var results = new List<TradeAccount>();

        foreach (var acc in rsp.S2C.AccListList)
        {
            // US securities only - see the class remarks.
            if (!acc.TrdMarketAuthListList.Contains((int)TrdCommon.TrdMarket.TrdMarket_US))
            {
                continue;
            }

            var env = acc.TrdEnv == (int)TrdCommon.TrdEnv.TrdEnv_Simulate
                ? TradeEnvironment.Paper
                : TradeEnvironment.Live;

            var (cash, power) = await FetchFundsAsync(acc, cancellationToken).ConfigureAwait(false);

            // Only an ACTIVE account can trade. A disabled one is left out of the picker
            // entirely rather than shown greyed: it can never accept an order under any
            // circumstance, so it is pure noise in a list where choosing the wrong row sends
            // money somewhere unintended. Logged rather than silently dropped, so an account
            // vanishing from the list is still explainable.
            if (acc.AccStatus != (int)TrdCommon.TrdAccStatus.TrdAccStatus_Active)
            {
                _logger.LogInformation(
                    "Account {AccId} excluded - broker reports {Status}",
                    acc.AccID, (TrdCommon.TrdAccStatus)acc.AccStatus);
                continue;
            }

            // Retained on the model even though the list is now pre-filtered. An account can
            // be disabled between this call and an order being sent, and OrderRequest.Validate
            // is the last gate before money moves - it should not depend on a stale list.
            const bool usable = true;

            // Cash and margin are named, not left to the account number. They behave
            // differently and a user with both needs to see which one a ticket is aimed at
            // without cross-referencing an id against their broker statement.
            var kind = (TrdCommon.TrdAccType)acc.AccType switch
            {
                TrdCommon.TrdAccType.TrdAccType_Cash => "CASH",
                TrdCommon.TrdAccType.TrdAccType_Margin => "MARGIN",
                TrdCommon.TrdAccType.TrdAccType_Derivatives => "DERIVATIVES",
                TrdCommon.TrdAccType.TrdAccType_RRSP => "RRSP",
                TrdCommon.TrdAccType.TrdAccType_TFSA => "TFSA",
                TrdCommon.TrdAccType.TrdAccType_SRRSP => "SRRSP",
                _ => "UNKNOWN"
            };

            var label = $"{(env == TradeEnvironment.Live ? "LIVE" : "PAPER")} · {kind} · {acc.AccID}";

            results.Add(new TradeAccount(acc.AccID, env, label, cash, power, usable));
        }

        // Register for order push now that the account ids are known. Without this the
        // gateway sends no updates at all and the tracker would show every order as
        // Submitted indefinitely while looking live.
        await _connection
            .SubscribeAccountPushAsync(results.Select(a => a.AccountId).ToList(), cancellationToken)
            .ConfigureAwait(false);

        // Paper first, so the safe option is the one nearest the top of any picker.
        return results
            .OrderBy(a => a.Environment == TradeEnvironment.Live)
            .ThenBy(a => a.AccountId)
            .ToList();
    }

    private async Task<(decimal Cash, decimal Power)> FetchFundsAsync(
        TrdCommon.TrdAcc acc, CancellationToken ct)
    {
        try
        {
            var header = HeaderFor(acc.AccID, acc.TrdEnv);

            var rsp = await _connection
                .GetFundsAsync(
                    TrdGetFunds.Request.CreateBuilder()
                        .SetC2S(TrdGetFunds.C2S.CreateBuilder()
                            .SetHeader(header)
                            // Required, and load-bearing - see the class remarks.
                            .SetCurrency((int)TrdCommon.Currency.Currency_USD)
                            .Build())
                        .Build(),
                    ct)
                .ConfigureAwait(false);

            return rsp.RetType == (int)Common.RetType.RetType_Succeed
                ? ((decimal)rsp.S2C.Funds.Cash, (decimal)rsp.S2C.Funds.Power)
                : (0m, 0m);
        }
        catch (Exception ex)
        {
            // A missing balance must not remove the account from the list - the user may
            // still want to trade it, and a zero shown beside it is honest.
            _logger.LogWarning(ex, "Funds unavailable for {AccId}", acc.AccID);
            return (0m, 0m);
        }
    }

    /// <inheritdoc />
    public async Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Validate() is { } invalid)
        {
            return OrderResult.Fail(invalid);
        }

        if (!IsConnected)
        {
            return OrderResult.Fail("Not connected to the broker.");
        }

        // Live orders need the session unlocked. Checked here as well as in the UI, because
        // this is the last point before money moves and a UI-only gate is one refactor away
        // from being bypassed.
        if (request.Account.Environment == TradeEnvironment.Live && !IsUnlocked)
        {
            return OrderResult.Fail("Trading is locked. Unlock with your trade password before placing live orders.");
        }

        // Written BEFORE the request is sent - see OrderAuditLog. An order that goes out and
        // then loses the connection is exactly the case the record exists for.
        await _audit.WriteAsync(OrderAuditLog.From(request, "SUBMIT"), cancellationToken).ConfigureAwait(false);

        try
        {
            var c2s = TrdPlaceOrder.C2S.CreateBuilder()
                .SetPacketID(_connection.NextPacketId())
                .SetHeader(HeaderFor(request.Account))
                .SetTrdSide((int)(request.Side == OrderSide.Buy
                    ? TrdCommon.TrdSide.TrdSide_Buy
                    : TrdCommon.TrdSide.TrdSide_Sell))
                .SetOrderType((int)(request.Pricing == OrderPricing.Limit
                    ? TrdCommon.OrderType.OrderType_Normal
                    : TrdCommon.OrderType.OrderType_Market))
                .SetCode(request.ContractCode)
                .SetQty(request.Quantity)
                .SetPrice((double)(request.LimitPrice ?? 0m))
                .SetSecMarket((int)TrdCommon.TrdSecMarket.TrdSecMarket_US)
                .Build();

            var rsp = await _connection
                .PlaceOrderAsync(TrdPlaceOrder.Request.CreateBuilder().SetC2S(c2s).Build(), cancellationToken)
                .ConfigureAwait(false);

            if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
            {
                await _audit
                    .WriteAsync(OrderAuditLog.From(request, "REJECTED", result: rsp.RetMsg), cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogWarning("Order rejected for {Contract}: {Msg}", request.ContractCode, rsp.RetMsg);
                return OrderResult.Fail(rsp.RetMsg);
            }

            var orderId = rsp.S2C.OrderID;

            await _audit
                .WriteAsync(OrderAuditLog.From(request, "ACCEPTED", orderId, "accepted"), cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Order {OrderId} placed: {Side} {Qty} {Contract} on {Env} account {AccId}",
                orderId, request.Side, request.Quantity, request.ContractCode,
                request.Account.Environment, request.Account.AccountId);

            return OrderResult.Ok(orderId);
        }
        catch (Exception ex)
        {
            await _audit
                .WriteAsync(OrderAuditLog.From(request, "FAILED", result: ex.Message), cancellationToken)
                .ConfigureAwait(false);

            _logger.LogError(ex, "Order submission threw for {Contract}", request.ContractCode);
            return OrderResult.Fail(ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<OrderResult> CancelOrderAsync(
        TradeAccount account, ulong orderId, CancellationToken cancellationToken = default) =>
        ModifyAsync(account, orderId, TrdCommon.ModifyOrderOp.ModifyOrderOp_Cancel, 0, 0m, "CANCEL", cancellationToken);

    /// <inheritdoc />
    public Task<OrderResult> ModifyOrderAsync(
        TradeAccount account, ulong orderId, int quantity, decimal limitPrice,
        CancellationToken cancellationToken = default) =>
        ModifyAsync(account, orderId, TrdCommon.ModifyOrderOp.ModifyOrderOp_Normal, quantity, limitPrice, "MODIFY", cancellationToken);

    private async Task<OrderResult> ModifyAsync(
        TradeAccount account, ulong orderId, TrdCommon.ModifyOrderOp op,
        int quantity, decimal price, string action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!IsConnected)
        {
            return OrderResult.Fail("Not connected to the broker.");
        }

        if (account.Environment == TradeEnvironment.Live && !IsUnlocked)
        {
            return OrderResult.Fail("Trading is locked. Unlock with your trade password first.");
        }

        await _audit.WriteAsync(new OrderAuditRecord
        {
            Action = action,
            Timestamp = DateTimeOffset.Now,
            AccountId = account.AccountId,
            Environment = account.Environment == TradeEnvironment.Live ? "LIVE" : "PAPER",
            ContractCode = "-",
            Side = "-",
            Quantity = quantity,
            Pricing = "-",
            LimitPrice = price > 0 ? price : null,
            OrderId = orderId
        }, ct).ConfigureAwait(false);

        try
        {
            var builder = TrdModifyOrder.C2S.CreateBuilder()
                .SetPacketID(_connection.NextPacketId())
                .SetHeader(HeaderFor(account))
                .SetOrderID(orderId)
                .SetModifyOrderOp((int)op);

            if (op == TrdCommon.ModifyOrderOp.ModifyOrderOp_Normal)
            {
                builder.SetQty(quantity).SetPrice((double)price);
            }

            var rsp = await _connection
                .ModifyOrderAsync(TrdModifyOrder.Request.CreateBuilder().SetC2S(builder.Build()).Build(), ct)
                .ConfigureAwait(false);

            if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
            {
                _logger.LogWarning("{Action} refused for order {OrderId}: {Msg}", action, orderId, rsp.RetMsg);
                return OrderResult.Fail(rsp.RetMsg);
            }

            _logger.LogInformation("{Action} accepted for order {OrderId}", action, orderId);
            return OrderResult.Ok(orderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Action} threw for order {OrderId}", action, orderId);
            return OrderResult.Fail(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TradeOrder>> GetOrdersAsync(
        TradeAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!IsConnected)
        {
            return [];
        }

        var rsp = await _connection
            .GetOrdersAsync(
                TrdGetOrderList.Request.CreateBuilder()
                    .SetC2S(TrdGetOrderList.C2S.CreateBuilder().SetHeader(HeaderFor(account)).Build())
                    .Build(),
                cancellationToken)
            .ConfigureAwait(false);

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            _logger.LogWarning("Order list failed: {Msg}", rsp.RetMsg);
            return [];
        }

        return rsp.S2C.OrderListList
            .Select(o => MapOrder(o, account))
            .OrderByDescending(o => o.SubmittedAt)
            .ToList();
    }

    // ---- Mapping -------------------------------------------------------------

    private static TrdCommon.TrdHeader HeaderFor(TradeAccount account) =>
        HeaderFor(
            account.AccountId,
            (int)(account.Environment == TradeEnvironment.Live
                ? TrdCommon.TrdEnv.TrdEnv_Real
                : TrdCommon.TrdEnv.TrdEnv_Simulate));

    private static TrdCommon.TrdHeader HeaderFor(ulong accountId, int trdEnv) =>
        TrdCommon.TrdHeader.CreateBuilder()
            .SetTrdEnv(trdEnv)
            .SetAccID(accountId)
            .SetTrdMarket((int)TrdCommon.TrdMarket.TrdMarket_US)
            .Build();

    private TradeOrder MapOrder(TrdCommon.Order order, TradeAccount? account = null) => new()
    {
        OrderId = order.OrderID,
        ContractCode = order.Code,
        Side = order.TrdSide == (int)TrdCommon.TrdSide.TrdSide_Sell ? OrderSide.Sell : OrderSide.Buy,
        State = MapState(order.OrderStatus),
        Quantity = (int)order.Qty,
        FilledQuantity = (int)order.FillQty,
        Price = (decimal)order.Price,
        AverageFillPrice = order.FillQty > 0 ? (decimal)order.FillAvgPrice : null,
        Message = string.IsNullOrWhiteSpace(order.LastErrMsg) ? null : order.LastErrMsg,
        SubmittedAt = MoomooMapping.ParseExchangeTime(order.CreateTime),
        AccountId = account?.AccountId ?? 0,
        Environment = account?.Environment ?? TradeEnvironment.Paper
    };

    /// <summary>Maps a broker status onto the domain state.</summary>
    /// <remarks>
    /// Anything unrecognised becomes <see cref="OrderState.Unknown"/> rather than being
    /// guessed at. Showing an unmapped status as "Working" would be a lie and as "Filled" a
    /// dangerous one; saying it is unknown sends the user to the broker, which is right.
    /// </remarks>
    private static OrderState MapState(int status) => (TrdCommon.OrderStatus)status switch
    {
        TrdCommon.OrderStatus.OrderStatus_Submitting or
        TrdCommon.OrderStatus.OrderStatus_Submitted or
        TrdCommon.OrderStatus.OrderStatus_WaitingSubmit => OrderState.Submitted,

        TrdCommon.OrderStatus.OrderStatus_Filled_Part => OrderState.PartiallyFilled,
        TrdCommon.OrderStatus.OrderStatus_Filled_All => OrderState.Filled,

        TrdCommon.OrderStatus.OrderStatus_Cancelled_Part or
        TrdCommon.OrderStatus.OrderStatus_Cancelled_All or
        TrdCommon.OrderStatus.OrderStatus_Cancelling_Part or
        TrdCommon.OrderStatus.OrderStatus_Cancelling_All => OrderState.Cancelled,

        TrdCommon.OrderStatus.OrderStatus_Failed or
        TrdCommon.OrderStatus.OrderStatus_Disabled or
        TrdCommon.OrderStatus.OrderStatus_Deleted or
        TrdCommon.OrderStatus.OrderStatus_SubmitFailed => OrderState.Rejected,

        _ => OrderState.Unknown
    };
}
