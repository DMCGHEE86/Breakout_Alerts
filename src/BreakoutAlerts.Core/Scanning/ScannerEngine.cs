using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.Core.Scanning;

/// <summary>
/// Evaluates every active strategy against every watchlist symbol and publishes what fires.
/// </summary>
/// <remarks>
/// Deliberately a plain class rather than a hosted service, with a single public
/// <see cref="RunCycleAsync"/>. The timing loop lives in a thin host wrapper in the app
/// project, which means a full scan cycle can be driven directly from a unit test with
/// mocked dependencies and no timers.
///
/// <para>One symbol's failure does not abort the cycle. A single bad tick or a transient
/// gateway error on one name must not silently stop the other forty from being scanned -
/// that is the kind of failure that looks like "no setups today".</para>
/// </remarks>
public sealed class ScannerEngine
{
    private readonly IWatchlistService _watchlist;
    private readonly IMarketDataProvider _marketData;
    private readonly IStrategyRegistry _registry;
    private readonly IAlertNotificationService _notifications;
    private readonly ILogger<ScannerEngine> _logger;

    /// <summary>
    /// Bars of history requested per symbol per cycle.
    /// </summary>
    /// <remarks>
    /// Enough to cover a full session at the finest supported timeframe (1-minute over
    /// 04:00-16:00 is 720 bars) with headroom. Strategies skip bars they have already
    /// consumed, so over-fetching costs bandwidth but never correctness.
    /// </remarks>
    private const int BarHistoryCount = 800;

    /// <summary>Creates the engine.</summary>
    public ScannerEngine(
        IWatchlistService watchlist,
        IMarketDataProvider marketData,
        IStrategyRegistry registry,
        IAlertNotificationService notifications,
        ILogger<ScannerEngine> logger)
    {
        _watchlist = watchlist ?? throw new ArgumentNullException(nameof(watchlist));
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Timeframe in minutes that bars are requested at.</summary>
    /// <remarks>
    /// Read from the active strategies' own configuration so the scanner does not hold a
    /// second copy of a setting the user edits on the Strategies page. Falls back to 5.
    /// </remarks>
    public int TimeframeMinutes { get; set; } = 5;

    /// <summary>
    /// Latest time of day, in exchange time, at which a signal is still worth publishing.
    /// Null publishes all day.
    /// </summary>
    /// <remarks>
    /// An opening-range breakout is a claim about the day's direction, and a claim made
    /// twenty minutes before the close is one there is no time left to act on. The whole
    /// intended use here is a same-day entry and exit, so a late signal is not a smaller
    /// opportunity - it is noise competing for attention with the ones that still matter.
    ///
    /// <para>Compared against the bar's close time rather than the wall clock, so a scan that
    /// runs late - after a restart, say - still judges each signal by when it actually
    /// fired.</para>
    /// </remarks>
    public TimeOnly? AlertCutoff { get; set; }

    /// <summary>Signals published during the most recent cycle. Diagnostics only.</summary>
    public int LastCycleSignalCount { get; private set; }

    /// <summary>Signals suppressed by <see cref="AlertCutoff"/> in the most recent cycle.</summary>
    /// <remarks>
    /// Counted rather than discarded silently. A filter that quietly removes signals is
    /// indistinguishable from a strategy that stopped working, and the difference matters at
    /// exactly the moment someone asks why the afternoon was quiet.
    /// </remarks>
    public int LastCycleSuppressedCount { get; private set; }

    /// <summary>Runs one full scan across the watchlist.</summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var strategies = _registry.Active;
        if (strategies.Count == 0)
        {
            return;
        }

        var symbols = _watchlist.Items;
        if (symbols.Count == 0)
        {
            return;
        }

        var published = 0;
        var suppressed = 0;
        var today = Strategies.MarketSession.SessionDate(DateTimeOffset.Now);

        foreach (var entry in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var bars = await _marketData
                    .GetBarsAsync(entry.Ticker, TimeframeMinutes, BarHistoryCount, cancellationToken)
                    .ConfigureAwait(false);

                if (bars.Count == 0)
                {
                    continue;
                }

                foreach (var strategy in strategies)
                {
                    var signals = strategy.Evaluate(entry.Ticker, bars);

                    foreach (var signal in signals)
                    {
                        // Only today's signals are published. A live provider returns
                        // several days of history so the strategy has a full session to
                        // work from, and it correctly evaluates each of those days - which
                        // means a first scan after startup would otherwise dump a week of
                        // historical breakouts into the alert log and the console as though
                        // they had just fired. Alerts are a call to act now; a signal from
                        // last Tuesday is not one.
                        if (Strategies.MarketSession.SessionDate(signal.TriggeredAt) != today)
                        {
                            continue;
                        }

                        // Too late in the session to act on - see AlertCutoff.
                        if (AlertCutoff is { } cutoff && TimeOfDay(signal.TriggeredAt) > cutoff)
                        {
                            suppressed++;
                            continue;
                        }

                        await _notifications
                            .PublishAsync(ToAlert(signal, _marketData.DataSource), cancellationToken)
                            .ConfigureAwait(false);
                        published++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Contained per symbol - see the class remarks.
                _logger.LogError(ex, "Scan failed for {Ticker}", entry.Ticker);
            }
        }

        LastCycleSignalCount = published;
        LastCycleSuppressedCount = suppressed;

        if (published > 0)
        {
            _logger.LogInformation("Scan cycle published {Count} signals across {Symbols} symbols",
                published, symbols.Count);
        }

        if (suppressed > 0)
        {
            _logger.LogInformation("{Count} signals suppressed - fired after the {Cutoff} cutoff",
                suppressed, AlertCutoff);
        }
    }

    /// <summary>Time of day in exchange time.</summary>
    private static TimeOnly TimeOfDay(DateTimeOffset instant) =>
        TimeOnly.FromDateTime(Strategies.MarketSession.ToExchangeTime(instant).DateTime);

    /// <summary>Projects a strategy signal into the persisted alert shape.</summary>
    /// <param name="signal">The signal that fired.</param>
    /// <param name="dataSource">
    /// Provenance of the bars behind the signal, taken from the provider that produced them
    /// so the record cannot claim a source it did not come from.
    /// </param>
    private static AlertRecord ToAlert(StrategySignal signal, string? dataSource) => new()
    {
        Ticker = signal.Ticker,
        Strategy = signal.StrategyId,
        AlertPath = signal.Path,
        Direction = signal.Direction == TradeDirection.Long ? "LONG" : "SHORT",

        // Passed through whole. The engine deliberately does not know which levels a strategy
        // reports - naming them here is what tied the record to one strategy in the first
        // place, and every strategy added afterwards would have needed an edit to this line.
        Levels = signal.Context,

        TriggerPrice = signal.TriggerPrice,
        Timestamp = signal.TriggeredAt,
        DataSource = dataSource
    };
}
