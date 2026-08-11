namespace BreakoutAlerts.Core.Models;

/// <summary>
/// A single OHLCV price bar for one ticker at one timeframe.
/// </summary>
/// <remarks>
/// Declared as a <c>readonly record struct</c> deliberately. A scan across a few
/// hundred tickers holds tens of thousands of bars in memory at once; making these
/// reference types would put that entire working set on the heap and hand the GC a
/// large amount of short-lived garbage on every refresh.
///
/// Prices are <see cref="decimal"/> rather than <see cref="double"/>. Money must not
/// carry binary floating-point error - a strategy comparing a close against a level
/// for strict inequality has to be exact, or a breakout can be missed (or invented)
/// by a fraction of a cent that does not really exist.
/// </remarks>
/// <param name="Timestamp">
/// The bar's OPEN time, with offset. Always store the offset: session logic is defined
/// in US Eastern while the machine may be in any timezone, and a naive DateTime here
/// would silently reintroduce exactly the bug the Pine indicator had to guard against.
/// </param>
/// <param name="Open">Opening trade price of the bar.</param>
/// <param name="High">Highest trade price within the bar, wick included.</param>
/// <param name="Low">Lowest trade price within the bar, wick included.</param>
/// <param name="Close">Closing trade price of the bar.</param>
/// <param name="Volume">Shares traded during the bar.</param>
public readonly record struct Bar(
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume)
{
    /// <summary>True when the bar closed above its open (a "green" candle).</summary>
    /// <remarks>
    /// The retest-reversal leg of the ORB strategy triggers on candle body direction,
    /// so this is expressed once here rather than being re-derived at each call site
    /// where an inverted comparison would be easy to write and hard to spot.
    /// </remarks>
    public bool IsBullish => Close > Open;

    /// <summary>True when the bar closed below its open (a "red" candle).</summary>
    public bool IsBearish => Close < Open;

    /// <summary>
    /// The candle body's upper bound - the higher of open and close, ignoring wicks.
    /// </summary>
    /// <remarks>
    /// Breakout and invalidation rules are defined on the BODY, not the wick. Keeping
    /// that distinction in named members stops it from being lost in ad-hoc comparisons.
    /// </remarks>
    public decimal BodyHigh => Math.Max(Open, Close);

    /// <summary>The candle body's lower bound - the lower of open and close.</summary>
    public decimal BodyLow => Math.Min(Open, Close);
}
