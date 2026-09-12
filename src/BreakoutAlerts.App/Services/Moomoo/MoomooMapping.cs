using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Strategies;
using Futu.OpenApi.Pb;
using PriceBar = BreakoutAlerts.Core.Models.Bar;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Translation between moomoo's protobuf messages and this application's domain types.
/// </summary>
/// <remarks>
/// Kept separate from the provider because this is the part worth testing: it is pure,
/// deterministic, and contains the timestamp conversion that everything downstream depends
/// on. The socket layer around it is not testable without a gateway.
/// </remarks>
public static class MoomooMapping
{
    /// <summary>Builds a US equity security reference.</summary>
    public static QotCommon.Security UsSecurity(string code) =>
        QotCommon.Security.CreateBuilder()
            .SetMarket((int)QotCommon.QotMarket.QotMarket_US_Security)
            .SetCode(code)
            .Build();

    /// <summary>Maps a bar size in minutes onto moomoo's K-line type.</summary>
    /// <returns>Null when the gateway has no K-line type of that length.</returns>
    /// <remarks>
    /// <b>This maps what the gateway offers, and nothing more.</b> Whether a bar size is
    /// appropriate for a given strategy is a separate question with a separate answer -
    /// <see cref="MarketSession.IsSupportedTimeframe"/> is what refuses the sizes that would
    /// straddle the 09:45 opening-range lock. Those two rules were briefly the same rule, and
    /// the result was that 30-minute bars could not be fetched at all, for a reason that had
    /// nothing to do with the strategy asking for them.
    /// </remarks>
    public static QotCommon.KLType? ToKLType(int minutes) => minutes switch
    {
        1 => QotCommon.KLType.KLType_1Min,
        3 => QotCommon.KLType.KLType_3Min,
        5 => QotCommon.KLType.KLType_5Min,
        15 => QotCommon.KLType.KLType_15Min,
        30 => QotCommon.KLType.KLType_30Min,
        60 => QotCommon.KLType.KLType_60Min,
        _ => null
    };

    /// <summary>Bar length in minutes for a K-line type.</summary>
    /// <remarks>
    /// Load-bearing: <see cref="ToBar"/> subtracts this to convert the gateway's close stamp
    /// into the open time the domain expects. A wrong value here shifts every bar of that size
    /// by the difference, silently.
    /// </remarks>
    public static int MinutesOf(QotCommon.KLType type) => type switch
    {
        QotCommon.KLType.KLType_1Min => 1,
        QotCommon.KLType.KLType_3Min => 3,
        QotCommon.KLType.KLType_5Min => 5,
        QotCommon.KLType.KLType_15Min => 15,
        QotCommon.KLType.KLType_30Min => 30,
        QotCommon.KLType.KLType_60Min => 60,
        _ => 5
    };

    /// <summary>Subscription type matching a K-line type.</summary>
    public static QotCommon.SubType ToSubType(QotCommon.KLType type) => type switch
    {
        QotCommon.KLType.KLType_1Min => QotCommon.SubType.SubType_KL_1Min,
        QotCommon.KLType.KLType_3Min => QotCommon.SubType.SubType_KL_3Min,
        QotCommon.KLType.KLType_5Min => QotCommon.SubType.SubType_KL_5Min,
        QotCommon.KLType.KLType_15Min => QotCommon.SubType.SubType_KL_15Min,
        QotCommon.KLType.KLType_30Min => QotCommon.SubType.SubType_KL_30Min,
        QotCommon.KLType.KLType_60Min => QotCommon.SubType.SubType_KL_60Min,
        _ => QotCommon.SubType.SubType_KL_5Min
    };

    /// <summary>
    /// Converts a moomoo K-line into a domain bar, re-stamping it with its OPEN time.
    /// </summary>
    /// <remarks>
    /// <b>The subtraction is the important line in this file.</b> moomoo stamps each bar
    /// with the time it CLOSED - the first bar of an extended session comes back as 04:05,
    /// meaning the 04:00-04:05 bar. Every session window in <see cref="MarketSession"/>
    /// assumes the OPEN, and they are half-open ranges.
    ///
    /// <para>Passed through unchanged, the 09:30-09:45 opening range window would capture
    /// the bars closing at 09:35 and 09:40 but exclude the one closing at 09:45 - building
    /// the range from two bars instead of three and producing an opening range that is
    /// wrong on every symbol, every day, while looking entirely plausible.</para>
    /// </remarks>
    public static PriceBar ToBar(QotCommon.KLine kline, QotCommon.KLType type)
    {
        var closeTime = ParseExchangeTime(kline.Time);
        var openTime = closeTime.AddMinutes(-MinutesOf(type));

        return new PriceBar(
            openTime,
            (decimal)kline.OpenPrice,
            (decimal)kline.HighPrice,
            (decimal)kline.LowPrice,
            (decimal)kline.ClosePrice,
            (long)kline.Volume);
    }

