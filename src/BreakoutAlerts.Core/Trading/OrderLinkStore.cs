using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.Core.Trading;

/// <summary>
/// Durable record of stop-loss orders waiting for their entry to fill.
/// </summary>
/// <remarks>
/// Written to disk on every change, because the whole reason this exists is to survive the
/// application not being there. An entry that fills during a restart must still get its stop.
///
/// <para>Unlike the alert and order logs this file is <b>rewritten</b> rather than appended.
/// It is current state, not history - a handful of live entries - and append-only would mean
/// replaying the whole file to work out what is still outstanding. History is already captured
/// in <c>orders.jsonl</c>, which is append-only and never rewritten.</para>
///
/// <para>Written via a temporary file and a move, so a process killed mid-write leaves the
/// previous state rather than a truncated file that would parse as fewer order links than
/// really exist.</para>
/// </remarks>
public sealed class OrderLinkStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<OrderLinkStore> _logger;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private List<OrderLink>? _cache;

    /// <summary>Full path to the state file.</summary>
    public string FilePath { get; }

    /// <summary>Creates the store.</summary>
    public OrderLinkStore(string filePath, ILogger<OrderLinkStore> logger)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>All order links, including failed ones.</summary>
    /// <remarks>
    /// Failed entries are kept rather than discarded. A stop that could not be placed means an
    /// unprotected position, and silently dropping the record would remove the only evidence
    /// that the user should go and look.
    /// </remarks>
    public async Task<IReadOnlyList<OrderLink>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds or replaces the entry for a parent order.</summary>
    public async Task SaveAsync(OrderLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            all.RemoveAll(s => s.EntryOrderId == link.EntryOrderId);
            all.Add(link);

            await WriteAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes the entry for a parent order, if present.</summary>
    public async Task RemoveAsync(ulong entryOrderId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();

            if (all.RemoveAll(s => s.EntryOrderId == entryOrderId) > 0)
            {
                await WriteAsync(all, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<OrderLink>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(FilePath))
        {
            _cache = [];
            return _cache;
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            _cache = await JsonSerializer
                .DeserializeAsync<List<OrderLink>>(stream, cancellationToken: ct)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            // Logged at Error, not Warning. An unreadable file here means order links are
            // lost and positions may be unprotected without anyone being told - this is not a
            // cache miss, it is a safety-relevant failure.
            _logger.LogError(ex, "Could not read order links from {Path}. Any stop waiting on a fill is lost.", FilePath);
            _cache = [];
        }

        return _cache;
    }

    private async Task WriteAsync(List<OrderLink> all, CancellationToken ct)
    {
        _cache = all;

        var temp = FilePath + ".tmp";

        try
        {
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, all, _json, ct).ConfigureAwait(false);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist order links to {Path}", FilePath);

            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Inert leftover; nothing useful to do.
            }
        }
    }
}

