using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Abstractions;

/// <summary>
/// Local store of bars for completed trading sessions.
/// </summary>
/// <remarks>
/// <b>Only completed sessions are ever stored.</b> A session still in progress changes with
/// every bar, so caching it would freeze a partial day and then serve it as though it were
/// whole - producing a chart missing its afternoon and, far worse, an opening range or a
/// previous-session high computed from data that stopped at lunchtime. The current session
/// always comes from the provider.
///
/// <para>Exists because the gateway serves a narrow window - three days, bounded by its own
/// 1000-bar response cap - so anything older is simply unavailable. Accumulating sessions
/// locally is the only way this application ever sees more history than that.</para>
/// </remarks>
public interface IBarCache
{
    /// <summary>Reads a stored session, or an empty list if it is not held.</summary>
    Task<IReadOnlyList<Bar>> GetSessionAsync(
        string ticker, int timeframeMinutes, DateOnly session, CancellationToken cancellationToken = default);

    /// <summary>Stores one completed session, replacing anything already held for it.</summary>
    Task StoreSessionAsync(
        string ticker, int timeframeMinutes, DateOnly session,
        IReadOnlyList<Bar> bars, CancellationToken cancellationToken = default);

    /// <summary>Session dates held for a ticker and timeframe, newest first.</summary>
    Task<IReadOnlyList<DateOnly>> GetStoredSessionsAsync(
        string ticker, int timeframeMinutes, CancellationToken cancellationToken = default);
}
