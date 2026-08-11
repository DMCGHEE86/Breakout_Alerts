using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.Core.Alerts;

/// <summary>
/// Durable alert log backed by a JSON Lines file - one complete JSON object per line,
/// appended and never rewritten.
/// </summary>
/// <remarks>
/// <b>Why JSON Lines rather than a single JSON array.</b> Appending to a JSON array means
/// reading the file, parsing it, adding an element and rewriting the whole thing. That
/// gets slower as the log grows, and a crash mid-rewrite can destroy the entire history
/// rather than one record. It also fights any concurrent reader, since the file is
/// invalid JSON for the duration of the write.
///
/// <para>JSON Lines avoids all of it. An append is a single write to the end of the file.
/// A crash or a full disk damages at most the final line, and every line before it stays
/// readable. A reader can stream the file without holding it in memory, and standard
/// tools handle the format directly - pandas, jq, DuckDB and Excel's Power Query all read
/// JSONL, which matters for a log whose entire purpose is offline analysis.</para>
///
/// <para><b>Concurrency.</b> A <see cref="SemaphoreSlim"/> serialises writers within this
/// process. Cross-process safety is not attempted, because there is exactly one writer by
/// design - the scanner. Readers do not take the semaphore; they open the file with
/// permissive sharing and tolerate a torn final line.</para>
/// </remarks>
public sealed class JsonLinesAlertStore : IAlertStore, IDisposable
{
    // One writer at a time. Interleaved appends would produce corrupted lines, which is
    // the one failure mode JSON Lines cannot recover from on its own.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<JsonLinesAlertStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    /// <summary>Identities already on disk, so the same signal is never appended twice.</summary>
    /// <remarks>
    /// <b>Re-publication is normal, not exceptional.</b> The scanner keeps no state across
    /// restarts, so every launch re-evaluates the whole of today's session and re-publishes
    /// signals that already fired. Without this the log gained a fresh copy of every one of
    /// today's alerts on each restart - and the log is the backtesting substrate, so those
    /// copies would quietly inflate the apparent frequency of every setup.
    ///
    /// <para>Seeded from the file on first append rather than in the constructor, so
    /// construction stays cheap and non-blocking and a store that is only ever read never
    /// pays for it.</para>
    /// </remarks>
    private readonly HashSet<string> _appended = new(StringComparer.Ordinal);

    private bool _identitiesLoaded;

    /// <inheritdoc />
    public string LogPath { get; }

    /// <summary>Creates a store writing to the given path, creating directories as needed.</summary>
    /// <param name="logPath">Full path to the .jsonl file.</param>
    /// <param name="logger">Diagnostics sink.</param>
    public JsonLinesAlertStore(string logPath, ILogger<JsonLinesAlertStore> logger)
    {
        LogPath = logPath ?? throw new ArgumentNullException(nameof(logPath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // WriteIndented is off deliberately: indentation would put a single record across
        // many lines and break the one-object-per-line invariant the whole format rests on.
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        var directory = Path.GetDirectoryName(LogPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never throws. A scan that is finding real setups must not be taken down because the
    /// disk is full or a file is locked - losing a log line is bad, losing the session is
    /// worse. Failures are logged at Error so they are visible rather than silent.
    /// </remarks>
    public async Task AppendAsync(AlertRecord alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_identitiesLoaded)
            {
                _identitiesLoaded = true;

                await foreach (var existing in ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    _appended.Add(existing.Identity);
                }
            }

            if (!_appended.Add(alert.Identity))
            {
                // Already recorded. Silently skipped rather than logged - a restart mid-session
                // legitimately produces one of these per alert already fired today, and
                // warning about each would bury anything that mattered.
                return;
            }

            var line = JsonSerializer.Serialize(alert, _jsonOptions);

            // FileMode.Append plus FileShare.Read: the OS positions every write at the end
            // of the file, and readers may stream it concurrently while we do so.
            await using var stream = new FileStream(
                LogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal shutdown path, not an error. Let it propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append alert for {Ticker} to {LogPath}", alert.Ticker, LogPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A malformed line is skipped with a warning rather than aborting the read. This is
    /// the payoff of the format: a torn final line from an interrupted write costs one
    /// record, and every earlier record still loads. Throwing here would make a single
    /// bad byte destroy access to the entire history.
    /// </remarks>
    /// <summary>Level names written by the flat-column format used before 2026-08-07.</summary>
    private static readonly string[] LegacyLevelNames =
        ["orb_high", "orb_low", "premarket_high", "premarket_low", "orb_duration"];

    /// <summary>
    /// Lifts levels out of the old flat columns into <see cref="AlertRecord.Levels"/>.
    /// </summary>
    /// <remarks>
    /// <b>Format compatibility belongs here, not on the record.</b> Keeping the old property
    /// names on <c>AlertRecord</c> so they would deserialize would have left the type carrying
    /// one strategy's vocabulary forever - the exact coupling this change exists to remove.
    /// The store is where a persistence format lives, so the store translates.
    ///
    /// <para>The file is never rewritten. It is append-only and it is the backtesting
    /// substrate; a migration that rewrote history in place would put every existing record at
    /// risk to tidy a field name.</para>
    ///
    /// <para>Applied only when a record has no levels of its own, so a new-format line is
    /// never touched.</para>
    /// </remarks>
    private static AlertRecord? MigrateLegacyLevels(AlertRecord? record, string line)
    {
        if (record is null || record.Levels.Count > 0)
        {
            return record;
        }

        Dictionary<string, decimal?>? levels = null;

        using var document = JsonDocument.Parse(line);

        foreach (var name in LegacyLevelNames)
        {
            if (!document.RootElement.TryGetProperty(name, out var element))
            {
                continue;
            }

            levels ??= [];
            levels[name] = element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value)
                ? value
                : null;
        }

        return levels is null ? record : record with { Levels = levels };
    }

    public async IAsyncEnumerable<AlertRecord> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LogPath))
        {
            yield break;
        }

        // FileShare.ReadWrite so reading never blocks the scanner from appending.
        await using var stream = new FileStream(
            LogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);

        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lineNumber = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AlertRecord? record = null;
            try
            {
                record = MigrateLegacyLevels(JsonSerializer.Deserialize<AlertRecord>(line, _jsonOptions), line);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed alert log line {LineNumber} in {LogPath}", lineNumber, LogPath);
            }

            // yield sits outside the try because C# forbids yielding from a try that has
            // a catch clause.
            if (record is not null)
            {
                yield return record;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }
}
