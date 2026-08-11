using System.Collections.Concurrent;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.Services;

/// <summary>
/// Phase 1.5 stand-in for the moomoo gateway. Replays a synthetic but session-accurate
/// trading day so real strategies can be exercised with no network and no OpenD.
/// </summary>
/// <remarks>
/// <b>This replaced a timestamp-agnostic random walk, and had to.</b> The opening range
/// strategy locks its range from bars falling between 09:30 and 09:45 Eastern. A feed that
/// emits plausible prices at meaningless times has no such window, so the strategy would
/// find no range and fire nothing at all. Generating a real session with correct clock
/// times is what makes the port testable before the gateway exists.
///
/// <para><b>Simulated clock.</b> A full session takes six and a half hours, which is
/// useless for development. The clock starts just before the open and advances
/// <see cref="TimeAccelerationFactor"/> times faster than real time, so a breakout arrives
/// within a minute or two of launch. Bars are only ever returned up to the simulated
/// present - the strategy never sees the future, which would make any signal meaningless.</para>
///
/// <para><b>Deterministic.</b> Fixed seed, so a given launch replays the same day. Random
/// mock data makes UI and logic bugs unreproducible.</para>
/// </remarks>
public sealed class MockMarketDataProvider : IMarketDataProvider, IDisposable
{
    /// <summary>Simulated minutes elapsed per real second.</summary>
    public const int TimeAccelerationFactor = 30;

    private readonly Random _random = new(20260804);
    private readonly ConcurrentDictionary<string, DaySeries> _series = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MockMarketDataProvider> _logger;
    private readonly Timer _tickTimer;
    private readonly DateTimeOffset _wallClockStart;
    private readonly DateTimeOffset _simulatedStart;
    private bool _disposed;

    /// <summary>Symbols the mock can generate. Watchlist entries outside this set return nothing.</summary>
    public static readonly IReadOnlyList<(string Ticker, string Name, decimal Price)> KnownUniverse =
    [
        ("AMD", "Advanced Micro Devices", 484.64m),
        ("NVDA", "NVIDIA Corp", 212.38m),
        ("TSLA", "Tesla Inc", 322.08m),
        ("AAPL", "Apple Inc", 303.42m),
        ("MSFT", "Microsoft Corp", 518.75m),
        ("META", "Meta Platforms", 742.19m),
        ("AMZN", "Amazon.com Inc", 248.91m),
        ("GOOGL", "Alphabet Inc", 201.55m),
        ("NFLX", "Netflix Inc", 73.33m),
        ("AVGO", "Broadcom Inc", 388.02m),
        ("COIN", "Coinbase Global", 412.67m),
        ("PLTR", "Palantir Technologies", 178.44m)
    ];

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// Every alert produced from this provider is permanently marked as generated. A
    /// backtest run over the alert log can then exclude these lines instead of treating
    /// invented prices as historical fact.
    /// </remarks>
    public string DataSource => "synthetic";

    /// <inheritdoc />
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <inheritdoc />
    public event EventHandler<TickerSnapshot>? SnapshotUpdated;

    /// <summary>Creates the provider and generates one session per known symbol.</summary>
    public MockMarketDataProvider(ILogger<MockMarketDataProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Anchor to the most recent weekday so the replayed session is a plausible trading
        // day rather than a Saturday.
        var today = DateTime.Now.Date;
        while (today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            today = today.AddDays(-1);
        }

        var easternOffset = MarketSession.ExchangeTimeZone.GetUtcOffset(today);

        // Start the simulated clock at 09:29 - one minute before the open - so the premarket
        // levels are already established and the opening range forms immediately.
        _simulatedStart = new DateTimeOffset(today.Add(new TimeSpan(9, 29, 0)), easternOffset);
        _wallClockStart = DateTimeOffset.Now;

        foreach (var (ticker, name, price) in KnownUniverse)
        {
            _series[ticker] = GenerateSession(ticker, name, price, today, easternOffset);
        }

        IsConnected = true;

        _tickTimer = new Timer(OnTick, null, TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(750));

        _logger.LogInformation(
            "Mock session replay generated for {Count} symbols, starting {Start:HH:mm} ET at {Factor}x",
            _series.Count, _simulatedStart, TimeAccelerationFactor);
    }

    /// <summary>Simulated exchange time right now.</summary>
    public DateTimeOffset SimulatedNow =>
        _simulatedStart + TimeSpan.FromMinutes((DateTimeOffset.Now - _wallClockStart).TotalSeconds * TimeAccelerationFactor);

