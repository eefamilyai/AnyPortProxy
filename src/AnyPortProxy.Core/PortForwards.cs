namespace AnyPortProxy.Core;

public static class PortForwards
{
    public const int MaxPortsPerRule = 1000;

    /// <summary>Plain-English reason the rule can't work, or null if it's fine. <paramref name="ignore"/> = the rule being edited.</summary>
    public static string? Validate(ProxyOptions p, PortForward f, PortForward? ignore = null)
    {
        if (f.Port is < 1 or > 65535 || f.Last is < 1 or > 65535) return "Ports go from 1 to 65535.";
        if (f.EndPort is int e && e < f.Port) return "The second port must be bigger than the first.";
        if (f.Last - f.Port >= MaxPortsPerRule) return $"That's a lot of ports. Use at most {MaxPortsPerRule} per rule.";
        if (string.IsNullOrWhiteSpace(f.Target) || f.Target.Contains("://") || f.Target.Contains('/') || f.Target.Contains(' ')
            || !TargetParser.TryParseValid(f.Target, out var host, out var targetPort))
            return "Type the computer's IP address, like 192.168.1.20 (optionally with :port).";
        if (targetPort is int tp && tp + (f.Last - f.Port) > 65535) return "The destination ports would go past 65535.";

        if (f.HasTcp && p.SniffPorts.FirstOrDefault(s => s >= f.Port && s <= f.Last) is int sniff and > 0)
            return $"TCP port {sniff} is used for websites. Add it as a website instead.";
        if (p.CatchAll.ListenPort >= f.Port && p.CatchAll.ListenPort <= f.Last)
            return $"Port {p.CatchAll.ListenPort} is used internally by AnyPortProxy.";
        if (NetInfo.IsThisPc(host) && (targetPort is null || targetPort == f.Port))
            return "Sending a port to the same port on this PC would loop. Pick another computer, or a different port on this PC.";

        foreach (var other in p.Forwards)
        {
            if (ReferenceEquals(other, ignore) || ReferenceEquals(other, f)) continue;
            bool sameProto = (f.HasTcp && other.HasTcp) || (f.HasUdp && other.HasUdp);
            if (sameProto && f.Port <= other.Last && other.Port <= f.Last)
                return $"Port {Math.Max(f.Port, other.Port)} is already sent to {other.Target}" + (other.Name.Length > 0 ? $" ({other.Name})." : ".");
        }
        return null;
    }
}
