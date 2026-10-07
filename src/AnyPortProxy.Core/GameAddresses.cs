namespace AnyPortProxy.Core;

/// <summary>A game/app server reached by its own address on a shared port, e.g. mc2.example.com:25565 → 192.168.1.30.</summary>
public sealed record GameAddress(string Host, int Port, string Target)
{
    /// <summary>The catch-all entry ("*") for that port: players using an IP or an unknown name.</summary>
    public bool IsFallback => Host == "*";
}

/// <summary>
/// Several servers on one port, told apart by the address players type. Only works for protocols that send
/// that address: Minecraft Java (handshake), HTTPS/TLS (SNI) and HTTP (Host header).
/// </summary>
public static class GameAddresses
{
    private static string Norm(string h) => h.Trim().TrimEnd('.').ToLowerInvariant();

    public static List<GameAddress> List(ProxyOptions p) =>
        p.Routes.Where(r => !Websites.IsWebRule(r)).Select(r => new GameAddress(r.Host.Trim(), r.Port!.Value, r.Target)).ToList();

    /// <summary>What players type: Minecraft assumes 25565 when no port is given, so the port can be left out there.</summary>
    public static string ConnectAddress(string host, int port) => port == 25565 ? host : $"{host}:{port}";

    public static string? Validate(ProxyOptions p, string host, int port, string target)
    {
        if (Websites.ValidateHost(host) is { } hostError) return hostError;
        if (port is < 1 or > 65535) return "Ports go from 1 to 65535.";
        if (port is 80 or 443) return "Ports 80 and 443 are for websites — add it on the Websites page instead.";
        if (port == p.CatchAll.ListenPort) return $"Port {port} is used internally by AnyPortProxy.";
        if (p.Forwards.FirstOrDefault(f => f.HasTcp && port >= f.Port && port <= f.Last) is { } fw)
            return $"TCP port {port} is already sent to {fw.Target} by a port rule. Remove that rule first.";
        if (!TargetParser.TryParseValid(target, out var th, out var tp))
            return "Type the computer's IP address, like 192.168.1.20 (optionally with :port).";
        if (NetInfo.IsThisPc(th) && (tp ?? port) == port)
            return $"A server on this PC must use a different port (like {port + 1}), because AnyPortProxy answers on {port} to read the address.";
        return null;
    }

    /// <summary>
    /// Adds (or replaces) the address. The first address on a port also becomes the fallback for players who use
    /// an IP address or a name that isn't listed, so they don't hit a dead end.
    /// </summary>
    public static void Add(ProxyOptions p, string host, int port, string target)
    {
        host = host.Trim().TrimEnd('.');
        if (!p.SniffPorts.Contains(port)) p.SniffPorts.Add(port);
        p.Routes.RemoveAll(r => r.Port == port && Norm(r.Host) == Norm(host));
        p.Routes.Add(new RouteRule { Host = host, Port = port, Target = target });
        if (!p.Routes.Any(r => r.Port == port && r.Host.Trim() == "*"))
            p.Routes.Add(new RouteRule { Host = "*", Port = port, Target = target });
    }

    /// <summary>Removes an address; when the last one on a port goes, the port returns to normal forwarding.</summary>
    public static bool Remove(ProxyOptions p, string host, int port)
    {
        int removed = p.Routes.RemoveAll(r => r.Port == port && Norm(r.Host) == Norm(host));
        if (!p.Routes.Any(r => r.Port == port && r.Host.Trim() != "*"))
        {
            p.Routes.RemoveAll(r => r.Port == port);
            p.SniffPorts.Remove(port);
        }
        return removed > 0;
    }

    /// <summary>Plain-English explanation for a game that can't share a port by address.</summary>
    public static string WhyNot(string game, int port) =>
        $"{game} doesn't send the address players type, so servers can't share port {port} by address (no program can tell them apart). " +
        $"Instead give each server its own port: e.g. {port} → first computer, {port + 1} → second computer " +
        "(Ports → \"Send a port to another computer\").";
}
