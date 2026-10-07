using System.Diagnostics;

namespace AnyPortProxy.Core;

/// <summary>Installs/uninstalls the Windows service, shortcuts, firewall rule and the "apx" command.</summary>
public static class Installer
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AnyPortProxy";

    /// <summary>Version currently installed (from Apps &amp; features), or null.</summary>
    public static string? InstalledVersion()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey);
        if (key?.GetValue("DisplayVersion") is string v) return v;
        var exe = Path.Combine(AppPaths.InstallDir, AppPaths.CliExe);
        return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] : null;
    }

    /// <param name="sourceDir">Folder holding the program files (default: this app's folder). The setup passes its extracted payload.</param>
    public static void Install(Action<string> log, string? sourceDir = null)
    {
        Elevation.Require();
        var src = sourceDir ?? AppPaths.AppDir;
        var dst = AppPaths.InstallDir;
        var state = ServiceManager.GetState();

        if (state is ServiceState.Running or ServiceState.Starting)
        {
            log("Stopping the running proxy…");
            ServiceManager.Stop();
        }

        if (!AppPaths.SameDir(src, dst))
        {
            log($"Copying program files to {dst}…");
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                CopyWithRetry(file, Path.Combine(dst, name));
            }
        }

        var exe = Path.Combine(dst, AppPaths.CliExe);
        if (!File.Exists(exe)) throw new FileNotFoundException($"{AppPaths.CliExe} is missing from {dst}. Run build.ps1 first.");
        if (!File.Exists(Path.Combine(dst, "WinDivert64.sys")))
            log("WARNING: WinDivert files are missing, so all-ports forwarding won't work. Run build.ps1 to get them.");

        ConfigStore.EnsureExists();
        log($"Settings file: {AppPaths.ConfigFile}");

        var sc = ProcessRunner.System32("sc.exe");
        var binPath = $"\"{exe}\" run";
        if (state == ServiceState.NotInstalled)
        {
            log("Registering the background service…");
            Check(ProcessRunner.Run(sc, "create", AppPaths.ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", AppPaths.DisplayName));
        }
        else
        {
            Check(ProcessRunner.Run(sc, "config", AppPaths.ServiceName, "binPath=", binPath, "start=", "auto"));
        }
        ProcessRunner.Run(sc, "description", AppPaths.ServiceName,
            "Lets the internet reach your computers: websites by subdomain on 80/443, every other port forwarded.");
        ProcessRunner.Run(sc, "failure", AppPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");

        log("Allowing AnyPortProxy through Windows Firewall…");
        if (!Firewall.AllowProgram(exe, out var fwError)) log($"WARNING: couldn't add the firewall rule: {fwError}");

        log("Adding Start menu and desktop shortcuts…");
        var gui = Path.Combine(dst, AppPaths.GuiExe);
        if (File.Exists(gui))
        {
            TryShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "AnyPortProxy.lnk"), gui, log);
            TryShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "AnyPortProxy.lnk"), gui, log);
        }

        log("Adding the \"apx\" terminal command…");
        File.WriteAllText(Path.Combine(dst, "apx.cmd"), "@\"%~dp0AnyPortProxy.exe\" %*\r\n");
        AddToPath(dst);

        log("Adding AnyPortProxy to Windows' installed apps list…");
        RegisterUninstall(dst, exe, gui);

        log("Starting AnyPortProxy…");
        ServiceManager.Start();
        log("Done! AnyPortProxy is running and will start automatically with Windows.");
    }

    public static async Task UninstallAsync(bool removeSettings, Action<string> log)
    {
        Elevation.Require();
        AppConfig? cfg = null;
        try { cfg = ConfigStore.Load(); } catch { }

        if (ServiceManager.GetState() != ServiceState.NotInstalled)
        {
            log("Stopping and removing the background service…");
            try { ServiceManager.Stop(); } catch { }
            ProcessRunner.Run(ProcessRunner.System32("sc.exe"), "delete", AppPaths.ServiceName);
        }

        log("Removing firewall rules…");
        Firewall.Delete(Firewall.ProxyRuleName);
        if (cfg is not null)
        {
            foreach (var rule in cfg.Ports)
                foreach (var proto in rule.Protocols())
                    Firewall.Delete(Firewall.PortRuleName(rule.Range, proto));

            var routerRules = cfg.Ports.Where(r => r.Router).ToList();
            if (routerRules.Count > 0)
            {
                log("Removing router forwards…");
                var gw = await UpnpGateway.DiscoverAsync();
                if (gw is not null)
                {
                    foreach (var rule in routerRules)
                        foreach (var proto in rule.Protocols())
                            for (int p = rule.Port; p <= rule.Last; p++)
                                try { await gw.DeletePortMappingAsync(p, proto); } catch { }
                }
            }
        }

        log("Removing shortcuts and the apx command…");
        foreach (var lnk in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "AnyPortProxy.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "AnyPortProxy.lnk"),
                 })
        {
            try { File.Delete(lnk); } catch { }
        }
        RemoveFromPath(AppPaths.InstallDir);
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }

        if (removeSettings && Directory.Exists(AppPaths.DataDir))
        {
            log("Deleting settings and logs…");
            try { Directory.Delete(AppPaths.DataDir, true); } catch (Exception ex) { log($"Couldn't delete {AppPaths.DataDir}: {ex.Message}"); }
        }

        if (Directory.Exists(AppPaths.InstallDir))
        {
            if (AppPaths.SameDir(AppPaths.AppDir, AppPaths.InstallDir))
            {
                // Can't delete our own running exe; let a helper finish after we exit.
                log("Program files will be deleted after this window closes.");
                Process.Start(new ProcessStartInfo(ProcessRunner.System32("cmd.exe"),
                    $"/c ping 127.0.0.1 -n 5 >nul & rmdir /s /q \"{AppPaths.InstallDir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            else
            {
                try { Directory.Delete(AppPaths.InstallDir, true); } catch (Exception ex) { log($"Couldn't delete {AppPaths.InstallDir}: {ex.Message}"); }
            }
        }
        log("AnyPortProxy has been uninstalled.");
    }

    /// <summary>Settings → Apps → Installed apps entry, so it can be uninstalled the normal Windows way.</summary>
    private static void RegisterUninstall(string dir, string cliExe, string guiExe)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UninstallKey);
            long bytes = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);
            key.SetValue("DisplayName", "AnyPortProxy");
            key.SetValue("DisplayVersion", AppPaths.Version);
            key.SetValue("Publisher", "AnyPortProxy");
            key.SetValue("DisplayIcon", File.Exists(guiExe) ? $"\"{guiExe}\",0" : $"\"{cliExe}\",0");
            key.SetValue("InstallLocation", dir);
            key.SetValue("UninstallString", $"\"{cliExe}\" uninstall --pause");
            key.SetValue("QuietUninstallString", $"\"{cliExe}\" uninstall --yes");
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(bytes / 1024), Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        catch
        {
            // Not fatal: the app works without the Apps & features entry.
        }
    }

    private static void Check((int Code, string Output) r)
    {
        if (r.Code != 0) throw new InvalidOperationException(r.Output);
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (i < 10)
            {
                Thread.Sleep(500);
            }
            catch (IOException ex)
            {
                throw new IOException($"Couldn't replace {Path.GetFileName(to)}. Close any other AnyPortProxy windows and try again. ({ex.Message})");
            }
        }
    }

    private static void TryShortcut(string lnkPath, string target, Action<string> log)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = Path.GetDirectoryName(target);
            lnk.Description = "AnyPortProxy control panel";
            lnk.Save();
        }
        catch (Exception ex)
        {
            log($"Couldn't create shortcut {lnkPath}: {ex.Message}");
        }
    }

    // Edit the raw registry value so %SystemRoot%-style entries stay unexpanded (REG_EXPAND_SZ).
    private const string EnvKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private static bool IsDir(string entry, string dir)
    {
        try { return AppPaths.SameDir(Environment.ExpandEnvironmentVariables(entry), dir); }
        catch { return false; }
    }

    private static void AddToPath(string dir) => EditPath(parts =>
    {
        if (parts.Any(p => IsDir(p, dir))) return false;
        parts.Add(dir);
        return true;
    });

    private static void RemoveFromPath(string dir) => EditPath(parts => parts.RemoveAll(p => IsDir(p, dir)) > 0);

    private static void EditPath(Func<List<string>, bool> edit)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(EnvKey, writable: true);
        if (key is null) return;
        var raw = key.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!edit(parts)) return;
        key.SetValue("Path", string.Join(';', parts), Microsoft.Win32.RegistryValueKind.ExpandString);
        Native.SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }
}
