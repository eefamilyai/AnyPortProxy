using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// UDP relay load test against a running proxy with a UDP port rule (relayPort → 127.0.0.1:backendPort).
/// usage: udp <relayPort> <backendPort> <clients> <datagramsPerClient>
/// </summary>
static class Udp
{
    public static async Task<int> Run(string[] args)
    {
        int relayPort = int.Parse(args[1]), backendPort = int.Parse(args[2]), clients = int.Parse(args[3]), per = int.Parse(args[4]);

        // Echo backend
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, backendPort));
        backend.Client.ReceiveBufferSize = 8 << 20;
        backend.Client.SendBufferSize = 8 << 20;
        using var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var r = await backend.ReceiveAsync(cts.Token);
                    await backend.SendAsync(r.Buffer, r.RemoteEndPoint, cts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { }
            }
        });

        long received = 0, outOfOrder = 0, sent = 0;
        var sw = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, clients), new ParallelOptions { MaxDegreeOfParallelism = clients }, async (id, ct) =>
        {
            using var c = new UdpClient();
            c.Client.ReceiveBufferSize = 1 << 20;
            c.Connect(IPAddress.Loopback, relayPort);
            var payload = new byte[1200];
            int lastSeq = -1;
            int window = 32; // keep a few in flight, like a real game/VPN stream
            int nextSend = 0, got = 0;
            while (got < per)
            {
                while (nextSend < per && nextSend - got < window)
                {
                    BinaryPrimitives.WriteInt32BigEndian(payload, id);
                    BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), nextSend++);
                    await c.SendAsync(payload, ct);
                    Interlocked.Increment(ref sent);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(2000);
                UdpReceiveResult r;
                try { r = await c.ReceiveAsync(timeout.Token); }
                catch (OperationCanceledException) { got++; continue; } // lost datagram (UDP): move on
                if (BinaryPrimitives.ReadInt32BigEndian(r.Buffer) != id) continue;
                int seq = BinaryPrimitives.ReadInt32BigEndian(r.Buffer.AsSpan(4));
                if (seq < lastSeq) Interlocked.Increment(ref outOfOrder);
                lastSeq = seq;
                got++;
                Interlocked.Increment(ref received);
            }
        });
        double secs = sw.Elapsed.TotalSeconds;
        cts.Cancel();
        double lossPct = 100.0 * (sent - received) / Math.Max(1, sent);
        Console.WriteLine($"UDP: {clients} clients x {per} datagrams (1200 B): sent {sent}, echoed back {received} ({lossPct:0.00}% lost), " +
                          $"{outOfOrder} out of order, {received / secs:0} round-trips/s, {received * 1200.0 * 2 / secs / 1_000_000:0.0} MB/s through the relay");
        return lossPct < 1 ? 0 : 1;
    }
}
