using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy.CatchAll;

/// <summary>What the packet workers should do with new connections. Immutable; swapped when settings change.</summary>
internal sealed record RedirectPolicy(bool TargetIsLocal, bool Smart, FrozenSet<int> DirectPorts)
{
    public static readonly RedirectPolicy Default = new(false, false, FrozenSet<int>.Empty);
}

/// <summary>
/// Transparent redirect (like iptables REDIRECT): inbound SYNs to an allowed port are rewritten to
/// ListenPort and remembered; replies from ListenPort are rewritten back to the original port.
/// Packets are received and re-injected in batches by several worker threads.
/// </summary>
internal sealed unsafe class PacketRedirector : IDisposable
{
    private const int ErrorInvalidHandle = 6;
    private const int ErrorNoData = 232;
    private const int ErrorOperationAborted = 995;
    private const int BatchMax = 128;
    private const int BufferSize = 4 * 1024 * 1024; // room for a full batch even with 64 KB coalesced packets
    private const int MaxConsecutiveErrors = 200;

    private readonly IntPtr _handle;
    private readonly ushort _listenPort;
    private readonly FlowTable _flows;
    private readonly ListenerTable _listeners;
    private readonly ILogger _log;
    private readonly List<Thread> _threads = new();
    private volatile RedirectPolicy _policy;
    private int _disposed;
    private int _running;
    private long _lastErrorLog;

    /// <summary>Raised once if the driver handle stops working while we weren't shutting down.</summary>
    public event Action<string>? Faulted;

    public PacketRedirector(string filter, ushort listenPort, FlowTable flows, ListenerTable listeners, RedirectPolicy policy, ILogger log)
    {
        _listenPort = listenPort;
        _flows = flows;
        _listeners = listeners;
        _policy = policy;
        _log = log;
        _handle = WinDivert.Open(filter);
    }

    public RedirectPolicy Policy
    {
        get => _policy;
        set => _policy = value;
    }

