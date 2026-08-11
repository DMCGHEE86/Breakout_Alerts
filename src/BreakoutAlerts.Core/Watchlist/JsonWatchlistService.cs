using System.Text.Json;
using BreakoutAlerts.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.Core.Watchlist;

/// <summary>
/// Watchlist persisted to a JSON file.
/// </summary>
/// <remarks>
/// A plain JSON array is correct here, unlike the alert log. The watchlist is small,
/// bounded, and rewritten wholesale on every change - there is no append pattern to
/// preserve and no risk of an unbounded file. The append-only JSON Lines reasoning that
/// applies to <c>alerts.jsonl</c> simply does not apply to a list of a few dozen symbols.
///
/// <para>Writes go through a temporary file and an atomic replace, so an interrupted save
/// leaves the previous watchlist intact rather than a truncated one. Losing a watchlist to
/// a crash mid-write would be a small disaster for something the user curated by hand.</para>
/// </remarks>
public sealed class JsonWatchlistService : IWatchlistService
{
    private readonly string _path;
    private readonly ILogger<JsonWatchlistService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<WatchlistEntry> _items = [];
    private readonly Lock _itemsGate = new();

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<WatchlistEntry> Items
    {
        get
        {
            // Snapshot under lock. Handing out the live list would let a UI enumeration
            // race a background add and throw mid-render.
            lock (_itemsGate)
            {
                return _items.ToList();
            }
        }
    }

    /// <summary>Creates the service over a given file path.</summary>
    public JsonWatchlistService(string path, ILogger<JsonWatchlistService> logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>Symbols seeded on first run, so the app is not empty on launch.</summary>
    public static readonly IReadOnlyList<string> DefaultSeed =
        ["AMD", "NVDA", "TSLA", "AAPL", "MSFT", "META", "AMZN", "GOOGL"];

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<List<WatchlistEntry>>(json);

                if (loaded is { Count: > 0 })
                {
                    lock (_itemsGate)
                    {
                        _items.Clear();
                        _items.AddRange(loaded);
                    }

                    _logger.LogInformation("Loaded {Count} watchlist symbols", loaded.Count);
                    OnChanged();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt watchlist falls back to the seed rather than leaving the scanner
            // with nothing to scan. The file is rewritten on the next change.
            _logger.LogError(ex, "Could not read watchlist from {Path}; seeding defaults", _path);
        }

        lock (_itemsGate)
        {
            _items.Clear();
            _items.AddRange(DefaultSeed.Select(t => new WatchlistEntry(t, DateTimeOffset.Now)));
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        OnChanged();
    }

    /// <inheritdoc />
    public async Task<bool> AddAsync(string ticker, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return false;
        }

        var normalised = ticker.Trim().ToUpperInvariant();

        lock (_itemsGate)
        {
            if (_items.Any(e => string.Equals(e.Ticker, normalised, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _items.Add(new WatchlistEntry(normalised, DateTimeOffset.Now));
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        OnChanged();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string ticker, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return false;
        }

        bool removed;
        lock (_itemsGate)
        {
            removed = _items.RemoveAll(e =>
                string.Equals(e.Ticker, ticker, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (!removed)
        {
            return false;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        OnChanged();
        return true;
    }

    /// <inheritdoc />
    public bool Contains(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return false;
        }

        lock (_itemsGate)
        {
            return _items.Any(e => string.Equals(e.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Writes the list via a temp file and atomic replace.</summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<WatchlistEntry> snapshot;
            lock (_itemsGate)
            {
                snapshot = _items.ToList();
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

            // Write beside the target, then move over it. File.Move with overwrite is
            // atomic on NTFS, so a crash cannot leave a half-written watchlist.
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save watchlist to {Path}", _path);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
