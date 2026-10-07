using AnyPortProxy.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy;

/// <summary>
/// Automatic updates (only when running as the Windows service, and only if allowed in settings):
/// checks the GitHub releases every 6 hours, downloads + verifies a newer installer, and installs it
/// silently when nobody is connected — or after a day at the latest. Settings are always kept.
/// </summary>
public sealed class UpdateService(StatusTracker status, ILogger<UpdateService> log) : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxWaitForIdle = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!WindowsServiceHelpers.IsWindowsService()) return; // never auto-install from a test run ("apx run")
        try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAndInstallAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                status.SetUpdate($"Last update check failed: {ex.Message}");
                log.LogWarning("Update check failed: {Error}", ex.Message);
            }
            try { await Task.Delay(CheckEvery, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAndInstallAsync(CancellationToken ct)
    {
        AppConfig cfg;
        try { cfg = ConfigStore.Load(); } catch { return; }
        var repo = Updater.Repo(cfg);
        if (repo is null) return; // this build has no update source

        var update = await Updater.CheckAsync(repo, ct);
        if (update is null)
        {
            status.SetUpdate(null);
            return;
        }
        if (!cfg.Updates.AutoInstall)
        {
            status.SetUpdate($"Version {update.Version} is available (automatic install is off).");
            return;
        }

        log.LogInformation("Update {Version} found; downloading", update.Version);
        status.SetUpdate($"Downloading version {update.Version}…");
        var path = await Updater.DownloadAsync(update, null, ct);

        // Wait for a quiet moment so nobody's game or download is cut off.
        var deadline = DateTime.UtcNow + MaxWaitForIdle;
        status.SetUpdate($"Version {update.Version} is ready — installing when nobody is connected.");
        while (status.ActiveConnections > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMinutes(1), ct);

        log.LogWarning("Installing update {Version} (the service restarts in a moment)", update.Version);
        status.SetUpdate($"Installing version {update.Version}…");
        Updater.RunInstaller(path, reopenApp: false); // the installer stops this service, replaces the files, starts it again
    }
}
