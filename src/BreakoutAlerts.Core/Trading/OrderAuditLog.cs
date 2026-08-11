using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.Core.Trading;

/// <summary>
/// Append-only record of every order this application asked the broker to place, amend or
/// cancel.
/// </summary>
/// <remarks>
/// <b>Written before the request is sent, not after.</b> That ordering is the whole point: an
/// order that was submitted and then lost the connection before its reply is exactly the case
/// you most need a record of, and a log written on success would be missing precisely those.
/// A line with no result is a line whose outcome is unknown - which is the truth, and far
/// more useful than silence.
///
/// <para>JSON Lines, for the same reasons as the alert log: appends are effectively atomic,
/// a torn final line costs one record rather than the file, and it is trivially readable by
/// anything.</para>
///
/// <para>Separate from <c>alerts.jsonl</c> deliberately. Alerts are observations; these are
/// actions taken with money. Mixing them would make either one harder to reason about, and
/// this file has a different retention and sensitivity profile.</para>
/// </remarks>
public sealed class OrderAuditLog
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<OrderAuditLog> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    /// <summary>Full path to the audit file.</summary>
    public string LogPath { get; }

    /// <summary>Creates the log, creating directories as needed.</summary>
    public OrderAuditLog(string logPath, ILogger<OrderAuditLog> logger)
    {
        LogPath = logPath ?? throw new ArgumentNullException(nameof(logPath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var directory = Path.GetDirectoryName(LogPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>Appends one line.</summary>
    /// <remarks>
    /// Never throws. A failure to write the audit line must not stop the order it describes -
    /// but it is logged at Error, because an order placed without a record is a real problem
    /// even though it is not a reason to block the trade.
    /// </remarks>
    public async Task WriteAsync(OrderAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var line = JsonSerializer.Serialize(record, _jsonOptions);

            await using var stream = new FileStream(
                LogPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write order audit record for {Contract}", record.ContractCode);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Builds a record from a request.</summary>
    public static OrderAuditRecord From(OrderRequest request, string action, ulong? orderId = null, string? result = null) =>
        new()
        {
            Action = action,
            Timestamp = DateTimeOffset.Now,
            AccountId = request.Account.AccountId,
            Environment = request.Account.Environment == TradeEnvironment.Live ? "LIVE" : "PAPER",
            ContractCode = request.ContractCode,
            Side = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            Quantity = request.Quantity,
            Pricing = request.Pricing == OrderPricing.Limit ? "LIMIT" : "MARKET",
            LimitPrice = request.LimitPrice,
            OrderId = orderId,
            SourceAlertIdentity = request.SourceAlertIdentity,
            Result = result
        };
}
