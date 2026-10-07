using System.Text.Json.Serialization;

namespace AnyPortProxy.Core;

public sealed class ProxyOptions
{
    public const string Section = "Proxy";

    /// <summary>Where traffic goes when no route matches. "host" (keep incoming port) or "host:port".</summary>
    public string DefaultTarget { get; set; } = "127.0.0.1";

    /// <summary>Ports where the first packet is inspected for a hostname (TLS SNI / HTTP Host / Minecraft).</summary>
    public List<int> SniffPorts { get; set; } = new();

    public List<RouteRule> Routes { get; set; } = new();

    public int SniffTimeoutMs { get; set; } = 5000;

    public int ConnectTimeoutMs { get; set; } = 5000;

    public CatchAllOptions CatchAll { get; set; } = new();

    /// <summary>"Send port X to computer Y" rules (TCP and/or UDP). Work without the WinDivert driver.</summary>
    public List<PortForward> Forwards { get; set; } = new();

    public LimitOptions Limits { get; set; } = new();
}

/// <summary>Sends one port (or a range) on this PC to another computer.</summary>
public sealed class PortForward
{
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public int? EndPort { get; set; }
    public PortProtocol Protocol { get; set; } = PortProtocol.Tcp;

    /// <summary>"host" (same port) or "host:port" (for a range: the first port; the rest follow in order).</summary>
    public string Target { get; set; } = "";

    [JsonIgnore] public int Last => EndPort is int e && e > Port ? e : Port;
    [JsonIgnore] public string Range => Last > Port ? $"{Port}-{Last}" : $"{Port}";
    [JsonIgnore] public string ProtocolText => Protocol switch { PortProtocol.Udp => "UDP", PortProtocol.Both => "TCP+UDP", _ => "TCP" };

    public bool HasTcp => Protocol != PortProtocol.Udp;
    public bool HasUdp => Protocol != PortProtocol.Tcp;

    /// <summary>Destination host and port for incoming port <paramref name="port"/>.</summary>
    public (string Host, int Port) TargetFor(int port)
    {
        TargetParser.TryParse(Target, out var host, out var tp);
        return (host, tp is int first ? first + (port - Port) : port);
    }
}

/// <summary>Protection against floods and runaway clients.</summary>
public sealed class LimitOptions
{
    /// <summary>Open connections the proxy will hold at once (all clients together).</summary>
    public int MaxConnections { get; set; } = 20_000;

    /// <summary>Open connections a single internet address may hold. Home-network addresses are exempt.</summary>
    public int MaxConnectionsPerIp { get; set; } = 300;
}

public sealed class RouteRule
{
    /// <summary>Exact hostname, "*.example.com" wildcard, or "*".</summary>
    public string Host { get; set; } = "";

    /// <summary>Only match connections arriving on this port. Null = any sniff port.</summary>
    public int? Port { get; set; }

    /// <summary>"host" (keep incoming port) or "host:port".</summary>
    public string Target { get; set; } = "";
}

public sealed class CatchAllOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Internal port that redirected connections land on. Never exposed directly.</summary>
    public int ListenPort { get; set; } = 34010;

    /// <summary>Comma-separated ports/ranges to forward, e.g. "1-49151" or "3000-3999, 8080".</summary>
    public string AllowedPorts { get; set; } = "1-49151";

    public List<int> BlockedPorts { get; set; } = new();

    /// <summary>Also redirect connections from private/LAN/CGNAT source addresses.</summary>
    public bool InterceptLan { get; set; }

    /// <summary>Host to forward catch-all ports to. Null = host part of DefaultTarget.</summary>
    public string? Target { get; set; }

    /// <summary>Packet worker threads. 0 = automatic (based on CPU count).</summary>
    public int Workers { get; set; }

    /// <summary>
    /// When forwarding to this PC: ignore probes to ports where nothing is running (port scanners),
    /// find apps that only listen on ::1, and let apps you opened with the port helper receive
    /// connections directly (faster, and they see the visitor's real IP).
    /// </summary>
    public bool SmartRouting { get; set; } = true;

    /// <summary>
    /// Also forward UDP (games, voice chat, VPNs). Apps on this PC that listen on all interfaces get UDP directly;
    /// apps that only listen on localhost — or a forwarding target on another computer — are relayed.
    /// </summary>
    public bool Udp { get; set; } = true;

    /// <summary>UDP ports Windows itself uses; never redirected (DHCP, NTP, NetBIOS, IPsec, SSDP, mDNS, LLMNR, WS-Discovery).</summary>
    [JsonIgnore]
    public static readonly int[] SystemUdpPorts = [53, 67, 68, 123, 137, 138, 500, 1900, 3702, 4500, 5353, 5355];

    [JsonIgnore]
    public static readonly int[] DefaultBlocked = [22, 23, 135, 137, 138, 139, 445, 3389, 5357, 5985, 5986];
}

public enum PortProtocol { Tcp, Udp, Both }

/// <summary>A port the user opened with the helper (firewall rule and/or router mapping).</summary>
public sealed class PortRule
{
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public int? EndPort { get; set; }
    public PortProtocol Protocol { get; set; } = PortProtocol.Tcp;
    public bool Firewall { get; set; } = true;
    public bool Router { get; set; }

    [JsonIgnore] public int Last => EndPort is int e && e > Port ? e : Port;
    [JsonIgnore] public string Range => Last > Port ? $"{Port}-{Last}" : $"{Port}";
    [JsonIgnore] public string ProtocolText => Protocol switch { PortProtocol.Udp => "UDP", PortProtocol.Both => "TCP+UDP", _ => "TCP" };

    public IEnumerable<string> Protocols()
    {
        if (Protocol != PortProtocol.Udp) yield return "TCP";
        if (Protocol != PortProtocol.Tcp) yield return "UDP";
    }
}

public sealed class AppConfig
{
    /// <summary>The user's domain (e.g. reggilion.com), used to show "connect to" addresses.</summary>
    public string? Domain { get; set; }

    public ProxyOptions Proxy { get; set; } = new();

    public List<PortRule> Ports { get; set; } = new();

    /// <summary>The user finished (or skipped) the getting-started tour.</summary>
    public bool Onboarded { get; set; }

    [JsonIgnore]
    internal System.Text.Json.Nodes.JsonObject? Raw { get; set; }

    /// <summary>Host that catch-all ports are forwarded to.</summary>
    [JsonIgnore]
    public string CatchAllHost
    {
        get
        {
            TargetParser.TryParse(Proxy.CatchAll.Target ?? Proxy.DefaultTarget, out var h, out _);
            return h;
        }
    }
}
