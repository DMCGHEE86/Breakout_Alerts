using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Charting;

/// <summary>
/// Resolves an alert back to the bar whose close produced it.
/// </summary>
/// <remarks>
/// <b>Alerts and bars are stamped with opposite ends of the same candle.</b> A signal carries
/// the bar's CLOSE - the moment the setup became knowable, which is what an alert should
/// report - while a bar carries its OPEN, which is where the candle is drawn. The close of
/// bar N is therefore numerically identical to the open of bar N+1, and anything that treats
/// an alert timestamp as a bar timestamp is off by exactly one candle.
///
/// <para>On a breakout chart that is not cosmetic: the candle the eye lands on would not be
/// the one whose body closed out of the range, so the distance being judged is measured from
/// the wrong bar. This has been got wrong twice, which is why the arithmetic lives here - in
/// an assembly with no rendering dependency - rather than inside the renderer where it can
/// only be checked by looking at a picture.</para>
/// </remarks>
public static class AlertBarLocator
{
    /// <summary>The open time of the bar whose close produced an alert.</summary>
    /// <param name="alertAt">Alert timestamp, which is a bar close time.</param>
    /// <param name="timeframeMinutes">Bar size the alert was evaluated on.</param>
    public static DateTimeOffset TriggerBarOpen(DateTimeOffset alertAt, int timeframeMinutes) =>
        alertAt.AddMinutes(-timeframeMinutes);

    /// <summary>
    /// Finds the bar whose close produced an alert.
    /// </summary>
    /// <param name="bars">Plotted bars, ascending, stamped with their open times.</param>
    /// <param name="alertAt">Alert timestamp, which is a bar close time.</param>
    /// <param name="timeframeMinutes">Bar size the alert was evaluated on.</param>
    /// <returns>
    /// Index into <paramref name="bars"/>, or -1 when the alert falls outside them.
    /// </returns>
    /// <remarks>
    /// Returning -1 rather than a nearest match for out-of-range alerts is deliberate. The
    /// alert log persists across restarts, so the list routinely holds signals from sessions
    /// that ran further than the one on screen; snapping those to the closest bar would paint
    /// a flag onto an unrelated candle, which is worse than not drawing it. A caller that
    /// wants to explain the omission can do so - silently inventing a position cannot be
    /// undone downstream.
    ///
    /// <para>Within range the result is snapped to the nearest bar rather than requiring an
    /// exact hit, so a chart drawn at a different timeframe from the one that fired the alert
    /// still lands on a real candle instead of in a gap.</para>
    /// </remarks>
    public static int FindTriggerBar(
        IReadOnlyList<Bar> bars, DateTimeOffset alertAt, int timeframeMinutes)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (bars.Count == 0)
        {
            return -1;
        }

        var target = TriggerBarOpen(alertAt, timeframeMinutes);

        // Inclusive at both ends. The upper bound matters: an alert fired on the final
        // plotted candle has a timestamp one bar-length past that bar's open, and comparing
        // the raw timestamp instead of the derived open silently discarded the last signal
        // of every session.
        if (target < bars[0].Timestamp || target > bars[^1].Timestamp)
        {
            return -1;
        }

        var best = 0;
        var bestGap = Math.Abs((bars[0].Timestamp - target).TotalMinutes);

        for (var i = 1; i < bars.Count; i++)
        {
            var gap = Math.Abs((bars[i].Timestamp - target).TotalMinutes);
            if (gap < bestGap)
            {
                best = i;
                bestGap = gap;
            }
        }

        return best;
    }
}
