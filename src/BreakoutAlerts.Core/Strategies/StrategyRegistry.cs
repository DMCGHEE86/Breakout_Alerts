using System.Collections.Concurrent;
using BreakoutAlerts.Core.Abstractions;

namespace BreakoutAlerts.Core.Strategies;

/// <summary>
/// Default <see cref="IStrategyRegistry"/> - a thread-safe, runtime-mutable set of
/// strategies with independent enabled flags.
/// </summary>
/// <remarks>
/// Thread safety is required rather than defensive: the scanner reads
/// <see cref="Active"/> from a background thread while the user toggles strategies from
/// the UI thread. A plain Dictionary here would produce intermittent corruption that is
/// close to impossible to reproduce on demand.
///
/// <para><see cref="Active"/> returns a fresh snapshot list on each call so the scanner
/// can iterate without holding a lock and without risking a collection-modified
/// exception mid-scan. The cost is one small allocation per scan cycle, which is
/// negligible next to the network round trips a scan performs.</para>
/// </remarks>
public sealed class StrategyRegistry : IStrategyRegistry
{
    private readonly ConcurrentDictionary<string, RegistrationEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public event EventHandler? ActiveSetChanged;

    /// <inheritdoc />
    public IReadOnlyList<IPriceStrategy> All =>
        _entries.Values.Select(e => e.Strategy).ToList();

    /// <inheritdoc />
    public IReadOnlyList<IPriceStrategy> Active =>
        _entries.Values.Where(e => e.IsEnabled).Select(e => e.Strategy).ToList();

    /// <inheritdoc />
    /// <remarks>
    /// Newly registered strategies start ENABLED. Registering something the scanner then
    /// ignores until it is separately switched on is a surprising default, and a silent
    /// one - the strategy appears in the list and simply never fires.
    /// </remarks>
    public void Register(IPriceStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        if (string.IsNullOrWhiteSpace(strategy.Id))
        {
            throw new ArgumentException("Strategy Id must not be empty.", nameof(strategy));
        }

        _entries[strategy.Id] = new RegistrationEntry(strategy, IsEnabled: true);
        OnActiveSetChanged();
    }

    /// <inheritdoc />
    public bool SetEnabled(string strategyId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        if (!_entries.TryGetValue(strategyId, out var existing))
        {
            return false;
        }

        // No-op when the flag already matches, so the UI does not trigger a needless
        // scanner reconfiguration on every checkbox repaint.
        if (existing.IsEnabled == enabled)
        {
            return true;
        }

        _entries[strategyId] = existing with { IsEnabled = enabled };
        OnActiveSetChanged();
        return true;
    }

    /// <inheritdoc />
    public bool IsEnabled(string strategyId) =>
        !string.IsNullOrWhiteSpace(strategyId)
        && _entries.TryGetValue(strategyId, out var entry)
        && entry.IsEnabled;

    private void OnActiveSetChanged() => ActiveSetChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Pairs a strategy with its enabled flag inside the registry.</summary>
    private sealed record RegistrationEntry(IPriceStrategy Strategy, bool IsEnabled);
}
