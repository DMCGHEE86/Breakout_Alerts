using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.Core.Caching;

/// <summary>
/// Wraps a market data provider, accumulating completed sessions locally so history reaches
/// further back than the gateway will serve.
/// </summary>
/// <remarks>
/// <b>A decorator, not a change to the provider.</b> The moomoo adapter carries three
/// verified gateway quirks and is the most expensive code in the application to re-verify;
/// caching is an orthogonal concern and does not belong tangled up in it. This also means the
/// cache is testable against a substitute provider with no gateway at all.
///
/// <para><b>The current session is never served from cache.</b> It is still forming - its
/// high, its low and its bar count all change as the day goes on - so a cached copy would be
/// a frozen partial day presented as a whole one. Every request fetches today live and the
/// cache only ever contributes days that have finished.</para>
///
/// <para>Why this exists: the gateway serves roughly three days, bounded by its 1000-bar
/// response cap. That is enough for the scanner, which only cares about today, but not for
/// charting an older alert - and not for any strategy that reasons about a previous session.
/// Accumulating locally is the only way this application ever sees more.</para>
/// </remarks>
public sealed class CachingMarketDataProvider : IMarketDataProvider, IDisposable
{
    /// <summary>How many stored sessions to consider when topping up a request.</summary>
    /// <remarks>
    /// Bounded so a request cannot degrade into reading a year of files. Ten sessions covers
    /// two trading weeks, which is well beyond what any current caller asks for.
    /// </remarks>
    private const int MaxCachedSessions = 10;

    private readonly IMarketDataProvider _inner;
    private readonly IBarCache _cache;
    private readonly ILogger<CachingMarketDataProvider> _logger;

    /// <inheritdoc />
    public bool IsConnected => _inner.IsConnected;

    /// <inheritdoc />
    public string DataSource => _inner.DataSource;

    /// <inheritdoc />
    public event EventHandler<bool>? ConnectionStateChanged
    {
        add => _inner.ConnectionStateChanged += value;
        remove => _inner.ConnectionStateChanged -= value;
    }

    /// <inheritdoc />
    public event EventHandler<TickerSnapshot>? SnapshotUpdated
    {
        add => _inner.SnapshotUpdated += value;
        remove => _inner.SnapshotUpdated -= value;
    }

    /// <summary>Creates the decorator.</summary>
    public CachingMarketDataProvider(
        IMarketDataProvider inner, IBarCache cache, ILogger<CachingMarketDataProvider> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bar>> GetBarsAsync(
        string ticker, int timeframeMinutes, int count, CancellationToken cancellationToken = default)
    {
        var live = await _inner.GetBarsAsync(ticker, timeframeMinutes, count, cancellationToken)
            .ConfigureAwait(false);

        var today = MarketSession.SessionDate(DateTimeOffset.Now);

        await StoreCompletedSessionsAsync(ticker, timeframeMinutes, live, today, cancellationToken)
            .ConfigureAwait(false);

        // Merged on timestamp, live winning. A session the gateway still serves is the more
        // authoritative copy - it may carry a correction the cache was written before.
        var merged = new SortedDictionary<DateTimeOffset, Bar>();

        if (live.Count < count)
        {
            foreach (var bar in await ReadOlderSessionsAsync(
                         ticker, timeframeMinutes, live, today, cancellationToken).ConfigureAwait(false))
            {
                merged[bar.Timestamp] = bar;
            }
        }

        foreach (var bar in live)
        {
            merged[bar.Timestamp] = bar;
        }

        var result = merged.Values.ToList();
        return result.Count > count ? result.Skip(result.Count - count).ToList() : result;
    }

    /// <summary>Writes every completed session in a live response to the cache.</summary>
    private async Task StoreCompletedSessionsAsync(
        string ticker, int timeframeMinutes, IReadOnlyList<Bar> live, DateOnly today, CancellationToken ct)
    {
        foreach (var group in live.GroupBy(b => MarketSession.SessionDate(b.Timestamp)))
        {
            // Today is skipped - it is still forming. See the class remarks.
            if (group.Key >= today)
            {
                continue;
            }

            await _cache
                .StoreSessionAsync(ticker, timeframeMinutes, group.Key,
                    group.OrderBy(b => b.Timestamp).ToList(), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Reads stored sessions older than anything the live response covered.</summary>
    private async Task<List<Bar>> ReadOlderSessionsAsync(
        string ticker, int timeframeMinutes, IReadOnlyList<Bar> live, DateOnly today, CancellationToken ct)
    {
        var earliestLive = live.Count > 0
            ? MarketSession.SessionDate(live[0].Timestamp)
            : today;

        var stored = await _cache.GetStoredSessionsAsync(ticker, timeframeMinutes, ct).ConfigureAwait(false);
        var extra = new List<Bar>();

        foreach (var session in stored.Where(s => s < earliestLive).Take(MaxCachedSessions))
        {
            extra.AddRange(await _cache.GetSessionAsync(ticker, timeframeMinutes, session, ct).ConfigureAwait(false));
        }

        if (extra.Count > 0)
        {
            _logger.LogDebug("Cache supplied {Count} bars for {Ticker} before {Date}",
                extra.Count, ticker, earliestLive);
        }

        return extra;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TickerSnapshot>> GetSnapshotsAsync(
        IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default) =>
        _inner.GetSnapshotsAsync(tickers, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Never cached. Option quotes are worthless the moment they are stale.</remarks>
    public Task<IReadOnlyList<OptionQuote>> GetOptionChainAsync(
        string underlying, DateOnly? expiry = null, CancellationToken cancellationToken = default) =>
        _inner.GetOptionChainAsync(underlying, expiry, cancellationToken);

    /// <inheritdoc />
    public Task SubscribeAsync(
        IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default) =>
        _inner.SubscribeAsync(tickers, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
