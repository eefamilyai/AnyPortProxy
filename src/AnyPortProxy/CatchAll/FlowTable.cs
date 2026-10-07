using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace AnyPortProxy.CatchAll;

public sealed class Flow
{
    public required ushort OriginalPort { get; init; }

    /// <summary>Where the proxy should connect instead of the configured target (e.g. "::1" for IPv6-only apps).</summary>
    public string? TargetOverride { get; init; }

    public long LastSeen = Environment.TickCount64;
    public int Active;
    public volatile bool Accepted;

    /// <summary>Cheap touch: only writes when the timestamp is stale, so busy flows don't bounce cache lines between cores.</summary>
    public void Touch()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref LastSeen) > 1000) Volatile.Write(ref LastSeen, now);
    }
}

public enum SynResult { Created, Retransmit, Collision, Full }

/// <summary>
/// Remembers which port each redirected client (IPv4 address + source port) originally connected to,
/// so the listener knows where to forward and replies can be rewritten back to the original port.
/// </summary>
public sealed class FlowTable
{
    private const int MaxFlows = 250_000;
    private const long HalfOpenExpiryMs = 15_000;  // handshake never completed (SYN floods, scanners)
    private const long ClosedExpiryMs = 120_000;   // past TIME_WAIT

    private readonly ConcurrentDictionary<ulong, Flow> _flows = new(Environment.ProcessorCount * 2, 4096);
    private int _count; // ConcurrentDictionary.Count takes every lock; keep our own

    public int Count => Volatile.Read(ref _count);

    private static ulong Key(uint ip, ushort port) => ((ulong)ip << 16) | port;

    public SynResult OnSyn(uint clientIp, ushort clientPort, ushort originalPort, string? targetOverride)
    {
        var key = Key(clientIp, clientPort);
        if (_flows.TryGetValue(key, out var existing))
        {
            if (existing.OriginalPort == originalPort)
            {
                existing.Touch();
                return SynResult.Retransmit;
            }
            // Same client address+port but a different destination while the old connection is still open:
            // redirecting would hijack the live connection, so leave this SYN alone.
            if (Volatile.Read(ref existing.Active) > 0) return SynResult.Collision;
            var replacement = new Flow { OriginalPort = originalPort, TargetOverride = targetOverride };
            return _flows.TryUpdate(key, replacement, existing) ? SynResult.Created : SynResult.Collision;
        }

        if (Volatile.Read(ref _count) >= MaxFlows) return SynResult.Full;
        if (_flows.TryAdd(key, new Flow { OriginalPort = originalPort, TargetOverride = targetOverride }))
        {
            Interlocked.Increment(ref _count);
            return SynResult.Created;
        }
        return SynResult.Retransmit; // another worker added it at the same moment
    }

    public Flow? Get(uint clientIp, ushort clientPort)
    {
        if (!_flows.TryGetValue(Key(clientIp, clientPort), out var f)) return null;
        f.Touch();
        return f;
    }

    public bool TryAcquire(IPEndPoint client, out Flow flow)
    {
        flow = null!;
        var addr = client.Address.IsIPv4MappedToIPv6 ? client.Address.MapToIPv4() : client.Address;
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        Span<byte> bytes = stackalloc byte[4];
        addr.TryWriteBytes(bytes, out _);
        if (!_flows.TryGetValue(Key(BinaryPrimitives.ReadUInt32BigEndian(bytes), (ushort)client.Port), out var f)) return false;
        Interlocked.Increment(ref f.Active);
        f.Accepted = true;
        Volatile.Write(ref f.LastSeen, Environment.TickCount64);
        flow = f;
        return true;
    }

    public void Release(Flow f)
    {
        Interlocked.Decrement(ref f.Active);
        Volatile.Write(ref f.LastSeen, Environment.TickCount64);
    }

    /// <summary>Drops half-open flows quickly and closed flows once they're past TIME_WAIT.</summary>
    public int Sweep()
    {
        long now = Environment.TickCount64;
        int removed = 0;
        foreach (var kv in _flows)
        {
            var f = kv.Value;
            if (Volatile.Read(ref f.Active) > 0) continue;
            long idle = now - Volatile.Read(ref f.LastSeen);
            if (idle > (f.Accepted ? ClosedExpiryMs : HalfOpenExpiryMs) && _flows.TryRemove(kv))
            {
                Interlocked.Decrement(ref _count);
                removed++;
            }
        }
        return removed;
    }
}
