using Futu.OpenApi;
using Futu.OpenApi.Pb;

namespace BreakoutAlerts.OpenDProbe;

/// <summary>
/// One-shot diagnostic against a running moomoo OpenD gateway.
/// </summary>
/// <remarks>
/// Answers the question everything in Phase 3 rests on: <b>does this account actually get
/// real-time data through OpenD, or delayed?</b> API entitlements are not the same as app
/// entitlements - moomoo's own docs warn that some data packages apply only to the app
/// side - so seeing live quotes in the moomoo app proves nothing about what the gateway
/// hands this program.
///
/// <para>It matters because the chart popup exists for decision speed. A chart fed
/// 15-minute-delayed bars looks completely live and is worse than no chart at all, and
/// nothing in the UI would reveal it.</para>
///
/// <para>Read-only: quote requests only. This never opens the trading context, so it
/// cannot place, modify or cancel anything.</para>
/// </remarks>
internal sealed partial class Program : FTSPI_Qot, FTSPI_Conn
{
    private const string Host = "127.0.0.1";
    private const ushort Port = 11111;

    /// <summary>A liquid US name, so a thin book cannot be mistaken for a stale feed.</summary>
    private const string TestSymbol = "AAPL";

    private readonly FTAPI_Qot _qot = new();
    private readonly TaskCompletionSource<bool> _finished = new();
    private int _pendingReplies;

    private static async Task<int> Main()
    {
        Console.WriteLine("moomoo OpenD probe");
        Console.WriteLine("==================");
        Console.WriteLine($"Target     : {Host}:{Port}");
        Console.WriteLine($"Local time : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine();

        return await new Program().RunAsync();
    }

    private async Task<int> RunAsync()
    {
        FTAPI.Init();

        _qot.SetClientInfo("BreakoutAlertsProbe", 1);
        _qot.SetConnCallback(this);
        _qot.SetQotCallback(this);
        _qot.InitConnect(Host, Port, false);

        // Hard ceiling. If OpenD is not running, the connect callback never fires - and a
        // diagnostic that hangs forever is useless.
        var completed = await Task.WhenAny(_finished.Task, Task.Delay(TimeSpan.FromSeconds(25)));

        if (completed != _finished.Task)
        {
            Console.WriteLine();
            Console.WriteLine("TIMED OUT after 25s.");
            Console.WriteLine("Most likely OpenD is not running, or is not listening on this port.");
            Console.WriteLine(@"Start it from: %APPDATA%\moomoo_OpenD\moomoo_OpenD.exe");
            return 2;
        }

        Console.WriteLine();

        // Non-zero on a failed connect. A diagnostic that reports failure on screen but
        // exits 0 will silently "pass" the moment anyone wraps it in a script.
        if (!_finished.Task.Result)
        {
            Console.WriteLine("Probe FAILED - see the error above.");
            Console.WriteLine(@"If the connection was refused, start %APPDATA%\moomoo_OpenD\moomoo_OpenD.exe and log in first.");
            return 1;
        }

        Console.WriteLine("Probe complete.");
        return 0;
    }

    /// <inheritdoc />
    public void OnInitConnect(FTAPI_Conn client, long errCode, string desc)
    {
        if (errCode != 0)
        {
            Console.WriteLine($"CONNECT FAILED  errCode={errCode}  {desc}");
            _finished.TrySetResult(false);
            return;
        }

        Console.WriteLine("Connected to OpenD.");
        Console.WriteLine();

        RequestSnapshot();
        RequestHistory();
        RequestOptionChain();

        // History returned zero bars on the first live run. These three narrow down why:
        // a wider window separates "history is broken" from "today is not covered", the
        // quota check separates a permission problem from an exhausted allowance, and the
        // subscribe path is the documented route to current-session intraday bars.
        RequestHistoryQuota();
        RequestHistoryWideWindow();
        SubscribeForLiveKline();

        // Phase 3 design questions: can the gateway filter the option chain by delta
        // server-side, do chain entries carry Greeks, and what is the subscription budget.
        RequestFilteredOptionChain();
        RequestSubInfo();
    }

    /// <inheritdoc />
    public void OnDisconnect(FTAPI_Conn client, long errCode)
    {
        Console.WriteLine($"Disconnected (errCode={errCode}).");
        _finished.TrySetResult(false);
    }

    private static QotCommon.Security UsSecurity(string code) =>
        QotCommon.Security.CreateBuilder()
            .SetMarket((int)QotCommon.QotMarket.QotMarket_US_Security)
            .SetCode(code)
            .Build();

    // ---- Snapshot: the real-time-vs-delayed answer --------------------------

    private void RequestSnapshot()
    {
        var c2s = QotGetSecuritySnapshot.C2S.CreateBuilder()
            .AddSecurityList(UsSecurity(TestSymbol))
            .Build();

        var req = QotGetSecuritySnapshot.Request.CreateBuilder().SetC2S(c2s).Build();

        Interlocked.Increment(ref _pendingReplies);
        _qot.GetSecuritySnapshot(req);
    }

    /// <inheritdoc />
    public void OnReply_GetSecuritySnapshot(FTAPI_Conn client, uint nSerialNo, QotGetSecuritySnapshot.Response rsp)
    {
        Console.WriteLine("--- SNAPSHOT ---");

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            Console.WriteLine($"  FAILED: {rsp.RetMsg}");
            Console.WriteLine("  A permission error here means quote entitlements are not granted to OpenD.");
        }
        else if (rsp.S2C.SnapshotListCount == 0)
        {
            Console.WriteLine("  No snapshot returned.");
        }
        else
        {
            var basic = rsp.S2C.SnapshotListList[0].Basic;

            // UpdateTime is the exchange-side timestamp. Its distance from local time is
            // the delay, and is the entire point of this probe.
            Console.WriteLine($"  Symbol      : {basic.Security.Code}");
            Console.WriteLine($"  Last price  : {basic.CurPrice}");
            Console.WriteLine($"  Update time : {basic.UpdateTime}");

            if (DateTime.TryParse(basic.UpdateTime, out var updated))
            {
                var lag = DateTime.Now - updated;
                Console.WriteLine($"  Lag vs local: {lag.TotalMinutes:F1} minutes");
                Console.WriteLine(lag.TotalMinutes switch
                {
                    < 2 => "  => REAL-TIME. Good enough for the decision-speed chart.",
                    < 25 => "  => LOOKS DELAYED (~15 min is the classic tier). Verify before relying on it.",
                    _ => "  => Stale, or the market is closed. Re-run during regular hours to be sure."
                });
            }
        }

        Console.WriteLine();
        Complete();
    }

