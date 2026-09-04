using System.IO;
using System.Text;
using BreakoutAlerts.Core.Configuration;
using Futu.OpenApi.Pb;
using Microsoft.Extensions.Logging.Abstractions;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Read-only inspection of the trading channel, run with <c>--probe-trade out.txt</c>.
/// </summary>
/// <remarks>
/// <b>Nothing here can place, modify or cancel an order.</b> It lists accounts, funds and
/// open positions and stops. That restriction is the point: the questions this answers -
/// which accounts exist, whether a paper account is among them, whether funds and positions
/// come back in the shape the code expects - all need answering before any order-placing code
/// is written, and none of them require sending an order to answer.
///
/// <para>Same reasoning as the quote-side probe in <see cref="LiveDataProbe"/>, which found
/// three shipping bugs on its first run. The trading API is the half of this application
/// where a wrong assumption costs money rather than a redraw.</para>
/// </remarks>
public static class TradeProbe
{
    /// <summary>Connects, enumerates accounts and reports what each one holds.</summary>
    public static async Task RunAsync(string outputPath)
    {
        var report = new StringBuilder();
        var options = new MarketDataOptions();

        void Line(string text = "")
        {
            report.AppendLine(text);
        }

        Line($"TRADE PROBE (READ ONLY) - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line(new string('=', 72));

        using var connection = new MoomooTradeConnection(options, NullLogger<MoomooTradeConnection>.Instance);

        try
        {
            if (!await connection.ConnectAsync().ConfigureAwait(false))
            {
                Line($"FAILED: no trade channel at {options.Host}:{options.Port}.");
                await File.WriteAllTextAsync(outputPath, report.ToString()).ConfigureAwait(false);
                return;
            }

            Line($"Connected to {options.Host}:{options.Port}");
            Line();

            var accounts = await ListAccountsAsync(connection, Line).ConfigureAwait(false);

            foreach (var account in accounts)
            {
                await DescribeAccountAsync(connection, account, Line).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Line();
            Line($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Line(ex.StackTrace ?? string.Empty);
        }

        await File.WriteAllTextAsync(outputPath, report.ToString()).ConfigureAwait(false);
    }

    private static async Task<List<TrdCommon.TrdAcc>> ListAccountsAsync(
        MoomooTradeConnection connection, Action<string> line)
    {
        line("---- ACCOUNTS " + new string('-', 58));

        // Without this the gateway omits general securities accounts, so a user with separate
        // cash and margin accounts sees only one of them.
        var c2s = TrdGetAccList.C2S.CreateBuilder()
            .SetUserID(0)
            .SetNeedGeneralSecAccount(true)
            .Build();

        var rsp = await connection
            .GetAccountsAsync(TrdGetAccList.Request.CreateBuilder().SetC2S(c2s).Build(), default)
            .ConfigureAwait(false);

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            line($"FAILED: {rsp.RetMsg}");
            line(string.Empty);
            return [];
        }

        var accounts = rsp.S2C.AccListList.ToList();
        line($"{accounts.Count} accounts");

        foreach (var acc in accounts)
        {
            var env = acc.TrdEnv == (int)TrdCommon.TrdEnv.TrdEnv_Simulate ? "SIMULATE" : "REAL";

            // Market codes printed as names. The raw integers are meaningless on sight and
            // picking the wrong one silently targets the wrong account.
            var markets = string.Join(",", acc.TrdMarketAuthListList.Select(m => ((TrdCommon.TrdMarket)m).ToString()));

            line($"    {acc.AccID,-20} {env,-9} {(TrdCommon.TrdAccType)acc.AccType,-24} " +
                 $"status {(TrdCommon.TrdAccStatus)acc.AccStatus}");
            line($"        markets: {markets}");

            // The brokerage entity, which the unlock request has to name. One gateway can
            // serve several, each with its own trade password, so an unlock that does not say
            // which one is being unlocked names no password to check and is refused in a way
            // that reads exactly like a wrong password.
            line($"        securityFirm: " +
                 (acc.HasSecurityFirm
                     ? $"{(TrdCommon.SecurityFirm)acc.SecurityFirm} ({acc.SecurityFirm})"
                     : "NOT REPORTED"));
        }

        // The paper account is what makes a full rehearsal possible without risking money,
        // so its absence changes the plan rather than being a detail.
        var simulate = accounts.Count(a => a.TrdEnv == (int)TrdCommon.TrdEnv.TrdEnv_Simulate);
        line(simulate > 0
            ? $"    -> {simulate} paper account(s) available."
            : "    -> NO PAPER ACCOUNT. Order routing cannot be rehearsed without real money.");

        line(string.Empty);
        return accounts;
    }

    private static async Task DescribeAccountAsync(
        MoomooTradeConnection connection, TrdCommon.TrdAcc account, Action<string> line)
    {
        var env = account.TrdEnv == (int)TrdCommon.TrdEnv.TrdEnv_Simulate ? "SIMULATE" : "REAL";
        line($"---- {account.AccID} ({env}) " + new string('-', Math.Max(4, 48 - env.Length)));

        var header = TrdCommon.TrdHeader.CreateBuilder()
            .SetTrdEnv(account.TrdEnv)
            .SetAccID(account.AccID)
            .SetTrdMarket((int)TrdCommon.TrdMarket.TrdMarket_US)
            .Build();

        try
        {
            // Currency is required, and the gateway says so only for some accounts - the
            // first probe run got "missing required parameter currency" from one account
            // while another answered fine without it. Always sending it removes the
            // inconsistency rather than depending on which account is asked.
            var fundsRsp = await connection
                .GetFundsAsync(
                    TrdGetFunds.Request.CreateBuilder()
                        .SetC2S(TrdGetFunds.C2S.CreateBuilder()
                            .SetHeader(header)
                            .SetCurrency((int)TrdCommon.Currency.Currency_USD)
                            .Build())
                        .Build(),
                    default)
                .ConfigureAwait(false);

            if (fundsRsp.RetType == (int)Common.RetType.RetType_Succeed)
            {
                var f = fundsRsp.S2C.Funds;
                line($"    cash {f.Cash:N2}  power {f.Power:N2}  assets {f.TotalAssets:N2}  " +
                     $"marketVal {f.MarketVal:N2}  currency {f.Currency}");
            }
            else
            {
                line($"    funds unavailable: {fundsRsp.RetMsg}");
            }
        }
        catch (Exception ex)
        {
            line($"    funds threw: {ex.Message}");
        }

        try
        {
            var posRsp = await connection
                .GetPositionsAsync(
                    TrdGetPositionList.Request.CreateBuilder()
                        .SetC2S(TrdGetPositionList.C2S.CreateBuilder()
                            .SetHeader(header)
                            .SetCurrency((int)TrdCommon.Currency.Currency_USD)
                            .Build())
                        .Build(),
                    default)
                .ConfigureAwait(false);

            if (posRsp.RetType == (int)Common.RetType.RetType_Succeed)
            {
                var positions = posRsp.S2C.PositionListList;
                line($"    {positions.Count} open positions");

                foreach (var p in positions.Take(10))
                {
                    line($"        {p.Code,-24} qty {p.Qty,8}  cost {p.CostPrice,10:N2}  " +
                         $"val {p.Val,12:N2}  pl {p.PlVal,10:N2}");
                }
            }
            else
            {
                line($"    positions unavailable: {posRsp.RetMsg}");
            }
        }
        catch (Exception ex)
        {
            line($"    positions threw: {ex.Message}");
        }

        try
        {
            var ordRsp = await connection
                .GetOrdersAsync(
                    TrdGetOrderList.Request.CreateBuilder()
                        .SetC2S(TrdGetOrderList.C2S.CreateBuilder().SetHeader(header).Build())
                        .Build(),
                    default)
                .ConfigureAwait(false);

            if (ordRsp.RetType == (int)Common.RetType.RetType_Succeed)
            {
                line($"    {ordRsp.S2C.OrderListList.Count} orders today");
            }
            else
            {
                line($"    orders unavailable: {ordRsp.RetMsg}");
            }
        }
        catch (Exception ex)
        {
            line($"    orders threw: {ex.Message}");
        }

        line(string.Empty);
    }
}
