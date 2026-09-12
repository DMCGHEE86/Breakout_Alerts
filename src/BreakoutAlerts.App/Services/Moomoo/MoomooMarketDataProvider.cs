using System.Collections.Concurrent;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Configuration;
using BreakoutAlerts.Core.Models;
using Futu.OpenApi.Pb;
using Microsoft.Extensions.Logging;
using PriceBar = BreakoutAlerts.Core.Models.Bar;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Live market data from the moomoo OpenD gateway.
/// </summary>
/// <remarks>
/// Handles three behaviours of the moomoo API that were verified against a live gateway on
/// 2026-08-04 and that all differ from what a first reading of the documentation suggests.
/// Each would produce a subtly wrong result rather than an obvious failure:
///
/// <list type="number">
/// <item><b><c>RequestHistoryKL</c>'s EndTime is exclusive.</b> An end of "today" returns
/// data up to yesterday's close, which is what made this call look like it served completed
/// days only. Today's premarket bars were therefore always missing, so the premarket high
/// and low were never computed for the live session and PATH-3 could not fire on it. The
/// request uses T+1. <c>Qot_Sub</c> plus <c>GetKL</c> is still issued and stitched in,
/// because that path is the one guaranteed to carry the most recently closed bar.</item>
/// <item><b>Extended hours are off by default.</b> Without <c>ExtendedTime</c> a five-day
/// request returned 390 bars instead of 960 - regular session only. No premarket bars means
/// no premarket high or low, which means PATH-3 can never fire.</item>
/// <item><b>Bars are stamped with their CLOSE time.</b> The domain, and every session
/// window in <c>MarketSession</c>, assumes the OPEN. Passed through unchanged the half-open
/// 09:30-09:45 window captures two bars instead of three and silently produces a wrong
/// opening range.</item>
/// </list>
///
/// <para>Timeframes are requested from the gateway directly rather than aggregated from
/// 1-minute data. Local aggregation already caused a defect in this codebase - a partially
/// formed bucket corrupting the locked range - and broker-supplied bars cannot disagree
/// with the broker's own charts.</para>
/// </remarks>
public sealed class MoomooMarketDataProvider : IMarketDataProvider, IDisposable
{
    /// <summary>How far ahead option expiries are requested, in days.</summary>
    /// <remarks>
    /// The gateway caps the option-chain window at 30 days and rejects anything wider. Held
    /// as a named constant so the limit is visible at the point someone would widen it.
    /// </remarks>
    private const int OptionExpiryWindowDays = 28;

    /// <summary>Calendar days of history requested per call.</summary>
    /// <remarks>
    /// Five rather than three, because a strategy that needs the <i>previous</i> session needs
    /// it on a Tuesday after a holiday Monday too - where three days back stops at Saturday and
    /// the previous session is Friday. The response is paged, so this no longer trades against
    /// the 1000-bar ceiling the way it used to.
    /// </remarks>
    private const int HistoryLookbackDays = 5;

    /// <summary>Ceiling the gateway enforces on a single history response.</summary>
    private const int MaxHistoryBars = 1000;

    /// <summary>Pages followed before giving up on a history request.</summary>
    /// <remarks>
    /// Five pages is 5000 bars - far more than any window this application asks for, so
    /// reaching it means the gateway is repeating itself rather than that the data is large.
    /// </remarks>
    private const int MaxHistoryPages = 5;

    private readonly MoomooConnection _connection;
    private readonly MarketDataOptions _options;
    private readonly ILogger<MoomooMarketDataProvider> _logger;

    // Symbols already subscribed for K-line push. Subscribing twice is harmless but wastes
    // quota, and the budget is finite.
    private readonly ConcurrentDictionary<string, byte> _subscribed = new(StringComparer.OrdinalIgnoreCase);

    // Symbols whose quotes feed the watchlist rail, and the loop that refreshes them.
    private readonly CancellationTokenSource _quoteCts = new();
    private readonly object _quoteGate = new();
    private string[] _quoteSymbols = [];
    private Task? _quoteLoop;

