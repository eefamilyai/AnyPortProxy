using System.Diagnostics;
using System.Security.Principal;

namespace AnyPortProxy.Core;

public static class Elevation
{
    public static bool IsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static void Require()
    {
        if (!IsAdmin) throw new UnauthorizedAccessException("This needs Administrator rights.");
    }
}

public static class ProcessRunner
{
    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static (int ExitCode, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(); } catch { }
            return (-1, "timed out");
        }
        return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }
}

public static class Firewall
{
    public const string ProxyRuleName = "AnyPortProxy (proxy)";

    public static string PortRuleName(string range, string protocol) => $"AnyPortProxy port {range} {protocol}";

    private static (int, string) Netsh(params string[] args) => ProcessRunner.Run(ProcessRunner.System32("netsh.exe"), args);

    public static bool Exists(string name) => Netsh("advfirewall", "firewall", "show", "rule", $"name={name}").Item1 == 0;

    public static void Delete(string name) => Netsh("advfirewall", "firewall", "delete", "rule", $"name={name}");

    public static bool AllowProgram(string exe, out string error)
    {
        Delete(ProxyRuleName);
        var (code, output) = Netsh("advfirewall", "firewall", "add", "rule", $"name={ProxyRuleName}", "dir=in", "action=allow",
            $"program={exe}", "protocol=TCP", "profile=any", "enable=yes",
            "description=Lets internet traffic reach AnyPortProxy. Added by AnyPortProxy.");
        error = output;
        return code == 0;
    }

    public static bool AllowPort(string range, string protocol, string description, out string error)
    {
        var name = PortRuleName(range, protocol);
        Delete(name);
        var (code, output) = Netsh("advfirewall", "firewall", "add", "rule", $"name={name}", "dir=in", "action=allow",
            $"protocol={protocol}", $"localport={range}", "profile=any", "enable=yes",
            $"description={description} (added by AnyPortProxy)");
        error = output;
        return code == 0;
    }
}
