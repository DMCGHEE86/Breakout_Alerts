namespace BreakoutAlerts.Core.Strategies;

/// <summary>
/// US equity session boundaries, evaluated in exchange time.
/// </summary>
/// <remarks>
/// Every window here is defined in US Eastern and converted from the bar's own offset, so
/// results are identical regardless of the machine's timezone. This mirrors the hardcoded
/// exchange timezone in the Pine indicator, and for the same reason: the session is a
/// property of the exchange, not of whoever happens to be looking at the chart.
///
/// <para>Windows are half-open - <c>[start, end)</c> - so a bar opening exactly at 09:30 is
/// the first regular-session bar and is <b>not</b> counted as premarket. Getting that
/// boundary wrong shifts the entire opening range by one bar.</para>
/// </remarks>
public static class MarketSession
{
    /// <summary>Exchange timezone. Resolved once - the lookup is not cheap.</summary>
    /// <remarks>
    /// The IANA id works on Windows from .NET 6 onward because the runtime uses ICU. On
    /// older frameworks this would have needed the Windows id "Eastern Standard Time".
    /// </remarks>
    public static readonly TimeZoneInfo ExchangeTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Premarket opens at 04:00 ET.</summary>
    public static readonly TimeOnly PremarketOpen = new(4, 0);

    /// <summary>Regular session opens at 09:30 ET. Also closes the premarket window.</summary>
    public static readonly TimeOnly RegularOpen = new(9, 30);

    /// <summary>The opening range locks at 09:45 ET.</summary>
    public static readonly TimeOnly OpeningRangeClose = new(9, 45);

    /// <summary>Regular session closes at 16:00 ET.</summary>
    public static readonly TimeOnly RegularClose = new(16, 0);

    /// <summary>After-hours trading ends at 20:00 ET.</summary>
    /// <remarks>
    /// The end of the data, not just the end of a window. The gateway serves extended-hours
    /// bars from 04:00 to 20:00 and nothing at all between 20:00 and 04:00 - verified against
    /// a cached session: 192 five-minute bars, hours 04 through 19, no gaps and no others.
    /// A strategy that reasons about "overnight" is reasoning about a window with an eight-hour
    /// hole in it, and must say so rather than treat silence as calm.
    /// </remarks>
    public static readonly TimeOnly AfterHoursClose = new(20, 0);

    /// <summary>Opening range length in minutes. Fixed at 15 by design, never configurable.</summary>
    public const int OpeningRangeMinutes = 15;

    /// <summary>Converts an instant to exchange local time.</summary>
    public static DateTimeOffset ToExchangeTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, ExchangeTimeZone);

    /// <summary>The trading date an instant belongs to, in exchange time.</summary>
    public static DateOnly SessionDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(ToExchangeTime(instant).DateTime);

    /// <summary>The exchange-local time of day an instant falls on.</summary>
    /// <remarks>
    /// Exposed so callers can compare against the window constants directly instead of
    /// building their own boundary instants. Constructing "yesterday at 16:00 ET" as a
    /// <see cref="DateTimeOffset"/> means choosing a UTC offset for that date, which is wrong
    /// twice a year - comparing times of day has no such trap.
    /// </remarks>
    public static TimeOnly TimeOfDay(DateTimeOffset instant) =>
        TimeOnly.FromDateTime(ToExchangeTime(instant).DateTime);

    /// <summary>True when the instant falls in the 04:00-09:30 premarket window.</summary>
    public static bool IsPremarket(DateTimeOffset instant)
    {
        var t = TimeOfDay(instant);
        return t >= PremarketOpen && t < RegularOpen;
    }

    /// <summary>True when the instant falls in the 16:00-20:00 after-hours window.</summary>
    public static bool IsAfterHours(DateTimeOffset instant)
    {
        var t = TimeOfDay(instant);
        return t >= RegularClose && t < AfterHoursClose;
    }

    /// <summary>True when the instant falls in the 09:30-09:45 opening range window.</summary>
    public static bool IsOpeningRange(DateTimeOffset instant)
    {
        var t = TimeOfDay(instant);
        return t >= RegularOpen && t < OpeningRangeClose;
    }

    /// <summary>True when the instant falls in the 09:30-16:00 regular session.</summary>
    public static bool IsRegularSession(DateTimeOffset instant)
    {
        var t = TimeOfDay(instant);
        return t >= RegularOpen && t < RegularClose;
    }

    /// <summary>
    /// Whether a bar size divides the opening range evenly.
    /// </summary>
    /// <remarks>
    /// 1, 3, 5 and 15 divide 15; 2, 4 and 10 do not. On a 2-minute chart the 09:44-09:46
    /// bar straddles the lock and would silently widen the range - the same constraint the
    /// Pine indicator enforces by refusing to draw. Kept here so the C# port produces
    /// signal-for-signal identical results rather than quietly accepting a wider range.
    /// </remarks>
    public static bool IsSupportedTimeframe(int minutes) =>
        minutes > 0 && OpeningRangeMinutes % minutes == 0;
}
