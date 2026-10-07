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

    static ushort FullUdpChecksum(byte[] pkt)
    {
        int udpLen = pkt.Length - 20;
        uint sum = 0;
        for (int i = 12; i < 20; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(i));
        sum += 17;
        sum += (uint)udpLen;
        for (int i = 0; i < udpLen; i += 2)
        {
            if (i == 6) continue;
            sum += i + 1 < udpLen ? BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(20 + i)) : (uint)(pkt[20 + i] << 8);
        }
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        ushort r = (ushort)~sum;
        return r == 0 ? (ushort)0xFFFF : r;
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

        // --- UDP incremental checksum == full recompute (with pseudo-header), and "no checksum" stays 0
        int udpBefore = _pass;
        for (int t = 0; t < 20000 && _fail < 5; t++)
        {
            var pkt = new byte[28 + rnd.Next(0, 1400)];
            rnd.NextBytes(pkt);
            pkt[0] = 0x45;
            pkt[9] = 17;
            BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(24), (ushort)(pkt.Length - 20));
            BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(26), FullUdpChecksum(pkt));
            int off = rnd.Next(2) * 2;
            ushort oldPort = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(20 + off));
            PacketRedirector.SetUdpPort(pkt.AsSpan(20), off, oldPort, (ushort)rnd.Next(1, 65536), true);
            ushort got = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(26)), want = FullUdpChecksum(pkt);
            Check(got == want, $"udp checksum mismatch t={t} got={got:x4} want={want:x4}");
        }
        var noSum = new byte[8];
        Check(PacketRedirector.SetUdpPort(noSum, 2, 0, 1234, true) == PacketRedirector.RewriteResult.ChecksumUpdated
              && BinaryPrimitives.ReadUInt16BigEndian(noSum.AsSpan(6)) == 0, "udp no-checksum stays 0");
        Console.WriteLine($"udp checksum: {_pass - udpBefore} checks");

        // --- Filter: exclusions merge into runs (WinDivert filters have a size limit), UDP half present
        var runs = FilterBuilder.Merge([5, 3, 4, 10, 1000, 1001, 1002]);
        Check(runs.SequenceEqual([(3, 5), (10, 10), (1000, 1002)]), "merge runs");
        var filter = FilterBuilder.Build(new CatchAllOptions { BlockedPorts = [22, 3389] }, [80, 443], Enumerable.Range(2000, 1000), udp: true);
        Check(filter.Contains("inbound and udp") && filter.Contains("udp.SrcPort == 34010") && filter.Contains("(udp.DstPort < 2000 or udp.DstPort > 2999)")
              && filter.Contains("udp.DstPort != 123"), "filter has udp half with merged exclusions");
        Check(!FilterBuilder.Build(new CatchAllOptions(), [80], [], udp: false).Contains("udp"), "no udp when off");
        Check(filter.Count(ch => ch == '(') == filter.Count(ch => ch == ')'), "filter parentheses balanced");

        // --- UDP flows: create, reuse, collision protection
        var uf = new UdpFlowTable();
        Check(uf.TryCreate(7, 5000, 27015, null) && uf.TryCreate(7, 5000, 27015, null), "udp create/reuse");
        Check(!uf.TryCreate(7, 5000, 9999, null), "udp collision while active");
        Check(uf.Get(7, 5000)?.OriginalPort == 27015 && uf.Count == 1, "udp get");

        // --- Port rules: validation catches the classic mistakes
        var po = new ProxyOptions { SniffPorts = [80, 443] };
        PortForward Fw(int port, string target, PortProtocol proto = PortProtocol.Udp, int? end = null) => new() { Port = port, EndPort = end, Target = target, Protocol = proto };
        Check(PortForwards.Validate(po, Fw(51820, "192.168.58.20")) is null, "valid rule");
        Check(PortForwards.Validate(po, Fw(443, "192.168.58.20", PortProtocol.Tcp)) is not null, "website port blocked for tcp");
        Check(PortForwards.Validate(po, Fw(443, "192.168.58.20", PortProtocol.Udp)) is null, "udp 443 allowed");
        Check(PortForwards.Validate(po, Fw(5000, "127.0.0.1")) is not null, "loop to self rejected");
        Check(PortForwards.Validate(po, Fw(5000, "nas-ip-typo!")) is not null, "bad characters rejected");
        Check(PortForwards.Validate(po, Fw(5000, "my-nas.local")) is null, "host names allowed");
        Check(Websites.ValidateComputer("192.168.1.20!") is not null && Websites.ValidateComputer("nas") is null, "website computer validated");
        Check(PortForwards.Validate(po, Fw(5000, "127.0.0.1:6000")) is null, "remap on this PC allowed");
        Check(PortForwards.Validate(po, Fw(1, "x", PortProtocol.Udp, 5000)) is not null, "too many ports");
        Check(PortForwards.Validate(po, Fw(60000, "1.2.3.4:65000", PortProtocol.Udp, 61000)) is not null, "target range overflow");
        po.Forwards.Add(Fw(2456, "192.168.58.20", PortProtocol.Udp, 2458));
        Check(PortForwards.Validate(po, Fw(2457, "192.168.58.30")) is not null, "overlap rejected");
        Check(PortForwards.Validate(po, Fw(2457, "192.168.58.30", PortProtocol.Tcp)) is null, "same port other protocol ok");
        Check(Fw(2456, "10.0.0.1:3000", PortProtocol.Udp, 2458).TargetFor(2458) == ("10.0.0.1", 3002), "range target mapping");
        Console.WriteLine("udp/forward logic: done");

        // --- Game addresses: shared port, fallback, isolation from websites, cleanup
        var gp = new ProxyOptions { SniffPorts = [80, 443], DefaultTarget = "127.0.0.1" };
        Websites.Upsert(gp, new Website { Host = "nas.ex.com", Computer = "10.0.0.2" });
        Check(GameAddresses.Validate(gp, "mc1.ex.com", 25565, "10.0.0.5") is null, "game address valid");
        Check(GameAddresses.Validate(gp, "mc1.ex.com", 25565, "127.0.0.1") is not null, "this PC on the shared port rejected");
        Check(GameAddresses.Validate(gp, "mc1.ex.com", 443, "10.0.0.5") is not null, "web port rejected");
        GameAddresses.Add(gp, "mc1.ex.com", 25565, "10.0.0.5");
        GameAddresses.Add(gp, "mc2.ex.com", 25565, "127.0.0.1:25566");
        Check(gp.SniffPorts.Contains(25565), "shared port listened on");
        var gl = GameAddresses.List(gp);
        Check(gl.Count == 3 && gl.Count(g => g.IsFallback) == 1 && gl.First(g => g.IsFallback).Target == "10.0.0.5", "first server becomes the fallback");
        Check(Websites.List(gp).Count == 1, "game addresses don't show up as websites");
        Websites.Upsert(gp, new Website { Host = "mc1.ex.com", Computer = "10.0.0.9" }); // a website with the same name
        Check(GameAddresses.List(gp).Any(g => g.Host == "mc1.ex.com"), "adding a website keeps the game address");
        Websites.Remove(gp, "mc1.ex.com");
        Check(GameAddresses.List(gp).Any(g => g.Host == "mc1.ex.com"), "removing a website keeps the game address");
        var gr = new Router(new FakeMonitor(gp).Monitor, NullLogger<Router>.Instance);
        Check(gr.Resolve("mc1.ex.com", 25565) is { Host: "10.0.0.5", Port: 25565 }, "mc1 routed");
        Check(gr.Resolve("mc2.ex.com", 25565) is { Host: "127.0.0.1", Port: 25566 }, "mc2 routed to this PC:25566");
        Check(gr.Resolve("45.1.2.3", 25565) is { Host: "10.0.0.5" } && gr.Resolve(null, 25565) is { Host: "10.0.0.5" }, "IP / no name → fallback");
        Check(gr.Resolve("nas.ex.com", 25565) is { Host: "10.0.0.5" }, "website rule never captures a game port");
        Check(gr.Resolve("nas.ex.com", 443) is { Host: "10.0.0.2", Port: 443 }, "website still works");
        GameAddresses.Remove(gp, "mc1.ex.com", 25565);
        Check(gp.SniffPorts.Contains(25565), "port kept while servers remain");
        GameAddresses.Remove(gp, "mc2.ex.com", 25565);
        Check(!gp.SniffPorts.Contains(25565) && GameAddresses.List(gp).Count == 0, "last one removed → port back to normal");
        Check(GameAddresses.ConnectAddress("mc.ex.com", 25565) == "mc.ex.com" && GameAddresses.ConnectAddress("x.ex.com", 8080) == "x.ex.com:8080", "connect address");
        Console.WriteLine("game addresses: done");

        // --- Updater: release parsing, version compare, safety checks
        Check(Updater.ParseVersion("v1.5.0") == new Version(1, 5, 0) && Updater.ParseVersion("1.5") == new Version(1, 5, 0)
              && Updater.ParseVersion("v2.0.1-beta") == new Version(2, 0, 1) && Updater.ParseVersion("latest") is null, "version parsing");
        Check(Updater.IsValidRepo("jdoe/AnyPortProxy") && !Updater.IsValidRepo("jdoe") && !Updater.IsValidRepo("a/b/c") && !Updater.IsValidRepo("../x"), "repo names");
        const string release = """
            { "tag_name": "v9.9.0", "html_url": "https://github.com/jdoe/AnyPortProxy/releases/tag/v9.9.0", "body": "Notes",
              "assets": [ { "name": "readme.txt", "browser_download_url": "https://github.com/x", "size": 1 },
                          { "name": "AnyPortProxySetup-9.9.0.exe", "browser_download_url": "https://github.com/jdoe/AnyPortProxy/releases/download/v9.9.0/AnyPortProxySetup-9.9.0.exe",
                            "size": 123, "digest": "sha256:ABCDEF" } ] }
            """;
        var ui = Updater.Parse(release);
        Check(ui.Version == new Version(9, 9, 0) && ui.AssetName == "AnyPortProxySetup-9.9.0.exe" && ui.Size == 123 && ui.Sha256 == "abcdef", "release parsed");
        bool threw = false;
        try { Updater.Parse(release.Replace("https://github.com/jdoe/AnyPortProxy/releases/download", "https://evil.example/download")); }
        catch (UpdateException) { threw = true; }
        Check(threw, "non-github download link refused");
        threw = false;
        try { Updater.Parse("""{ "tag_name": "v1.0.0", "assets": [] }"""); } catch (UpdateException) { threw = true; }
        Check(threw, "release without installer refused");
        Console.WriteLine("updater: done");

        // --- Listener table: sees real listeners (RPC on 135 always listens on 0.0.0.0)
        using var lt = new ListenerTable();
        var k = lt.Lookup(135, 0, out _);
        Check((k & ListenKind.V4Any) != 0, $"listener 135 kind={k}");
        Check(lt.Lookup(1, 0, out _) == ListenKind.None, "port 1 has no listener");
        Console.WriteLine($"listener table: port 135 = {k}");
        using (var u = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            int up = ((IPEndPoint)u.Client.LocalEndPoint!).Port;
            lt.Refresh();
            lt.Lookup(udp: true, up, 0, out _, out bool own);
            Check(own, $"own UDP socket {up} recognised (never redirected)");
        }

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
