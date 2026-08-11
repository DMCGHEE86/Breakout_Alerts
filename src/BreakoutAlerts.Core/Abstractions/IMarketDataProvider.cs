using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Abstractions;

/// <summary>
/// Source of price and option data. Implemented by a mock in Phase 1 and by the moomoo
/// OpenD gateway client from Phase 3.
/// </summary>
/// <remarks>
/// The whole UI and scanner are written against this interface so that Phase 1 can be
/// built, run and demonstrated with zero network access and zero dependency on a running
/// OpenD gateway. When the real provider arrives it is a registration change in the DI
/// container, not a rewrite.
///
/// <para>Every method is async and takes a <see cref="CancellationToken"/> because the
/// real implementation is network-bound and subject to moomoo's rate limits. Making
/// these synchronous now would force an invasive change later - and worse, would invite
/// UI-thread blocking in the interim.</para>
/// </remarks>
public interface IMarketDataProvider
{
    /// <summary>Whether the provider currently has a usable connection.</summary>
    /// <remarks>
    /// The real gateway can drop without warning. The UI surfaces this so a stale feed is
    /// visibly stale rather than silently frozen - a scanner showing old prices as though
    /// they were live is worse than one that shows nothing.
    /// </remarks>
    bool IsConnected { get; }

    /// <summary>Raised when the connection state changes.</summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// Short identifier for where this provider's data comes from, e.g. "moomoo" or
    /// "synthetic". Stamped onto every alert the scanner publishes.
    /// </summary>
    /// <remarks>
    /// The alert log is the substrate for backtesting, and a generated alert that is
    /// indistinguishable from a real one silently poisons every result computed from it.
    /// The property lives on the provider rather than being read from configuration so the
    /// label cannot drift from the object that actually produced the bars: whatever answers
    /// <see cref="GetBarsAsync"/> is what gets recorded.
    /// </remarks>
    string DataSource { get; }

    /// <summary>Fetches historical bars for one ticker.</summary>
    /// <remarks>
    /// <b>Implementations must stamp each bar with its OPEN time and must include
    /// extended-hours bars.</b> Both were verified against a live moomoo OpenD gateway on
    /// 2026-08-04 and both differ from what that API returns by default:
    ///
    /// <list type="bullet">
    /// <item><b>Close times.</b> moomoo stamps K-line bars with the CLOSE. Passed through
    /// unchanged, the half-open 09:30-09:45 opening-range window would capture the bars
    /// closing at 09:35 and 09:40 but exclude the one closing at 09:45 - building the range
    /// from two bars instead of three and silently producing a wrong ORB high and low. An
    /// adapter must subtract the bar duration.</item>
    /// <item><b>Regular hours only.</b> moomoo omits extended-hours bars unless
    /// <c>ExtendedTime</c> is set; with it, a five-day 5-minute request went from 390 bars
    /// to 960. Without premarket bars there is no premarket high or low, so PATH-3 could
    /// never fire.</item>
    /// <item><b>Exclusive end dates.</b> <c>RequestHistoryKL</c>'s <c>EndTime</c> does not
    /// include the day named, so an end of "today" returns data up to yesterday's close.
    /// This is what made the call appear to serve completed days only. Left uncorrected,
    /// today's premarket bars are absent, no premarket high or low is computed for the live
    /// session, and PATH-3 cannot fire on the day being traded - with nothing failing and no
    /// error to notice. Verified 2026-08-04: an end of T+1 returned 40 premarket bars for
    /// today where an end of T returned none.</item>
    /// <item><b>Truncation from the newest end.</b> A response over the gateway's 1000-bar
    /// cap is trimmed by discarding the <i>most recent</i> bars, not the oldest, and still
    /// looks like a full response. With extended hours a 5-minute session is 192 bars, so an
    /// over-wide window quietly loses the bars that matter most.</item>
    /// </list>
    /// </remarks>
    /// <param name="ticker">Symbol to fetch.</param>
    /// <param name="timeframeMinutes">Bar size in minutes.</param>
    /// <param name="count">Maximum bars to return, most recent last.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Bar>> GetBarsAsync(
        string ticker,
        int timeframeMinutes,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a current snapshot for each requested ticker.</summary>
    /// <remarks>
    /// Batched by design. The real gateway enforces per-request quotas, so one call for
    /// two hundred symbols is the difference between a working scan and a throttled one.
    /// </remarks>
    Task<IReadOnlyList<TickerSnapshot>> GetSnapshotsAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches the option chain for one underlying.</summary>
    /// <param name="underlying">Underlying symbol.</param>
    /// <param name="expiry">Specific expiry, or null for the nearest available.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<OptionQuote>> GetOptionChainAsync(
        string underlying,
        DateOnly? expiry = null,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes to streaming snapshot updates for the given tickers.</summary>
    /// <remarks>
    /// Updates arrive on a background thread. Subscribers touching UI state must dispatch.
    /// </remarks>
    Task SubscribeAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default);

    /// <summary>Raised for each streaming snapshot update. Background thread.</summary>
    event EventHandler<TickerSnapshot>? SnapshotUpdated;
}
