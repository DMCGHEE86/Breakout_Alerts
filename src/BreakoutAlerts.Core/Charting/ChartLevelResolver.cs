using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.Core.Charting;

/// <summary>A shaded region between two prices.</summary>
/// <param name="Label">Prefix for the edge labels.</param>
/// <param name="Upper">Top edge.</param>
/// <param name="Lower">Bottom edge.</param>
public sealed record ChartBand(string Label, decimal Upper, decimal Lower);

/// <summary>A horizontal level.</summary>
/// <param name="Label">Prefix for the label.</param>
/// <param name="Value">Price.</param>
/// <param name="Tone">Directional meaning, mapped to colour by the renderer.</param>
public sealed record ChartLine(string Label, decimal Value, LevelTone Tone);

/// <summary>
/// Turns an alert's levels into the bands and lines a chart draws, using the declaration of
/// the strategy that produced it.
/// </summary>
/// <remarks>
/// <b>One resolver, used by every chart path.</b> The window, the headless
/// <c>--render-alert</c> and the sample renderer all come through here. That is not tidiness:
/// a previous version had the live render build its own model, which skipped the store's level
/// migration and drew candles with no levels at all - correct-looking output that was quietly
/// missing the thing being checked. A second path through this logic is exactly how that
/// happens again.
///
/// <para>In Core rather than beside the renderer, because it is the part with decisions in it
/// and Core is the half of this application that can be tested without a window.</para>
/// </remarks>
public static class ChartLevelResolver
{
    /// <summary>Resolves the focus alert's levels against its strategy's declaration.</summary>
    /// <param name="focus">The alert being charted. Null yields nothing to draw.</param>
    /// <param name="strategies">
    /// Candidate strategies, matched by <see cref="IPriceStrategy.Id"/> against
    /// <see cref="AlertRecord.Strategy"/>.
    /// </param>
    /// <returns>
    /// Bands and lines with prices filled in. A declared level whose key is absent from the
    /// record - an older alert written before that level existed - is skipped rather than
    /// drawn at zero, because a level at 0.00 is a line across the bottom of the chart that
    /// looks like data.
    /// </returns>
    public static (IReadOnlyList<ChartBand> Bands, IReadOnlyList<ChartLine> Lines) Resolve(
        AlertRecord? focus, IEnumerable<IPriceStrategy>? strategies)
    {
        var bands = new List<ChartBand>();
        var lines = new List<ChartLine>();

        if (focus is null || strategies is null)
        {
            return (bands, lines);
        }

        var strategy = strategies.FirstOrDefault(
            s => string.Equals(s.Id, focus.Strategy, StringComparison.OrdinalIgnoreCase));

        if (strategy is null)
        {
            // An alert from a strategy that is not registered - renamed, removed, or simply an
            // old log being browsed. The candles and flags still draw; only the levels are
            // unavailable, which is the truth rather than a guess at what they meant.
            return (bands, lines);
        }

        foreach (var display in strategy.LevelDisplays)
        {
            if (focus.Level(display.Key) is not { } value)
            {
                continue;
            }

            if (display.Style == LevelStyle.Band)
            {
                if (display.SecondKey is null || focus.Level(display.SecondKey) is not { } second)
                {
                    continue;
                }

                // Ordered rather than trusted. A band drawn with its edges the wrong way round
                // renders as a zero-height rectangle on some plot libraries and as an inverted
                // one on others; neither is worth debugging twice.
                bands.Add(new ChartBand(display.Label, Math.Max(value, second), Math.Min(value, second)));
            }
            else
            {
                lines.Add(new ChartLine(display.Label, value, display.Tone));
            }
        }

        return (bands, lines);
    }
}
