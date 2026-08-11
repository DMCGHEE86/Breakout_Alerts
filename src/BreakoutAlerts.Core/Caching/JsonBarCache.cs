using System.Globalization;
using System.Text.Json;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.Core.Caching;

/// <summary>
/// Bar cache backed by one JSON file per ticker, timeframe and session.
/// </summary>
/// <remarks>
/// <b>One file per session, deliberately.</b> A session is the natural unit here: it is
/// written once when the day completes, read whole, and expired whole. Appending every
/// session into a single per-ticker file would mean re-reading everything to serve one day,
/// and would make a duplicate append silently corrupt the history rather than replace a file.
///
/// <para>Writes go to a temporary file and are then moved into place, so a process killed
/// mid-write leaves either the previous session data or nothing - never a half-written
/// session that would parse as a short trading day.</para>
/// </remarks>
public sealed class JsonBarCache : IBarCache
{
    private readonly string _root;
    private readonly ILogger<JsonBarCache> _logger;

    /// <summary>Creates the cache under a root directory.</summary>
    public JsonBarCache(string rootDirectory, ILogger<JsonBarCache> logger)
    {
        _root = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bar>> GetSessionAsync(
        string ticker, int timeframeMinutes, DateOnly session, CancellationToken cancellationToken = default)
    {
        var path = PathFor(ticker, timeframeMinutes, session);

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var bars = await JsonSerializer
                .DeserializeAsync<List<Bar>>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return bars ?? [];
        }
        catch (Exception ex)
        {
            // A damaged cache file is a cache miss, never a failure. The provider can still
            // serve recent sessions, and a corrupt file that took down a scan would be a
            // spectacularly bad trade for a performance optimisation.
            _logger.LogWarning(ex, "Discarding unreadable cache file {Path}", path);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task StoreSessionAsync(
        string ticker, int timeframeMinutes, DateOnly session,
        IReadOnlyList<Bar> bars, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (bars.Count == 0)
        {
            return;
        }

        var path = PathFor(ticker, timeframeMinutes, session);
        var temp = path + ".tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using (var stream = File.Create(temp))
            {
                await JsonSerializer
                    .SerializeAsync(stream, bars, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            // Move into place, so a kill mid-write cannot leave a truncated session that
            // would parse as a legitimately short trading day.
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache {Ticker} {Session}", ticker, session);

            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Nothing useful to do; the temp file is inert.
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DateOnly>> GetStoredSessionsAsync(
        string ticker, int timeframeMinutes, CancellationToken cancellationToken = default)
    {
        var directory = DirectoryFor(ticker, timeframeMinutes);

        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<DateOnly>>([]);
        }

        var sessions = new List<DateOnly>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (DateOnly.TryParseExact(
                    Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                sessions.Add(date);
            }
        }

        sessions.Sort((a, b) => b.CompareTo(a));
        return Task.FromResult<IReadOnlyList<DateOnly>>(sessions);
    }

    private string DirectoryFor(string ticker, int timeframeMinutes) =>
        Path.Combine(_root, $"{ticker.ToUpperInvariant()}_{timeframeMinutes}m");

    private string PathFor(string ticker, int timeframeMinutes, DateOnly session) =>
        Path.Combine(DirectoryFor(ticker, timeframeMinutes), $"{session:yyyy-MM-dd}.json");
}