    /// <summary>
    /// Parses a moomoo timestamp string as exchange local time.
    /// </summary>
    /// <remarks>
    /// The gateway returns times without an offset, expressed in the market's own timezone.
    /// Parsed as local machine time they would shift by however far the machine is from New
    /// York - which would move every session boundary and break the opening range for any
    /// user outside Eastern.
    /// </remarks>
    public static DateTimeOffset ParseExchangeTime(string value)
    {
        if (!DateTime.TryParse(value, out var naive))
        {
            return default;
        }

        var offset = MarketSession.ExchangeTimeZone.GetUtcOffset(naive);
        return new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Unspecified), offset);
    }

    /// <summary>Projects a security snapshot into a scanner row.</summary>
    public static TickerSnapshot ToSnapshot(QotGetSecuritySnapshot.Snapshot snapshot)
    {
        var basic = snapshot.Basic;
        var last = (decimal)basic.CurPrice;
        var previousClose = (decimal)basic.LastClosePrice;
        var change = last - previousClose;

        return new TickerSnapshot
        {
            Ticker = basic.Security.Code,
            Name = basic.Name,
            Last = last,
            Change = change,
            ChangePercent = previousClose == 0 ? 0d : (double)(change / previousClose),
            Volume = (long)basic.Volume,
            // Opening range and premarket levels are derived by the strategy from bars, not
            // reported by the gateway. Left null so the grid shows an em dash rather than a
            // zero that would read as a real price.
            OrbHigh = null,
            OrbLow = null,
            PremarketHigh = null,
            PremarketLow = null,
            Position = RangePosition.Unknown,
            UpdatedAt = ParseExchangeTime(basic.UpdateTime)
        };
    }

    /// <summary>Extracts contract identities from an option chain response.</summary>
    public static IEnumerable<OptionContractRef> ToOptionContracts(
        QotGetOptionChain.Response rsp, string underlying)
    {
        foreach (var chain in rsp.S2C.OptionChainList)
        {
            foreach (var item in chain.OptionList)
            {
                if (item.HasCall)
                {
                    yield return ToContractRef(item.Call, underlying, OptionRight.Call);
                }

                if (item.HasPut)
                {
                    yield return ToContractRef(item.Put, underlying, OptionRight.Put);
                }
            }
        }
    }

    private static OptionContractRef ToContractRef(
        QotCommon.SecurityStaticInfo info, string underlying, OptionRight right)
    {
        var option = info.OptionExData;

        return new OptionContractRef(
            info.Basic.Security,
            underlying,
            right,
            (decimal)option.StrikePrice,
            DateOnly.TryParse(option.StrikeTime, out var expiry) ? expiry : default);
    }

    /// <summary>
    /// Projects an option snapshot into a quote with Greeks.
    /// </summary>
    /// <returns>Null when the snapshot carries no option data.</returns>
    /// <remarks>
    /// Greeks are nullable throughout. A contract with no two-sided market has no
    /// meaningful implied volatility and therefore no meaningful Greeks - reporting 0.0
    /// would make a worthless contract look like a legitimate deep out-of-the-money one.
    /// </remarks>
    public static OptionQuote? ToOptionQuote(QotGetSecuritySnapshot.Snapshot snapshot)
    {
        if (!snapshot.HasOptionExData)
        {
            return null;
        }

        var basic = snapshot.Basic;
        var option = snapshot.OptionExData;

        var expiry = DateOnly.TryParse(option.StrikeTime, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(DateTime.Today);

        var daysToExpiry = Math.Max(0, expiry.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber);

        return new OptionQuote
        {
            Underlying = option.Owner.Code,
            ContractId = basic.Security.Code,
            Right = option.Type == (int)QotCommon.OptionType.OptionType_Call ? OptionRight.Call : OptionRight.Put,
            Strike = (decimal)option.StrikePrice,
            Expiry = expiry,
            Bid = basic.HasBidPrice ? (decimal)basic.BidPrice : null,
            Ask = basic.HasAskPrice ? (decimal)basic.AskPrice : null,
            Last = (decimal)basic.CurPrice,
            Volume = (long)basic.Volume,
            OpenInterest = option.HasOpenInterest ? option.OpenInterest : 0,
            ImpliedVolatility = option.HasImpliedVolatility ? option.ImpliedVolatility / 100d : null,
            Delta = option.HasDelta ? option.Delta : null,
            Gamma = option.HasGamma ? option.Gamma : null,
            Theta = option.HasTheta ? option.Theta : null,
            Vega = option.HasVega ? option.Vega : null,
            DaysToExpiry = daysToExpiry
        };
    }
}