    /// <summary>
    /// Builds one full session of 1-minute bars: premarket, opening range, then a scenario.
    /// </summary>
    /// <remarks>
    /// Scenarios are assigned round-robin rather than randomly so every run exercises all of
    /// them - including the two premarket geometries that decide whether PATH-3 fires or is
    /// suppressed. A purely random assignment could easily produce a session where the
    /// suppression branch is never hit and a bug in it goes unnoticed.
    /// </remarks>
    private DaySeries GenerateSession(string ticker, string name, decimal basePrice, DateTime day, TimeSpan offset)
    {
        var bars = new List<Bar>(760);
        var index = _series.Count;
        var scenario = (Scenario)(index % 4);

        // Premarket high sits outside the opening range for even-indexed symbols and inside
        // it for odd ones, so both PATH-3 branches occur in every replay.
        var premarketOutsideRange = index % 2 == 0;

        var price = basePrice;
        var cursor = new DateTimeOffset(day.Add(new TimeSpan(4, 0, 0)), offset);
        var premarketEnd = new DateTimeOffset(day.Add(new TimeSpan(9, 30, 0)), offset);

        // ---- Premarket 04:00-09:30 ----
        // Reducing volatility alone does NOT keep the premarket range inside the opening
        // range: premarket is 330 bars against the range's 15, so even a quiet random walk
        // accumulates a far wider extreme. Symbols that need the premarket level INSIDE the
        // range are hard-clamped to a band narrower than the range will be, which is the
        // only way to guarantee the geometry rather than hope for it.
        var clampBand = premarketOutsideRange ? (decimal?)null : basePrice * 0.0015m;

        while (cursor < premarketEnd)
        {
            var bar = NextBar(ref price, cursor, volatility: 0.0006m, bias: 0m);

            if (clampBand is { } band)
            {
                var lower = basePrice - band;
                var upper = basePrice + band;
                bar = bar with
                {
                    Open = Math.Clamp(bar.Open, lower, upper),
                    High = Math.Clamp(bar.High, lower, upper),
                    Low = Math.Clamp(bar.Low, lower, upper),
                    Close = Math.Clamp(bar.Close, lower, upper)
                };

                // Feed the clamped close back, or the running price drifts outside the band
                // and every subsequent bar is pinned flat against the boundary.
                price = bar.Close;
            }

            bars.Add(bar);
            cursor = cursor.AddMinutes(1);
        }

        // Pull price back toward the base so the opening range forms around it rather than
        // wherever the premarket walk happened to end.
        price = basePrice;

        // ---- Opening range 09:30-09:45 ----
        var rangeEnd = new DateTimeOffset(day.Add(new TimeSpan(9, 45, 0)), offset);
        while (cursor < rangeEnd)
        {
            bars.Add(NextBar(ref price, cursor, volatility: 0.0009m, bias: 0m));
            cursor = cursor.AddMinutes(1);
        }

        // ---- Post-lock 09:45-16:00 ----
        // Bias drives the scenario: a sustained push produces a clean breakout, chop keeps
        // price inside the range so nothing fires, and the fade cases break out and then
        // reverse back through - which is what exercises PATH-2 invalidation.
        var sessionEnd = new DateTimeOffset(day.Add(new TimeSpan(16, 0, 0)), offset);
        var minutesAfterLock = 0;

        while (cursor < sessionEnd)
        {
            // Bias is per-minute and compounds over the 375 minutes after the lock, so these
            // numbers are much smaller than they look. The first pass used 0.00022, which
            // produced 8-11% daily moves on mega-caps and made the watchlist read as
            // nonsense. These land around 2-4%, which is a plausible trending session.
            var bias = scenario switch
            {
                Scenario.BreakoutUp => 0.00008m,
                Scenario.BreakoutDown => -0.00008m,
                Scenario.FadeAfterBreak => minutesAfterLock < 45 ? 0.00030m : -0.00012m,
                _ => 0m
            };

            bars.Add(NextBar(ref price, cursor, volatility: 0.0008m, bias: bias));
            cursor = cursor.AddMinutes(1);
            minutesAfterLock++;
        }

        return new DaySeries(ticker, name, basePrice, bars);
    }