    // ---- History: what the chart popup will actually draw --------------------

    private void RequestHistory()
    {
        var c2s = QotRequestHistoryKL.C2S.CreateBuilder()
            .SetSecurity(UsSecurity(TestSymbol))
            .SetKlType((int)QotCommon.KLType.KLType_5Min)
            .SetRehabType((int)QotCommon.RehabType.RehabType_Forward)
            .SetBeginTime(DateTime.Today.ToString("yyyy-MM-dd"))
            .SetEndTime(DateTime.Today.ToString("yyyy-MM-dd"))
            .SetMaxAckKLNum(200)
            .Build();

        var req = QotRequestHistoryKL.Request.CreateBuilder().SetC2S(c2s).Build();

        Interlocked.Increment(ref _pendingReplies);
        _qot.RequestHistoryKL(req);
    }

    /// <inheritdoc />
    public void OnReply_RequestHistoryKL(FTAPI_Conn client, uint nSerialNo, QotRequestHistoryKL.Response rsp)
    {
        var isWide = nSerialNo == _wideWindowSerial;
        Console.WriteLine(isWide
            ? "--- HISTORY (5-min bars, last 7 days) ---"
            : "--- HISTORY (5-min bars, today) ---");

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            Console.WriteLine($"  FAILED: {rsp.RetMsg}");
        }
        else
        {
            var bars = rsp.S2C.KlListCount;
            Console.WriteLine($"  Bars returned: {bars}");

            if (bars > 0)
            {
                // First few printed, not just the extremes: the earliest timestamps of a
                // day reveal both whether premarket is included and whether the API stamps
                // bars with their OPEN or CLOSE time - which decides whether the 09:30-09:45
                // window captures three bars or two.
                Console.WriteLine("  Earliest bars:");
                foreach (var k in rsp.S2C.KlListList.Take(4))
                {
                    Console.WriteLine($"    {k.Time}  O={k.OpenPrice} C={k.ClosePrice}");
                }

                var last = rsp.S2C.KlListList[bars - 1];
                Console.WriteLine($"  Last bar  : {last.Time}  O={last.OpenPrice} C={last.ClosePrice}");
            }
        }

