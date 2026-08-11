using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Options;

/// <summary>
/// Narrows a full option chain to the handful of contracts worth acting on, and ranks them.
/// </summary>
/// <remarks>
/// This is a deliberate seam around a decision that has not been made. The intended
/// criteria are delta- and theta-based but not yet specified, so Phase 1.5 ships
/// <see cref="DeltaTargetRanker"/> as a placeholder and defers the real rules to a second
/// implementation. Nothing downstream depends on how ranking works - only that it produces
/// an ordered list - so replacing it later is a registration change, not a rewrite.
/// </remarks>
public interface IOptionRanker
{
    /// <summary>Stable identifier.</summary>
    string Id { get; }

    /// <summary>Name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Filters and ranks a chain, best first.</summary>
    /// <param name="chain">Every contract available for the underlying.</param>
    /// <param name="context">Signal direction and underlying state driving the selection.</param>
    /// <returns>Only contracts that pass the filter, ordered best first.</returns>
    IReadOnlyList<RankedOption> Rank(IReadOnlyList<OptionQuote> chain, OptionRankContext context);
}

/// <summary>Inputs a ranker uses beyond the chain itself.</summary>
/// <param name="Direction">Trade direction implied by the alert that selected this ticker.</param>
/// <param name="Spot">Current underlying price.</param>
/// <param name="RestrictToDirection">
/// When true, calls are considered for a long signal and puts for a short one. When false
/// the whole chain is eligible - the UI's "show both" toggle.
/// </param>
/// <param name="MaxResults">Upper bound on returned contracts.</param>
public sealed record OptionRankContext(
    TradeDirection Direction,
    decimal Spot,
    bool RestrictToDirection = true,
    int MaxResults = 12);

/// <summary>A contract that passed the filter, with its score and the reason.</summary>
/// <param name="Quote">The contract.</param>
/// <param name="Score">Ranking score, higher is better. Comparable only within one ranker.</param>
/// <param name="Rationale">
/// Short human-readable explanation of why this contract ranked where it did. Surfaced in
/// the UI so a recommendation is never an unexplained number - the user has to be able to
/// disagree with it.
/// </param>
public sealed record RankedOption(OptionQuote Quote, double Score, string Rationale);
