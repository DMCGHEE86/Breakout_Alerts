namespace BreakoutAlerts.Core.Charting;

/// <summary>How a level should be drawn.</summary>
public enum LevelStyle
{
    /// <summary>A shaded region between two levels.</summary>
    Band,

    /// <summary>A single horizontal line.</summary>
    Line
}

/// <summary>
/// What a level means directionally, so the renderer can colour it without knowing its name.
/// </summary>
/// <remarks>
/// A tone rather than a colour, deliberately. Colours belong to the theme and live with the
/// drawing code; a strategy in Core naming a hex value would be Core deciding what the
/// application looks like, and the same level would then be un-themeable.
/// </remarks>
public enum LevelTone
{
    /// <summary>Structural. The strategy's main reference.</summary>
    Neutral,

    /// <summary>An upside level - resistance, a high.</summary>
    Positive,

    /// <summary>A downside level - support, a low.</summary>
    Negative
}

/// <summary>
/// A strategy's description of one of its levels, for the chart to draw.
/// </summary>
/// <param name="Label">Short prefix shown beside the value, e.g. "ORB" or "YDAY".</param>
/// <param name="Style">Band or line.</param>
/// <param name="Key">Context key holding the value - the upper edge, for a band.</param>
/// <param name="SecondKey">Lower edge of a band. Null for a line.</param>
/// <param name="Tone">Directional meaning, used for colour.</param>
/// <remarks>
/// <b>This is how the chart stopped being ORB-shaped.</b> The renderer previously took four
/// named nullable decimals - an opening-range pair drawn as a band and a premarket pair drawn
/// as dotted lines - so a second strategy's levels had nowhere to go and its alerts drew
/// candles and flags over empty space.
///
/// <para>The levels themselves already travelled generically, as a
/// <c>string -&gt; decimal?</c> map on the signal and the alert record. What was missing was
/// any statement of <i>how</i> to draw them, and only the strategy that invented the keys
/// knows that. So the strategy says band-or-line and the renderer says what a band looks
/// like, which is the split that lets a third strategy need no drawing changes at all.</para>
/// </remarks>
public sealed record LevelDisplay(
    string Label,
    LevelStyle Style,
    string Key,
    string? SecondKey = null,
    LevelTone Tone = LevelTone.Neutral)
{
    /// <summary>A shaded band between two context keys.</summary>
    public static LevelDisplay Band(string label, string upperKey, string lowerKey) =>
        new(label, LevelStyle.Band, upperKey, lowerKey);

    /// <summary>A single horizontal line.</summary>
    public static LevelDisplay Line(string label, string key, LevelTone tone) =>
        new(label, LevelStyle.Line, key, null, tone);
}
