namespace BreakoutAlerts.Core.Models;

/// <summary>
/// Describes one tunable input on a strategy, so the configuration UI can be generated
/// rather than hand-built per strategy.
/// </summary>
/// <remarks>
/// This is what makes the "pluggable registry" requirement actually pay off. Without a
/// descriptor, every new strategy needs a bespoke settings panel, and adding a strategy
/// means touching the UI project - which defeats the point of the plug-in architecture.
/// With it, a strategy declares its inputs and the UI renders editors automatically.
///
/// The numeric type is <see cref="double"/> rather than <see cref="decimal"/> here on
/// purpose: these are indicator parameters (periods, multipliers, thresholds), not
/// money. Keeping the money/parameter distinction visible in the type prevents a
/// parameter from being quietly used where a price belongs.
/// </remarks>
/// <param name="Key">Stable identifier used in config files. Never shown to the user.</param>
/// <param name="DisplayName">Human-readable label for the settings UI.</param>
/// <param name="Description">Tooltip text explaining what the parameter changes.</param>
/// <param name="DefaultValue">Value used when no saved configuration exists.</param>
/// <param name="Minimum">Inclusive lower bound enforced by the editor.</param>
/// <param name="Maximum">Inclusive upper bound enforced by the editor.</param>
/// <param name="Kind">How the editor should present the value.</param>
public sealed record StrategyParameterDescriptor(
    string Key,
    string DisplayName,
    string Description,
    double DefaultValue,
    double Minimum,
    double Maximum,
    StrategyParameterKind Kind = StrategyParameterKind.Number);

/// <summary>How a strategy parameter should be rendered in the configuration UI.</summary>
public enum StrategyParameterKind
{
    /// <summary>Free numeric entry within the declared bounds.</summary>
    Number,

    /// <summary>Whole numbers only - periods, bar counts, lookbacks.</summary>
    Integer,

    /// <summary>On/off. Stored as 0 or 1 to keep one uniform parameter dictionary.</summary>
    Toggle,

    /// <summary>A 0-1 proportion, rendered as a percentage.</summary>
    Percent
}

/// <summary>
/// A strategy's verdict for one ticker on one evaluated bar.
/// </summary>
/// <remarks>
/// Returned as null by <c>IPriceStrategy.Evaluate</c> when nothing fired, rather than
/// returning a signal carrying a "did not trigger" flag. A nullable result makes the
/// no-signal case impossible to ignore at the call site.
/// </remarks>
/// <param name="Ticker">Symbol the signal applies to.</param>
/// <param name="StrategyId">Identifier of the strategy that produced it.</param>
/// <param name="Path">Which leg fired, e.g. "PATH-1".</param>
/// <param name="Direction">Trade direction implied by the signal.</param>
/// <param name="TriggerPrice">Price at the moment of the trigger.</param>
/// <param name="TriggeredAt">Close time of the triggering bar.</param>
/// <param name="Context">
/// Strategy-specific levels worth carrying into the alert - opening range boundaries,
/// premarket levels, and so on. Kept loosely typed so the registry does not need to
/// know about any particular strategy's shape.
/// </param>
public sealed record StrategySignal(
    string Ticker,
    string StrategyId,
    string Path,
    TradeDirection Direction,
    decimal TriggerPrice,
    DateTimeOffset TriggeredAt,
    IReadOnlyDictionary<string, decimal?> Context);

/// <summary>Direction of a signal or position.</summary>
public enum TradeDirection
{
    /// <summary>Upside - a break above resistance.</summary>
    Long,

    /// <summary>Downside - a break below support.</summary>
    Short
}
