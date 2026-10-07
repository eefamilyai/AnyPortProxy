using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using AnyPortProxy.Core;
using AnyPortProxy.Proxy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy.CatchAll;

/// <summary>
/// Forwards every allowed non-website port to the catch-all target, preserving the port number.
/// Follows settings changes live (a new driver handle opens before the old one closes), restarts the
/// packet driver by itself if it ever stops, and retries after errors.
/// </summary>
public sealed class CatchAllProxyService : BackgroundService
{
    private static readonly int AcceptLoops = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private readonly ConfigMonitor _config;
    private readonly FlowTable _flows;
    private readonly StatusTracker _status;
    private readonly ConnectionGate _gate;
    private readonly LogLimiter _logLimit;
    private readonly ILogger<CatchAllProxyService> _log;
    private readonly object _lock = new();

    private ListenerTable? _listeners;
    private Socket? _listener;
    private int _listenPort;
    private PacketRedirector? _redirector;
    private string? _filter;
    private string? _lastError;
    private CancellationToken _ct;

    public CatchAllProxyService(ConfigMonitor config, FlowTable flows, StatusTracker status, ConnectionGate gate, LogLimiter logLimit,
        ILogger<CatchAllProxyService> log)
    {
        _config = config;
        _flows = flows;
        _status = status;
        _gate = gate;
        _logLimit = logLimit;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ct = ct;
        Apply();
        _config.Changed += OnConfigChanged;
        try
        {
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                try
                {
                    _flows.Sweep();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Flow cleanup failed");
                }
                // Self-heal: if forwarding should be on but isn't (driver blocked, port busy…), retry every 30 s.
                if (++tick % 6 == 0)
                {
                    bool broken;
                    lock (_lock) broken = _config.Current.CatchAll.Enabled && _redirector is null;
                    if (broken) Apply();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _config.Changed -= OnConfigChanged;
            lock (_lock)
            {
                StopAll();
                _listeners?.Dispose();
                _listeners = null;
            }
        }
    }

    private void OnConfigChanged(ProxyOptions _) => Apply();

    private void Apply()
    {
        if (_ct.IsCancellationRequested) return;
        lock (_lock)
        {
            try
            {
                ApplyCore();
            }
            catch (Exception ex)
            {
                Fail($"unexpected error: {ex.Message}");
                _log.LogError(ex, "Applying forwarding settings failed");
            }
        }
    }

    private void ApplyCore()
    {
        var o = _config.Current;
        var c = o.CatchAll;
        if (!c.Enabled)
        {
            if (_redirector is not null || _listener is not null) _log.LogInformation("All-ports forwarding turned off");
            StopAll();
            _lastError = null;
            _status.SetCatchAll("Off", "Turned off in settings.");
            return;
        }

        string filter;
        try
        {
            filter = FilterBuilder.Build(c, o.SniffPorts);
        }
        catch (FormatException ex)
        {
            Fail($"Invalid forwarded ports setting: {ex.Message}");
            return;
        }

        if (_listener is null || _listenPort != c.ListenPort)
        {
            StopAll();
            // The listener must exist before packets start being redirected to it.
            Socket listener;
            try
            {
                listener = Net.Listen(c.ListenPort, ipv4Only: true);
            }
            catch (SocketException ex)
            {
                Fail($"Can't use internal port {c.ListenPort}: {ex.Message}. Change CatchAll.ListenPort.");
                return;
            }
            _listener = listener;
            _listenPort = c.ListenPort;
            for (int i = 0; i < AcceptLoops; i++) _ = AcceptLoopAsync(listener);
        }

        var policy = BuildPolicy(o);
        if (filter == _filter && _redirector is not null)
        {
            _redirector.Policy = policy; // smart-routing changes apply instantly, no driver restart
            SetRunning(o);
            return;
        }

        PacketRedirector redirector;
        try
        {
            _listeners ??= new ListenerTable();
            redirector = new PacketRedirector(filter, (ushort)c.ListenPort, _flows, _listeners, policy, _log);
            redirector.Faulted += reason => _ = Task.Run(() => OnFaulted(redirector, reason));
            redirector.Start(c.Workers);
        }
        catch (DllNotFoundException)
        {
            Fail("WinDivert.dll wasn't found next to AnyPortProxy.exe. Run build.ps1 and install again.");
            return;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return;
        }

        var old = _redirector;
        _redirector = redirector;
        _filter = filter;
        old?.Dispose();
        _lastError = null;
        SetRunning(o);
        _log.LogInformation("All-ports forwarding active: ports {Ports} (blocked: {Blocked}) -> {Target}:<same port>{Lan}; {Workers} packet workers{Smart}",
            c.AllowedPorts, string.Join(",", c.BlockedPorts), CatchAllHost(o), c.InterceptLan ? "" : ", internet visitors only",
            c.Workers > 0 ? c.Workers : PacketRedirector.AutoWorkers, policy.Smart && policy.TargetIsLocal ? ", smart routing on" : "");
        _log.LogDebug("WinDivert filter: {Filter}", filter);
    }

    private void OnFaulted(PacketRedirector which, string reason)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(which, _redirector)) return; // an old handle we already replaced
            _log.LogError("All-ports forwarding: {Reason}. Restarting the packet driver…", reason);
            _redirector = null;
            _filter = null;
            try { which.Dispose(); } catch { }
            _status.SetCatchAll("Error", $"Restarting after: {reason}");
        }
        Thread.Sleep(2000);
        Apply();
    }

    private RedirectPolicy BuildPolicy(ProxyOptions o)
    {
        TargetParser.TryParse(o.CatchAll.Target ?? o.DefaultTarget, out var host, out _);
        bool local = NetInfo.IsThisPc(host);
        // Ports opened with the helper have a firewall rule, so handing them straight to the app is safe.
        var direct = _config.Ports
            .Where(r => r.Firewall && r.Protocol != PortProtocol.Udp && r.Port is >= 1 and <= 65535 && r.Last <= 65535 && r.Last - r.Port < 2000)
            .SelectMany(r => Enumerable.Range(r.Port, r.Last - r.Port + 1))
            .ToFrozenSet();
        return new RedirectPolicy(local, o.CatchAll.SmartRouting, direct);
    }

    private void SetRunning(ProxyOptions o) =>
        _status.SetCatchAll("Running", $"Ports {o.CatchAll.AllowedPorts} → {CatchAllHost(o)}" +
                                       (o.CatchAll.BlockedPorts.Count > 0 ? $" (blocked: {string.Join(", ", o.CatchAll.BlockedPorts)})" : ""));

    private void Fail(string message)
    {
        StopAll();
        _status.SetCatchAll("Error", message);
        if (message != _lastError) _log.LogError("All-ports forwarding disabled: {Error} (will retry automatically)", message);
        _lastError = message;
    }

    private void StopAll()
    {
        try { _redirector?.Dispose(); } catch { }
        _redirector = null;
        _filter = null;
        _listener?.Dispose();
        _listener = null;
    }

    private static string CatchAllHost(ProxyOptions o)
    {
        TargetParser.TryParse(o.CatchAll.Target ?? o.DefaultTarget, out var host, out _);
        return NetInfo.IsThisPc(host) ? "127.0.0.1" : host;
    }

    private async Task AcceptLoopAsync(Socket listener)
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
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected accept error (all-ports forwarding)");
                await Task.Delay(100);
                continue;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(Socket client)
    {
        var ct = _ct;
        if (client.RemoteEndPoint is not IPEndPoint remoteEp)
        {
            client.Dispose();
            return;
        }
        var remote = Net.Format(remoteEp);
        if (!_flows.TryAcquire(remoteEp, out var flow))
        {
            // Someone connected to the internal port directly (scanner). Not one of ours.
            Net.Abort(client);
            return;
        }

        var remoteIp = ConnectionGate.Normalize(remoteEp.Address);
        int port = flow.OriginalPort;
        if (!_gate.TryEnter(remoteIp, out var reason))
        {
            _flows.Release(flow);
            if (_logLimit.Allow()) _log.LogWarning("[{Port}] {Client} refused: {Reason}", port, remote, reason);
            Net.Abort(client);
            return;
        }

        Socket? upstream = null;
        _status.Opened();
        try
        {
            var o = _config.Current;
            var host = flow.TargetOverride ?? CatchAllHost(o);
            Net.Configure(client);
            if (_logLimit.Allow()) _log.LogInformation("[{Port}] {Client} forwarded -> {Target}:{Port}", port, remote, host, port);

            upstream = await Net.ConnectAsync(host, port, o.ConnectTimeoutMs, ct);
            var (up, down) = await Net.PipeAsync(client, upstream, ct);
            _log.LogDebug("[{Port}] {Client} closed ({Up} B up, {Down} B down)", port, remote, up, down);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _status.Failed();
            var msg = ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                ? $"nothing is running on port {port}"
                : ex.Message;
            if (_logLimit.Allow()) _log.LogWarning("[{Port}] {Client}: {Error}", port, remote, msg);
        }
        finally
        {
            _status.Closed();
            _gate.Exit(remoteIp);
            client.Dispose();
            upstream?.Dispose();
            _flows.Release(flow);
        }
    }
}