    public static int AutoWorkers => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    public void Start(int workers)
    {
        int n = workers > 0 ? workers : AutoWorkers;
        _running = n;
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = $"WinDivert-{i}", Priority = ThreadPriority.AboveNormal };
            _threads.Add(t);
            t.Start();
        }
    }

    private void Worker()
    {
        byte* buf = (byte*)NativeMemory.AlignedAlloc(BufferSize, 64);
        var addrs = (WinDivertAddress*)NativeMemory.AlignedAlloc((nuint)(BatchMax * sizeof(WinDivertAddress)), 64);
        string? fault = null;
        int errors = 0;
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                uint recvLen = 0;
                uint addrLen = (uint)(BatchMax * sizeof(WinDivertAddress));
                if (!WinDivert.WinDivertRecvEx(_handle, buf, BufferSize, &recvLen, 0, addrs, &addrLen, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err is ErrorNoData or ErrorOperationAborted or ErrorInvalidHandle)
                    {
                        if (Volatile.Read(ref _disposed) == 0) fault = $"the packet driver stopped (error {err})";
                        break;
                    }
                    if (++errors >= MaxConsecutiveErrors)
                    {
                        fault = $"the packet driver keeps failing (error {err})";
                        break;
                    }
                    LogRateLimited($"Packet receive failed (error {err})");
                    Thread.Sleep(Math.Min(errors, 50));
                    continue;
                }
                errors = 0;

                int count = (int)(addrLen / (uint)sizeof(WinDivertAddress));
                ProcessBatch(buf, recvLen, addrs, count);

                // Re-inject the whole batch at once (modified or not) — nothing is ever silently dropped.
                if (!WinDivert.WinDivertSendEx(_handle, buf, recvLen, null, 0, addrs, addrLen, IntPtr.Zero))
                    LogRateLimited($"Packet re-inject failed (error {Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            fault = $"packet worker crashed: {ex.Message}";
            _log.LogError(ex, "Packet worker crashed");
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
            NativeMemory.AlignedFree(addrs);
            if (Interlocked.Decrement(ref _running) == 0 && fault is not null && Volatile.Read(ref _disposed) == 0)
                Faulted?.Invoke(fault);
            else if (fault is not null && Volatile.Read(ref _disposed) == 0)
                _log.LogWarning("A packet worker stopped: {Reason}", fault);
        }
    }

    private void ProcessBatch(byte* buf, uint recvLen, WinDivertAddress* addrs, int count)
    {
        Interlocked.Add(ref AnyPortProxy.Metrics.Packets, count);
        var policy = _policy;
        byte* cur = buf;
        uint remaining = recvLen;
        for (int i = 0; i < count && remaining > 0; i++)
        {
            byte* next = null;
            uint nextLen = 0;
            WinDivert.WinDivertHelperParsePacket(cur, remaining, null, null, null, null, null, null, null, null, null, &next, &nextLen);
            uint len = next != null ? (uint)(next - cur) : remaining;
            if (len == 0 || len > remaining) break;

            try
            {
                var pkt = new Span<byte>(cur, (int)len);
                var addr = addrs + i;
                switch (Rewrite(pkt, addr->Outbound, addr->TcpChecksumValid, policy))
                {
                    case RewriteResult.ChecksumUpdated:
                        break;
                    case RewriteResult.NeedsChecksum:
                        WinDivert.WinDivertHelperCalcChecksums(cur, len, addr, 0);
                        break;
                }
            }
            catch (Exception ex)
            {
                // One bad packet must never stop the others: it's re-injected unchanged.
                LogRateLimited($"Packet rewrite failed: {ex.Message}");
            }

            if (next == null) break;
            cur = next;
            remaining = nextLen;
        }
    }

    internal enum RewriteResult { Unchanged, ChecksumUpdated, NeedsChecksum }

    private RewriteResult Rewrite(Span<byte> p, bool outbound, bool checksumValid, RedirectPolicy policy)
    {
        if (p.Length < 20 || (p[0] >> 4) != 4 || p[9] != 6) return RewriteResult.Unchanged;
        if ((BinaryPrimitives.ReadUInt16BigEndian(p[6..]) & 0x1FFF) != 0) return RewriteResult.Unchanged; // non-first fragment
        int ihl = (p[0] & 0x0F) * 4;
        if (ihl < 20 || p.Length < ihl + 20) return RewriteResult.Unchanged;

        uint src = BinaryPrimitives.ReadUInt32BigEndian(p[12..]);
        uint dst = BinaryPrimitives.ReadUInt32BigEndian(p[16..]);
        var tcp = p[ihl..];
        ushort srcPort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
        ushort dstPort = BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]);
        byte flags = tcp[13];

        if (!outbound)
        {
            bool syn = (flags & 0x02) != 0, ack = (flags & 0x10) != 0;
            if (syn && !ack)
            {
                if (!DecideNewConnection(policy, dstPort, dst, out var targetOverride)) return RewriteResult.Unchanged;
                var r = _flows.OnSyn(src, srcPort, dstPort, targetOverride);
                if (r is SynResult.Collision or SynResult.Full)
                {
                    if (r == SynResult.Full) LogRateLimited("Connection table is full (flood?) — new connections pass through unproxied");
                    return RewriteResult.Unchanged;
                }
            }
            else
            {
                // Only touch packets belonging to a connection we redirected; anything else
                // (e.g. a connection that predates the service) passes through untouched.
                var f = _flows.Get(src, srcPort);
                if (f is null || f.OriginalPort != dstPort) return RewriteResult.Unchanged;
            }
            return SetPort(tcp, 2, dstPort, _listenPort, checksumValid);
        }

        if (srcPort != _listenPort) return RewriteResult.Unchanged;
        var flow = _flows.Get(dst, dstPort);
        if (flow is null) return RewriteResult.Unchanged;
        return SetPort(tcp, 0, srcPort, flow.OriginalPort, checksumValid);
    }

    /// <summary>
    /// Smart routing for a new inbound connection. Returns false to leave it alone (Windows handles it directly).
    /// </summary>
    private bool DecideNewConnection(RedirectPolicy policy, ushort port, uint dstIp, out string? targetOverride)
    {
        targetOverride = null;
        if (!policy.TargetIsLocal || !policy.Smart) return true;

        var kind = _listeners.Lookup(port, dstIp, out bool boundToDst);
        if (kind == ListenKind.None)
        {
            // Nothing runs on this port: a scanner or a typo. Let Windows refuse it — costs us nothing.
            Interlocked.Increment(ref AnyPortProxy.Metrics.IgnoredProbes);
            return false;
        }

        bool reachableDirectly = (kind & ListenKind.V4Any) != 0 || boundToDst;
        if (reachableDirectly && policy.DirectPorts.Contains(port))
        {
            // The app accepts outside connections and has its own firewall rule (opened with the port helper):
            // hand the connection straight to it — zero proxy overhead and the app sees the real visitor IP.
            Interlocked.Increment(ref AnyPortProxy.Metrics.DirectConnections);
            return false;
        }

        if ((kind & (ListenKind.V4Any | ListenKind.V4Loopback)) != 0) return true;              // 127.0.0.1 works
        if ((kind & (ListenKind.V6Any | ListenKind.V6Loopback)) != 0) targetOverride = "::1";   // e.g. Vite/Node on "localhost"
        else if (boundToDst) targetOverride = new System.Net.IPAddress(BinaryPrimitives.ReverseEndianness(dstIp)).ToString();
        return true;
    }

    /// <summary>Writes a port and fixes the TCP checksum incrementally (RFC 1624) when it was valid.</summary>
    internal static RewriteResult SetPort(Span<byte> tcp, int offset, ushort oldPort, ushort newPort, bool checksumValid)
    {
        BinaryPrimitives.WriteUInt16BigEndian(tcp[offset..], newPort);
        if (!checksumValid || tcp.Length < 18) return RewriteResult.NeedsChecksum;
        ushort hc = BinaryPrimitives.ReadUInt16BigEndian(tcp[16..]);
        uint sum = (uint)(ushort)~hc + (ushort)~oldPort + newPort;
        sum = (sum & 0xFFFF) + (sum >> 16);
        sum = (sum & 0xFFFF) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], (ushort)~sum);
        return RewriteResult.ChecksumUpdated;
    }

    private void LogRateLimited(string message)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastErrorLog);
        if (now - last > 10_000 && Interlocked.CompareExchange(ref _lastErrorLog, now, last) == last)
            _log.LogWarning("{Message}", message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        WinDivert.Shutdown(_handle);
        foreach (var t in _threads) t.Join(3000);
        WinDivert.Close(_handle);
    }
}
