using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Abstractions;

/// <summary>
/// A pluggable price-action strategy. Consumes OHLCV bars for one ticker and reports
/// whether a signal fired on the most recent bar.
/// </summary>
/// <remarks>
/// This is the seam that keeps the scanner from knowing about any specific strategy.
/// The scanner iterates whatever the registry hands it; adding a strategy requires no
/// scanner change and, because parameters are self-describing, no UI change either.
///
/// <para><b>Implementations must be stateless across tickers.</b> One instance is
/// evaluated against many symbols, potentially concurrently. Any per-ticker state - an
/// opening range, a breakout flag - belongs in a keyed structure owned by the
/// implementation, never in a bare field. A field holding "the current ORB high" would
/// work perfectly in single-ticker testing and silently cross-contaminate symbols the
/// moment a real scan runs.</para>
/// </remarks>
public interface IPriceStrategy
{
    /// <summary>Stable identifier used in config files and alert records.</summary>
    string Id { get; }

    /// <summary>Human-readable name for the strategy list.</summary>
    string DisplayName { get; }

    /// <summary>What the strategy looks for, shown in the configuration UI.</summary>
    string Description { get; }

    /// <summary>
    /// The strategy's tunable inputs. Drives automatic generation of the settings editor.
    /// </summary>
    IReadOnlyList<StrategyParameterDescriptor> Parameters { get; }

    /// <summary>
    /// Applies a set of parameter values, keyed by <see cref="StrategyParameterDescriptor.Key"/>.
    /// </summary>
    /// <param name="values">
    /// Values to apply. Keys absent from the dictionary must fall back to the descriptor's
    /// declared default rather than throwing - a config file written by an older build
    /// will not contain parameters added since.
    /// </param>
    void Configure(IReadOnlyDictionary<string, double> values);

    /// <summary>
    /// Evaluates the strategy against a ticker's bar history.
    /// </summary>
    /// <param name="ticker">Symbol being evaluated.</param>
    /// <param name="bars">
    /// Bars in ascending time order, the most recent last. Implementations must treat
    /// the final bar as CLOSED - the scanner is responsible for not passing a partially
    /// formed bar, because body-close semantics are meaningless on an in-progress candle.
    /// </param>
    /// <returns>
    /// Signals produced since the last call, oldest first. Empty when nothing fired.
    /// </returns>
    /// <remarks>
    /// Returns a list rather than a single signal because more than one can legitimately
    /// fire on the same bar - a close above both the opening range high and a higher
    /// premarket high produces PATH-1 and PATH-3 together, and collapsing that to one
    /// would silently discard the stronger of the two.
    ///
    /// <para>Implementations are responsible for de-duplication. The scanner re-evaluates
    /// on a cycle and will pass overlapping bar ranges, so a strategy must track which
    /// bars it has already processed. Without that, one breakout produces an alert on
    /// every scan cycle for the remainder of the session.</para>
    /// </remarks>
    IReadOnlyList<StrategySignal> Evaluate(string ticker, IReadOnlyList<Bar> bars);

    /// <summary>
    /// Bar sizes, in minutes, this strategy needs in addition to the scanner's own.
    /// </summary>
    /// <remarks>
    /// Empty for a strategy that works entirely on the scanner's timeframe, which is why this
    /// has a default implementation - existing strategies neither declare nor notice it.
    ///
    /// <para><b>Why this exists rather than aggregating locally.</b> A strategy could build
    /// 30-minute bars from 5-minute ones, and the first draft of ZEBRA did. But a locally
    /// aggregated bar can disagree with the broker's own chart whenever a source bar is
    /// missing - which is routine in thin overnight hours - and a body that disagrees with the
    /// chart is precisely the input a level test must never be given. The same reasoning is
    /// already recorded on the moomoo provider for why 5-minute bars are requested rather than
    /// built from 1-minute ones.</para>
    ///
    /// <para>The scanner fetches the union of these across active strategies, once per symbol
    /// per cycle, so two strategies asking for the same size cost one request.</para>
    /// </remarks>
    IReadOnlyList<int> AdditionalTimeframes => [];

    /// <summary>
    /// How this strategy's levels should be drawn on the alert chart.
    /// </summary>
    /// <remarks>
    /// The keys named here are the ones the strategy puts in
    /// <see cref="StrategySignal.Context"/>. A strategy that declares nothing still produces
    /// correct alerts; its chart simply shows candles and flags with no levels drawn, which
    /// is the honest result of never having said what its numbers mean.
    /// </remarks>
    IReadOnlyList<Charting.LevelDisplay> LevelDisplays => [];

    /// <summary>
    /// Evaluates with the extra bar series this strategy declared.
    /// </summary>
    /// <param name="ticker">Symbol being evaluated.</param>
    /// <param name="bars">Primary bars, at the scanner's timeframe. Same contract as above.</param>
    /// <param name="additionalBars">
    /// The series named by <see cref="AdditionalTimeframes"/>, keyed by size in minutes. A
    /// timeframe the gateway could not serve is absent rather than empty, so a strategy can
    /// tell "no data" from "no bars in range" - the two mean very different things when the
    /// answer decides whether a level existed.
    /// </param>
    /// <remarks>
    /// The default forwards to the single-series overload, so a strategy that declares no
    /// additional timeframes needs no knowledge of this at all.
    /// </remarks>
    IReadOnlyList<StrategySignal> Evaluate(
        string ticker,
        IReadOnlyList<Bar> bars,
        IReadOnlyDictionary<int, IReadOnlyList<Bar>> additionalBars) => Evaluate(ticker, bars);

    /// <summary>
    /// Discards any accumulated per-ticker state, e.g. at a session boundary.
    /// </summary>
    /// <param name="ticker">Symbol to reset, or null to reset every symbol.</param>
    void Reset(string? ticker = null);
}

/// <summary>
/// Runtime coordinator holding the set of strategies the scanner should evaluate.
/// </summary>
/// <remarks>
/// Deliberately mutable at runtime: enabling or disabling a strategy is a normal user
/// action from the configuration screen, not a restart-level change.
/// </remarks>
public interface IStrategyRegistry
{
    /// <summary>Every registered strategy, enabled or not.</summary>
    IReadOnlyList<IPriceStrategy> All { get; }

    /// <summary>Only the strategies currently enabled for scanning.</summary>
    IReadOnlyList<IPriceStrategy> Active { get; }

    /// <summary>Adds a strategy. Registering a duplicate <see cref="IPriceStrategy.Id"/> replaces the existing entry.</summary>
    void Register(IPriceStrategy strategy);

    /// <summary>Enables or disables a strategy by id.</summary>
    /// <returns>False when no strategy with that id is registered.</returns>
    bool SetEnabled(string strategyId, bool enabled);

    /// <summary>Whether a strategy is currently enabled.</summary>
    bool IsEnabled(string strategyId);

    /// <summary>Raised when the active set changes, so the scanner can pick it up.</summary>
    event EventHandler? ActiveSetChanged;
}
