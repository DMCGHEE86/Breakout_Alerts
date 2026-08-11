namespace BreakoutAlerts.Core.Abstractions;

/// <summary>
/// The set of symbols the scanner evaluates. Single source of truth for the watchlist.
/// </summary>
/// <remarks>
/// The scanner reads from here rather than holding its own list, so adding a symbol in the
/// UI immediately changes what is being scanned with no restart and no second copy of the
/// list to drift out of sync.
/// </remarks>
public interface IWatchlistService
{
    /// <summary>Current entries, in user-defined order.</summary>
    IReadOnlyList<WatchlistEntry> Items { get; }

    /// <summary>Raised whenever the list changes. Fires on the caller's thread.</summary>
    event EventHandler? Changed;

    /// <summary>Loads the persisted list. Call once at startup.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a symbol.</summary>
    /// <returns>False when the symbol is blank or already present.</returns>
    Task<bool> AddAsync(string ticker, CancellationToken cancellationToken = default);

    /// <summary>Removes a symbol.</summary>
    /// <returns>False when the symbol was not in the list.</returns>
    Task<bool> RemoveAsync(string ticker, CancellationToken cancellationToken = default);

    /// <summary>Whether a symbol is being watched.</summary>
    bool Contains(string ticker);
}

/// <summary>One watched symbol.</summary>
/// <param name="Ticker">Symbol, normalised to upper case.</param>
/// <param name="AddedAt">When it was added, for stable ordering of equal entries.</param>
public sealed record WatchlistEntry(string Ticker, DateTimeOffset AddedAt);