    /// <summary>Produces one 1-minute bar and advances the running price.</summary>
    private Bar NextBar(ref decimal price, DateTimeOffset timestamp, decimal volatility, decimal bias)
    {
        var open = price;
        var shock = (decimal)((_random.NextDouble() * 2) - 1) * price * volatility;
        var close = Math.Round(open + shock + (price * bias), 2);

        // Wicks extend beyond the body by a fraction of the bar's own range, so retests can
        // pierce a level without the body closing through it - the exact geometry PATH-2
        // depends on.
        var wick = Math.Abs(close - open) * (decimal)(0.3 + (_random.NextDouble() * 0.9));
        var high = Math.Round(Math.Max(open, close) + wick, 2);
        var low = Math.Round(Math.Min(open, close) - wick, 2);

        price = close;
        return new Bar(timestamp, open, high, low, close, _random.Next(20_000, 300_000));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        string ticker, int timeframeMinutes, int count, CancellationToken cancellationToken = default)
    {
        if (!_series.TryGetValue(ticker, out var series) || timeframeMinutes <= 0)
        {
            return Task.FromResult<IReadOnlyList<Bar>>([]);
        }

        var now = SimulatedNow;

        // Only bars whose close has already passed. Returning a bar that has not finished
        // forming would let the strategy act on a candle that can still change.
        var closed = series.Minutes
            .Where(b => b.Timestamp.AddMinutes(1) <= now)
            .ToList();

        var aggregated = Aggregate(closed, timeframeMinutes);

        // Drop a final bucket that has not finished forming. Filtering the 1-minute source
        // by close time is not enough: at 09:37 the 09:35-09:40 bucket contains only two
        // minutes of data, and aggregating it yields a 5-minute bar with a too-narrow high
        // and low. The strategy consumes that partial bar, records it as processed, and
        // never revisits it once the real bar completes - so a partial opening-range bar
        // permanently corrupts the locked range.
        //
        // IPriceStrategy.Evaluate documents that the caller must never pass a partially
        // formed bar. This is the provider holding up its end of that contract.
        if (aggregated.Count > 0)
        {
            var lastBucketEnd = aggregated[^1].Timestamp.AddMinutes(timeframeMinutes);
            if (lastBucketEnd > now)
            {
                aggregated.RemoveAt(aggregated.Count - 1);
            }
        }

        if (aggregated.Count > count)
        {
            aggregated = aggregated.Skip(aggregated.Count - count).ToList();
        }

        return Task.FromResult<IReadOnlyList<Bar>>(aggregated);
    }

