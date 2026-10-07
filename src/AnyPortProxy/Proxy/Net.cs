using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AnyPortProxy.Proxy;

internal static class Net
{
    private const int BufferSize = 64 * 1024;

    /// <summary>Largest backlog Windows allows (SOMAXCONN), so bursts of new connections aren't refused.</summary>
    public const int MaxBacklog = int.MaxValue;

    public static void Configure(Socket s)
    {
        try
        {
            s.NoDelay = true;
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 6);
        }
        catch (SocketException)
        {
            // Socket already reset by the peer; the caller will find out on first use.
        }
    }

    /// <summary>Closes with a TCP reset (no lingering TIME_WAIT); used for refused / abusive clients.</summary>
    public static void Abort(Socket s)
    {
        try { s.LingerState = new LingerOption(true, 0); } catch { }
        s.Dispose();
    }

    /// <summary>A listening socket that no other program can steal (exclusive), dual-stack when IPv6 is available.</summary>
    public static Socket Listen(int port, bool ipv4Only = false)
    {
        Socket s;
        if (!ipv4Only && Socket.OSSupportsIPv6)
        {
            try
            {
                s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true, ExclusiveAddressUse = true };
                try
                {
                    s.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                    s.Listen(MaxBacklog);
                    return s;
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
                // IPv6 disabled on this PC: fall back to IPv4.
            }
        }
        s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try
        {
            s.Bind(new IPEndPoint(IPAddress.Any, port));
            s.Listen(MaxBacklog);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    public static async Task<Socket> ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        bool loopback = IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        for (int attempt = 0; ; attempt++)
        {
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp); // dual-mode
            try
            {
                Configure(s);
                if (loopback) NoSynRetransmits(s);
                await s.ConnectAsync(host, port, cts.Token);
                return s;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused && loopback && attempt < 2 && !cts.IsCancellationRequested)
            {
                // Windows refuses (instead of queueing) when a busy server's accept queue is full: back off briefly and retry.
                s.Dispose();
                try { await Task.Delay(50 * (attempt + 1), cts.Token); } catch (OperationCanceledException) { throw; }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                s.Dispose();
                throw new TimeoutException($"connect to {host}:{port} timed out");
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Windows normally retries a refused connect for ~2 s. On loopback no packet can be lost, so turn that off
    /// (SIO_TCP_INITIAL_RTO with TCP_INITIAL_RTO_NO_SYN_RETRANSMISSIONS): "nothing running" is detected instantly.
    /// </summary>
    private static void NoSynRetransmits(Socket s)
    {
        const int SioTcpInitialRto = unchecked((int)0x98000011);
        try
        {
            // TCP_INITIAL_RTO_PARAMETERS { USHORT Rtt = unspecified; UCHAR MaxSynRetransmissions = none }
            s.IOControl(SioTcpInitialRto, [0xFF, 0xFF, 0xFE, 0x00], null);
        }
        catch (SocketException)
        {
            // Older Windows: fall back to normal behaviour.
        }
    }

    /// <summary>Copies both directions until both sides close. Returns (client→upstream, upstream→client) bytes.</summary>
    public static async Task<(long Up, long Down)> PipeAsync(Socket client, Socket upstream, CancellationToken ct)
    {
        var up = PumpAsync(client, upstream, ct);
        var down = PumpAsync(upstream, client, ct);
        await Task.WhenAll(up, down);
        return (up.Result, down.Result);
    }

    /// <summary>
    /// Memory-efficient copy: an idle connection holds no buffer at all (a zero-byte read waits for data
    /// first); a busy stream keeps its buffer while reads keep filling it.
    /// </summary>
    private static async Task<long> PumpAsync(Socket from, Socket to, CancellationToken ct)
    {
        byte[]? buf = null;
        long total = 0;
        try
        {
            while (true)
            {
                if (buf is null)
                {
                    await from.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None, ct); // wait for data without holding memory
                    buf = ArrayPool<byte>.Shared.Rent(BufferSize);
                }
                int n = await from.ReceiveAsync(buf, SocketFlags.None, ct);
                if (n == 0) break;
                await to.SendAsync(buf.AsMemory(0, n), SocketFlags.None, ct);
                total += n;
                Interlocked.Add(ref AnyPortProxy.Metrics.Bytes, n);
                if (n < buf.Length && from.Available == 0)
                {
                    ArrayPool<byte>.Shared.Return(buf); // stream went quiet: give the memory back
                    buf = null;
                }
            }
            try { to.Shutdown(SocketShutdown.Send); } catch { /* peer already gone */ }
        }
        catch
        {
            // Any error tears down both sides so the opposite pump unblocks.
            from.Dispose();
            to.Dispose();
        }
        finally
        {
            if (buf is not null) ArrayPool<byte>.Shared.Return(buf);
        }
        return total;
    }

    public static string Format(EndPoint? ep) => ep switch
    {
        IPEndPoint ip when ip.Address.IsIPv4MappedToIPv6 => $"{ip.Address.MapToIPv4()}:{ip.Port}",
        null => "?",
        _ => ep.ToString() ?? "?",
    };

    private sealed record LocalAddresses(HashSet<IPAddress> Set, long At);

    private static volatile LocalAddresses _local = new(new HashSet<IPAddress>(), long.MinValue);

    /// <summary>True if host refers to this machine (used to stop a route from looping back into ourselves).</summary>
    public static bool IsThisMachine(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;

        var snap = _local;
        if (Environment.TickCount64 - snap.At > 30_000)
        {
            try
            {
                snap = new LocalAddresses(NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(u => u.Address)
                    .ToHashSet(), Environment.TickCount64);
                _local = snap;
            }
            catch (NetworkInformationException)
            {
            }
        }
        return snap.Set.Contains(ip);
    }
}
