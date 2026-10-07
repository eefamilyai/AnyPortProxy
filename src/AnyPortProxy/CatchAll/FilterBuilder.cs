using AnyPortProxy.Core;
using System.Text;

namespace AnyPortProxy.CatchAll;

internal static class FilterBuilder
{
    // Private, link-local and CGNAT (Tailscale) source ranges skipped unless InterceptLan is on.
    private static readonly (string Lo, string Hi)[] LanRanges =
    {
        ("10.0.0.0", "10.255.255.255"),
        ("172.16.0.0", "172.31.255.255"),
        ("192.168.0.0", "192.168.255.255"),
        ("169.254.0.0", "169.254.255.255"),
        ("100.64.0.0", "100.127.255.255"),
    };

    public static string Build(CatchAllOptions c, IEnumerable<int> sniffPorts)
    {
        var ranges = PortRanges.Parse(c.AllowedPorts);
        var excluded = new SortedSet<int>(c.BlockedPorts.Concat(sniffPorts).Append(c.ListenPort));

        var sb = new StringBuilder("ip and tcp and !loopback and ((inbound and (");
        sb.Append(string.Join(" or ", ranges.Select(r => r.Lo == r.Hi
            ? $"tcp.DstPort == {r.Lo}"
            : $"(tcp.DstPort >= {r.Lo} and tcp.DstPort <= {r.Hi})")));
        sb.Append(')');
        foreach (var p in excluded) sb.Append($" and tcp.DstPort != {p}");
        if (!c.InterceptLan)
        {
            foreach (var (lo, hi) in LanRanges) sb.Append($" and !(ip.SrcAddr >= {lo} and ip.SrcAddr <= {hi})");
        }
        sb.Append($") or (outbound and tcp.SrcPort == {c.ListenPort}))");
        return sb.ToString();
    }

}
