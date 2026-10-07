using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// End-to-end: several "Minecraft servers" share one port on a running proxy, chosen by the address in the handshake.
/// usage: games <sharedPort> <backendPort1> <backendPort2>   (config: mc1 → backend1, mc2 → backend2, first = fallback)
/// </summary>
static class Games
{
    public static async Task<int> Run(string[] args)
    {
        int shared = int.Parse(args[1]), b1 = int.Parse(args[2]), b2 = int.Parse(args[3]);
        using var cts = new CancellationTokenSource();
        // Each fake server answers with its own name, so we can see where the proxy sent us.
        foreach (var (port, name) in new[] { (b1, "server-one"), (b2, "server-two") })
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var c = await l.AcceptTcpClientAsync(cts.Token);
                        _ = Task.Run(async () =>
                        {
                            using (c)
                            {
                                var buf = new byte[512];
                                await c.GetStream().ReadAsync(buf);
                                await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes(name));
                            }
                        });
                    }
                    catch { return; }
                }
            });
        }

        int failures = 0;
        foreach (var (address, expect) in new[]
                 {
                     ("mc1.reggilion.com", "server-one"),
                     ("MC2.Reggilion.com.", "server-two"),        // case and trailing dot don't matter
                     ("mc2.reggilion.com\0FML\0", "server-two"),  // Forge client suffix
                     ("45.119.154.92", "server-one"),             // typed the IP → fallback
                     ("unknown.reggilion.com", "server-one"),     // unknown name → fallback
                 })
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, shared);
            await c.GetStream().WriteAsync(Handshake(address, (ushort)shared));
            var buf = new byte[64];
            int n = await c.GetStream().ReadAsync(buf);
            var got = Encoding.ASCII.GetString(buf, 0, n);
            bool ok = got == expect;
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {address.Replace("\0", "\\0"),-28} → {got} (expected {expect})");
        }
        cts.Cancel();
        return failures;
    }

    /// <summary>A real Minecraft Java handshake packet (protocol 767, next state = login).</summary>
    private static byte[] Handshake(string address, ushort port)
    {
        static void VarInt(List<byte> o, int v)
        {
            do
            {
                byte b = (byte)(v & 0x7F);
                v >>= 7;
                if (v != 0) b |= 0x80;
                o.Add(b);
            } while (v != 0);
        }
        var body = new List<byte>();
        VarInt(body, 0x00);
        VarInt(body, 767);
        var a = Encoding.UTF8.GetBytes(address);
        VarInt(body, a.Length);
        body.AddRange(a);
        body.Add((byte)(port >> 8));
        body.Add((byte)port);
        VarInt(body, 2);
        var packet = new List<byte>();
        VarInt(packet, body.Count);
        packet.AddRange(body);
        return packet.ToArray();
    }
}