        Console.WriteLine();
        Complete();
    }

    // ---- History diagnostics -------------------------------------------------

    private void RequestHistoryQuota()
    {
        var req = QotRequestHistoryKLQuota.Request.CreateBuilder()
            .SetC2S(QotRequestHistoryKLQuota.C2S.CreateBuilder().SetBGetDetail(false).Build())
            .Build();

        Interlocked.Increment(ref _pendingReplies);
        _qot.RequestHistoryKLQuota(req);
    }

    /// <inheritdoc />
    public void OnReply_RequestHistoryKLQuota(FTAPI_Conn client, uint nSerialNo, QotRequestHistoryKLQuota.Response rsp)
    {
        Console.WriteLine("--- HISTORY QUOTA ---");

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            Console.WriteLine($"  FAILED: {rsp.RetMsg}");
        }
        else
        {
            Console.WriteLine($"  Used  : {rsp.S2C.UsedQuota}");
            Console.WriteLine($"  Left  : {rsp.S2C.RemainQuota}");
            Console.WriteLine(rsp.S2C.RemainQuota <= 0
                ? "  => EXHAUSTED. That alone would explain zero bars."
                : "  => Quota available, so an empty history is not a quota problem.");
        }

        Console.WriteLine();
        Complete();
    }

    /// <summary>Same request over a week, to tell a broken call from an uncovered date.</summary>
    private void RequestHistoryWideWindow()
    {
        var c2s = QotRequestHistoryKL.C2S.CreateBuilder()
            .SetSecurity(UsSecurity(TestSymbol))
            .SetKlType((int)QotCommon.KLType.KLType_5Min)
            .SetRehabType((int)QotCommon.RehabType.RehabType_None)
            .SetBeginTime(DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd"))
            .SetEndTime(DateTime.Today.ToString("yyyy-MM-dd"))
            .SetMaxAckKLNum(1000)
            // Without this the feed returns regular-session bars only. The ORB strategy
            // needs premarket highs and lows for PATH-3, so this flag is not optional.
            .SetExtendedTime(true)
            .Build();

        var req = QotRequestHistoryKL.Request.CreateBuilder().SetC2S(c2s).Build();

        Interlocked.Increment(ref _pendingReplies);
        _wideWindowSerial = _qot.RequestHistoryKL(req);
    }

    private uint _wideWindowSerial;

    /// <summary>Subscribes to 5-minute K-line, the documented route to current-session bars.</summary>
    private void SubscribeForLiveKline()
    {
        var c2s = QotSub.C2S.CreateBuilder()
            .AddSecurityList(UsSecurity(TestSymbol))
            .AddSubTypeList((int)QotCommon.SubType.SubType_KL_5Min)
            .SetIsSubOrUnSub(true)
            .SetIsRegOrUnRegPush(false)
            .Build();

        var req = QotSub.Request.CreateBuilder().SetC2S(c2s).Build();

        Interlocked.Increment(ref _pendingReplies);
        _qot.Sub(req);
    }

    /// <inheritdoc />
    public void OnReply_Sub(FTAPI_Conn client, uint nSerialNo, QotSub.Response rsp)
    {
        Console.WriteLine("--- SUBSCRIBE (5-min K-line) ---");

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            Console.WriteLine($"  FAILED: {rsp.RetMsg}");
            Console.WriteLine();
            Complete();
            return;
        }

        Console.WriteLine("  Subscribed. Requesting current-session bars via GetKL...");
        Console.WriteLine();

        var c2s = QotGetKL.C2S.CreateBuilder()
            .SetRehabType((int)QotCommon.RehabType.RehabType_None)
            .SetKlType((int)QotCommon.KLType.KLType_5Min)
            .SetSecurity(UsSecurity(TestSymbol))
            .SetReqNum(100)
            .Build();

        // Incremented before Complete() below, so the probe waits for the GetKL reply too.
        Interlocked.Increment(ref _pendingReplies);
        _qot.GetKL(QotGetKL.Request.CreateBuilder().SetC2S(c2s).Build());

        Complete();
    }

    /// <inheritdoc />
    public void OnReply_GetKL(FTAPI_Conn client, uint nSerialNo, QotGetKL.Response rsp)
    {
        Console.WriteLine("--- GetKL (subscribed, current session) ---");

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            Console.WriteLine($"  FAILED: {rsp.RetMsg}");
        }
        else
        {
            var bars = rsp.S2C.KlListCount;
            Console.WriteLine($"  Bars returned: {bars}");

            if (bars > 0)
            {
                var last = rsp.S2C.KlListList[bars - 1];
                Console.WriteLine($"  Last bar : {last.Time}  O={last.OpenPrice} H={last.HighPrice} L={last.LowPrice} C={last.ClosePrice}");
                Console.WriteLine("  => THIS is the path the chart popup should use for today's bars.");
            }
            else
            {
                Console.WriteLine("  => Still empty. Intraday bars are not reaching this account.");
            }
        }

        Console.WriteLine();
        Complete();
    }

    // ---- Option chain: the entitlement most likely to be missing -------------

    private void RequestOptionChain()
    {
        var c2s = QotGetOptionChain.C2S.CreateBuilder()
            .SetOwner(UsSecurity(TestSymbol))
            .SetBeginTime(DateTime.Today.ToString("yyyy-MM-dd"))
            .SetEndTime(DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"))
            .Build();

        var req = QotGetOptionChain.Request.CreateBuilder().SetC2S(c2s).Build();

        Interlocked.Increment(ref _pendingReplies);
        _qot.GetOptionChain(req);
    }

    /// <inheritdoc />
    public void OnReply_GetOptionChain(FTAPI_Conn client, uint nSerialNo, QotGetOptionChain.Response rsp)
    {
        var isFiltered = nSerialNo == _filteredChainSerial;
        Console.WriteLine(isFiltered
            ? "--- OPTION CHAIN (server-side delta filter 0.60-0.70) ---"
            : "--- OPTION CHAIN (unfiltered) ---");

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            Console.WriteLine($"  FAILED: {rsp.RetMsg}");
            Console.WriteLine("  US options are excluded from the free real-time equity tier, so a");
            Console.WriteLine("  permission error here is the expected failure - and the one that matters");
            Console.WriteLine("  most, because the candidates pane depends on this data.");
        }
        else
        {
            var expiries = rsp.S2C.OptionChainCount;
            Console.WriteLine($"  Expiries returned: {expiries}");

            var totalContracts = rsp.S2C.OptionChainList.Sum(c => c.OptionCount);
            Console.WriteLine($"  Total contracts  : {totalContracts}");

            if (isFiltered)
            {
                Console.WriteLine(totalContracts > 0
                    ? "  => Compare against the unfiltered count above. A much smaller number means"
                    : "  => Filter returned nothing - either unsupported, or no contract is in band.");
                Console.WriteLine("     the delta band costs ONE request per underlying.");
            }
            else if (expiries > 0)
            {
                Console.WriteLine("  => Option data IS available to OpenD on this account.");
            }
        }

        Console.WriteLine();
        Complete();
    }

    // ---- Phase 3 design probes ----------------------------------------------

    private uint _filteredChainSerial;

    /// <summary>
    /// Asks for the option chain narrowed to the 0.60-0.70 delta band.
    /// </summary>
    /// <remarks>
    /// This single answer decides the architecture of the candidates pane. If the gateway
    /// filters server-side, the band costs one request per underlying. If it does not, the
    /// whole chain has to come back and be filtered locally - and if Greeks additionally
    /// require a snapshot per contract, that is roughly a thousand requests per underlying,
    /// which is not viable and forces a completely different approach.
    /// </remarks>
    private void RequestFilteredOptionChain()
    {
        var filter = QotGetOptionChain.DataFilter.CreateBuilder()
            .SetDeltaMin(0.60)
            .SetDeltaMax(0.70)
            .Build();

        var c2s = QotGetOptionChain.C2S.CreateBuilder()
            .SetOwner(UsSecurity(TestSymbol))
            .SetBeginTime(DateTime.Today.ToString("yyyy-MM-dd"))
            .SetEndTime(DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"))
            .SetDataFilter(filter)
            .Build();

        var req = QotGetOptionChain.Request.CreateBuilder().SetC2S(c2s).Build();

        Interlocked.Increment(ref _pendingReplies);
        _filteredChainSerial = _qot.GetOptionChain(req);
    }

    private void RequestSubInfo()
    {
        var c2s = QotGetSubInfo.C2S.CreateBuilder().SetIsReqAllConn(false).Build();
        var req = QotGetSubInfo.Request.CreateBuilder().SetC2S(c2s).Build();

        Interlocked.Increment(ref _pendingReplies);
        _qot.GetSubInfo(req);
    }

    /// <inheritdoc />
    public void OnReply_GetSubInfo(FTAPI_Conn client, uint nSerialNo, QotGetSubInfo.Response rsp)
    {
        Console.WriteLine("--- SUBSCRIPTION BUDGET ---");

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            Console.WriteLine($"  FAILED: {rsp.RetMsg}");
        }
        else
        {
            Console.WriteLine($"  Total used  : {rsp.S2C.TotalUsedQuota}");
            Console.WriteLine($"  Remaining   : {rsp.S2C.RemainQuota}");
            Console.WriteLine($"  Connections : {rsp.S2C.ConnSubInfoListCount}");
            Console.WriteLine("  => Current-session bars need Qot_Sub per symbol. If the remaining");
            Console.WriteLine("     budget is below the watchlist size, the scanner needs subscribe/");
            Console.WriteLine("     unsubscribe rotation rather than subscribing to everything once.");
        }

        Console.WriteLine();
        Complete();
    }

    /// <summary>Ends the probe once every outstanding request has replied.</summary>
    private void Complete()
    {
        if (Interlocked.Decrement(ref _pendingReplies) <= 0)
        {
            _finished.TrySetResult(true);
        }
    }
}