    /// <summary>Rolls 1-minute bars up into a larger timeframe.</summary>
    /// <remarks>
    /// Buckets are anchored to the hour, so a 5-minute bar always starts at :00, :05, :10
    /// and so on. That is what makes the 09:30-09:45 window land on exact bar boundaries -
    /// the same alignment property the Pine indicator relies on.
    /// </remarks>
    private static List<Bar> Aggregate(List<Bar> minutes, int timeframeMinutes)
    {
        if (timeframeMinutes == 1)
        {
            return minutes;
        }

        var result = new List<Bar>(minutes.Count / timeframeMinutes + 1);

        foreach (var group in minutes.GroupBy(b =>
                     new DateTimeOffset(
                         b.Timestamp.Year, b.Timestamp.Month, b.Timestamp.Day,
                         b.Timestamp.Hour,
                         b.Timestamp.Minute / timeframeMinutes * timeframeMinutes,
                         0, b.Timestamp.Offset))
                 .OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(b => b.Timestamp).ToList();
            result.Add(new Bar(
                group.Key,
                ordered[0].Open,
                ordered.Max(b => b.High),
                ordered.Min(b => b.Low),
                ordered[^1].Close,
                ordered.Sum(b => b.Volume)));
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TickerSnapshot>> GetSnapshotsAsync(
        IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default)
    {
        var results = tickers
            .Where(_series.ContainsKey)
            .Select(t => BuildSnapshot(_series[t]))
            .ToList();

        return Task.FromResult<IReadOnlyList<TickerSnapshot>>(results);
    }

    /// <summary>Derives a snapshot from the replay position.</summary>
    private TickerSnapshot BuildSnapshot(DaySeries series)
    {
        var now = SimulatedNow;
        var elapsed = series.Minutes.Where(b => b.Timestamp <= now).ToList();
        var last = elapsed.Count > 0 ? elapsed[^1].Close : series.BasePrice;

        // Levels are computed from the same bars the strategy sees, so the grid and the
        // alerts can never disagree about where the range is.
        var rangeBars = elapsed.Where(b => MarketSession.IsOpeningRange(b.Timestamp)).ToList();
        var premarketBars = elapsed.Where(b => MarketSession.IsPremarket(b.Timestamp)).ToList();

        decimal? orbHigh = rangeBars.Count > 0 ? rangeBars.Max(b => b.High) : null;
        decimal? orbLow = rangeBars.Count > 0 ? rangeBars.Min(b => b.Low) : null;

        var position = orbHigh is { } oh && orbLow is { } ol
            ? last > oh ? RangePosition.Above : last < ol ? RangePosition.Below : RangePosition.Inside
            : RangePosition.Unknown;

        var change = last - series.PreviousClose;

        return new TickerSnapshot
        {
            Ticker = series.Ticker,
            Name = series.Name,
            Last = last,
            Change = change,
            ChangePercent = series.PreviousClose == 0 ? 0d : (double)(change / series.PreviousClose),
            Volume = elapsed.Sum(b => b.Volume),
            OrbHigh = orbHigh,
            OrbLow = orbLow,
            PremarketHigh = premarketBars.Count > 0 ? premarketBars.Max(b => b.High) : null,
            PremarketLow = premarketBars.Count > 0 ? premarketBars.Min(b => b.Low) : null,
            Position = position,
            UpdatedAt = now
        };
    }

    private void OnTick(object? _)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            foreach (var series in _series.Values)
            {
                SnapshotUpdated?.Invoke(this, BuildSnapshot(series));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mock snapshot tick failed");
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OptionQuote>> GetOptionChainAsync(
        string underlying, DateOnly? expiry = null, CancellationToken cancellationToken = default)
    {
        if (!_series.TryGetValue(underlying, out var series))
        {
            return Task.FromResult<IReadOnlyList<OptionQuote>>([]);
        }

        var spot = BuildSnapshot(series).Last;
        var quotes = new List<OptionQuote>();

        // Several expiries, so the candidate ranker has a time dimension to choose across
        // rather than only strikes.
        foreach (var days in (ReadOnlySpan<int>)[3, 10, 17, 31])
        {
            var contractExpiry = expiry ?? DateOnly.FromDateTime(DateTime.Today.AddDays(days));
            var spacing = spot switch
            {
                < 50m => 1m,
                < 200m => 2.5m,
                < 500m => 5m,
                _ => 10m
            };

            var atm = Math.Round(spot / spacing, MidpointRounding.ToEven) * spacing;

            for (var step = -8; step <= 8; step++)
            {
                var strike = atm + (spacing * step);
                if (strike <= 0)
                {
                    continue;
                }

                quotes.Add(BuildQuote(underlying, strike, spot, OptionRight.Call, contractExpiry, days));
                quotes.Add(BuildQuote(underlying, strike, spot, OptionRight.Put, contractExpiry, days));
            }

            if (expiry.HasValue)
            {
                break;
            }
        }

        return Task.FromResult<IReadOnlyList<OptionQuote>>(quotes);
    }

    /// <summary>Approximate contract pricing. The real engine lands in Phase 2.</summary>
    private OptionQuote BuildQuote(
        string underlying, decimal strike, decimal spot,
        OptionRight right, DateOnly expiry, int daysToExpiry)
    {
        var moneyness = (double)((spot - strike) / spot);
        var intrinsic = right == OptionRight.Call
            ? Math.Max(0m, spot - strike)
            : Math.Max(0m, strike - spot);

        var timeValue = (decimal)(Math.Exp(-Math.Abs(moneyness) * 8) * Math.Sqrt(daysToExpiry) * 0.9);
        var mid = Math.Round(intrinsic + timeValue, 2);
        var spread = Math.Max(0.01m, Math.Round(mid * 0.03m, 2));
        var iv = 0.28 + (Math.Abs(moneyness) * 0.55) + (_random.NextDouble() * 0.03);

        var delta = right == OptionRight.Call
            ? Math.Clamp(0.5 + (moneyness * 2.2), 0.01, 0.99)
            : -Math.Clamp(0.5 - (moneyness * 2.2), 0.01, 0.99);

        return new OptionQuote
        {
            Underlying = underlying,
            ContractId = $"{underlying}{expiry:yyMMdd}{(right == OptionRight.Call ? 'C' : 'P')}{strike:0000}",
            Right = right,
            Strike = strike,
            Expiry = expiry,
            Bid = Math.Max(0.01m, mid - spread),
            Ask = mid + spread,
            Last = mid,
            Volume = _random.Next(0, 12_000),
            OpenInterest = _random.Next(50, 60_000),
            ImpliedVolatility = Math.Round(iv, 4),
            Delta = Math.Round(delta, 4),
            Gamma = Math.Round(Math.Exp(-Math.Abs(moneyness) * 9) * 0.055, 4),
            Theta = Math.Round(-Math.Exp(-Math.Abs(moneyness) * 6) * 0.42 / Math.Max(1, Math.Sqrt(daysToExpiry)), 4),
            Vega = Math.Round(Math.Exp(-Math.Abs(moneyness) * 5) * 0.31, 4),
            DaysToExpiry = daysToExpiry
        };
    }

    /// <inheritdoc />
    public Task SubscribeAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Mock subscribe for {Count} tickers", tickers.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tickTimer.Dispose();
        IsConnected = false;
        ConnectionStateChanged?.Invoke(this, false);
    }

    /// <summary>Shape of the generated day for one symbol.</summary>
    private sealed record DaySeries(string Ticker, string Name, decimal BasePrice, List<Bar> Minutes)
    {
        /// <summary>Reference close for the change column.</summary>
        public decimal PreviousClose { get; } = Math.Round(BasePrice * 0.988m, 2);
    }

    /// <summary>Session shapes assigned round-robin across the universe.</summary>
    private enum Scenario
    {
        BreakoutUp,
        BreakoutDown,
        Chop,
        FadeAfterBreak
    }
}
