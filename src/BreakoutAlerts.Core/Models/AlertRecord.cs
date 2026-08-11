using System.Text.Json.Serialization;

namespace BreakoutAlerts.Core.Models;

/// <summary>
/// One triggered strategy signal. This is both the in-process event payload and the
/// on-disk record written to the alert log.
/// </summary>
/// <remarks>
/// The JSON property names deliberately match the payload emitted by the Pine Script
/// ORB indicator in the sibling <c>Indicators</c> project. Keeping one schema on both
/// sides means the Pine indicator and the C# port can be run over the same session and
/// their outputs compared directly - which is the cheapest correctness test available
/// for a strategy port, and the reason the schema is worth preserving even though
/// TradingView is not a signal source.
///
/// Nullable decimals on the level fields are load-bearing, not defensive habit. A
/// symbol with no extended-hours data has no premarket high or low, and that has to
/// round-trip as JSON <c>null</c>. Non-nullable decimals would deserialize a missing
/// level as 0.00 - a price that reads as real and would corrupt any backtest built on
/// the log.
/// </remarks>
public sealed record AlertRecord
{
    /// <summary>Symbol the signal fired on, e.g. "AMD".</summary>
    [JsonPropertyName("ticker")]
    public required string Ticker { get; init; }

    /// <summary>Strategy identifier, e.g. "ORB_Breakout".</summary>
    [JsonPropertyName("strategy")]
    public required string Strategy { get; init; }

    /// <summary>Which leg of the strategy fired, e.g. "PATH-1" or "PATH-2".</summary>
    [JsonPropertyName("alert_path")]
    public required string AlertPath { get; init; }

    /// <summary>"LONG" or "SHORT".</summary>
    [JsonPropertyName("direction")]
    public required string Direction { get; init; }

    /// <summary>
    /// Price levels the signal was measured against, keyed by name.
    /// </summary>
    /// <remarks>
    /// <b>Generic on purpose.</b> These were once named columns - <c>orb_high</c>,
    /// <c>premarket_low</c> and so on - which worked while one strategy existed and would
    /// have meant a new nullable column per level for every strategy after it. A record that
    /// is mostly nulls tells you nothing about which of them apply.
    ///
    /// <para>The strategy already produces exactly this shape:
    /// <c>StrategySignal.Context</c> is a name-to-value dictionary, and the old record simply
    /// flattened it on the way to disk. This stops flattening it.</para>
    ///
    /// <para>Values are nullable because a level can be genuinely absent - a symbol with no
    /// extended-hours data has no premarket high, and that has to round-trip as null rather
    /// than as 0.00, which would read as a real price and corrupt any backtest.</para>
    ///
    /// <para>Records written before 2026-08-07 carry the old flat field names. Those are
    /// translated into this dictionary when the log is read - see
    /// <c>JsonLinesAlertStore</c>. The file is never rewritten.</para>
    /// </remarks>
    [JsonPropertyName("levels")]
    public IReadOnlyDictionary<string, decimal?> Levels { get; init; } =
        new Dictionary<string, decimal?>();

    /// <summary>One level by name, or null when the strategy did not report it.</summary>
    public decimal? Level(string name) =>
        Levels.TryGetValue(name, out var value) ? value : null;

    /// <summary>Close time of the bar that triggered the signal, with offset.</summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Price at the moment the signal fired. Useful for backtest fills.</summary>
    /// <remarks>
    /// Not part of the original Pine payload. Added because a backtest run over the log
    /// needs an entry reference, and reconstructing it from the bar data afterwards
    /// means re-fetching history that was already in hand when the alert fired.
    /// </remarks>
    [JsonPropertyName("trigger_price")]
    public decimal? TriggerPrice { get; init; }

    /// <summary>
    /// Where the bars behind this signal came from - "moomoo" for the live gateway,
    /// "synthetic" for generated data.
    /// </summary>
    /// <remarks>
    /// Load-bearing for backtesting, which is the stated reason the log exists. Before this
    /// field, a run against generated data appended records that were byte-for-byte
    /// indistinguishable from live ones; anything computed over the mixed file would be
    /// silently wrong with no way to detect it after the fact.
    ///
    /// <para>Nullable because records written before 2026-08-04 carry no provenance. A null
    /// here means unknown, not real - treat those lines as suspect rather than assuming
    /// they came from the gateway.</para>
    /// </remarks>
    [JsonPropertyName("data_source")]
    public string? DataSource { get; init; }

    /// <summary>
    /// Stable identity for one signal: the same setup on the same bar is the same alert,
    /// however many times it is evaluated.
    /// </summary>
    /// <remarks>
    /// <b>Duplicates are structural here, not accidental.</b> The scanner holds no memory
    /// across restarts, so every launch re-evaluates the whole of today's history and
    /// re-publishes signals that already fired. Separately, the scanner persists an alert
    /// and raises it as an event, so anything published while the dashboard is mid-replay
    /// arrives twice - once live, once from the file being read underneath it. Observed live
    /// on 2026-08-05 as AMD appearing three times at 09:50 with an identical trigger price.
    ///
    /// <para>Not a record equality override. <c>AlertRecord</c> is a serialization shape and
    /// value equality over every field is the right default for it; this is specifically the
    /// question "is this the same signal", which deliberately ignores fields like
    /// <see cref="DataSource"/> that describe provenance rather than the setup.</para>
    ///
    /// <para>Timestamp is the bar close, so it identifies the bar. Direction is included
    /// because a long and a short on one bar are genuinely different signals, as are two
    /// paths - PATH-1 and PATH-3 firing on the same candle is a real pair, not a duplicate.</para>
    /// </remarks>
    [JsonIgnore]
    public string Identity =>
        $"{Ticker}|{Strategy}|{AlertPath}|{Direction}|{Timestamp.UtcTicks}";
}
