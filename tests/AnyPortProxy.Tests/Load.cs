using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Load test against a running proxy: a tiny HTTP backend, then many concurrent clients through the proxy.
/// usage: load <proxyPort> <backendPort> <connections> <bulkMB>
/// </summary>
static class Load
{
    public static async Task<int> Run(string[] args)
    {
        int proxyPort = int.Parse(args[1]), backendPort = int.Parse(args[2]), conns = int.Parse(args[3]), bulkMb = int.Parse(args[4]);
        using var cts = new CancellationTokenSource();
        var backend = new TcpListener(IPAddress.Loopback, backendPort);
        backend.Start(int.MaxValue);
        for (int i = 0; i < 8; i++) _ = Task.Run(() => ServeAsync(backend, cts.Token));

        // 1) Many concurrent short requests
        var sw = Stopwatch.StartNew();
        int ok = 0, failed = 0;
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        await Parallel.ForEachAsync(Enumerable.Range(0, conns), new ParallelOptions { MaxDegreeOfParallelism = 500 }, async (i, ct) =>
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, proxyPort, ct);
                var s = c.GetStream();
                await s.WriteAsync(Encoding.ASCII.GetBytes($"GET /{i} HTTP/1.1\r\nHost: load.test\r\nConnection: close\r\n\r\n"), ct);
                var buf = new byte[256];
                int n = await s.ReadAsync(buf, ct);
                if (n > 0 && Encoding.ASCII.GetString(buf, 0, n).Contains($"ok {i}")) Interlocked.Increment(ref ok);
                else { Interlocked.Increment(ref failed); errors.AddOrUpdate("bad reply", 1, (_, v) => v + 1); }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                errors.AddOrUpdate(ex.GetType().Name, 1, (_, v) => v + 1);
            }
        });
        Console.WriteLine($"{conns} requests: {ok} ok, {failed} failed in {sw.Elapsed.TotalSeconds:0.00}s ({conns / sw.Elapsed.TotalSeconds:0} req/s) {string.Join(", ", errors.Select(e => $"{e.Key}={e.Value}"))}");

        // 2) Many idle connections held open at once (memory check)
        var idle = new List<TcpClient>();
        for (int i = 0; i < 2000; i++)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, proxyPort);
            await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /idle HTTP/1.1\r\nHost: load.test\r\n\r\n"));
            idle.Add(c);
        }
        await Task.Delay(3000);
        Console.WriteLine($"holding {idle.Count} idle connections through the proxy");
        Console.Out.Flush();
        if (args.Length > 5) await File.WriteAllTextAsync(args[5], "idle");
        await Task.Delay(4000);
        foreach (var c in idle) c.Dispose();

        // 3) Bulk throughput
        sw.Restart();
        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, proxyPort);
            var s = c.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes($"GET /bulk/{bulkMb} HTTP/1.1\r\nHost: load.test\r\n\r\n"));
            var buf = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = await s.ReadAsync(buf)) > 0) total += n;
            Console.WriteLine($"bulk: {total / 1_000_000.0:0} MB in {sw.Elapsed.TotalSeconds:0.00}s = {total / 1_000_000.0 / sw.Elapsed.TotalSeconds:0} MB/s");
        }
        cts.Cancel();
        return failed == 0 ? 0 : 1;
    }

    private static async Task ServeAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var c = await l.AcceptTcpClientAsync(ct);
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try
                    {
                        var s = c.GetStream();
                        var buf = new byte[4096];
                        int n = await s.ReadAsync(buf, ct);
                        var req = Encoding.ASCII.GetString(buf, 0, n);
                        var path = req.Split(' ')[1];
                        if (path.StartsWith("/bulk/"))
                        {
                            long bytes = long.Parse(path[6..]) * 1_000_000;
                            var chunk = new byte[1 << 16];
                            for (long sent = 0; sent < bytes; sent += chunk.Length) await s.WriteAsync(chunk, ct);
                            return;
                        }
                        if (path == "/idle")
                        {
                            await Task.Delay(Timeout.Infinite, ct);
                            return;
                        }
                        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nok {path[1..]}"), ct);
                    }
                    catch
                    {
                    }
                }
            }, ct);
        }
    }
}
