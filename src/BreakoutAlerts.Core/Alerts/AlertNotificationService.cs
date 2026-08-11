using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.Core.Alerts;

/// <summary>
/// Default alert channel: persists each alert to the log, then notifies subscribers
/// in process.
/// </summary>
/// <remarks>
/// Ordering matters and is deliberate. The record is written to <see cref="IAlertStore"/>
/// <b>before</b> the event is raised, so any subscriber - the UI console today, a
/// Telegram bot later - can assume the alert already exists on disk. The reverse order
/// would allow a crash between notification and persistence, leaving a signal the user
/// saw but the backtest log has no record of.
///
/// <para>This is the extension point the spec's Telegram requirement hangs off: a future
/// notifier subscribes to <see cref="AlertRaised"/> and needs no scanner change at all.</para>
/// </remarks>
public sealed class AlertNotificationService : IAlertNotificationService
{
    private readonly IAlertStore _store;
    private readonly ILogger<AlertNotificationService> _logger;

    /// <inheritdoc />
    public event EventHandler<AlertRecord>? AlertRaised;

    /// <summary>Creates the service over a given store.</summary>
    public AlertNotificationService(IAlertStore store, ILogger<AlertNotificationService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task PublishAsync(AlertRecord alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        // Persist first - see the ordering note in the class remarks.
        await _store.AppendAsync(alert, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Alert {Strategy}/{Path} {Direction} on {Ticker} at {Price}",
            alert.Strategy, alert.AlertPath, alert.Direction, alert.Ticker, alert.TriggerPrice);

        // Snapshot the delegate before invoking. Without this, a subscriber unsubscribing
        // on another thread between the null check and the call causes a
        // NullReferenceException - a classic and genuinely intermittent event-race bug.
        var handlers = AlertRaised;
        if (handlers is null)
        {
            return;
        }

        // Each subscriber is invoked inside its own try/catch. A single misbehaving
        // subscriber must not prevent the others from being notified, and must not
        // propagate out of the scanner's publish call and kill the scan.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<AlertRecord>>())
        {
            try
            {
                handler(this, alert);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert subscriber threw while handling {Ticker}", alert.Ticker);
            }
        }
    }
}
