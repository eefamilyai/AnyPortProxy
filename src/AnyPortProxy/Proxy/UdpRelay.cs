using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy.Proxy;

/// <summary>Where a UDP client's datagrams should go.</summary>
public readonly record struct UdpTarget(string Host, int Port, string Label);

/// <summary>
/// UDP relay (a small NAT): each client address gets its own session with a dedicated upstream socket,
/// so replies find their way back. Sessions end after <see cref="IdleTimeout"/> of silence.
/// </summary>
public sealed class UdpRelay : IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);
    private const int SessionBufferSize = 16 * 1024; // replies larger than this (rare for UDP) are dropped
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private static int _activeSessions;

    /// <summary>Active sessions across all relays (for the status page).</summary>
    public static int ActiveSessions => Volatile.Read(ref _activeSessions);

    private readonly Socket _listen;
    private readonly int _port;
    private readonly Func<IPEndPoint, UdpTarget?> _resolve;
    private readonly ConnectionGate _gate;
    private readonly LogLimiter _logLimit;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPEndPoint, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, (IPAddress? Addr, long At)> _dns = new();
    private int _disposed;

    private sealed class Session(IPEndPoint client, IPAddress clientIp, Socket upstream, UdpTarget target)
    {
        public readonly IPEndPoint Client = client;
        public readonly IPAddress ClientIp = clientIp;
        public readonly Socket Upstream = upstream;
        public readonly UdpTarget Target = target;
        public long LastSeen = Environment.TickCount64;
        public int Closed;

        public void Touch()
        {
            long now = Environment.TickCount64;
            if (now - Volatile.Read(ref LastSeen) > 500) Volatile.Write(ref LastSeen, now);
        }
    }

    public UdpRelay(Socket listen, Func<IPEndPoint, UdpTarget?> resolve, ConnectionGate gate, LogLimiter logLimit, ILogger log)
    {
        _listen = listen;
        _port = ((IPEndPoint)listen.LocalEndPoint!).Port;
        _resolve = resolve;
        _gate = gate;
        _logLimit = logLimit;
        _log = log;
        // One receive loop keeps each client's datagrams in order (games and VPNs prefer that).
        _ = ReceiveLoopAsync();
    }

    public int Count => _sessions.Count;

    /// <summary>A UDP socket bound to <paramref name="port"/> that nobody else can steal.</summary>
    public static Socket Bind(int port, bool dualStack)
    {
        Socket s;
        if (dualStack && Socket.OSSupportsIPv6)
        {
            s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true, ExclusiveAddressUse = true };
            try
            {
                Prepare(s);
                s.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return s;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
                s.Dispose(); // IPv6 disabled: fall back to IPv4
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }
        s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try
        {
            Prepare(s);
            s.Bind(new IPEndPoint(IPAddress.Any, port));
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private static void Prepare(Socket s, int bufferBytes = 32 * 1024 * 1024)
    {
        // Windows reports "connection reset" on a UDP socket after an ICMP port-unreachable; that would
        // break the receive loop for every client. Turn it off.
        try { s.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); } catch (SocketException) { }
        try
        {
            // Big buffers absorb bursts (UDP drops whatever doesn't fit). These are limits, not allocations.
            s.ReceiveBufferSize = bufferBytes;
            s.SendBufferSize = bufferBytes;
        }
        catch (SocketException)
        {
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var ct = _cts.Token;
        var buf = GC.AllocateUninitializedArray<byte>(65536, pinned: true);
        EndPoint any = _listen.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _listen.ReceiveFromAsync(buf, SocketFlags.None, any, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset or SocketError.NetworkReset)
            {
                continue;
            }
            catch (SocketException) when (Volatile.Read(ref _disposed) == 0)
            {
                await Task.Delay(10, CancellationToken.None);
                continue;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                _log.LogError(ex, "UDP {Port}: receive error", _port);
                await Task.Delay(100, CancellationToken.None);
                continue;
            }

            try
            {
                var client = (IPEndPoint)r.RemoteEndPoint;
                var session = GetOrCreate(client);
                if (session is null) continue;
                session.Touch();
                await session.Upstream.SendAsync(buf.AsMemory(0, r.ReceivedBytes), SocketFlags.None, ct);
                Interlocked.Add(ref AnyPortProxy.Metrics.Bytes, r.ReceivedBytes);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Destination unreachable right now / session just closed: drop this datagram (that's UDP).
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_logLimit.Allow()) _log.LogWarning("UDP {Port}: {Error}", _port, ex.Message);
            }
        }
    }

    private Session? GetOrCreate(IPEndPoint client)
    {
        if (_sessions.TryGetValue(client, out var existing) && Volatile.Read(ref existing.Closed) == 0) return existing;

        var target = _resolve(client);
        if (target is not { } t) return null; // not one of ours (e.g. someone hitting the internal port directly)

        var clientIp = ConnectionGate.Normalize(client.Address);
        var addr = ResolveHost(t.Host);
        if (addr is null)
        {
            if (_logLimit.Allow()) _log.LogWarning("UDP {Port}: can't find {Host}", _port, t.Host);
            return null;
        }
        if (!_gate.TryEnter(clientIp, out var reason))
        {
            if (_logLimit.Allow()) _log.LogWarning("[UDP {Port}] {Client} refused: {Reason}", _port, Net.Format(client), reason);
            return null;
        }

        Socket upstream;
        try
        {
            upstream = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            Prepare(upstream, 2 * 1024 * 1024);
            upstream.Connect(new IPEndPoint(addr, t.Port));
        }
        catch (Exception ex)
        {
            _gate.Exit(clientIp);
            if (_logLimit.Allow()) _log.LogWarning("UDP {Port}: can't reach {Host}:{TargetPort}: {Error}", _port, t.Host, t.Port, ex.Message);
            return null;
        }

        var session = new Session(client, clientIp, upstream, t);
        if (!_sessions.TryAdd(client, session))
        {
            upstream.Dispose();
            _gate.Exit(clientIp);
            return _sessions.TryGetValue(client, out existing) ? existing : null;
        }
        Interlocked.Increment(ref _activeSessions);
        if (_logLimit.Allow())
            _log.LogInformation("[UDP {Port}] {Client} -> {Host}:{TargetPort} ({Label})", _port, Net.Format(client), t.Host, t.Port, t.Label);
        _ = UpstreamLoopAsync(session);
        return session;
    }

    private IPAddress? ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        long now = Environment.TickCount64;
        if (_dns.TryGetValue(host, out var cached) && now - cached.At < 60_000) return cached.Addr;
        IPAddress? addr = null;
        try { addr = Dns.GetHostAddresses(host).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault(); } catch { }
        _dns[host] = (addr, now);
        return addr;
    }

    private async Task UpstreamLoopAsync(Session s)
    {
        var ct = _cts.Token;
        var buf = ArrayPool<byte>.Shared.Rent(SessionBufferSize);
        try
        {
            while (Volatile.Read(ref s.Closed) == 0 && !ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await s.Upstream.ReceiveAsync(buf, SocketFlags.None, ct);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset or SocketError.NetworkReset)
                {
                    continue; // oversized reply / transient ICMP error: skip it
                }
                s.Touch();
                await _listen.SendToAsync(buf.AsMemory(0, n), SocketFlags.None, s.Client, ct);
                Interlocked.Add(ref AnyPortProxy.Metrics.Bytes, n);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        catch (Exception ex)
        {
            if (_logLimit.Allow()) _log.LogWarning("UDP {Port}: {Error}", _port, ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            Close(s);
        }
    }

    private void Close(Session s)
    {
        if (Interlocked.Exchange(ref s.Closed, 1) == 1) return;
        _sessions.TryRemove(new KeyValuePair<IPEndPoint, Session>(s.Client, s));
        try { s.Upstream.Dispose(); } catch { }
        _gate.Exit(s.ClientIp);
        Interlocked.Decrement(ref _activeSessions);
    }

    /// <summary>Ends sessions that have been quiet for <see cref="IdleTimeout"/>.</summary>
    public void Sweep()
    {
        long now = Environment.TickCount64;
        long idle = (long)IdleTimeout.TotalMilliseconds;
        foreach (var s in _sessions.Values)
            if (now - Volatile.Read(ref s.LastSeen) > idle) Close(s);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        try { _listen.Dispose(); } catch { }
        foreach (var s in _sessions.Values) Close(s);
    }
}
