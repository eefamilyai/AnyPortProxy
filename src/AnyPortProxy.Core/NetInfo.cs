using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AnyPortProxy.Core;

public sealed record Listener(string Protocol, IPAddress Address, int Port, int Pid, string Process);

public static class NetInfo
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>Everything listening on this PC (from netstat), with process names.</summary>
    public static List<Listener> GetListeners()
    {
        var (_, output) = ProcessRunner.Run(ProcessRunner.System32("netstat.exe"), "-ano");
        var names = new Dictionary<int, string>();
        var result = new List<Listener>();
        foreach (var raw in output.Split('\n'))
        {
            var t = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) continue;
            bool tcp = t[0] == "TCP", udp = t[0] == "UDP";
            if (tcp && !(t.Length >= 5 && (t[2] == "0.0.0.0:0" || t[2] == "[::]:0"))) continue; // only listening sockets
            if (udp && t[2] != "*:*") continue;
            if (!tcp && !udp) continue;
            if (!TrySplitEndpoint(t[1], out var ip, out var port) || !int.TryParse(t[^1], out var pid)) continue;

            if (!names.TryGetValue(pid, out var name))
            {
                name = pid switch
                {
                    0 => "Idle",
                    4 => "System",
                    _ => TryProcessName(pid),
                };
                names[pid] = name;
            }
            result.Add(new Listener(t[0], ip, port, pid, name));
        }
        return result;
    }

    private static string TryProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return $"PID {pid}"; }
    }

    private static bool TrySplitEndpoint(string s, out IPAddress ip, out int port)
    {
        ip = IPAddress.None;
        port = 0;
        int colon = s.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(s[(colon + 1)..], out port)) return false;
        return IPAddress.TryParse(s[..colon].Trim('[', ']'), out ip!);
    }

    /// <summary>IPv4 addresses of network adapters that have a default gateway (i.e. real LAN adapters).</summary>
    public static List<IPAddress> GatewayInterfaceAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(u => u.Address)
            .ToList();

    public static IPAddress? GetLanAddress() => GatewayInterfaceAddresses().FirstOrDefault();

    public static async Task<IPAddress?> GetPublicIpAsync(CancellationToken ct = default)
    {
        foreach (var url in new[] { "https://api.ipify.org", "https://ipv4.icanhazip.com" })
        {
            try
            {
                var text = (await Http.GetStringAsync(url, ct)).Trim();
                if (IPAddress.TryParse(text, out var ip)) return ip;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
        }
        return null;
    }

    public static async Task<bool> CanConnectAsync(string host, int port, int timeoutMs = 2000, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await s.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT
    }

    public static bool IsThisPc(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        return NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(u => u.Address.Equals(ip));
    }

    /// <summary>"netsh interface portproxy" rules (from an older setup, or other software).</summary>
    public static List<PortProxyRule> GetPortProxyRules()
    {
        var (_, output) = ProcessRunner.Run(ProcessRunner.System32("netsh.exe"), "interface", "portproxy", "show", "all");
        var rules = new List<PortProxyRule>();
        foreach (var line in output.Split('\n'))
        {
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 4 && int.TryParse(t[1], out var lp) && int.TryParse(t[3], out var cp))
                rules.Add(new PortProxyRule(t[0], lp, t[2], cp));
        }
        return rules;
    }

    /// <summary>Deletes one portproxy rule (tries each address-family combination, since "show all" doesn't say which).</summary>
    public static void DeletePortProxy(PortProxyRule rule)
    {
        foreach (var kind in new[] { "v4tov4", "v4tov6", "v6tov4", "v6tov6" })
        {
            ProcessRunner.Run(ProcessRunner.System32("netsh.exe"), "interface", "portproxy", "delete", kind,
                $"listenport={rule.ListenPort}", $"listenaddress={rule.ListenAddress}");
        }
    }
}

public sealed record PortProxyRule(string ListenAddress, int ListenPort, string ConnectAddress, int ConnectPort)
{
    public override string ToString() => $"{ListenAddress}:{ListenPort} → {ConnectAddress}:{ConnectPort}";
}
