namespace BreakoutAlerts.Core.Models;

/// <summary>
/// A live row in the scanner grid - the current state of one watched ticker.
/// </summary>
/// <remarks>
/// A record rather than a struct despite the earlier reasoning about <see cref="Bar"/>:
/// there is one of these per watched symbol (hundreds, not tens of thousands), and it is
/// bound directly to the UI, where reference semantics are what data binding expects.
/// </remarks>
public sealed record TickerSnapshot
{
    /// <summary>Symbol, e.g. "AMD".</summary>
    public required string Ticker { get; init; }

    /// <summary>Company or instrument name for display.</summary>
    public string? Name { get; init; }

    /// <summary>Most recent traded price.</summary>
    public decimal Last { get; init; }

    /// <summary>Absolute change against the previous session's close.</summary>
    public decimal Change { get; init; }

    /// <summary>Change as a proportion of the previous close. 0.0178 means +1.78%.</summary>
    public double ChangePercent { get; init; }

    /// <summary>Cumulative volume for the current session.</summary>
    public long Volume { get; init; }

    /// <summary>Locked opening range high, or null before 09:45 ET.</summary>
    public decimal? OrbHigh { get; init; }

    /// <summary>Locked opening range low, or null before 09:45 ET.</summary>
    public decimal? OrbLow { get; init; }

    /// <summary>Premarket high, or null when no extended-hours data exists.</summary>
    public decimal? PremarketHigh { get; init; }

    /// <summary>Premarket low, or null when no extended-hours data exists.</summary>
    public decimal? PremarketLow { get; init; }

    /// <summary>Where price currently sits relative to the opening range.</summary>
    public RangePosition Position { get; init; }

    /// <summary>When this snapshot was produced.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Where the last price sits relative to the locked opening range.</summary>
/// <remarks>
/// Precomputed rather than derived in a binding converter, because the scanner sorts and
/// filters on it. Recomputing per row during a sort would be wasteful, and duplicating
/// the comparison in a converter risks it drifting out of step with the strategy.
/// </remarks>
public enum RangePosition
{
    /// <summary>The range has not locked yet, or no data.</summary>
    Unknown,

    /// <summary>Trading inside the opening range.</summary>
    Inside,

    /// <summary>Trading above the opening range high.</summary>
    Above,

    /// <summary>Trading below the opening range low.</summary>
    Below
}

/// <summary>
/// One contract in an option chain, with its Greeks.
/// </summary>
/// <remarks>
/// Greeks are nullable because they are not always available: a contract with no bid/ask
/// has no meaningful implied volatility, and every Greek derived from it is therefore
/// undefined. Returning 0.0 for "unknown delta" would make a worthless contract look
/// like a legitimate deep-OTM one.
/// </remarks>
public sealed record OptionQuote
{
    /// <summary>Underlying symbol.</summary>
    public required string Underlying { get; init; }

    /// <summary>Full contract identifier.</summary>
    public required string ContractId { get; init; }

    /// <summary>Call or put.</summary>
    public required OptionRight Right { get; init; }

    /// <summary>Strike price.</summary>
    public decimal Strike { get; init; }

    /// <summary>Expiration date.</summary>
    public DateOnly Expiry { get; init; }

    /// <summary>Best bid.</summary>
    public decimal? Bid { get; init; }

    /// <summary>Best ask.</summary>
    public decimal? Ask { get; init; }

    /// <summary>Last traded price.</summary>
    public decimal? Last { get; init; }

    /// <summary>Contracts traded today.</summary>
    public long Volume { get; init; }

    /// <summary>Contracts currently outstanding.</summary>
    public long OpenInterest { get; init; }

    /// <summary>Implied volatility as a proportion. 0.45 means 45%.</summary>
    public double? ImpliedVolatility { get; init; }

    /// <summary>Rate of change of option price with respect to the underlying.</summary>
    public double? Delta { get; init; }

    /// <summary>Rate of change of delta with respect to the underlying.</summary>
    public double? Gamma { get; init; }

    /// <summary>Daily time decay.</summary>
    public double? Theta { get; init; }

    /// <summary>Sensitivity to a one-point change in implied volatility.</summary>
    public double? Vega { get; init; }

    /// <summary>Whole days until expiry.</summary>
    public int DaysToExpiry { get; init; }

    /// <summary>Midpoint of the spread, or null when either side is missing.</summary>
    public decimal? Mid => Bid.HasValue && Ask.HasValue ? (Bid.Value + Ask.Value) / 2m : null;
}

/// <summary>Call or put.</summary>
public enum OptionRight
{
    /// <summary>Right to buy the underlying.</summary>
    Call,

    /// <summary>Right to sell the underlying.</summary>
    Put
}
