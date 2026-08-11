using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Abstractions;

/// <summary>
/// Append-only durable log of every alert the system has raised.
/// </summary>
/// <remarks>
/// This is the backtesting substrate. Alerts are the expensive part of the system to
/// reproduce - they depend on live intraday data that is awkward and sometimes
/// impossible to reconstruct after the fact - so every one gets written down as it
/// happens, whether or not anyone is watching the UI.
///
/// <para><b>Append-only is a deliberate constraint.</b> The log is a record of what the
/// system decided at a point in time. Editing history would destroy the only honest
/// account of how a strategy actually behaved, which is precisely what a backtest needs
/// to be trustworthy.</para>
/// </remarks>
public interface IAlertStore
{
    /// <summary>Appends one alert to the log.</summary>
    /// <remarks>
    /// Must be safe to call concurrently from multiple scanner threads. Must not throw
    /// on I/O failure - a locked or full disk is not a reason to take down a running
    /// scan. Failures are logged and swallowed.
    /// </remarks>
    Task AppendAsync(AlertRecord alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every alert in the log, oldest first.
    /// </summary>
    /// <remarks>
    /// Streamed rather than returned as a list because the log grows without bound and
    /// a backtest over a year of alerts should not require all of them in memory at once.
    /// Malformed lines are skipped, not thrown on - see the JSON Lines rationale on the
    /// default implementation.
    /// </remarks>
    IAsyncEnumerable<AlertRecord> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Full path of the log file, for display and for opening it externally.</summary>
    string LogPath { get; }
}

/// <summary>
/// In-process publish/subscribe channel for alerts.
/// </summary>
/// <remarks>
/// This is the transport between the scanner and everything that reacts to a signal -
/// the UI console today, a Telegram bot later. Notably it is NOT the file system: the
/// scanner and the UI live in the same process, so routing an in-process message through
/// disk would add latency and a long list of failure modes to buy nothing.
///
/// <para>Persisting to <see cref="IAlertStore"/> and notifying subscribers are separate
/// concerns joined here: an implementation writes the alert down first, then raises the
/// event, so anything reacting to the event can rely on the record already existing.</para>
/// </remarks>
public interface IAlertNotificationService
{
    /// <summary>
    /// Raised for each published alert.
    /// </summary>
    /// <remarks>
    /// <b>Fired on the publishing thread, which is a background scanner thread.</b> WPF
    /// subscribers must marshal to the UI thread before touching bound collections.
    /// Doing that marshalling here instead would drag a UI dependency into Core and make
    /// this untestable.
    /// </remarks>
    event EventHandler<AlertRecord>? AlertRaised;

    /// <summary>Persists an alert, then notifies subscribers.</summary>
    Task PublishAsync(AlertRecord alert, CancellationToken cancellationToken = default);
}
