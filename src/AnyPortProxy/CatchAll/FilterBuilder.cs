using System.Text;
using AnyPortProxy.Core;

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

    /// <param name="tcpOwnPorts">TCP ports AnyPortProxy listens on itself (websites, port rules).</param>
    /// <param name="udpOwnPorts">UDP ports AnyPortProxy listens on itself (port rules).</param>
    /// <param name="udp">Include the UDP half.</param>
    public static string Build(CatchAllOptions c, IEnumerable<int> tcpOwnPorts, IEnumerable<int> udpOwnPorts, bool udp)
    {
        var ranges = PortRanges.Parse(c.AllowedPorts);
        var tcpExcluded = c.BlockedPorts.Concat(tcpOwnPorts).Append(c.ListenPort);
        var udpExcluded = c.BlockedPorts.Concat(udpOwnPorts).Concat(CatchAllOptions.SystemUdpPorts).Append(c.ListenPort);

        var lan = new StringBuilder();
        if (!c.InterceptLan)
            foreach (var (lo, hi) in LanRanges) lan.Append($" and !(ip.SrcAddr >= {lo} and ip.SrcAddr <= {hi})");

        var sb = new StringBuilder("ip and !loopback and (");
        sb.Append($"(inbound and tcp and ({Ranges("tcp.DstPort", ranges)}){Exclusions("tcp.DstPort", tcpExcluded)}{lan})");
        sb.Append($" or (outbound and tcp and tcp.SrcPort == {c.ListenPort})");
        if (udp)
        {
            sb.Append($" or (inbound and udp and ({Ranges("udp.DstPort", ranges)}){Exclusions("udp.DstPort", udpExcluded)}{lan})");
            sb.Append($" or (outbound and udp and udp.SrcPort == {c.ListenPort})");
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string Ranges(string field, IEnumerable<(int Lo, int Hi)> ranges) =>
        string.Join(" or ", ranges.Select(r => r.Lo == r.Hi ? $"{field} == {r.Lo}" : $"({field} >= {r.Lo} and {field} <= {r.Hi})"));

    /// <summary>
    /// Excluded ports, merged into runs so big port rules don't overflow WinDivert's filter size limit
    /// (a run of 1,000 ports costs two comparisons, not 1,000).
    /// </summary>
    private static string Exclusions(string field, IEnumerable<int> ports)
    {
        var sb = new StringBuilder();
        foreach (var (lo, hi) in Merge(ports))
            sb.Append(lo == hi ? $" and {field} != {lo}" : $" and ({field} < {lo} or {field} > {hi})");
        return sb.ToString();
    }

    internal static List<(int Lo, int Hi)> Merge(IEnumerable<int> ports)
    {
        var runs = new List<(int Lo, int Hi)>();
        foreach (var p in ports.Where(p => p is >= 1 and <= 65535).Distinct().Order())
        {
            if (runs.Count > 0 && runs[^1].Hi == p - 1) runs[^1] = (runs[^1].Lo, p);
            else runs.Add((p, p));
        }
        return runs;
    }
}
