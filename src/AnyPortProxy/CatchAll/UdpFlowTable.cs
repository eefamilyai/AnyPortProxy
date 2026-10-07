using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace AnyPortProxy.CatchAll;

public sealed class UdpFlow
{
    public required ushort OriginalPort { get; init; }
    public string? TargetOverride { get; init; }
    public long LastSeen = Environment.TickCount64;

    public void Touch()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref LastSeen) > 1000) Volatile.Write(ref LastSeen, now);
    }
}

/// <summary>
/// Which port each redirected UDP client (IPv4 address + source port) was talking to. UDP has no
/// connection, so entries live while datagrams keep flowing and expire after a quiet period.
/// </summary>
public sealed class UdpFlowTable
{
    private const int MaxFlows = 200_000;
    private const long IdleExpiryMs = 130_000;   // a little longer than relay sessions, so replies always find their flow
    private const long CollisionWindowMs = 30_000;

    private readonly ConcurrentDictionary<ulong, UdpFlow> _flows = new(Environment.ProcessorCount * 2, 4096);
    private int _count;

    public int Count => Volatile.Read(ref _count);

    private static ulong Key(uint ip, ushort port) => ((ulong)ip << 16) | port;

    public UdpFlow? Get(uint ip, ushort port)
    {
        if (!_flows.TryGetValue(Key(ip, port), out var f)) return null;
        f.Touch();
        return f;
    }

    public UdpFlow? Get(IPEndPoint client)
    {
        var addr = client.Address.IsIPv4MappedToIPv6 ? client.Address.MapToIPv4() : client.Address;
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        Span<byte> b = stackalloc byte[4];
        addr.TryWriteBytes(b, out _);
        return Get(BinaryPrimitives.ReadUInt32BigEndian(b), (ushort)client.Port);
    }

    /// <summary>Records a new client. False when the table is full or the client is busy talking to another of our ports.</summary>
    public bool TryCreate(uint ip, ushort port, ushort originalPort, string? targetOverride)
    {
        var key = Key(ip, port);
        var flow = new UdpFlow { OriginalPort = originalPort, TargetOverride = targetOverride };
        if (_flows.TryGetValue(key, out var existing))
        {
            if (existing.OriginalPort == originalPort) return true;
            // The same client socket is actively using another port of ours: don't steal its replies.
            if (Environment.TickCount64 - Volatile.Read(ref existing.LastSeen) < CollisionWindowMs) return false;
            return _flows.TryUpdate(key, flow, existing);
        }
        if (Volatile.Read(ref _count) >= MaxFlows) return false;
        if (_flows.TryAdd(key, flow))
        {
            Interlocked.Increment(ref _count);
            return true;
        }
        return _flows.TryGetValue(key, out existing) && existing.OriginalPort == originalPort;
    }

    public int Sweep()
    {
        long now = Environment.TickCount64;
        int removed = 0;
        foreach (var kv in _flows)
        {
            if (now - Volatile.Read(ref kv.Value.LastSeen) > IdleExpiryMs && _flows.TryRemove(kv))
            {
                Interlocked.Decrement(ref _count);
                removed++;
            }
        }
        return removed;
    }
}