    /// <inheritdoc />
    public bool IsConnected => _connection.IsConnected;

    /// <inheritdoc />
    public string DataSource => "moomoo";

    /// <inheritdoc />
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <inheritdoc />
    /// <remarks>
    /// Raised from an internal polling loop rather than from a gateway push channel. The
    /// distinction matters and is deliberate: bar data for the strategy is never pushed,
    /// because the scanner already polls on its own cadence and a second delivery path for
    /// the same bars would be a second source of truth. These snapshots are display only -
    /// they feed the watchlist rail's price and change column and nothing evaluates them -
    /// so a poll here cannot disagree with anything.
    /// </remarks>
    public event EventHandler<TickerSnapshot>? SnapshotUpdated;

    /// <summary>Creates the provider over an established connection.</summary>
    public MoomooMarketDataProvider(
        MoomooConnection connection,
        MarketDataOptions options,
        ILogger<MoomooMarketDataProvider> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connection.ConnectionStateChanged += (_, state) =>
        {
            if (!state)
            {
                // Subscriptions do not survive a reconnect, so forget them or the next
                // GetKL will be issued against a subscription the gateway no longer has.
                _subscribed.Clear();
            }

            ConnectionStateChanged?.Invoke(this, state);
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PriceBar>> GetBarsAsync(
        string ticker, int timeframeMinutes, int count, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return [];
        }

        var klType = MoomooMapping.ToKLType(timeframeMinutes);
        if (klType is null)
        {
            _logger.LogWarning("Unsupported timeframe {Minutes}m - only 1, 3, 5 and 15 divide the 15-minute range evenly", timeframeMinutes);
            return [];
        }

        var history = await FetchCompletedDaysAsync(ticker, klType.Value, cancellationToken).ConfigureAwait(false);
        var today = await FetchCurrentSessionAsync(ticker, klType.Value, cancellationToken).ConfigureAwait(false);

        // Merged on timestamp. The two calls can overlap at a day boundary, and a duplicate
        // bar would be processed twice by the strategy.
        var merged = new SortedDictionary<DateTimeOffset, PriceBar>();
        foreach (var bar in history.Concat(today))
        {
            merged[bar.Timestamp] = bar;
        }

        var result = merged.Values.ToList();
        return result.Count > count ? result.Skip(result.Count - count).ToList() : result;
    }

    /// <summary>
    /// Bars from history, including today's extended-hours session.
    /// </summary>
    /// <remarks>
    /// <b><c>EndTime</c> is exclusive.</b> Asking for an end of "today" returns everything up
    /// to <i>yesterday's</i> close, which is why this call originally appeared to serve
    /// completed days only - and why a today-only request looked like it returned nothing,
    /// when in fact it was an empty range. The consequence was not a visible failure: today's
    /// premarket bars were simply absent, so the premarket high and low were never computed
    /// for the current session and <b>PATH-3 could not fire on the day being traded</b>.
    /// Measured against the live gateway on 2026-08-04 - an end of T+1 returned 40 premarket
    /// bars for today where an end of T returned none.
    ///
    /// <para><b>The 1000-bar cap is paged, not avoided.</b> The gateway caps one response and
    /// truncates the overflow from the <i>newest</i> end - so an over-long request does not
    /// return less history, it silently discards today's later bars while still handing back a
    /// plausible-looking thousand. The window used to be kept deliberately short to stay under
    /// that ceiling, which worked only for as long as nothing needed more data. Following
    /// <c>NextReqKey</c> removes the ceiling instead of dodging it.</para>
    ///
    /// <para><b>Session_ALL, so the request covers the whole 24 hours.</b> With only
    /// <c>ExtendedTime</c> set the response runs 04:00-20:00, and the missing 20:00-04:00
    /// block was twice mistaken for an absence of data rather than an absence of asking - see
    /// <c>--probe-sessions</c>, which reports what each session mode actually returns.</para>
    /// </remarks>
    private async Task<List<PriceBar>> FetchCompletedDaysAsync(
        string ticker, QotCommon.KLType klType, CancellationToken ct)
    {
        var bars = new List<PriceBar>();
        Google.ProtocolBuffers.ByteString? nextKey = null;

        // Bounded. A gateway that kept handing back a key would otherwise spin here forever,
        // and an unbounded loop against a remote service is a hang waiting for a bad day.
        for (var page = 0; page < MaxHistoryPages; page++)
        {
            var builder = QotRequestHistoryKL.C2S.CreateBuilder()
                .SetSecurity(MoomooMapping.UsSecurity(ticker))
                .SetKlType((int)klType)
                .SetRehabType((int)QotCommon.RehabType.RehabType_None)
                .SetBeginTime(DateTime.Today.AddDays(-HistoryLookbackDays).ToString("yyyy-MM-dd"))
                // Exclusive - see the remarks. T+1 is what includes today.
                .SetEndTime(DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"))
                .SetMaxAckKLNum(MaxHistoryBars)
                // Quirk 2. Without this the response contains regular-session bars only, there
                // is no premarket high or low, and PATH-3 can never fire. Subsumed by the
                // session below on current gateways; set anyway, so the two never disagree.
                .SetExtendedTime(true)
                // Verified 2026-09-12: RTH gives 09:30-16:00, ETH gives 04:00-20:00, and ALL
                // gives all 24 hours. OVERNIGHT is refused outright for history requests.
                .SetSession((int)Common.Session.Session_ALL);

            if (nextKey is not null)
            {
                builder.SetNextReqKey(nextKey);
            }

            var rsp = await _connection
                .RequestHistoryAsync(
                    QotRequestHistoryKL.Request.CreateBuilder().SetC2S(builder.Build()).Build(), ct)
                .ConfigureAwait(false);

            if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
            {
                _logger.LogWarning("History request failed for {Ticker}: {Msg}", ticker, rsp.RetMsg);

                // Partial pages are kept. Half a history is still better than none for a
                // strategy that only needs the recent end of it, and returning [] here would
                // turn a transient hiccup into a symbol that silently drops out of the scan.
                break;
            }

            bars.AddRange(rsp.S2C.KlListList.Select(k => MoomooMapping.ToBar(k, klType)));

            if (!rsp.S2C.HasNextReqKey || rsp.S2C.NextReqKey.Length == 0)
            {
                break;
            }

            nextKey = rsp.S2C.NextReqKey;
        }

        return bars;
    }

    /// <summary>
    /// Bars for the session in progress.
    /// </summary>
    /// <remarks>
    /// Quirk 1. <c>RequestHistoryKL</c> returns nothing at all for the current day, so
    /// today's bars require a subscription followed by <c>GetKL</c>. Without this the chart
    /// and the scanner would both be blind during exactly the hours they matter.
    /// </remarks>
    private async Task<List<PriceBar>> FetchCurrentSessionAsync(
        string ticker, QotCommon.KLType klType, CancellationToken ct)
    {
        await EnsureSubscribedAsync(ticker, klType, ct).ConfigureAwait(false);

        var c2s = QotGetKL.C2S.CreateBuilder()
            .SetSecurity(MoomooMapping.UsSecurity(ticker))
            .SetKlType((int)klType)
            .SetRehabType((int)QotCommon.RehabType.RehabType_None)
            .SetReqNum(1000)
            .Build();

        var rsp = await _connection
            .GetKlineAsync(QotGetKL.Request.CreateBuilder().SetC2S(c2s).Build(), ct)
            .ConfigureAwait(false);

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            _logger.LogWarning("Current-session K-line failed for {Ticker}: {Msg}", ticker, rsp.RetMsg);
            return [];
        }

        var bars = rsp.S2C.KlListList.Select(k => MoomooMapping.ToBar(k, klType)).ToList();

        // Drop a bar that has not finished forming. Body-close semantics are meaningless on
        // an in-progress candle, and the strategy records every bar it consumes as final -
        // so a partial bar is never revisited once the real one completes.
        if (bars.Count > 0 && bars[^1].Timestamp.AddMinutes((int)klType switch
        {
            _ => MoomooMapping.MinutesOf(klType)
        }) > DateTimeOffset.Now)
        {
            bars.RemoveAt(bars.Count - 1);
        }

        return bars;
    }

    private async Task EnsureSubscribedAsync(string ticker, QotCommon.KLType klType, CancellationToken ct)
    {
        if (!_subscribed.TryAdd(ticker, 0))
        {
            return;
        }

        var c2s = QotSub.C2S.CreateBuilder()
            .AddSecurityList(MoomooMapping.UsSecurity(ticker))
            .AddSubTypeList((int)MoomooMapping.ToSubType(klType))
            .SetIsSubOrUnSub(true)
            // No push registration: the scanner polls, so a push channel would be a second
            // source of truth for the same data.
            .SetIsRegOrUnRegPush(false)
            .Build();

        var rsp = await _connection
            .SubscribeAsync(QotSub.Request.CreateBuilder().SetC2S(c2s).Build(), ct)
            .ConfigureAwait(false);

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            // Forget it so a later cycle retries rather than silently never subscribing.
            _subscribed.TryRemove(ticker, out _);
            _logger.LogWarning("Subscribe failed for {Ticker}: {Msg}", ticker, rsp.RetMsg);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TickerSnapshot>> GetSnapshotsAsync(
        IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || tickers.Count == 0)
        {
            return [];
        }

        var builder = QotGetSecuritySnapshot.C2S.CreateBuilder();
        foreach (var ticker in tickers)
        {
            builder.AddSecurityList(MoomooMapping.UsSecurity(ticker));
        }

        var rsp = await _connection
            .GetSnapshotAsync(QotGetSecuritySnapshot.Request.CreateBuilder().SetC2S(builder.Build()).Build(), cancellationToken)
            .ConfigureAwait(false);

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            _logger.LogWarning("Snapshot request failed: {Msg}", rsp.RetMsg);
            return [];
        }

        return rsp.S2C.SnapshotListList.Select(MoomooMapping.ToSnapshot).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses the gateway's own delta filter. Verified live: AAPL returned 575 contracts
    /// unfiltered and 10 with a 0.60-0.70 band, so the candidate set costs one request
    /// rather than pulling the whole chain and filtering locally.
    /// </remarks>
    public async Task<IReadOnlyList<OptionQuote>> GetOptionChainAsync(
        string underlying, DateOnly? expiry = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return [];
        }

        // The gateway rejects any window wider than 30 days outright - "the requested time
        // span cannot exceed 30 days" - and the rejection is a failed response, not an
        // exception, so the original 45-day window silently returned an empty candidate list
        // on every single request. 28 days keeps a margin under the limit while still
        // covering the weekly and monthly expiries a swing entry would use.
        var begin = expiry?.ToString("yyyy-MM-dd") ?? DateTime.Today.ToString("yyyy-MM-dd");
        var end = expiry?.ToString("yyyy-MM-dd") ?? DateTime.Today.AddDays(OptionExpiryWindowDays).ToString("yyyy-MM-dd");

        // TWO requests, because the gateway's delta filter takes SIGNED bounds and puts have
        // NEGATIVE delta. A single 0.60-to-0.70 band therefore matches calls only, and every
        // put is excluded before the data ever arrives - which showed up as short alerts
        // producing an empty candidates pane while long alerts worked, and made the
        // "show both sides" toggle incapable of ever showing the other side.
        //
        // Widening to -0.70..0.70 would not work either: that band also contains everything
        // between -0.60 and 0.60, which is the entire rest of the chain.
        var contracts = new List<OptionContractRef>();

        foreach (var (min, max) in new[]
                 {
                     (_options.OptionMinimumDelta, _options.OptionMaximumDelta),
                     (-_options.OptionMaximumDelta, -_options.OptionMinimumDelta)
                 })
        {
            var filter = QotGetOptionChain.DataFilter.CreateBuilder()
                .SetDeltaMin(min)
                .SetDeltaMax(max)
                .Build();

            var c2s = QotGetOptionChain.C2S.CreateBuilder()
                .SetOwner(MoomooMapping.UsSecurity(underlying))
                .SetBeginTime(begin)
                .SetEndTime(end)
                .SetDataFilter(filter)
                .Build();

            var rsp = await _connection
                .GetOptionChainAsync(QotGetOptionChain.Request.CreateBuilder().SetC2S(c2s).Build(), cancellationToken)
                .ConfigureAwait(false);

            if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
            {
                _logger.LogWarning("Option chain failed for {Underlying} (delta {Min} to {Max}): {Msg}",
                    underlying, min, max, rsp.RetMsg);
                continue;
            }

            contracts.AddRange(MoomooMapping.ToOptionContracts(rsp, underlying));
        }

        if (contracts.Count == 0)
        {
            return [];
        }

        // The chain carries contract identity; quotes and Greeks come from a snapshot. One
        // batched call covers the whole filtered set, which is why the server-side delta
        // filter matters so much - it is the difference between ~10 contracts and ~575.
        return await EnrichWithQuotesAsync(contracts, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<OptionQuote>> EnrichWithQuotesAsync(
        List<OptionContractRef> contracts, CancellationToken ct)
    {
        var builder = QotGetSecuritySnapshot.C2S.CreateBuilder();
        foreach (var contract in contracts)
        {
            builder.AddSecurityList(contract.Security);
        }

        var rsp = await _connection
            .GetSnapshotAsync(QotGetSecuritySnapshot.Request.CreateBuilder().SetC2S(builder.Build()).Build(), ct)
            .ConfigureAwait(false);

        if (rsp.RetType != (int)Common.RetType.RetType_Succeed)
        {
            _logger.LogWarning("Option snapshot failed: {Msg}", rsp.RetMsg);
            return [];
        }

        return rsp.S2C.SnapshotListList
            .Select(MoomooMapping.ToOptionQuote)
            .Where(q => q is not null)
            .Select(q => q!)
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// K-line subscription is not done here - that happens lazily inside
    /// <see cref="GetBarsAsync"/>, where the timeframe is known; subscribing here would have
    /// to guess it and could spend quota on a bar size never requested.
    ///
    /// <para>What this does start is the quote refresh that populates the watchlist rail.
    /// Without it the rail rendered every row as an em dash under the live provider, because
    /// nothing was raising <see cref="SnapshotUpdated"/> at all.</para>
    /// </remarks>
    public Task SubscribeAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default)
    {
        lock (_quoteGate)
        {
            _quoteSymbols = tickers.ToArray();
            _quoteLoop ??= Task.Run(() => PollQuotesAsync(_quoteCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <summary>Refreshes watchlist quotes on an interval for as long as the app runs.</summary>
    /// <remarks>
    /// One batched request per tick covers the whole watchlist, so the cost is a fixed few
    /// requests per minute regardless of how long the list grows. Failures are swallowed
    /// after logging - a transient snapshot error must not kill the loop and leave the rail
    /// frozen on stale prices for the rest of the session, which is exactly the silent-stale
    /// state the connection indicator exists to prevent.
    /// </remarks>
    private async Task PollQuotesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.QuotePollSeconds)));

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            string[] symbols;
            lock (_quoteGate)
            {
                symbols = _quoteSymbols;
            }

            if (symbols.Length == 0 || !IsConnected)
            {
                continue;
            }

            try
            {
                foreach (var snapshot in await GetSnapshotsAsync(symbols, ct).ConfigureAwait(false))
                {
                    SnapshotUpdated?.Invoke(this, snapshot);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Watchlist quote refresh failed");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _quoteCts.Cancel();
        _quoteCts.Dispose();
        _subscribed.Clear();
    }
}

/// <summary>A contract's identity from the option chain, before quotes are attached.</summary>
public sealed record OptionContractRef(
    QotCommon.Security Security,
    string Underlying,
    OptionRight Right,
    decimal Strike,
    DateOnly Expiry);
