using System.Net;
using System.Net.Sockets;
using AnyPortProxy.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy.Proxy;

/// <summary>
/// "Send port X to computer Y" rules: real TCP listeners / UDP sockets on this PC that relay to another
/// computer. They don't need the WinDivert driver. Follows settings changes live; busy ports are retried.
/// </summary>
public sealed class PortForwardService : BackgroundService
{
    private sealed class Active
    {
        public required string Signature { get; init; } // target + protocol; a change means restart
        public Socket? Tcp;
        public UdpRelay? Udp;

        public void Dispose()
        {
            try { Tcp?.Dispose(); } catch { }
            Udp?.Dispose();
        }
    }

    private readonly ConfigMonitor _config;
    private readonly StatusTracker _status;
    private readonly ConnectionGate _gate;
    private readonly LogLimiter _logLimit;
    private readonly ILogger<PortForwardService> _log;
    private readonly Dictionary<string, Active> _active = new();
    private readonly Dictionary<string, string> _failed = new();
    private readonly object _lock = new();
    private CancellationToken _ct;

    public PortForwardService(ConfigMonitor config, StatusTracker status, ConnectionGate gate, LogLimiter logLimit, ILogger<PortForwardService> log)
    {
        _config = config;
        _status = status;
        _gate = gate;
        _logLimit = logLimit;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ct = ct;
        Reconcile();
        _config.Changed += OnChanged;
        try
        {
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                lock (_lock)
                {
                    foreach (var a in _active.Values) a.Udp?.Sweep();
                }
                bool retry;
                lock (_lock) retry = _failed.Count > 0;
                if (++tick % 6 == 0 && retry) Reconcile();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _config.Changed -= OnChanged;
            lock (_lock)
            {
                foreach (var a in _active.Values) a.Dispose();
                _active.Clear();
            }
        }
    }

    private void OnChanged(ProxyOptions _) => Reconcile();

    private record struct Wanted(string Key, PortForward Rule, int Port, string Proto, string Host, int TargetPort)
    {
        public string Signature => $"{Proto}>{Host}:{TargetPort}";
    }

    private void Reconcile()
    {
        if (_ct.IsCancellationRequested) return;
        lock (_lock)
        {
            try
            {
                var wanted = new Dictionary<string, Wanted>();
                foreach (var f in _config.Current.Forwards)
                {
                    for (int p = f.Port; p <= f.Last; p++)
                    {
                        var (host, tp) = f.TargetFor(p);
                        if (NetInfo.IsThisPc(host)) host = "127.0.0.1";
                        if (f.HasTcp) wanted.TryAdd($"TCP:{p}", new Wanted($"TCP:{p}", f, p, "TCP", host, tp));
                        if (f.HasUdp) wanted.TryAdd($"UDP:{p}", new Wanted($"UDP:{p}", f, p, "UDP", host, tp));
                    }
                }

                foreach (var key in _active.Keys.ToList())
                {
                    if (wanted.TryGetValue(key, out var w) && w.Signature == _active[key].Signature) continue;
                    _active[key].Dispose();
                    _active.Remove(key);
                    _status.RemoveForward(key);
                }
                foreach (var key in _failed.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
                {
                    _failed.Remove(key);
                    _status.RemoveForward(key);
                }

                foreach (var w in wanted.Values)
                {
                    if (_active.ContainsKey(w.Key)) continue;
                    var target = TargetParser.Format(w.Host, w.TargetPort);
                    try
                    {
                        var a = new Active { Signature = w.Signature };
                        var label = w.Rule.Name.Length > 0 ? w.Rule.Name : $"port rule {w.Rule.Range}";
                        if (w.Proto == "TCP")
                        {
                            a.Tcp = Net.Listen(w.Port);
                            for (int i = 0; i < 2; i++) _ = AcceptLoopAsync(a.Tcp, w.Port, w.Host, w.TargetPort, label);
                        }
                        else
                        {
                            var host = w.Host;
                            var tp = w.TargetPort;
                            a.Udp = new UdpRelay(UdpRelay.Bind(w.Port, dualStack: true), _ => new UdpTarget(host, tp, label), _gate, _logLimit, _log);
                        }
                        _active[w.Key] = a;
                        _failed.Remove(w.Key);
                        _log.LogInformation("Port rule: {Proto} {Port} -> {Target} ({Label})", w.Proto, w.Port, target, label);
                        _status.SetForward(w.Key, new ForwardStatus { Port = w.Port, Protocol = w.Proto, Target = target, Listening = true });
                    }
                    catch (SocketException ex)
                    {
                        var msg = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                            ? "another program on this PC is already using this port"
                            : ex.Message;
                        if (!_failed.TryGetValue(w.Key, out var prev) || prev != msg)
                            _log.LogError("Port rule {Proto} {Port} -> {Target} can't start: {Error}. Will keep retrying.", w.Proto, w.Port, target, msg);
                        _failed[w.Key] = msg;
                        _status.SetForward(w.Key, new ForwardStatus { Port = w.Port, Protocol = w.Proto, Target = target, Listening = false, Error = msg });
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Updating port rules failed");
            }
        }
    }

    private async Task AcceptLoopAsync(Socket listener, int port, string host, int targetPort, string label)
    {
        while (!_ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(_ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted or SocketError.NotSocket)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = HandleAsync(client, port, host, targetPort, label);
        }
    }

    private async Task HandleAsync(Socket client, int port, string host, int targetPort, string label)
    {
        var ct = _ct;
        var remoteEp = client.RemoteEndPoint as IPEndPoint;
        var remoteIp = remoteEp is null ? IPAddress.None : ConnectionGate.Normalize(remoteEp.Address);
        var remote = Net.Format(remoteEp);
        if (!_gate.TryEnter(remoteIp, out var reason))
        {
            if (_logLimit.Allow()) _log.LogWarning("[{Port}] {Client} refused: {Reason}", port, remote, reason);
            Net.Abort(client);
            return;
        }

        Socket? upstream = null;
        _status.Opened();
        try
        {
            Net.Configure(client);
            if (_logLimit.Allow()) _log.LogInformation("[{Port}] {Client} -> {Target}:{TargetPort} ({Label})", port, remote, host, targetPort, label);
            upstream = await Net.ConnectAsync(host, targetPort, _config.Current.ConnectTimeoutMs, ct);
            var (up, down) = await Net.PipeAsync(client, upstream, ct);
            _log.LogDebug("[{Port}] {Client} closed ({Up} B up, {Down} B down)", port, remote, up, down);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _status.Failed();
            if (_logLimit.Allow()) _log.LogWarning("[{Port}] {Client}: can't reach {Target}:{TargetPort} ({Error})", port, remote, host, targetPort, ex.Message);
        }
        finally
        {
            _status.Closed();
            _gate.Exit(remoteIp);
            client.Dispose();
            upstream?.Dispose();
        }
    }
}
