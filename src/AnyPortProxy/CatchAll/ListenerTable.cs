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
/// Snapshot of which TCP ports have something listening on this PC (from the OS listener table),
/// refreshed every second and on demand. Lookups are lock-free.
/// </summary>
internal sealed unsafe class ListenerTable : IDisposable
{
    private const int TcpTableOwnerPidListener = 3;
    private const int AfInet = 2, AfInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;

    private sealed class Snapshot
    {
        public readonly Dictionary<int, ListenKind> Kinds = new();
        public readonly HashSet<ulong> V4Bound = new(); // (address << 16) | port for specific IPv4 listeners
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

    /// <summary>What listens on <paramref name="port"/>, and whether one of them is bound to <paramref name="dstIp"/>.</summary>
    public ListenKind Lookup(int port, uint dstIp, out bool boundToDst)
    {
        var s = _snap;
        var kind = s.Kinds.GetValueOrDefault(port);
        if (kind == ListenKind.None)
        {
            // Maybe the app started a moment ago: refresh now (rate-limited so scanners can't make us spin).
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastOnDemand);
            if (now - s.TakenAt > 250 && now - last > 250 && Interlocked.CompareExchange(ref _lastOnDemand, now, last) == last)
            {
                Refresh();
                s = _snap;
                kind = s.Kinds.GetValueOrDefault(port);
            }
        }
        boundToDst = (kind & ListenKind.V4Specific) != 0 && s.V4Bound.Contains(((ulong)dstIp << 16) | (uint)port);
        return kind;
    }

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var next = new Snapshot();
            Read(AfInet, next);
            Read(AfInet6, next);
            next.TakenAt = Environment.TickCount64;
            _snap = next;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private void Read(int af, Snapshot into)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidListener, 0);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            size += 16 * 1024;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                uint r = GetExtendedTcpTable(buf, ref size, false, af, TcpTableOwnerPidListener, 0);
                if (r == ErrorInsufficientBuffer) continue;
                if (r != 0) return;
                Parse((byte*)buf, size, af, into);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    private void Parse(byte* buf, int size, int af, Snapshot into)
    {
        int count = *(int*)buf;
        int rowSize = af == AfInet ? 24 : 56;
        if (4 + (long)count * rowSize > size) return;
        byte* row = buf + 4;
        for (int i = 0; i < count; i++, row += rowSize)
        {
            if (af == AfInet)
            {
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                if (*(int*)(row + 20) == _ownPid) continue;
                uint addr = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(row + 4, 4));
                int port = (row[8] << 8) | row[9];
                ListenKind k = addr == 0 ? ListenKind.V4Any : (addr >> 24) == 127 ? ListenKind.V4Loopback : ListenKind.V4Specific;
                if (k == ListenKind.V4Specific) into.V4Bound.Add(((ulong)addr << 16) | (uint)port);
                into.Kinds[port] = into.Kinds.GetValueOrDefault(port) | k;
            }
            else
            {
                // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid
                if (*(int*)(row + 52) == _ownPid) continue;
                var addr = new ReadOnlySpan<byte>(row, 16);
                int port = (row[20] << 8) | row[21];
                bool allZero = addr.IndexOfAnyExcept((byte)0) < 0;
                bool loop = !allZero && addr[..15].IndexOfAnyExcept((byte)0) < 0 && addr[15] == 1;
                ListenKind k = allZero ? ListenKind.V6Any : loop ? ListenKind.V6Loopback : ListenKind.V6Specific;
                into.Kinds[port] = into.Kinds.GetValueOrDefault(port) | k;
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
