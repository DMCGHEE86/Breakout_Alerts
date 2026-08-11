using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Options;

/// <summary>
/// Ranks option candidates by how close their delta sits to a target, within a hard band.
/// </summary>
/// <remarks>
/// Implements the stated selection rule: <b>keep contracts with an absolute delta between
/// 0.60 and 0.70, and order them by proximity to 0.65.</b> Delta is the criterion; nothing
/// else is allowed to reorder the list.
///
/// <para>The band is a <b>hard filter, not a preference</b>. A 0.45-delta contract is not a
/// worse version of the setup being looked for - it is a different trade. Letting it rank
/// last rather than excluding it would put contracts in the list that should never be
/// considered, and the pane exists to narrow the chain rather than reorder it.</para>
///
/// <para>Tradability filters (a two-sided market, minimum open interest, minimum days to
/// expiry) are applied <i>before</i> ranking. They are not scoring factors: a contract that
/// cannot be entered at a known price is not a candidate at all, whatever its delta.</para>
/// </remarks>
public sealed class DeltaTargetRanker : IOptionRanker
{
    /// <summary>Delta the ranking aims for.</summary>
    public double TargetDelta { get; init; } = 0.65;

    /// <summary>Lower bound of the acceptable delta band, inclusive.</summary>
    public double MinimumDelta { get; init; } = 0.60;

    /// <summary>Upper bound of the acceptable delta band, inclusive.</summary>
    public double MaximumDelta { get; init; } = 0.70;

    /// <summary>Contracts with open interest below this are dropped as untradeable.</summary>
    public long MinimumOpenInterest { get; init; } = 100;

    /// <summary>Contracts expiring sooner than this are dropped.</summary>
    public int MinimumDaysToExpiry { get; init; } = 2;

    /// <inheritdoc />
    public string Id => "delta_band";

    /// <inheritdoc />
    public string DisplayName => $"Delta {MinimumDelta:F2}-{MaximumDelta:F2}, target {TargetDelta:F2}";

    /// <inheritdoc />
    public IReadOnlyList<RankedOption> Rank(IReadOnlyList<OptionQuote> chain, OptionRankContext context)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(context);

        var wantedRight = context.Direction == TradeDirection.Long ? OptionRight.Call : OptionRight.Put;

        var eligible = chain.Where(q =>
            (!context.RestrictToDirection || q.Right == wantedRight)
            && q.Delta.HasValue
            // Absolute delta: puts carry a negative delta, and a -0.65 put is the mirror of
            // a +0.65 call, not something outside the band.
            && Math.Abs(q.Delta.Value) >= MinimumDelta
            && Math.Abs(q.Delta.Value) <= MaximumDelta
            && q.OpenInterest >= MinimumOpenInterest
            && q.DaysToExpiry >= MinimumDaysToExpiry
            // No two-sided market means no known entry price, so the Greeks are moot.
            && q is { Bid: > 0, Ask: > 0 });

        var ranked = new List<RankedOption>();

        foreach (var quote in eligible)
        {
            var delta = Math.Abs(quote.Delta!.Value);
            var gap = Math.Abs(delta - TargetDelta);

            // Score is 1.0 exactly at the target, falling linearly to 0 at the band edge,
            // so it is directly readable as "how close to target" rather than an opaque
            // weighted composite.
            var halfBand = Math.Max(TargetDelta - MinimumDelta, MaximumDelta - TargetDelta);
            var score = halfBand > 0 ? Math.Max(0, 1 - (gap / halfBand)) : 1;

            var mid = quote.Mid ?? 0m;
            var spread = mid > 0 ? (double)((quote.Ask!.Value - quote.Bid!.Value) / mid) : 1d;

            var rationale =
                $"δ {delta:F2} vs target {TargetDelta:F2} (band {MinimumDelta:F2}-{MaximumDelta:F2}), " +
                $"spread {spread:P0}, {quote.DaysToExpiry}d";

            ranked.Add(new RankedOption(quote, Math.Round(score, 4), rationale));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            // Tie-breaks only, and only between contracts of equal delta distance: the
            // tighter market first, then the nearer expiry. Deliberately secondary - the
            // rule is delta-based, so nothing here may reorder contracts that differ in
            // delta proximity.
            .ThenBy(r => SpreadOf(r.Quote))
            .ThenBy(r => r.Quote.DaysToExpiry)
            .Take(context.MaxResults)
            .ToList();
    }

    private static double SpreadOf(OptionQuote quote)
    {
        var mid = quote.Mid ?? 0m;
        return mid > 0 && quote is { Bid: not null, Ask: not null }
            ? (double)((quote.Ask.Value - quote.Bid.Value) / mid)
            : double.MaxValue;
    }
}
