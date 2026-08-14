using System.Diagnostics;
using System.IO;
using BreakoutAlerts.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.Services;

/// <summary>
/// Starts the moomoo OpenD gateway when it is not already running.
/// </summary>
/// <remarks>
/// After a reboot the gateway is usually not running, and without it this application can do
/// nothing at all - no quotes, no scanning, no orders. Making the user go and find OpenD
/// themselves is a small friction repeated every single morning.
///
/// <para><b>Only ever from an explicit click.</b> This is deliberately not wired to startup or
/// to the reconnect loop. Launching another application is a visible side effect, and one that
/// then prompts for a login - doing it unasked, possibly while the user is elsewhere, is not
/// this application's decision to make.</para>
///
/// <para>Whether OpenD then connects on its own depends on <c>OpenD.xml</c>: it holds
/// <c>login_account</c> and <c>login_pwd</c>, and if those are populated the gateway logs in
/// unattended. If they are not, launching it presents its own login window - still useful,
/// because the alternative is hunting for the shortcut.</para>
/// </remarks>
public sealed class OpenDLauncher
{
    private readonly MarketDataOptions _options;
    private readonly ILogger<OpenDLauncher> _logger;

    /// <summary>Process name OpenD runs under, without the extension.</summary>
    private const string ProcessName = "moomoo_OpenD";

    /// <summary>Creates the launcher.</summary>
    public OpenDLauncher(MarketDataOptions options, ILogger<OpenDLauncher> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Where the gateway executable is expected to be.</summary>
    /// <remarks>
    /// Configurable, defaulting to the standard per-user install. OpenD installs under
    /// <c>%APPDATA%</c> rather than Program Files, so the path contains the user's own profile
    /// and cannot be hard-coded for everyone.
    /// </remarks>
    public string ExecutablePath =>
        string.IsNullOrWhiteSpace(_options.OpenDPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "moomoo_OpenD", "moomoo_OpenD.exe")
            : _options.OpenDPath;

    /// <summary>True when a gateway process is already running.</summary>
    /// <remarks>
    /// Checked by process name rather than by whether the socket answers. The two are
    /// different questions: OpenD can be running but not yet logged in, and launching a second
    /// copy in that window would be worse than waiting.
    /// </remarks>
    public bool IsRunning => Process.GetProcessesByName(ProcessName).Length > 0;

    /// <summary>True when the executable can be found.</summary>
    public bool IsInstalled => File.Exists(ExecutablePath);

    /// <summary>Starts the gateway.</summary>
    /// <returns>A message describing what happened, for display.</returns>
    public string Launch()
    {
        if (IsRunning)
        {
            // Not an error. The usual reason the connection is down while OpenD runs is that
            // it is up but not logged in, and starting another copy would not help.
            return "OpenD is already running. If the connection is still down, check that it is logged in.";
        }

        if (!IsInstalled)
        {
            _logger.LogWarning("OpenD not found at {Path}", ExecutablePath);
            return $"Could not find OpenD at {ExecutablePath}. Set MarketData:OpenDPath in appsettings.json.";
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                // Its own folder, because OpenD reads OpenD.xml relative to where it starts.
                // Launched from this application's directory it would not find its config.
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
                UseShellExecute = true
            };

            Process.Start(info);

            _logger.LogInformation("Launched OpenD from {Path}", ExecutablePath);

            return "OpenD starting. It may take a few seconds, and will need logging in if credentials are not saved.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not launch OpenD from {Path}", ExecutablePath);
            return $"Could not start OpenD: {ex.Message}";
        }
    }
}
