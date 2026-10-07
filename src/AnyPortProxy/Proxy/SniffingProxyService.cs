using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AnyPortProxy.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy.Proxy;

/// <summary>
/// Listens on SniffPorts, reads the hostname from the first packet and forwards by route.
/// Listeners follow settings changes live; ports that were busy are retried every 30 seconds.
/// </summary>
public sealed class SniffingProxyService : BackgroundService
{
    private static readonly int AcceptLoops = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private readonly ConfigMonitor _config;
    private readonly Router _router;
    private readonly StatusTracker _status;
    private readonly ConnectionGate _gate;
    private readonly LogLimiter _logLimit;
    private readonly ILogger<SniffingProxyService> _log;
    private readonly Dictionary<int, Socket> _listeners = new();
    private readonly HashSet<int> _failed = new();
    private readonly object _lock = new();
    private CancellationToken _ct;

    public SniffingProxyService(ConfigMonitor config, Router router, StatusTracker status, ConnectionGate gate, LogLimiter logLimit,
        ILogger<SniffingProxyService> log)
    {
        _config = config;
        _router = router;
        _status = status;
        _gate = gate;
        _logLimit = logLimit;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ct = ct;
        _router.LogRoutes(_config.Current);
        Reconcile();
        _config.Changed += OnConfigChanged;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                bool retry;
                lock (_lock) retry = _failed.Count > 0;
                if (retry) Reconcile();
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
                foreach (var l in _listeners.Values) l.Dispose();
                _listeners.Clear();
            }
        }
    }

    private void OnConfigChanged(ProxyOptions _) => Reconcile();

    private void Reconcile()
    {
        if (_ct.IsCancellationRequested) return;
        lock (_lock)
        {
            try
            {
                var wanted = _config.Current.SniffPorts.ToHashSet();
                foreach (var port in _listeners.Keys.Except(wanted).ToList())
                {
                    _listeners[port].Dispose();
                    _listeners.Remove(port);
                    _status.RemoveListener(port);
                    _log.LogInformation("Stopped website routing on port {Port}", port);
                }
                foreach (var port in _failed.Except(wanted).ToList())
                {
                    _failed.Remove(port);
                    _status.RemoveListener(port);
                }

                foreach (var port in wanted.Except(_listeners.Keys))
                {
                    try
                    {
                        var s = Net.Listen(port);
                        _listeners[port] = s;
                        _failed.Remove(port);
                        _status.SetListener(port, true, null);
                        _log.LogInformation("Website routing listening on port {Port}", port);
                        for (int i = 0; i < AcceptLoops; i++) _ = AcceptLoopAsync(s, port);
                    }
                    catch (SocketException ex)
                    {
                        var msg = ex.SocketErrorCode switch
                        {
                            SocketError.AddressAlreadyInUse => "another program is using it (sslh, netsh portproxy, IIS or a web server?)",
                            SocketError.AccessDenied => "Windows refused access to it (another program has it locked)",
                            _ => ex.Message,
                        };
                        if (_failed.Add(port)) _log.LogError("Can't listen on port {Port}: {Error}. Will keep retrying.", port, msg);
                        _status.SetListener(port, false, msg);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Updating website listeners failed");
            }
        }
    }

    private async Task AcceptLoopAsync(Socket listener, int port)
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
                // e.g. client reset before we accepted it: harmless, keep accepting.
                continue;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected accept error on port {Port}", port);
                await Task.Delay(100);
                continue;
            }
            _ = HandleAsync(client, port);
        }
    }

    private async Task HandleAsync(Socket client, int port)
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

        var o = _config.Current;
        byte[]? buf = ArrayPool<byte>.Shared.Rent(HostnameSniffer.MaxPeekBytes);
        Socket? upstream = null;
        string? host = null;
        string proto = "unknown";
        int len = 0;
        _status.Opened();
        try
        {
            Net.Configure(client);
            var status = SniffStatus.NeedMore;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(o.SniffTimeoutMs);
                try
                {
                    while (status == SniffStatus.NeedMore && len < HostnameSniffer.MaxPeekBytes)
                    {
                        int n = await client.ReceiveAsync(buf.AsMemory(len, HostnameSniffer.MaxPeekBytes - len), SocketFlags.None, cts.Token);
                        if (n == 0) return; // closed before sending anything (health checks, scanners)
                        len += n;
                        status = HostnameSniffer.Sniff(buf.AsSpan(0, len), out host, out proto);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    proto = len == 0 ? "silent" : proto + "(timeout)";
                }
            }

            var route = _router.Resolve(host, port);
            if (o.SniffPorts.Contains(route.Port) && Net.IsThisMachine(route.Host))
            {
                _status.Failed();
                if (_logLimit.Allow())
                    _log.LogError("[{Port}] {Client} {Proto} host={Host}: route {Rule} -> {Target}:{TargetPort} points back at this proxy; dropping",
                        port, remote, proto, host ?? "-", route.Rule, route.Host, route.Port);
                return;
            }

            if (_logLimit.Allow())
                _log.LogInformation("[{Port}] {Client} {Proto} host={Host} -> {Target}:{TargetPort} ({Rule})",
                    port, remote, proto, host ?? "-", route.Host, route.Port, route.Rule);

            try
            {
                upstream = await Net.ConnectAsync(route.Host, route.Port, o.ConnectTimeoutMs, ct);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                _status.Failed();
                if (_logLimit.Allow()) _log.LogWarning("[{Port}] {Client} host={Host}: can't reach {Target}:{TargetPort} ({Error})",
                    port, remote, host ?? "-", route.Host, route.Port, ex.Message);
                if (proto == "http") await SendOfflinePageAsync(client, host, ct);
                return;
            }

            if (len > 0) await upstream.SendAsync(buf.AsMemory(0, len), SocketFlags.None, ct);
            ArrayPool<byte>.Shared.Return(buf);
            buf = null;

            var (up, down) = await Net.PipeAsync(client, upstream, ct);
            _log.LogDebug("[{Port}] {Client} closed ({Up} B up, {Down} B down)", port, remote, up, down);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _status.Failed();
            if (_logLimit.Allow()) _log.LogWarning("[{Port}] {Client}: {Error}", port, remote, ex.Message);
        }
        finally
        {
            _status.Closed();
            _gate.Exit(remoteIp);
            client.Dispose();
            upstream?.Dispose();
            if (buf is not null) ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Plain-English "this site is offline" page for http visitors (never reveals internal addresses).</summary>
    private static async Task SendOfflinePageAsync(Socket client, string? host, CancellationToken ct)
    {
        try
        {
            var name = System.Net.WebUtility.HtmlEncode(host ?? "This site");
            var body =
                "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
                $"<title>{name} is offline</title><style>body{{font-family:system-ui,sans-serif;max-width:36rem;margin:15vh auto;padding:0 1rem;color:#0f172a}}" +
                "h1{font-size:1.6rem}p{color:#475569;line-height:1.5}</style></head><body>" +
                $"<h1>{name} is offline right now</h1>" +
                "<p>The computer behind this address isn't answering. Please try again in a little while.</p>" +
                "<p>If this is your site: make sure that computer is turned on and the website is running.</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
                "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
            await client.SendAsync(head, SocketFlags.None, ct);
            await client.SendAsync(bytes, SocketFlags.None, ct);
            client.Shutdown(SocketShutdown.Send);
        }
        catch
        {
        }
    }
}
