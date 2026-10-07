using AnyPortProxy.Core;

namespace AnyPortProxy.Setup;

internal static class Program
{
    /// <summary>
    /// AnyPortProxySetup.exe          → setup wizard
    /// AnyPortProxySetup.exe /S       → silent install/update (exit code 0 = success); log in %TEMP%\AnyPortProxySetup.log
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("/silent", StringComparison.OrdinalIgnoreCase)
                                    || a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        if (silent) return SilentInstall();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "AnyPortProxy Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
        return 0;
    }

    private static int SilentInstall()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "AnyPortProxySetup.log");
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Log(string line) => log.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        try
        {
            Log($"AnyPortProxy Setup {AppPaths.Version} (silent)");
            Payload.CloseApps(Payload.RunningApps());
            Payload.Install(Log);
            Log("Success");
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            return 1;
        }
    }
}
