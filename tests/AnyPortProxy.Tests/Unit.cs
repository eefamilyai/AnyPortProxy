using System.Buffers.Binary;
using System.Net;
using AnyPortProxy;
using AnyPortProxy.CatchAll;
using AnyPortProxy.Core;
using AnyPortProxy.Proxy;
using Microsoft.Extensions.Logging.Abstractions;

static class Unit
{
    static int _fail, _pass;

    static void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else { _fail++; Console.WriteLine("FAIL: " + what); }
    }

    static ushort FullTcpChecksum(byte[] pkt, int ihl)
    {
        int tcpLen = pkt.Length - ihl;
        uint sum = 0;
        for (int i = 12; i < 20; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(i)); // src+dst addresses
        sum += 6;
        sum += (uint)tcpLen;
        for (int i = 0; i < tcpLen; i += 2)
        {
            if (i == 16) continue; // the checksum field itself
            sum += i + 1 < tcpLen ? BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(ihl + i)) : (uint)(pkt[ihl + i] << 8);
        }
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    public static int Run()
    {
        // --- RFC 1624 incremental checksum must equal a full recompute
        var rnd = new Random(42);
        for (int t = 0; t < 20000 && _fail < 5; t++)
        {
            var pkt = new byte[40 + rnd.Next(0, 1400)];
            rnd.NextBytes(pkt);
            pkt[0] = 0x45;
            pkt[9] = 6;
            BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), (ushort)pkt.Length);
            BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(36), FullTcpChecksum(pkt, 20));
            int off = rnd.Next(2) * 2;
            ushort oldPort = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(20 + off));
            ushort newPort = (ushort)rnd.Next(1, 65536);
            var r = PacketRedirector.SetPort(pkt.AsSpan(20), off, oldPort, newPort, true);
            Check(r == PacketRedirector.RewriteResult.ChecksumUpdated, "incremental path taken");
            ushort got = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(36));
            ushort want = FullTcpChecksum(pkt, 20);
            bool equivalent = got == want || ((got == 0 || got == 0xFFFF) && (want == 0 || want == 0xFFFF));
            Check(equivalent, $"checksum mismatch t={t} got={got:x4} want={want:x4}");
        }
        Check(PacketRedirector.SetPort(new byte[20], 2, 1, 2, false) == PacketRedirector.RewriteResult.NeedsChecksum, "offloaded checksum falls back");
        Console.WriteLine($"checksum: {_pass} checks");

        // --- Router: compiled table follows the documented priority rules
        var opts = new ProxyOptions
        {
            DefaultTarget = "10.0.0.1",
            SniffPorts = [80, 443],
            Routes =
            [
                new RouteRule { Host = "nas.ex.com", Target = "10.0.0.2" },
                new RouteRule { Host = "nas.ex.com", Port = 80, Target = "10.0.0.3:5000" },
                new RouteRule { Host = "*.ex.com", Target = "10.0.0.4" },
                new RouteRule { Host = "*.deep.ex.com", Target = "10.0.0.5" },
                new RouteRule { Host = "*", Port = 443, Target = "10.0.0.6" },
                new RouteRule { Host = "only80.ex.com", Port = 80, Target = "10.0.0.7" },
            ],
        };
        var router = new Router(new FakeMonitor(opts).Monitor, NullLogger<Router>.Instance);
        void R(string? host, int port, string expect)
        {
            var d = router.Resolve(host, port);
            Check($"{d.Host}:{d.Port}" == expect, $"route {host}:{port} -> {d.Host}:{d.Port} (want {expect})");
        }
        R("nas.ex.com", 443, "10.0.0.2:443");
        R("NAS.EX.COM.", 80, "10.0.0.3:5000");
        R("x.ex.com", 80, "10.0.0.4:80");
        R("a.deep.ex.com", 443, "10.0.0.5:443");
        R("other.org", 443, "10.0.0.6:443");
        R("other.org", 80, "10.0.0.1:80");
        R(null, 443, "10.0.0.6:443");
        R("only80.ex.com", 443, "10.0.0.4:443"); // port-specific exact rule is for 80 only, so the wildcard wins on 443
        R("ex.com", 80, "10.0.0.1:80");           // wildcards never match the bare domain
        Console.WriteLine("router: done");

        // --- FlowTable: collision protection, counting, replacing closed flows
        var flows = new FlowTable();
        Check(flows.OnSyn(1, 1000, 3000, null) == SynResult.Created, "create");
        Check(flows.OnSyn(1, 1000, 3000, null) == SynResult.Retransmit, "retransmit");
        var ep = new IPEndPoint(new IPAddress(BinaryPrimitives.ReverseEndianness(1u)), 1000);
        Check(flows.TryAcquire(ep, out var f) && f.OriginalPort == 3000, "acquire");
        Check(flows.OnSyn(1, 1000, 4000, null) == SynResult.Collision, "collision while active");
        flows.Release(f);
        Check(flows.OnSyn(1, 1000, 4000, null) == SynResult.Created, "replace after close");
        Check(flows.Count == 1, $"count {flows.Count}");
        for (uint i = 0; i < 1000; i++) flows.OnSyn(100 + i, 5, 80, null);
        Check(flows.Count == 1001, $"count after 1000 SYNs = {flows.Count}");
        Check(flows.Sweep() == 0, "nothing expires immediately");
        Console.WriteLine("flows: done");

        // --- Sanitizer: garbage in, safe settings out
        var bad = new ProxyOptions
        {
            DefaultTarget = "",
            SniffPorts = [0, 80, 80, 70000],
            Routes =
            [
                new RouteRule { Host = "http://bad/", Target = "1.2.3.4" },
                new RouteRule { Host = "ok.com", Target = "" },
                null!,
                new RouteRule { Host = "fine.com", Target = "1.2.3.4" },
            ],
            SniffTimeoutMs = -5,
            CatchAll = new CatchAllOptions { ListenPort = 80, BlockedPorts = [99999, 22, 22], Workers = 1000, Target = "" },
            Limits = new LimitOptions { MaxConnections = 1, MaxConnectionsPerIp = 999999 },
        };
        var notes = ConfigSanitizer.Sanitize(bad);
        Check(bad.SniffPorts.SequenceEqual([80]), "sniff ports cleaned");
        Check(bad.Routes.Count == 1 && bad.Routes[0].Host == "fine.com", "bad routes dropped");
        Check(bad.DefaultTarget == "127.0.0.1", "default target fixed");
        Check(bad.SniffTimeoutMs == 500 && bad.CatchAll.ListenPort == 34010 && bad.CatchAll.Workers == 64, "numbers clamped");
        Check(bad.CatchAll.BlockedPorts.SequenceEqual([22]) && bad.CatchAll.Target is null, "lists fixed");
        Check(bad.Limits.MaxConnections == 100 && bad.Limits.MaxConnectionsPerIp == 100, "limits clamped");
        Console.WriteLine($"sanitizer: {notes.Count} notes, e.g. \"{notes.FirstOrDefault()}\"");

        // --- Sniffer: junk is rejected immediately instead of waiting for the timeout
        Check(HostnameSniffer.Sniff([0x16, 0x99, 0x01, 0x00, 0x10], out _, out _) == SniffStatus.NotFound, "tls junk version");
        Check(HostnameSniffer.Sniff([0x16, 0x03, 0x01, 0xFF, 0xFF], out _, out _) == SniffStatus.NotFound, "tls huge record");
        Check(HostnameSniffer.Sniff([0x16, 0x03, 0x01, 0x02, 0x00], out _, out _) == SniffStatus.NeedMore, "tls partial record");

        // --- Listener table: sees real listeners (RPC on 135 always listens on 0.0.0.0)
        using var lt = new ListenerTable();
        var k = lt.Lookup(135, 0, out _);
        Check((k & ListenKind.V4Any) != 0, $"listener 135 kind={k}");
        Check(lt.Lookup(1, 0, out _) == ListenKind.None, "port 1 has no listener");
        Console.WriteLine($"listener table: port 135 = {k}");

        Console.WriteLine(_fail == 0 ? $"UNIT TESTS PASSED ({_pass} checks)" : $"UNIT TESTS FAILED: {_fail}");
        return _fail;
    }
}

/// <summary>A real ConfigMonitor over a settings file containing the given options.</summary>
sealed class FakeMonitor
{
    public ConfigMonitor Monitor { get; }

    public FakeMonitor(ProxyOptions o)
    {
        var cfg = ConfigStore.CreateDefault();
        cfg.Proxy = o;
        ConfigStore.Save(cfg);
        Monitor = new ConfigMonitor(NullLogger<ConfigMonitor>.Instance);
    }
}
