using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AnyPortProxy.CatchAll;

[Flags]
internal enum ListenKind : byte
{
    None = 0,
    V4Any = 1,        // 0.0.0.0
    V4Loopback = 2,   // 127.x
    V4Specific = 4,   // a particular IPv4 address
    V6Any = 8,        // [::]
    V6Loopback = 16,  // [::1]
    V6Specific = 32,
}

/// <summary>
/// Snapshot of which TCP / UDP ports have something listening on this PC (from the OS tables),
/// refreshed every second and on demand. Lookups are lock-free.
/// </summary>
internal sealed unsafe class ListenerTable : IDisposable
{
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;
    private const int AfInet = 2, AfInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;

    private sealed class Table
    {
        public readonly Dictionary<int, ListenKind> Kinds = new();
        public readonly HashSet<ulong> V4Bound = new(); // (address << 16) | port for specific IPv4 listeners
        public readonly HashSet<int> Own = new();       // ports held by this process (never redirect those)
    }

    private sealed class Snapshot
    {
        public readonly Table Tcp = new();
        public readonly Table Udp = new();
        public long TakenAt;
    }

    private volatile Snapshot _snap = new() { TakenAt = long.MinValue };
    private readonly Timer _timer;
    private readonly int _ownPid = Environment.ProcessId;
    private long _lastOnDemand;
    private int _refreshing;

    public ListenerTable()
    {
        Refresh();
        _timer = new Timer(_ => { try { Refresh(); } catch { } }, null, 1000, 1000);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

    /// <summary>What listens on <paramref name="port"/>, and whether one of them is bound to <paramref name="dstIp"/>.</summary>
    public ListenKind Lookup(int port, uint dstIp, out bool boundToDst) => Lookup(false, port, dstIp, out boundToDst, out _);

    public ListenKind Lookup(bool udp, int port, uint dstIp, out bool boundToDst, out bool own)
    {
        var s = _snap;
        var t = udp ? s.Udp : s.Tcp;
        var kind = t.Kinds.GetValueOrDefault(port);
        if (kind == ListenKind.None && !t.Own.Contains(port))
        {
            // Maybe the app started a moment ago: refresh now (rate-limited so scanners can't make us spin).
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastOnDemand);
            if (now - s.TakenAt > 250 && now - last > 250 && Interlocked.CompareExchange(ref _lastOnDemand, now, last) == last)
            {
                Refresh();
                s = _snap;
                t = udp ? s.Udp : s.Tcp;
                kind = t.Kinds.GetValueOrDefault(port);
            }
        }
        own = t.Own.Contains(port);
        boundToDst = (kind & ListenKind.V4Specific) != 0 && t.V4Bound.Contains(((ulong)dstIp << 16) | (uint)port);
        return kind;
    }

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var next = new Snapshot();
            Read(false, AfInet, next.Tcp);
            Read(false, AfInet6, next.Tcp);
            Read(true, AfInet, next.Udp);
            Read(true, AfInet6, next.Udp);
            next.TakenAt = Environment.TickCount64;
            _snap = next;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private void Read(bool udp, int af, Table into)
    {
        int size = 0;
        int cls = udp ? UdpTableOwnerPid : TcpTableOwnerPidListener;
        if (udp) GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, cls, 0);
        else GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, cls, 0);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            size += 16 * 1024;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                uint r = udp ? GetExtendedUdpTable(buf, ref size, false, af, cls, 0) : GetExtendedTcpTable(buf, ref size, false, af, cls, 0);
                if (r == ErrorInsufficientBuffer) continue;
                if (r != 0) return;
                Parse((byte*)buf, size, udp, af, into);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    private void Parse(byte* buf, int size, bool udp, int af, Table into)
    {
        // Row layouts (all fields 4-byte aligned, ports in network byte order in the low two bytes):
        //   MIB_TCPROW_OWNER_PID   state, localAddr, localPort, remoteAddr, remotePort, pid      (24)
        //   MIB_TCP6ROW_OWNER_PID  localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid (56)
        //   MIB_UDPROW_OWNER_PID   localAddr, localPort, pid                                    (12)
        //   MIB_UDP6ROW_OWNER_PID  localAddr[16], scope, localPort, pid                         (28)
        int rowSize, addrOff, portOff, pidOff;
        if (af == AfInet)
            (rowSize, addrOff, portOff, pidOff) = udp ? (12, 0, 4, 8) : (24, 4, 8, 20);
        else
            (rowSize, addrOff, portOff, pidOff) = udp ? (28, 0, 20, 24) : (56, 0, 20, 52);

        int count = *(int*)buf;
        if (count < 0 || 4 + (long)count * rowSize > size) return;
        byte* row = buf + 4;
        for (int i = 0; i < count; i++, row += rowSize)
        {
            int port = (row[portOff] << 8) | row[portOff + 1];
            if (*(int*)(row + pidOff) == _ownPid)
            {
                into.Own.Add(port);
                continue;
            }
            ListenKind k;
            if (af == AfInet)
            {
                uint addr = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(row + addrOff, 4));
                k = addr == 0 ? ListenKind.V4Any : (addr >> 24) == 127 ? ListenKind.V4Loopback : ListenKind.V4Specific;
                if (k == ListenKind.V4Specific) into.V4Bound.Add(((ulong)addr << 16) | (uint)port);
            }
            else
            {
                var addr = new ReadOnlySpan<byte>(row + addrOff, 16);
                bool allZero = addr.IndexOfAnyExcept((byte)0) < 0;
                bool loop = !allZero && addr[..15].IndexOfAnyExcept((byte)0) < 0 && addr[15] == 1;
                k = allZero ? ListenKind.V6Any : loop ? ListenKind.V6Loopback : ListenKind.V6Specific;
            }
            into.Kinds[port] = into.Kinds.GetValueOrDefault(port) | k;
        }
    }

    public void Dispose() => _timer.Dispose();
}
