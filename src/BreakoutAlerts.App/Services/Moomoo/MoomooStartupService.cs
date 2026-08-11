using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// Opens the gateway connection once at startup.
/// </summary>
/// <remarks>
/// Registered only when the live provider is selected. A failed connect is <b>not</b> a
/// startup failure: the application must come up and show a disconnected state rather than
/// refusing to launch, so the user can see what is wrong and start OpenD.
/// </remarks>
public sealed class MoomooStartupService : IHostedService
{
    private readonly MoomooConnection _connection;
    private readonly ILogger<MoomooStartupService> _logger;

    /// <summary>Creates the service.</summary>
    public MoomooStartupService(MoomooConnection connection, ILogger<MoomooStartupService> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connected = await _connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (!connected)
        {
            _logger.LogWarning(
                "Could not reach OpenD. The application will run in a disconnected state - " +
                "no data and no alerts - until the gateway is started and logged in. " +
                "Synthetic data is NOT substituted.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }
}
