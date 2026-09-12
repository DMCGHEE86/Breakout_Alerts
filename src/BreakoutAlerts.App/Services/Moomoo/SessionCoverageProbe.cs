using System.IO;
using System.Text;
using BreakoutAlerts.Core.Configuration;
using Futu.OpenApi.Pb;
using Microsoft.Extensions.Logging.Abstractions;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Reports which hours of the day the gateway will serve, run with
/// <c>--probe-sessions SYMBOL output.txt</c>.
/// </summary>
/// <remarks>
/// <b>The question this answers is "what are we not asking for?"</b> The bar request carries
/// both an <c>ExtendedTime</c> flag and a <c>Session</c> field, and this application set the
/// first and never the second. Every cached session therefore runs 04:00 to 20:00, which was
/// read as "the gateway has no overnight data" - a conclusion drawn from the shape of our own
/// request rather than from anything the gateway said. That is the same mistake as reading an
/// absent API field as an absent capability, recorded in plan.md section 4s.2.
///
/// <para>So this issues the identical history request once per <see cref="Common.Session"/>
/// value, with extended hours both on and off, and prints an hour histogram of whatever comes
/// back. Raw <c>KLine.Time</c> strings, unmapped - the mapping is the thing under suspicion, so
/// putting it in the path would only hide an answer.</para>
///
/// <para>Read-only, and cheap enough to re-run whenever a moomoo release note mentions
/// sessions.</para>
/// </remarks>
public static class SessionCoverageProbe
{
    private static readonly Common.Session[] Sessions =
    [
        Common.Session.Session_NONE,
        Common.Session.Session_RTH,
        Common.Session.Session_ETH,
        Common.Session.Session_ALL,
        Common.Session.Session_OVERNIGHT
    ];

    /// <summary>Requests bars under every session mode and reports the hours returned.</summary>
    public static async Task RunAsync(string symbol, string outputPath)
    {
        var report = new StringBuilder();
        var options = new MarketDataOptions();

        void Line(string text = "") => report.AppendLine(text);

        Line($"SESSION COVERAGE PROBE - {symbol} - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line(new string('=', 78));
        Line("Identical history request, varying only Session and ExtendedTime.");
        Line("Hours are as the gateway stamps them - bars carry CLOSE times here, unmapped.");
        Line();

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

            foreach (var extended in new[] { true, false })
            {
                foreach (var session in Sessions)
                {
                    await ProbeOneAsync(connection, symbol, session, extended, Line)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Line();
            Line($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }

        await File.WriteAllTextAsync(outputPath, report.ToString()).ConfigureAwait(false);
    }

    private static async Task ProbeOneAsync(
        MoomooConnection connection, string symbol,
        Common.Session session, bool extendedTime, Action<string> line)
    {
        var label = $"{session} + ExtendedTime={extendedTime}";
        line($"---- {label} " + new string('-', Math.Max(4, 60 - label.Length)));

        try
        {
            var c2s = QotRequestHistoryKL.C2S.CreateBuilder()
                .SetSecurity(MoomooMapping.UsSecurity(symbol))
                .SetKlType((int)QotCommon.KLType.KLType_5Min)
                .SetRehabType((int)QotCommon.RehabType.RehabType_None)
                .SetBeginTime(DateTime.Today.AddDays(-4).ToString("yyyy-MM-dd"))
                // Exclusive, so T+1 to include today. See MoomooMarketDataProvider.
                .SetEndTime(DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"))
                .SetMaxAckKLNum(1000)
                .SetExtendedTime(extendedTime)
                .SetSession((int)session)
                .Build();

            var rsp = await connection
                .RequestHistoryAsync(QotRequestHistoryKL.Request.CreateBuilder().SetC2S(c2s).Build(), default)
                .ConfigureAwait(false);

            if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
            {
                // A refusal is a real answer: it says the combination is not offered, which is
                // worth as much as a histogram and is the outcome a silent empty list hides.
                line($"    REFUSED (retType {rsp.RetType}): {rsp.RetMsg}");
                line(string.Empty);
                return;
            }

            var bars = rsp.S2C.KlListList;

            if (bars.Count == 0)
            {
                line("    0 bars returned.");
                line(string.Empty);
                return;
            }

            line($"    {bars.Count} bars   first {bars[0].Time}   last {bars[^1].Time}");

            var byHour = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var kl in bars)
            {
                // "yyyy-MM-dd HH:mm:ss" - take the hour without parsing, so a format change
                // shows up as odd output rather than as a silent exception.
                var hour = kl.Time.Length >= 13 ? kl.Time.Substring(11, 2) : "??";
                byHour[hour] = byHour.TryGetValue(hour, out var n) ? n + 1 : 1;
            }

            line("    hours: " + string.Join("  ", byHour.Select(h => $"{h.Key}:{h.Value}")));

            // The whole point of the probe. 20:00-03:59 is the block ZEBRA's overnight rule
            // needs and the current request has never returned.
            var overnight = byHour
                .Where(h => int.TryParse(h.Key, out var v) && (v >= 20 || v < 4))
                .Sum(h => h.Value);

            line(overnight > 0
                ? $"    -> {overnight} bars in the 20:00-04:00 block. OVERNIGHT DATA IS AVAILABLE."
                : "    -> nothing between 20:00 and 04:00.");
        }
        catch (Exception ex)
        {
            line($"    threw: {ex.GetType().Name}: {ex.Message}");
        }

        line(string.Empty);
    }
}
