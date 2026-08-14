namespace BreakoutAlerts.Core.Configuration;

/// <summary>
/// Which market data source the application runs against, and how to reach it.
/// </summary>
/// <remarks>
/// Read once at startup. The provider is deliberately <b>not</b> switchable at runtime: it
/// is a singleton holding a socket and live subscriptions, and swapping it in place would
/// mean unsubscribing, disposing, reconnecting and re-seeding every cached ViewModel. That
/// is a large surface for bugs in exchange for a setting that changes approximately never.
/// Startup-only makes the provider immutable for the process lifetime.
/// </remarks>
public sealed class MarketDataOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MarketData";

    /// <summary>
    /// Which provider to use.
    /// </summary>
    /// <remarks>
    /// <b>Defaults to live.</b> A missing, malformed or deleted configuration file lands on
    /// the real gateway, never on generated data - the safe state is the default rather
    /// than something that has to be selected correctly. See
    /// <see cref="MarketDataProviderKind"/> for why the synthetic value is named as it is.
    /// </remarks>
    public MarketDataProviderKind Provider { get; set; } = MarketDataProviderKind.Moomoo;

    /// <summary>Host the OpenD gateway listens on.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Port the OpenD gateway listens on. Matches OpenD.xml's api_port.</summary>
    public ushort Port { get; set; } = 11111;

    /// <summary>
    /// Bar size the scanner evaluates, in minutes.
    /// </summary>
    /// <remarks>
    /// Requested from the gateway directly rather than aggregated locally. Local
    /// aggregation already produced a defect here - a partially formed bucket corrupting
    /// the locked opening range - and broker-supplied bars cannot disagree with the
    /// broker's own charts. Only 1, 3, 5 and 15 are valid; anything else produces a bar
    /// straddling the 09:45 lock.
    /// </remarks>
    public int TimeframeMinutes { get; set; } = 5;

    /// <summary>Seconds between scan cycles.</summary>
    public int ScanIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Latest exchange time at which an alert is still published, as "HH:mm". Empty publishes
    /// all day.
    /// </summary>
    /// <remarks>
    /// An opening-range breakout is a claim about the day's direction, and one made shortly
    /// before the close leaves no time to act. Configurable rather than fixed because where
    /// "too late" falls is a trading judgement, not a property of the strategy.
    /// </remarks>
    public string AlertCutoffTime { get; set; } = "14:00";

    /// <summary>Parsed form of <see cref="AlertCutoffTime"/>, or null when unset or malformed.</summary>
    /// <remarks>
    /// A malformed value disables the cutoff rather than throwing. Failing to start over a
    /// typo in an optional convenience setting would be a worse outcome than publishing a few
    /// late alerts, and the difference is visible in the log either way.
    /// </remarks>
    public TimeOnly? ParsedAlertCutoff =>
        TimeOnly.TryParse(AlertCutoffTime, out var parsed) ? parsed : null;

    /// <summary>
    /// Full path to the OpenD executable. Empty uses the standard per-user install.
    /// </summary>
    /// <remarks>
    /// OpenD installs under <c>%APPDATA%</c> rather than Program Files, so the default has to
    /// be built from the current user's profile and cannot be a fixed string. Overridable
    /// because a portable or relocated install is entirely possible.
    /// </remarks>
    public string OpenDPath { get; set; } = string.Empty;

    /// <summary>Seconds between watchlist quote refreshes.</summary>
    /// <remarks>
    /// Separate from <see cref="ScanIntervalSeconds"/> because the two answer different
    /// questions. A scan cycle is expensive and only produces something new when a bar
    /// closes; the rail is a glance-value price and wants to feel live. One batched snapshot
    /// request covers the whole watchlist, so this stays cheap however long the list grows.
    /// </remarks>
    public int QuotePollSeconds { get; set; } = 5;

    /// <summary>Target absolute delta for option candidate ranking.</summary>
    public double OptionTargetDelta { get; set; } = 0.65;

    /// <summary>Lower bound of the acceptable delta band, inclusive.</summary>
    public double OptionMinimumDelta { get; set; } = 0.60;

    /// <summary>Upper bound of the acceptable delta band, inclusive.</summary>
    public double OptionMaximumDelta { get; set; } = 0.70;

    /// <summary>True when the configured provider generates data rather than fetching it.</summary>
    public bool IsSynthetic => Provider == MarketDataProviderKind.SyntheticForTestingOnly;
}

/// <summary>Available market data sources.</summary>
public enum MarketDataProviderKind
{
    /// <summary>The live moomoo OpenD gateway. The default.</summary>
    Moomoo = 0,

    /// <summary>
    /// Locally generated data. <b>Testing and offline development only.</b>
    /// </summary>
    /// <remarks>
    /// Named for what it is rather than something neutral like "Mock", so it cannot be set
    /// casually or mistaken for a normal operating mode. When active the application shows
    /// a persistent banner and logs a warning at startup.
    ///
    /// <para>The reasoning is worth stating plainly: an application that quietly serves
    /// invented prices while looking exactly like it is serving real ones, in a tool used
    /// to size real trades, is the most damaging failure this codebase could produce. The
    /// mode is kept because offline development and tests need it - it is made loud rather
    /// than removed.</para>
    /// </remarks>
    SyntheticForTestingOnly = 1
}
