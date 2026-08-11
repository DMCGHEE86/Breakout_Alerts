using BreakoutAlerts.Core.Alerts;
using BreakoutAlerts.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BreakoutAlerts.Core.Tests;

/// <summary>
/// Tests for the append-only alert log - the substrate every future backtest reads from.
/// </summary>
public sealed class JsonLinesAlertStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ba-tests-" + Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, "alerts.jsonl");

    private JsonLinesAlertStore CreateStore() =>
        new(LogPath, NullLogger<JsonLinesAlertStore>.Instance);

    private static AlertRecord Sample(string ticker = "TEST", decimal? premarketHigh = 101m) => new()
    {
        Ticker = ticker,
        Strategy = "ORB_Breakout",
        AlertPath = "PATH-1",
        Direction = "LONG",
        Levels = new Dictionary<string, decimal?>
        {
            ["orb_duration"] = 15,
            ["orb_high"] = 100m,
            ["orb_low"] = 98m,
            ["premarket_high"] = premarketHigh,
            ["premarket_low"] = 97m
        },
        TriggerPrice = 100.5m,
        Timestamp = DateTimeOffset.Parse("2026-08-03T09:50:00-04:00")
    };

    private async Task<int> CountAsync(JsonLinesAlertStore store)
    {
        var n = 0;
        await foreach (var _ in store.ReadAllAsync())
        {
            n++;
        }

        return n;
    }

    [Fact]
    public async Task TheSameSignalIsNeverAppendedTwice()
    {
        using var store = CreateStore();

        await store.AppendAsync(Sample());
        await store.AppendAsync(Sample());

        Assert.Equal(1, await CountAsync(store));
    }

    [Fact]
    public async Task ARestartDoesNotDuplicateAlreadyLoggedAlerts()
    {
        using (var first = CreateStore())
        {
            await first.AppendAsync(Sample());
        }

        // A new process re-evaluates the whole of today's session and re-publishes signals
        // that already fired - the scanner holds no state across restarts. Observed live on
        // 2026-08-05 as AMD appearing three times at 09:50 with an identical trigger price.
        // The log is what backtests read, so extra copies would inflate the apparent
        // frequency of every setup.
        using var second = CreateStore();
        await second.AppendAsync(Sample());

        Assert.Equal(1, await CountAsync(second));
    }

    [Fact]
    public async Task DifferentPathsOnTheSameBarAreBothKept()
    {
        using var store = CreateStore();

        var path1 = Sample();
        var path3 = path1 with { AlertPath = "PATH-3" };

        await store.AppendAsync(path1);
        await store.AppendAsync(path3);

        // A close clearing both the opening range and a premarket level is genuinely two
        // signals. Collapsing them would discard the stronger confirmation.
        Assert.Equal(2, await CountAsync(store));
    }

    [Fact]
    public async Task OppositeDirectionsOnTheSameBarAreBothKept()
    {
        using var store = CreateStore();

        var longSignal = Sample();
        var shortSignal = longSignal with { Direction = "SHORT" };

        await store.AppendAsync(longSignal);
        await store.AppendAsync(shortSignal);

        Assert.Equal(2, await CountAsync(store));
    }

    [Fact]
    public async Task TheSameSetupOnALaterBarIsANewAlert()
    {
        using var store = CreateStore();

        var first = Sample();
        var later = first with { Timestamp = first.Timestamp.AddMinutes(5) };

        await store.AppendAsync(first);
        await store.AppendAsync(later);

        Assert.Equal(2, await CountAsync(store));
    }

    [Fact]
    public async Task AppendThenReadRoundTripsEveryField()
    {
        using var store = CreateStore();
        await store.AppendAsync(Sample());

        var records = new List<AlertRecord>();
        await foreach (var r in store.ReadAllAsync())
        {
            records.Add(r);
        }

        var record = Assert.Single(records);
        Assert.Equal("TEST", record.Ticker);
        Assert.Equal("PATH-1", record.AlertPath);
        Assert.Equal(15, record.Level("orb_duration"));
        Assert.Equal(100m, record.Level("orb_high"));
        Assert.Equal(100.5m, record.TriggerPrice);
    }

    [Fact]
    public async Task NullLevelsSurviveAsNullNotZero()
    {
        using var store = CreateStore();

        // A symbol with no extended-hours data genuinely has no premarket high. If this
        // round-trips as 0.00 it reads as a real price and corrupts any backtest built on it.
        await store.AppendAsync(Sample(premarketHigh: null));

        var raw = await File.ReadAllTextAsync(LogPath);
        Assert.Contains("\"premarket_high\":null", raw);

        await foreach (var r in store.ReadAllAsync())
        {
            Assert.Null(r.Level("premarket_high"));
        }
    }

    [Fact]
    public async Task LegacyFlatLevelsAreStillReadable()
    {
        // Written by the format used before 2026-08-07, when levels were flat columns. The
        // log is append-only and is the backtesting substrate, so old lines have to keep
        // loading with their levels intact - the file is never rewritten to suit a new shape.
        const string legacy =
            """
            {"ticker":"OLD","strategy":"ORB_Breakout","alert_path":"PATH-1","direction":"LONG","orb_duration":15,"orb_high":100.0,"orb_low":98.0,"premarket_high":101.0,"premarket_low":null,"timestamp":"2026-08-03T09:50:00-04:00","trigger_price":100.5}
            """;

        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(LogPath, legacy + Environment.NewLine);

        using var store = CreateStore();

        var records = new List<AlertRecord>();
        await foreach (var r in store.ReadAllAsync())
        {
            records.Add(r);
        }

        var record = Assert.Single(records);
        Assert.Equal(100m, record.Level("orb_high"));
        Assert.Equal(98m, record.Level("orb_low"));
        Assert.Equal(101m, record.Level("premarket_high"));

        // An explicit null must stay null, not vanish and not become zero.
        Assert.True(record.Levels.ContainsKey("premarket_low"));
        Assert.Null(record.Level("premarket_low"));
    }

    [Fact]
    public async Task AppendsAccumulateOneRecordPerLine()
    {
        using var store = CreateStore();

        for (var i = 0; i < 5; i++)
        {
            await store.AppendAsync(Sample($"SYM{i}"));
        }

        var lines = await File.ReadAllLinesAsync(LogPath);
        Assert.Equal(5, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{", l));
    }

    [Fact]
    public async Task MalformedLineIsSkippedAndLaterRecordsStillLoad()
    {
        using var store = CreateStore();
        await store.AppendAsync(Sample("GOOD1"));

        // Simulate a write interrupted mid-line, then a later clean append.
        await File.AppendAllTextAsync(LogPath, "{\"ticker\":\"TRUNCA" + Environment.NewLine);
        await store.AppendAsync(Sample("GOOD2"));

        var records = new List<AlertRecord>();
        await foreach (var r in store.ReadAllAsync())
        {
            records.Add(r);
        }

        // This is the whole point of JSON Lines: damage costs one record, not the file.
        Assert.Equal(2, records.Count);
        Assert.Equal("GOOD1", records[0].Ticker);
        Assert.Equal("GOOD2", records[1].Ticker);
    }

    [Fact]
    public async Task ReadingAMissingFileYieldsNothingRatherThanThrowing()
    {
        using var store = CreateStore();

        var count = 0;
        await foreach (var _ in store.ReadAllAsync())
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ConcurrentAppendsProduceNoTornLines()
    {
        using var store = CreateStore();

        // The scanner publishes from multiple threads. Interleaved writes would produce
        // corrupted lines, which is the one failure JSON Lines cannot recover from.
        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => store.AppendAsync(Sample($"SYM{i}"))));

        var records = new List<AlertRecord>();
        await foreach (var r in store.ReadAllAsync())
        {
            records.Add(r);
        }

        Assert.Equal(50, records.Count);
    }

    [Fact]
    public async Task AppendDoesNotThrowWhenTheFileIsLocked()
    {
        using var store = CreateStore();
        await store.AppendAsync(Sample());

        // Hold the file exclusively, as another process might.
        using (new FileStream(LogPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            // A full disk or a locked file must not take down a running scan - losing a log
            // line is bad, losing the session is worse.
            await store.AppendAsync(Sample("BLOCKED"));
        }
    }

    [Fact]
    public void LogPathIsExposedForTheUi()
    {
        using var store = CreateStore();
        Assert.Equal(LogPath, store.LogPath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup only; a locked file here must not fail the test run.
        }
    }
}

