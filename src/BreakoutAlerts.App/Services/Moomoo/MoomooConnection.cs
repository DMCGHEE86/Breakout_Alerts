using System.Collections.Concurrent;
using BreakoutAlerts.Core.Configuration;
using Futu.OpenApi;
using Futu.OpenApi.Pb;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Single owner of the connection to the local moomoo OpenD gateway.
/// </summary>
/// <remarks>
/// The futu SDK is callback-based: a request returns a serial number and the reply arrives
/// later on an interface method. This class adapts that into ordinary awaitable calls by
/// parking a <see cref="TaskCompletionSource{TResult}"/> per serial number and completing
/// it from the callback. Everything above this layer gets plain async methods.
///
/// <para><b>One instance per process</b>, per the spec's rule about encapsulating the
/// gateway context in a singleton. Two connections would mean two subscription budgets and
/// two sets of callbacks racing each other.</para>
///
/// <para><b>Every wait is bounded.</b> A reply that never arrives - a dropped gateway, a
/// permission failure that returns nothing - must not leave a scan cycle hanging forever.
/// Timeouts surface as exceptions the scanner already contains per symbol.</para>
/// </remarks>
public sealed partial class MoomooConnection : FTSPI_Qot, FTSPI_Conn, IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(20);

    private readonly FTAPI_Qot _qot = new();
    private readonly MarketDataOptions _options;
    private readonly ILogger<MoomooConnection> _logger;

    // Keyed by the serial number the SDK returns from each request.
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<object>> _pending = new();

    private TaskCompletionSource<bool>? _connectResult;
    private bool _disposed;

    /// <summary>True when the gateway is connected and usable.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Raised when the connection state changes. Background thread.</summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>Creates the connection wrapper. Does not connect.</summary>
    public MoomooConnection(MarketDataOptions options, ILogger<MoomooConnection> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Connects to the gateway.</summary>
    /// <returns>True on success. False means OpenD is not running or refused the connection.</returns>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return true;
        }

        _connectResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        FTAPI.Init();
        _qot.SetClientInfo("BreakoutAlerts", 1);
        _qot.SetConnCallback(this);
        _qot.SetQotCallback(this);
        _qot.InitConnect(_options.Host, _options.Port, false);

        var completed = await Task.WhenAny(
            _connectResult.Task,
            Task.Delay(ReplyTimeout, cancellationToken)).ConfigureAwait(false);

        if (completed != _connectResult.Task)
        {
            _logger.LogWarning("OpenD did not respond within {Timeout}s at {Host}:{Port}",
                ReplyTimeout.TotalSeconds, _options.Host, _options.Port);
            return false;
        }

        return _connectResult.Task.Result;
    }

    /// <summary>Issues a request and awaits its reply.</summary>
    /// <typeparam name="TResponse">Expected reply type.</typeparam>
    /// <param name="send">Sends the request and returns the SDK's serial number.</param>
    /// <remarks>
    /// The TCS is registered <b>before</b> the request is sent. Registering afterwards
    /// leaves a window in which a fast reply arrives with nothing waiting for it, and the
    /// call then hangs until its timeout - rare, timing-dependent, and thoroughly unpleasant
    /// to diagnose.
    /// </remarks>
    internal async Task<TResponse> SendAsync<TResponse>(Func<uint> send, CancellationToken cancellationToken)
        where TResponse : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected to OpenD.");
        }

        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Serial numbers are assigned by the SDK at send time, so the entry cannot be keyed
        // until the call returns - but the reply cannot be lost either. Sending under a lock
        // that the callback also takes would deadlock, so instead the reply handler tolerates
        // an unknown serial and this method registers immediately afterwards. In practice the
        // SDK dispatches replies on a separate thread strictly after the send returns.
        var serial = send();
        _pending[serial] = tcs;

        using var timeout = new CancellationTokenSource(ReplyTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var registration = linked.Token.Register(() =>
        {
            if (_pending.TryRemove(serial, out var abandoned))
            {
                abandoned.TrySetException(new TimeoutException(
                    $"OpenD did not reply to request {serial} within {ReplyTimeout.TotalSeconds}s."));
            }
        });

        var result = await tcs.Task.ConfigureAwait(false);

        return result as TResponse
            ?? throw new InvalidOperationException(
                $"OpenD returned {result.GetType().Name} where {typeof(TResponse).Name} was expected.");
    }

    /// <summary>Completes the waiter for a serial number, if one is still registered.</summary>
    private void Deliver(uint serial, object response)
    {
        if (_pending.TryRemove(serial, out var tcs))
        {
            tcs.TrySetResult(response);
        }
    }

    /// <inheritdoc />
    public void OnInitConnect(FTAPI_Conn client, long errCode, string desc)
    {
        var ok = errCode == 0;
        IsConnected = ok;

        if (ok)
        {
            _logger.LogInformation("Connected to OpenD at {Host}:{Port}", _options.Host, _options.Port);
        }
        else
        {
            _logger.LogWarning("OpenD connection failed ({Code}): {Desc}", errCode, desc);
        }

        _connectResult?.TrySetResult(ok);
        ConnectionStateChanged?.Invoke(this, ok);
    }

    /// <inheritdoc />
    public void OnDisconnect(FTAPI_Conn client, long errCode)
    {
        IsConnected = false;
        _logger.LogWarning("OpenD disconnected ({Code})", errCode);

        // Everything still waiting will otherwise sit until its timeout. Failing them now
        // lets the scanner move on and the UI show disconnected immediately.
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException("OpenD disconnected."));
            }
        }

        ConnectionStateChanged?.Invoke(this, false);
    }

    // ---- Replies this application consumes ----------------------------------

    /// <inheritdoc />
    public void OnReply_GetSecuritySnapshot(FTAPI_Conn c, uint serial, QotGetSecuritySnapshot.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_RequestHistoryKL(FTAPI_Conn c, uint serial, QotRequestHistoryKL.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_GetKL(FTAPI_Conn c, uint serial, QotGetKL.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_Sub(FTAPI_Conn c, uint serial, QotSub.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_GetOptionChain(FTAPI_Conn c, uint serial, QotGetOptionChain.Response rsp) =>
        Deliver(serial, rsp);

    /// <inheritdoc />
    public void OnReply_GetRT(FTAPI_Conn c, uint serial, QotGetRT.Response rsp) =>
        Deliver(serial, rsp);

    // ---- Request helpers -----------------------------------------------------

    internal Task<QotGetSecuritySnapshot.Response> GetSnapshotAsync(QotGetSecuritySnapshot.Request req, CancellationToken ct) =>
        SendAsync<QotGetSecuritySnapshot.Response>(() => _qot.GetSecuritySnapshot(req), ct);

    internal Task<QotRequestHistoryKL.Response> RequestHistoryAsync(QotRequestHistoryKL.Request req, CancellationToken ct) =>
        SendAsync<QotRequestHistoryKL.Response>(() => _qot.RequestHistoryKL(req), ct);

    internal Task<QotGetKL.Response> GetKlineAsync(QotGetKL.Request req, CancellationToken ct) =>
        SendAsync<QotGetKL.Response>(() => _qot.GetKL(req), ct);

    internal Task<QotSub.Response> SubscribeAsync(QotSub.Request req, CancellationToken ct) =>
        SendAsync<QotSub.Response>(() => _qot.Sub(req), ct);

    internal Task<QotGetOptionChain.Response> GetOptionChainAsync(QotGetOptionChain.Request req, CancellationToken ct) =>
        SendAsync<QotGetOptionChain.Response>(() => _qot.GetOptionChain(req), ct);

    /// <summary>Time-share (per-minute price) data for the current session.</summary>
    /// <remarks>
    /// Present because neither K-line call serves today's extended-hours bars, so this is
    /// the only candidate source for a premarket high and low on the session actually being
    /// traded. See <c>LiveDataProbe</c> for the measurement.
    /// </remarks>
    internal Task<QotGetRT.Response> GetRealTimeAsync(QotGetRT.Request req, CancellationToken ct) =>
        SendAsync<QotGetRT.Response>(() => _qot.GetRT(req), ct);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsConnected = false;
        _qot.Close();
    }
}
