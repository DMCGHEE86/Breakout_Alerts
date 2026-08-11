using System.IO;
using System.Text;
using BreakoutAlerts.Core.Configuration;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Options;
using BreakoutAlerts.Core.Strategies;
using Futu.OpenApi.Pb;
using Microsoft.Extensions.Logging.Abstractions;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Headless end-to-end check of the live adapter, run with
/// <c>--probe-live SYMBOL output.txt</c>.
/// </summary>
/// <remarks>
/// Exists because the option-chain half of <see cref="MoomooMapping"/> was written against
/// the futu assembly's API surface and never against a real response. Everything else in the
/// live adapter was verified with the standalone probe in <c>tools/</c>, but that probe
/// reimplements its own mapping - so it can only prove the gateway returns data, never that
/// <b>this application's</b> projection of it is right. A field that silently comes back
/// unset here shows up as an empty candidates pane, or worse, as plausible-looking Greeks
/// attached to the wrong contract.
///
/// <para>So this runs the production classes: the real connection, the real provider, the
/// real mapping and the real ranker. Raw protobuf counts are printed alongside the mapped
/// results, because "no candidates" has two very different causes - an empty chain from the
/// gateway, or a mapping that dropped everything - and the two are indistinguishable from
/// the outside.</para>
///
/// <para>Read-only. It subscribes and reads quotes; nothing here can place an order.</para>
/// </remarks>
public static class LiveDataProbe
{
    /// <summary>Runs every live read path and writes a report.</summary>
    public static async Task RunAsync(string symbol, string outputPath)
    {
        var report = new StringBuilder();
        var options = new MarketDataOptions();

        void Line(string text = "")
        {
            report.AppendLine(text);
        }

        Line($"LIVE DATA PROBE - {symbol} - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line(new string('=', 72));

        using var connection = new MoomooConnection(options, NullLogger<MoomooConnection>.Instance);

        try
        {
            if (!await connection.ConnectAsync().ConfigureAwait(false))
            {
                Line($"FAILED: could not connect to OpenD at {options.Host}:{options.Port}.");
                await File.WriteAllTextAsync(outputPath, report.ToString()).ConfigureAwait(false);
                return;
            }

            Line($"Connected to {options.Host}:{options.Port}");
            Line();

            using var provider = new MoomooMarketDataProvider(
                connection, options, NullLogger<MoomooMarketDataProvider>.Instance);

            await ProbeBarLegsAsync(connection, symbol, options, Line).ConfigureAwait(false);
            await ProbeBarsAsync(provider, symbol, options, Line).ConfigureAwait(false);
            await ProbeTimeShareAsync(connection, symbol, Line).ConfigureAwait(false);
            await ProbeSnapshotAsync(provider, symbol, Line).ConfigureAwait(false);
            await ProbeOptionsAsync(connection, provider, symbol, options, Line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Line();
            Line($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Line(ex.StackTrace ?? string.Empty);
        }

        await File.WriteAllTextAsync(outputPath, report.ToString()).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports each bar source separately, so where today's bars come from is measured
    /// rather than inferred.
    /// </summary>
    /// <remarks>
    /// The merged output of <c>GetBarsAsync</c> cannot answer this. When the first run showed
    /// today holding regular-session bars only, two explanations fitted equally well: history
    /// excluding today and <c>GetKL</c> supplying it without extended hours, or history
    /// including today but dropping its extended bars. Those call for different fixes, so
    /// this issues each leg on its own - and history both with and without
    /// <c>ExtendedTime</c>, which isolates what that flag is actually doing.
    /// </remarks>
    private static async Task ProbeBarLegsAsync(
        MoomooConnection connection, string symbol, MarketDataOptions options, Action<string> line)
    {
        line("---- BAR SOURCES " + new string('-', 55));

        var klType = MoomooMapping.ToKLType(options.TimeframeMinutes)!.Value;
        var today = MarketSession.SessionDate(DateTimeOffset.Now);

        async Task HistoryAsync(bool extended, int endOffsetDays)
        {
            var c2s = QotRequestHistoryKL.C2S.CreateBuilder()
                .SetSecurity(MoomooMapping.UsSecurity(symbol))
                .SetKlType((int)klType)
                .SetRehabType((int)QotCommon.RehabType.RehabType_None)
                .SetBeginTime(DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd"))
                .SetEndTime(DateTime.Today.AddDays(endOffsetDays).ToString("yyyy-MM-dd"))
                .SetMaxAckKLNum(1000)
                .SetExtendedTime(extended)
                .Build();

            var rsp = await connection
                .RequestHistoryAsync(QotRequestHistoryKL.Request.CreateBuilder().SetC2S(c2s).Build(), default)
                .ConfigureAwait(false);

            var label = $"history Ext={extended,-5} end=T{endOffsetDays:+0;-0;+0}";

            if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
            {
                line($"{label}  FAILED: {rsp.RetMsg}");
                return;
            }

            Describe(label, rsp.S2C.KlListList.Select(k => MoomooMapping.ToBar(k, klType)).ToList());
        }

        void Describe(string label, List<Bar> bars)
        {
            if (bars.Count == 0)
            {
                line($"{label}  0 bars");
                return;
            }

            var todays = bars.Where(b => MarketSession.SessionDate(b.Timestamp) == today).ToList();
            var todayPre = todays.Count(b => MarketSession.IsPremarket(b.Timestamp));

            line($"{label}  {bars.Count,4} bars  {bars[0].Timestamp:MM-dd HH:mm} .. {bars[^1].Timestamp:MM-dd HH:mm}" +
                 $"   today {todays.Count,3} ({todayPre} premarket)");
        }

        // Overnight coverage. ZEBRA-style strategies need to know whether a level was traded
        // between 20:00 and 04:00, and every measurement so far shows extended-hours days
        // running 04:00-20:00 only. If that gap is real, a level tested at 2am reads as
        // untested - the strategy would call a zone clean when it was not, which is the worst
        // possible direction for that error.
        async Task OvernightAsync()
        {
            var c2s = QotRequestHistoryKL.C2S.CreateBuilder()
                .SetSecurity(MoomooMapping.UsSecurity(symbol))
                .SetKlType((int)klType)
                .SetRehabType((int)QotCommon.RehabType.RehabType_None)
                .SetBeginTime(DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd"))
                .SetEndTime(DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"))
                .SetMaxAckKLNum(1000)
                .SetExtendedTime(true)
                .Build();

            var rsp = await connection
                .RequestHistoryAsync(QotRequestHistoryKL.Request.CreateBuilder().SetC2S(c2s).Build(), default)
                .ConfigureAwait(false);

            if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
            {
                line($"overnight check          FAILED: {rsp.RetMsg}");
                return;
            }

            var bars = rsp.S2C.KlListList.Select(k => MoomooMapping.ToBar(k, klType)).ToList();

            var byHour = bars
                .Select(b => MarketSession.ToExchangeTime(b.Timestamp).Hour)
                .GroupBy(h => h)
                .OrderBy(g => g.Key)
                .ToList();

            var overnight = bars.Count(b =>
            {
                var h = MarketSession.ToExchangeTime(b.Timestamp).Hour;
                return h >= 20 || h < 4;
            });

            line($"hours present            {string.Join(",", byHour.Select(g => g.Key))}");
            line($"bars in 20:00-04:00      {overnight}" +
                 (overnight > 0 ? "  <-- OVERNIGHT IS AVAILABLE" : "  <-- NO OVERNIGHT COVERAGE"));
        }

        await HistoryAsync(extended: false, endOffsetDays: 0).ConfigureAwait(false);
        await HistoryAsync(extended: true, endOffsetDays: 0).ConfigureAwait(false);

        // Tests whether EndTime is exclusive. If it is, asking for "up to today" has been
        // silently excluding today all along - which would explain both the missing premarket
        // bars and the original finding that a today-only history request returned nothing,
        // and would make this a one-line fix rather than a missing capability.
        await HistoryAsync(extended: true, endOffsetDays: 1).ConfigureAwait(false);
        await OvernightAsync().ConfigureAwait(false);

        // GetKL requires a live subscription, which GetBarsAsync establishes lazily. Issue it
        // here so this leg can be measured on its own.
        var subC2s = QotSub.C2S.CreateBuilder()
            .AddSecurityList(MoomooMapping.UsSecurity(symbol))
            .AddSubTypeList((int)MoomooMapping.ToSubType(klType))
            .SetIsSubOrUnSub(true)
            .SetIsRegOrUnRegPush(false)
            .Build();

        var subRsp = await connection
            .SubscribeAsync(QotSub.Request.CreateBuilder().SetC2S(subC2s).Build(), default)
            .ConfigureAwait(false);

        if (subRsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            line($"subscribe                FAILED: {subRsp.RetMsg}");
            line(string.Empty);
            return;
        }

        var klC2s = QotGetKL.C2S.CreateBuilder()
            .SetSecurity(MoomooMapping.UsSecurity(symbol))
            .SetKlType((int)klType)
            .SetRehabType((int)QotCommon.RehabType.RehabType_None)
            .SetReqNum(1000)
            .Build();

        var klRsp = await connection
            .GetKlineAsync(QotGetKL.Request.CreateBuilder().SetC2S(klC2s).Build(), default)
            .ConfigureAwait(false);

        if (klRsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            line($"GetKL                    FAILED: {klRsp.RetMsg}");
        }
        else
        {
            Describe("GetKL (subscribed)     ", klRsp.S2C.KlListList.Select(k => MoomooMapping.ToBar(k, klType)).ToList());
        }

        line(string.Empty);
    }

    /// <summary>Checks bars, and specifically the three quirks the adapter has to handle.</summary>
    private static async Task ProbeBarsAsync(
        MoomooMarketDataProvider provider, string symbol, MarketDataOptions options, Action<string> line)
    {
        line("---- BARS " + new string('-', 62));

        var bars = await provider.GetBarsAsync(symbol, options.TimeframeMinutes, 800).ConfigureAwait(false);
        line($"{bars.Count} bars at {options.TimeframeMinutes}m");

        if (bars.Count == 0)
        {
            line("NO BARS - nothing downstream can work.");
            line(string.Empty);
            return;
        }

        line($"first  {bars[0].Timestamp:yyyy-MM-dd HH:mm zzz}");
        line($"last   {bars[^1].Timestamp:yyyy-MM-dd HH:mm zzz}  close {bars[^1].Close}");

        var today = MarketSession.SessionDate(DateTimeOffset.Now);
        var todays = bars.Where(b => MarketSession.SessionDate(b.Timestamp) == today).ToList();
        var premarket = todays.Count(b => MarketSession.IsPremarket(b.Timestamp));
        var openingRange = todays.Where(b => MarketSession.IsOpeningRange(b.Timestamp)).ToList();

        line($"today  {todays.Count} bars, {premarket} premarket");

        // The opening range must contain exactly 15 minutes of bars. Two instead of three on
        // a 5-minute timeframe is the signature of the close-stamped-timestamp quirk being
        // passed through unfixed - the range comes out narrow, and every path fires early.
        // Only a complete range can be judged. Run between 09:30 and 09:45 the count is
        // legitimately short because the range is still forming, and reporting that as WRONG
        // cries wolf every single morning - which is how a diagnostic stops being read.
        var expected = MarketSession.OpeningRangeMinutes / options.TimeframeMinutes;
        var nowEt = MarketSession.ToExchangeTime(DateTimeOffset.Now);
        var rangeComplete = TimeOnly.FromDateTime(nowEt.DateTime) >= MarketSession.OpeningRangeClose
            || MarketSession.SessionDate(DateTimeOffset.Now) != today;

        var verdict = openingRange.Count == expected ? "  OK"
            : rangeComplete ? "  <-- WRONG"
            : "  (still forming - locks at 09:45)";

        line($"opening range  {openingRange.Count} bars (expected {expected}){verdict}");

        foreach (var bar in openingRange)
        {
            line($"    {bar.Timestamp:HH:mm}  O {bar.Open}  H {bar.High}  L {bar.Low}  C {bar.Close}");
        }

        if (premarket == 0)
        {
            // Not an ExtendedTime failure - the bar-source breakdown above shows that flag
            // working on completed days. Today's premarket bars are simply not served by
            // either K-line call, so PATH-3 cannot fire on the live session.
            line("NO PREMARKET BARS FOR TODAY - PATH-3 cannot fire on the current session.");
        }

        line(string.Empty);
    }

    /// <summary>
    /// Checks whether time-share data covers today's premarket.
    /// </summary>
    /// <remarks>
    /// Neither K-line call serves today's extended hours: history excludes the current day
    /// entirely and <c>GetKL</c> returns regular-session bars only. That leaves the premarket
    /// high and low - and therefore PATH-3 - unavailable on the one session being traded.
    /// Time-share is per-minute price rather than OHLC, which is enough for a running high
    /// and low, so this measures whether its points start at 04:00 or 09:30.
    /// </remarks>
    private static async Task ProbeTimeShareAsync(
        MoomooConnection connection, string symbol, Action<string> line)
    {
        line("---- TIME SHARE (premarket fallback) " + new string('-', 35));

        var subC2s = QotSub.C2S.CreateBuilder()
            .AddSecurityList(MoomooMapping.UsSecurity(symbol))
            .AddSubTypeList((int)QotCommon.SubType.SubType_RT)
            .SetIsSubOrUnSub(true)
            .SetIsRegOrUnRegPush(false)
            .Build();

        var subRsp = await connection
            .SubscribeAsync(QotSub.Request.CreateBuilder().SetC2S(subC2s).Build(), default)
            .ConfigureAwait(false);

        if (subRsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            line($"RT subscribe FAILED: {subRsp.RetMsg}");
            line(string.Empty);
            return;
        }

        var c2s = QotGetRT.C2S.CreateBuilder()
            .SetSecurity(MoomooMapping.UsSecurity(symbol))
            .Build();

        var rsp = await connection
            .GetRealTimeAsync(QotGetRT.Request.CreateBuilder().SetC2S(c2s).Build(), default)
            .ConfigureAwait(false);

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            line($"GetRT FAILED: {rsp.RetMsg}");
            line(string.Empty);
            return;
        }

        var points = rsp.S2C.RtListList;
        line($"{points.Count} time-share points");

        if (points.Count == 0)
        {
            line(string.Empty);
            return;
        }

        var first = MoomooMapping.ParseExchangeTime(points[0].Time);
        var last = MoomooMapping.ParseExchangeTime(points[^1].Time);
        line($"first {first:yyyy-MM-dd HH:mm zzz}   last {last:yyyy-MM-dd HH:mm zzz}");

        var premarket = points
            .Select(p => (Time: MoomooMapping.ParseExchangeTime(p.Time), p.Price))
            .Where(p => MarketSession.IsPremarket(p.Time))
            .ToList();

        line($"{premarket.Count} points inside the 04:00-09:30 premarket window");

        if (premarket.Count > 0)
        {
            line($"    premarket high {premarket.Max(p => p.Price):0.00}  low {premarket.Min(p => p.Price):0.00}");
            line("    USABLE as a premarket high/low source for the current session.");
        }
        else
        {
            line("    NOT usable - no premarket coverage here either.");
        }

        line(string.Empty);
    }

    private static async Task ProbeSnapshotAsync(
        MoomooMarketDataProvider provider, string symbol, Action<string> line)
    {
        line("---- SNAPSHOT " + new string('-', 58));

        var snapshots = await provider.GetSnapshotsAsync([symbol]).ConfigureAwait(false);
        if (snapshots.Count == 0)
        {
            line("NO SNAPSHOT returned.");
            line(string.Empty);
            return;
        }

        var s = snapshots[0];
        line($"{s.Ticker} \"{s.Name}\"  last {s.Last}  chg {s.Change} ({s.ChangePercent:P2})  vol {s.Volume}");
        line($"stamped {s.UpdatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        line(string.Empty);
    }

    /// <summary>
    /// The part this probe exists for: the option chain, mapped by the production code.
    /// </summary>
    private static async Task ProbeOptionsAsync(
        MoomooConnection connection,
        MoomooMarketDataProvider provider,
        string symbol,
        MarketDataOptions options,
        Action<string> line)
    {
        line("---- OPTION CHAIN " + new string('-', 54));

        // Raw first. If the mapped list is empty, this is what distinguishes "the gateway
        // sent nothing" from "the projection dropped everything".
        var filter = QotGetOptionChain.DataFilter.CreateBuilder()
            .SetDeltaMin(options.OptionMinimumDelta)
            .SetDeltaMax(options.OptionMaximumDelta)
            .Build();

        var c2s = QotGetOptionChain.C2S.CreateBuilder()
            .SetOwner(MoomooMapping.UsSecurity(symbol))
            .SetBeginTime(DateTime.Today.ToString("yyyy-MM-dd"))
            // Must match the provider's window. A wider one is rejected outright with "the
            // requested time span cannot exceed 30 days" - which is how the 45-day window in
            // the provider was found, and why this probe deliberately mirrors it rather than
            // picking its own.
            .SetEndTime(DateTime.Today.AddDays(28).ToString("yyyy-MM-dd"))
            .SetDataFilter(filter)
            .Build();

        var raw = await connection
            .GetOptionChainAsync(QotGetOptionChain.Request.CreateBuilder().SetC2S(c2s).Build(), default)
            .ConfigureAwait(false);

        line($"raw response  ret {raw.RetType}  \"{raw.RetMsg}\"");

        if (raw.RetType != (int)Common.RetType.RetType_Succeed)
        {
            line("chain request rejected - nothing further to check.");
            line(string.Empty);
            return;
        }

        line($"raw expiries  {raw.S2C.OptionChainList.Count}");

        var rawItems = raw.S2C.OptionChainList.Sum(c => c.OptionList.Count);
        line($"raw items     {rawItems}");

        var contracts = MoomooMapping.ToOptionContracts(raw, symbol).ToList();
        // Call band only. The provider issues a second request for the negative band, because
        // puts carry negative delta and a positive-only filter excludes every one of them -
        // so the mapped-quote count below is legitimately larger than this.
        line($"mapped refs   {contracts.Count}  (call band only)");

        foreach (var contract in contracts.Take(12))
        {
            // A default(DateOnly) expiry or a zero strike means StrikeTime/StrikePrice did
            // not populate the way the mapping assumes.
            var expiryNote = contract.Expiry == default ? "  <-- EXPIRY DID NOT PARSE" : string.Empty;
            var strikeNote = contract.Strike == 0m ? "  <-- STRIKE IS ZERO" : string.Empty;
            line($"    {contract.Security.Code,-24} {contract.Right,-4} " +
                 $"strike {contract.Strike,8} exp {contract.Expiry:yyyy-MM-dd}{expiryNote}{strikeNote}");
        }

        line(string.Empty);

        // Now the whole production path, including the batched snapshot for Greeks.
        var quotes = await provider.GetOptionChainAsync(symbol).ConfigureAwait(false);
        line($"mapped quotes {quotes.Count}  (of {contracts.Count} refs)");

        if (quotes.Count < contracts.Count)
        {
            // ToOptionQuote returns null when HasOptionExData is false, and those are
            // silently dropped. If that is most of the chain, the snapshot is not carrying
            // option data where the mapping expects it.
            line($"    {contracts.Count - quotes.Count} dropped - snapshot carried no OptionExData");
        }

        foreach (var q in quotes)
        {
            line($"    {q.ContractId,-24} {q.Right,-4} {q.Strike,8} exp {q.Expiry:MM-dd} " +
                 $"dte {q.DaysToExpiry,3}  bid {Fmt(q.Bid)} ask {Fmt(q.Ask)} last {q.Last,8}  " +
                 $"D {Fmt(q.Delta)} G {Fmt(q.Gamma)} T {Fmt(q.Theta)} V {Fmt(q.Vega)} " +
                 $"IV {Fmt(q.ImpliedVolatility)}  OI {q.OpenInterest}");
        }

        var missingGreeks = quotes.Count(q => q.Delta is null);
        if (missingGreeks > 0)
        {
            line($"    {missingGreeks} quotes have NO DELTA - the ranker will reject every one of them.");
        }

        var outOfBand = quotes
            .Where(q => q.Delta is { } d && (Math.Abs(d) < options.OptionMinimumDelta || Math.Abs(d) > options.OptionMaximumDelta))
            .ToList();

        if (outOfBand.Count > 0)
        {
            // The gateway filter is supposed to have already applied the band. If contracts
            // outside it come back, the server-side filter is not doing what the probe
            // measured on 2026-08-04 and the ranker's own band check is load-bearing.
            line($"    {outOfBand.Count} quotes OUTSIDE the {options.OptionMinimumDelta}-{options.OptionMaximumDelta} band - server filter not honoured.");
        }

        line(string.Empty);
        line("---- RANKED CANDIDATES " + new string('-', 49));

        var ranker = new DeltaTargetRanker
        {
            TargetDelta = options.OptionTargetDelta,
            MinimumDelta = options.OptionMinimumDelta,
            MaximumDelta = options.OptionMaximumDelta
        };

        var spot = (await provider.GetSnapshotsAsync([symbol]).ConfigureAwait(false))
            .FirstOrDefault()?.Last ?? 0m;

        var ranked = ranker.Rank(quotes, new OptionRankContext(TradeDirection.Long, spot, RestrictToDirection: true));
        line($"{ranked.Count} calls ranked for a LONG signal at spot {spot}");

        foreach (var item in ranked)
        {
            line($"    {item.Quote.Strike,8}C {item.Quote.Expiry:MM-dd}  " +
                 $"D {Fmt(item.Quote.Delta)}  score {item.Score:F3}  {item.Rationale}");
        }

        if (ranked.Count == 0 && quotes.Count > 0)
        {
            line("    NOTHING RANKED despite quotes being present - check the tradability filters.");
        }

        line(string.Empty);
    }

    private static string Fmt(decimal? value) => value is { } v ? v.ToString("0.00").PadLeft(8) : "       -";

    private static string Fmt(double? value) => value is { } v ? v.ToString("0.0000").PadLeft(8) : "       -";
}
