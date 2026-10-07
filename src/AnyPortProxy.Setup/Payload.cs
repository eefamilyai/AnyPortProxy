using System.Diagnostics;
using System.IO.Compression;
using AnyPortProxy.Core;

namespace AnyPortProxy.Setup;

/// <summary>The program files embedded in this setup, and the install / uninstall steps around them.</summary>
internal static class Payload
{
    private static readonly string[] Required = [AppPaths.CliExe, AppPaths.GuiExe, "WinDivert.dll", "WinDivert64.sys"];

    public static bool IsPresent => typeof(Payload).Assembly.GetManifestResourceInfo("payload.zip") is not null;

    /// <summary>Unpacks the program files to a fresh temp folder and returns it.</summary>
    public static string Extract()
    {
        using var zip = typeof(Payload).Assembly.GetManifestResourceStream("payload.zip")
                        ?? throw new InvalidOperationException("This setup file is incomplete (it has no program files inside). Rebuild it with build.ps1.");
        var dir = Path.Combine(Path.GetTempPath(), "AnyPortProxySetup-" + Guid.NewGuid().ToString("N")[..8]);
        ZipFile.ExtractToDirectory(zip, dir);
        var missing = Required.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
        if (missing.Count > 0)
        {
            TryDelete(dir);
            throw new InvalidOperationException($"This setup file is incomplete (missing {string.Join(", ", missing)}). Rebuild it with build.ps1.");
        }
        return dir;
    }

    /// <summary>AnyPortProxy windows/terminals running from the install folder (not the background service).</summary>
    public static List<Process> RunningApps()
    {
        var result = new List<Process>();
        foreach (var name in new[] { "AnyPortProxyGui", "AnyPortProxy" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    // The service runs in session 0; the installer stops it properly through Windows.
                    if (p.Id == Environment.ProcessId || p.SessionId == 0) continue;
                    var path = p.MainModule?.FileName;
                    if (path is not null && AppPaths.SameDir(Path.GetDirectoryName(path)!, AppPaths.InstallDir)) result.Add(p);
                }
                catch
                {
                    // Can't inspect it (exited, access denied): leave it alone.
                }
            }
        }
        return result;
    }

    public static void CloseApps(IEnumerable<Process> apps)
    {
        foreach (var p in apps)
        {
            try
            {
                if (!p.CloseMainWindow() || !p.WaitForExit(3000))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>Full install/update: unpack, then the same installer the app and "apx install" use.</summary>
    public static void Install(Action<string> log)
    {
        log("Unpacking program files…");
        var dir = Extract();
        try
        {
            Installer.Install(log, dir);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    public static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
