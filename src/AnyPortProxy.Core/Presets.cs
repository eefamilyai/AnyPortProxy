namespace AnyPortProxy.Core;

public enum Risk { Low, Medium, High }

public sealed record PortPreset(string Name, int Port, int? EndPort, PortProtocol Protocol, string Description, Risk Risk = Risk.Low, bool ByAddress = false)
{
    public int Last => EndPort ?? Port;
    public string Range => EndPort is int e ? $"{Port}-{e}" : $"{Port}";
    public string ProtocolText => Protocol switch { PortProtocol.Udp => "UDP", PortProtocol.Both => "TCP+UDP", _ => "TCP" };
    public override string ToString() => $"{Name}  ({Range} {ProtocolText})";
}

/// <summary>Known apps and games, plus warnings for ports that are dangerous to expose.</summary>
public static class Presets
{
    public static IReadOnlyList<PortPreset> All { get; } =
    [
        new("Minecraft Java Edition", 25565, null, PortProtocol.Tcp, "The normal PC version of Minecraft.", ByAddress: true),
        new("Minecraft Bedrock Edition", 19132, null, PortProtocol.Udp, "Minecraft for Windows 10/11, consoles and phones."),
        new("Terraria", 7777, null, PortProtocol.Tcp, "Terraria dedicated server."),
        new("Valheim", 2456, 2458, PortProtocol.Udp, "Valheim dedicated server."),
        new("Palworld", 8211, null, PortProtocol.Udp, "Palworld dedicated server."),
        new("Satisfactory", 7777, null, PortProtocol.Both, "Satisfactory dedicated server (1.0+)."),
        new("ARK: Survival Evolved", 7777, 7778, PortProtocol.Udp, "ARK game ports. Also open 27015 UDP for the server list."),
        new("Rust", 28015, 28016, PortProtocol.Both, "Rust game port + RCON."),
        new("Factorio", 34197, null, PortProtocol.Udp, "Factorio multiplayer."),
        new("Counter-Strike 2 / Source games", 27015, null, PortProtocol.Both, "Source-engine dedicated servers."),
        new("Project Zomboid", 16261, 16262, PortProtocol.Udp, "Project Zomboid dedicated server."),
        new("7 Days to Die", 26900, 26902, PortProtocol.Both, "7 Days to Die dedicated server."),
        new("Plex Media Server", 32400, null, PortProtocol.Tcp, "Watch your Plex library from anywhere.", ByAddress: true),
        new("Jellyfin", 8096, null, PortProtocol.Tcp, "Jellyfin media server (http).", ByAddress: true),
        new("Emby", 8096, null, PortProtocol.Tcp, "Emby media server (http).", ByAddress: true),
        new("Home Assistant", 8123, null, PortProtocol.Tcp, "Home Assistant web interface.", ByAddress: true),
        new("Web dev server (React / Next.js / Node)", 3000, null, PortProtocol.Tcp, "Typical port for `npm start` / `next dev`.", Risk.Medium, ByAddress: true),
        new("Vite dev server", 5173, null, PortProtocol.Tcp, "Typical port for `npm run dev` with Vite.", Risk.Medium, ByAddress: true),
        new("Web app on 8080", 8080, null, PortProtocol.Tcp, "Common alternative web port.", ByAddress: true),
        new("Web app on 8443 (https)", 8443, null, PortProtocol.Tcp, "Common alternative https port.", ByAddress: true),
        new("WireGuard VPN", 51820, null, PortProtocol.Udp, "WireGuard VPN server."),
        new("TeamSpeak 3", 9987, null, PortProtocol.Udp, "TeamSpeak voice server (also uses 30033 TCP for files)."),
        new("Mumble", 64738, null, PortProtocol.Both, "Mumble voice server."),
        new("SSH (remote terminal)", 22, null, PortProtocol.Tcp, "Remote command line access.", Risk.Medium),
        new("Remote Desktop (RDP)", 3389, null, PortProtocol.Tcp, "Windows Remote Desktop.", Risk.High),
    ];

    private static readonly Dictionary<int, (Risk Risk, string Why)> Dangerous = new()
    {
        [21] = (Risk.High, "FTP sends passwords unencrypted."),
        [22] = (Risk.Medium, "SSH is constantly attacked by bots. Only open it with key-based login and no passwords."),
        [23] = (Risk.High, "Telnet is unencrypted and heavily attacked."),
        [135] = (Risk.High, "Windows RPC should never be on the internet."),
        [137] = (Risk.High, "Windows file sharing (NetBIOS) should never be on the internet."),
        [138] = (Risk.High, "Windows file sharing (NetBIOS) should never be on the internet."),
        [139] = (Risk.High, "Windows file sharing should never be on the internet."),
        [445] = (Risk.High, "Windows file sharing (SMB) is how ransomware spreads. Never open this."),
        [1433] = (Risk.High, "SQL Server databases shouldn't be on the internet."),
        [2375] = (Risk.High, "The Docker API lets anyone take over this PC."),
        [3306] = (Risk.High, "MySQL databases shouldn't be on the internet."),
        [3389] = (Risk.High, "Remote Desktop is one of the most attacked ports on the internet. Use a VPN (e.g. Tailscale) instead."),
        [5432] = (Risk.High, "PostgreSQL databases shouldn't be on the internet."),
        [5900] = (Risk.High, "VNC remote control is heavily attacked."),
        [5985] = (Risk.High, "WinRM remote management should never be on the internet."),
        [5986] = (Risk.High, "WinRM remote management should never be on the internet."),
        [6379] = (Risk.High, "Redis has no password by default."),
        [11434] = (Risk.High, "Ollama has no password: anyone could use your AI models and GPU."),
        [27017] = (Risk.High, "MongoDB databases shouldn't be on the internet."),
    };

    public static PortPreset? ForPort(int port) =>
        All.FirstOrDefault(p => port >= p.Port && port <= p.Last);

    public static (Risk Risk, string Why)? Warning(int port) =>
        Dangerous.TryGetValue(port, out var w) ? w : null;

    public static (Risk Risk, string Why)? WarningForRange(int lo, int hi)
    {
        (Risk, string)? worst = null;
        for (int p = lo; p <= hi && p - lo < 2000; p++)
        {
            if (Warning(p) is { } w && (worst is null || w.Risk > worst.Value.Item1)) worst = w;
        }
        return worst;
    }

    public static List<PortPreset> Search(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return All.ToList();
        if (int.TryParse(text, out var port)) return All.Where(p => port >= p.Port && port <= p.Last).ToList();
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All.Where(p => words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                              || p.Description.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>One-line plain-English description of what a port is usually for.</summary>
    public static string? Describe(int port)
    {
        var known = All.Where(p => port >= p.Port && port <= p.Last).Select(p => p.Name).Distinct().ToList();
        if (known.Count > 0) return $"Port {port} is usually used by {string.Join(" / ", known)}.";
        return port switch
        {
            80 => "Port 80 is normal websites (http).",
            443 => "Port 443 is secure websites (https).",
            25 or 465 or 587 => "This is an email (SMTP) port. Many internet providers block it.",
            >= 49152 => "Ports 49152+ are used by Windows for outgoing connections; pick a lower port if you can.",
            _ => null,
        };
    }
}
