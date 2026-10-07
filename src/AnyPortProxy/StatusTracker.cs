using System.Collections.Concurrent;
using System.Diagnostics;
using AnyPortProxy.CatchAll;
using AnyPortProxy.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy;

/// <summary>Collects live state from the proxy services so the GUI / CLI can show it.</summary>
public sealed class StatusTracker
{
    private readonly ConcurrentDictionary<int, ListenerStatus> _listeners = new();
    private readonly ConcurrentQueue<string> _repairs = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private CatchAllStatus _catchAll = new() { State = "Off", Message = "Starting…" };
    private long _active, _total, _failed;

    // For per-second rates between snapshots.
    private long _prevBytes, _prevConns, _prevPackets, _prevAt = Stopwatch.GetTimestamp();

    public void SetListener(int port, bool listening, string? error) =>
        _listeners[port] = new ListenerStatus { Port = port, Listening = listening, Error = error };

    public void RemoveListener(int port) => _listeners.TryRemove(port, out _);

    private readonly ConcurrentDictionary<string, ForwardStatus> _forwards = new();

    public void SetForward(string key, ForwardStatus status) => _forwards[key] = status;

    public void RemoveForward(string key) => _forwards.TryRemove(key, out _);

    public void SetCatchAll(string state, string? message) =>
        _catchAll = new CatchAllStatus { State = state, Message = message };

    public void Repaired(string what)
    {
        _repairs.Enqueue($"{DateTime.Now:g}: {what}");
        while (_repairs.Count > 10) _repairs.TryDequeue(out _);
    }

    public void Opened()
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _total);
    }

    public void Closed() => Interlocked.Decrement(ref _active);

    public void Failed() => Interlocked.Increment(ref _failed);

    public ServiceStatus Snapshot(FlowTable flows, ConfigMonitor config)
    {
        long now = Stopwatch.GetTimestamp();
        double secs = Math.Max(0.001, (now - _prevAt) / (double)Stopwatch.Frequency);
        long bytes = Interlocked.Read(ref Metrics.Bytes), conns = Interlocked.Read(ref _total), packets = Interlocked.Read(ref Metrics.Packets);
        var status = new ServiceStatus
        {
            UpdatedUtc = DateTime.UtcNow,
            StartedUtc = _started,
            Pid = Environment.ProcessId,
            Version = AppPaths.Version,
            SniffPorts = _listeners.Values.OrderBy(l => l.Port).ToList(),
            CatchAll = new CatchAllStatus { State = _catchAll.State, Message = _catchAll.Message, Flows = flows.Count },
            ActiveConnections = Interlocked.Read(ref _active),
            TotalConnections = conns,
            FailedConnections = Interlocked.Read(ref _failed),
            RejectedConnections = Interlocked.Read(ref Metrics.Rejected),
            TotalBytes = bytes,
            BytesPerSec = (bytes - _prevBytes) / secs,
            ConnectionsPerSec = (conns - _prevConns) / secs,
            PacketsPerSec = (packets - _prevPackets) / secs,
            IgnoredProbes = Interlocked.Read(ref Metrics.IgnoredProbes),
            DirectConnections = Interlocked.Read(ref Metrics.DirectConnections),
            Forwards = _forwards.Values.OrderBy(f => f.Port).ThenBy(f => f.Protocol).ToList(),
            UdpSessions = AnyPortProxy.Proxy.UdpRelay.ActiveSessions,
            ConfigError = config.Error,
            ConfigNotes = config.Notes.ToList(),
            Repairs = _repairs.ToList(),
        };
        _prevBytes = bytes;
        _prevConns = conns;
        _prevPackets = packets;
        _prevAt = now;
        return status;
    }
}

/// <summary>Writes status.json every 2 seconds and summarises connections the log limiter skipped.</summary>
public sealed class StatusWriterService(StatusTracker tracker, FlowTable flows, ConfigMonitor config, LogLimiter logLimit,
    ILogger<StatusWriterService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                tracker.Snapshot(flows, config).Write();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            catch (Exception ex)
            {
                log.LogDebug("Status write failed: {Error}", ex.Message);
            }

            if (++tick % 5 == 0)
            {
                long skipped = logLimit.TakeSuppressed();
                if (skipped > 0) log.LogInformation("Busy: {Count} more connections in the last 10 seconds weren't logged individually", skipped);
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { }
        }
        try { File.Delete(AppPaths.StatusFile); } catch { }
    }
}
