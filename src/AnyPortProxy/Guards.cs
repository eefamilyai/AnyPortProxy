using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using AnyPortProxy.Core;

namespace AnyPortProxy;

/// <summary>Lock-free counters shared by all proxy components (read by the status writer).</summary>
internal static class Metrics
{
    public static long Bytes;
    public static long Packets;
    public static long IgnoredProbes;
    public static long DirectConnections;
    public static long Rejected;
}

/// <summary>
/// Token bucket for per-connection log lines. Under a flood, individual lines stop and a
/// periodic summary ("1,234 more connections") is written instead, so the log can't explode.
/// </summary>
public sealed class LogLimiter
{
    private const double RatePerSecond = 40;
    private const double Burst = 200;
    private readonly object _lock = new();
    private double _tokens = Burst;
    private long _last = Stopwatch.GetTimestamp();
    private long _suppressed;

    public bool Allow()
    {
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(Burst, _tokens + (now - _last) * RatePerSecond / Stopwatch.Frequency);
            _last = now;
            if (_tokens >= 1)
            {
                _tokens -= 1;
                return true;
            }
        }
        Interlocked.Increment(ref _suppressed);
        return false;
    }

    public long TakeSuppressed() => Interlocked.Exchange(ref _suppressed, 0);
}

/// <summary>Caps total connections and connections per internet address (home-network addresses are exempt).</summary>
public sealed class ConnectionGate
{
    private readonly ConfigMonitor _config;
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private int _total;

    public ConnectionGate(ConfigMonitor config) => _config = config;

    public int Total => Volatile.Read(ref _total);

    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    public bool TryEnter(IPAddress ip, out string? reason)
    {
        var limits = _config.Current.Limits;
        reason = null;
        if (Interlocked.Increment(ref _total) > limits.MaxConnections)
        {
            Interlocked.Decrement(ref _total);
            reason = $"connection limit reached ({limits.MaxConnections} open)";
            Interlocked.Increment(ref Metrics.Rejected);
            return false;
        }
        if (!NetInfo.IsPrivate(ip))
        {
            int n = _perIp.AddOrUpdate(ip, 1, (_, v) => v + 1);
            if (n > limits.MaxConnectionsPerIp)
            {
                Exit(ip);
                reason = $"too many connections from one address ({limits.MaxConnectionsPerIp} max)";
                Interlocked.Increment(ref Metrics.Rejected);
                return false;
            }
        }
        return true;
    }

    public void Exit(IPAddress ip)
    {
        Interlocked.Decrement(ref _total);
        if (NetInfo.IsPrivate(ip)) return;
        while (_perIp.TryGetValue(ip, out var v))
        {
            if (v <= 1 ? _perIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, v)) : _perIp.TryUpdate(ip, v - 1, v)) return;
        }
    }
}
