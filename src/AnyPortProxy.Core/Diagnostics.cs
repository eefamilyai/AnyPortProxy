using System.Net;

namespace AnyPortProxy.Core;

/// <summary>The health check: finds common problems and offers one-click fixes.</summary>
public static class Diagnostics
{
    public static async Task RunAsync(AppConfig c, Action<CheckResult> report, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!Elevation.IsAdmin)
            report(CheckResult.Info("Not running as Administrator", "Some checks and all fixes need Administrator rights."));

        // --- The service itself
        progress?.Invoke("Checking the background service…");
        var state = ServiceManager.GetState();
        switch (state)
        {
            case ServiceState.NotInstalled:
                report(CheckResult.Fail("AnyPortProxy isn't installed yet",
                    "Install it so it runs in the background and starts with Windows.",
                    new FixAction("Install now", async () =>
                    {
                        await Task.Run(() => Installer.Install(_ => { }));
                        return "Installed and started.";
                    })));
                break;
            case ServiceState.Stopped:
                report(CheckResult.Fail("AnyPortProxy is stopped", "Nobody can reach your stuff until it's running.",
                    new FixAction("Start it", async () =>
                    {
                        await Task.Run(ServiceManager.Start);
                        return "Started.";
                    })));
                break;
            default:
                report(CheckResult.Ok("AnyPortProxy is running"));
                break;
        }

        var status = ServiceStatus.Read();
        if (state == ServiceState.Running && status is { IsFresh: true })
        {
            foreach (var l in status.SniffPorts)
            {
                report(l.Listening
                    ? CheckResult.Ok($"Website routing is working on port {l.Port}")
                    : CheckResult.Fail($"Port {l.Port} couldn't be opened for website routing", l.Error));
            }
            switch (status.CatchAll.State)
            {
                case "Running": report(CheckResult.Ok("All-ports forwarding is working", status.CatchAll.Message)); break;
                case "Error": report(CheckResult.Fail("All-ports forwarding isn't working", status.CatchAll.Message)); break;
                default: report(CheckResult.Info("All-ports forwarding is turned off", status.CatchAll.Message)); break;
            }
        }
        else if (state == ServiceState.Running)
        {
            report(CheckResult.Warn("The service is running but not reporting its status yet", "Give it a few seconds after starting, then check again."));
        }

        if (status is { IsFresh: true })
        {
            if (status.ConfigError is not null)
                report(CheckResult.Fail("The settings file has a problem — the proxy is running on the last good settings",
                    status.ConfigError + " (Fix the typo in Settings → Edit settings file, or change any setting in the app to rewrite the file.)"));
            foreach (var note in status.ConfigNotes)
                report(CheckResult.Warn("A setting was ignored because it isn't valid", note));
            foreach (var repair in status.Repairs.TakeLast(3))
                report(CheckResult.Info("AnyPortProxy repaired something by itself", repair));
            if (status.RejectedConnections > 0)
                report(CheckResult.Info($"{status.RejectedConnections} connection(s) were refused by the flood limits",
                    "Normal during attacks. If real visitors are affected, raise the limits (apx limits)."));
        }

        // Is a newer version sitting in this folder than the one that's installed?
        if (state != ServiceState.NotInstalled && !AppPaths.SameDir(AppPaths.AppDir, AppPaths.ServiceDir()))
        {
            try
            {
                var installedExe = Path.Combine(AppPaths.ServiceDir(), AppPaths.CliExe);
                var installed = System.Diagnostics.FileVersionInfo.GetVersionInfo(installedExe).ProductVersion?.Split('+')[0];
                if (installed is not null && installed != AppPaths.Version)
                    report(CheckResult.Warn($"The installed version ({installed}) is different from this one ({AppPaths.Version})",
                        "Update so the background service runs the same version as this app.",
                        new FixAction("Update now", async () =>
                        {
                            await Task.Run(() => Installer.Install(_ => { }));
                            return "Updated and restarted.";
                        })));
            }
            catch
            {
            }
        }

        // --- Settings
        foreach (var r in c.Proxy.Routes)
        {
            if (!TargetParser.TryParse(r.Target, out _, out _))
                report(CheckResult.Fail($"Website {r.Host} has an invalid destination \"{r.Target}\"", "Edit it on the Websites page."));
        }
        if (!TargetParser.TryParse(c.Proxy.DefaultTarget, out _, out _))
            report(CheckResult.Fail($"The \"unknown addresses\" destination \"{c.Proxy.DefaultTarget}\" is invalid"));
        if (c.Proxy.CatchAll.Enabled && !PortRanges.TryParse(c.Proxy.CatchAll.AllowedPorts, out _, out var rangeError))
            report(CheckResult.Fail("The forwarded ports setting is invalid", rangeError));

        // --- Files and firewall
        var dir = AppPaths.ServiceDir();
        if (state != ServiceState.NotInstalled)
        {
            if (!File.Exists(Path.Combine(dir, "WinDivert.dll")) || !File.Exists(Path.Combine(dir, "WinDivert64.sys")))
                report(CheckResult.Fail("The WinDivert driver files are missing", $"All-ports forwarding needs WinDivert.dll and WinDivert64.sys in {dir}. Re-run build.ps1 and install again."));

            if (Firewall.Exists(Firewall.ProxyRuleName))
            {
                report(CheckResult.Ok("Windows Firewall lets traffic reach AnyPortProxy"));
            }
            else
            {
                report(CheckResult.Fail("Windows Firewall has no rule for AnyPortProxy", "Visitors from the internet may be blocked.",
                    new FixAction("Add the rule", () =>
                    {
                        var ok = Firewall.AllowProgram(Path.Combine(dir, AppPaths.CliExe), out var err);
                        return Task.FromResult(ok ? "Firewall rule added." : $"Failed: {err}");
                    })));
            }
        }

        // Only portproxy rules that sit on ports we need are a problem; leave other software's rules alone.
        var needed = c.Proxy.SniffPorts.Append(c.Proxy.CatchAll.ListenPort).ToHashSet();
        foreach (var rule in NetInfo.GetPortProxyRules().Where(r => needed.Contains(r.ListenPort)))
        {
            report(CheckResult.Fail($"An old \"netsh portproxy\" rule is holding port {rule.ListenPort}",
                $"{rule} — probably from the old sslh/WSL setup. AnyPortProxy can't use this port until it's removed.",
                new FixAction($"Remove this rule", () =>
                {
                    NetInfo.DeletePortProxy(rule);
                    return Task.FromResult($"Removed portproxy rule for port {rule.ListenPort}.");
                })));
        }

        // --- Who owns 80/443?
        progress?.Invoke("Checking which programs use your website ports…");
        var listeners = NetInfo.GetListeners();
        int ourPid = status is { IsFresh: true } ? status.Pid : -1;
        foreach (var port in c.Proxy.SniffPorts)
        {
            var others = listeners.Where(l => l.Protocol == "TCP" && l.Port == port && l.Pid != ourPid && l.Process != "AnyPortProxy")
                .Select(l => l.Process).Distinct().ToList();
            if (others.Count == 0) continue;
            report(CheckResult.Fail($"Port {port} is being used by another program: {string.Join(", ", others)}",
                OwnerHint(others[0])));
        }

        // --- Can we reach the computers websites point to?
        progress?.Invoke("Testing the computers your websites point to…");
        var probes = new List<(string Label, string Host, int Port)>();
        foreach (var r in c.Proxy.Routes)
        {
            if (!TargetParser.TryParse(r.Target, out var host, out var tport)) continue;
            var incoming = r.Port is int p ? new[] { p } : c.Proxy.SniffPorts.ToArray();
            foreach (var inc in incoming)
                probes.Add(($"{r.Host} ({(inc == 443 ? "https" : inc == 80 ? "http" : $"port {inc}")})", host, tport ?? inc));
        }
        var probeResults = await Task.WhenAll(probes.DistinctBy(x => (x.Label, x.Host, x.Port))
            .Select(async x => (x, ok: await NetInfo.CanConnectAsync(x.Host, x.Port, 2000, ct))));
        foreach (var (x, ok) in probeResults)
        {
            report(ok
                ? CheckResult.Ok($"{x.Label} → {TargetParser.Format(x.Host, x.Port)} is answering")
                : CheckResult.Warn($"{x.Label} → {TargetParser.Format(x.Host, x.Port)} isn't answering",
                    "Is that computer turned on, and is the website running on that port?"));
        }

        // --- Internet address and DNS
        progress?.Invoke("Looking up your internet address…");
        var publicIp = await NetInfo.GetPublicIpAsync(ct);
        if (publicIp is null)
        {
            report(CheckResult.Warn("Couldn't find your internet address", "Are you connected to the internet?"));
        }
        else
        {
            report(CheckResult.Info($"Your internet address is {publicIp}"));
            var names = c.Proxy.Routes.Select(r => r.Host.Trim().TrimEnd('.'))
                .Where(h => !h.Contains('*'))
                .Append(c.Domain ?? "")
                .Where(h => h.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                IPAddress[] addrs;
                try
                {
                    addrs = (await Dns.GetHostAddressesAsync(name, ct)).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray();
                }
                catch
                {
                    addrs = [];
                }
                if (addrs.Length == 0)
                    report(CheckResult.Warn($"{name} doesn't exist on the internet yet",
                        $"At your domain provider, add an A record for {name} pointing to {publicIp}."));
                else if (addrs.Contains(publicIp))
                    report(CheckResult.Ok($"{name} points to your internet address"));
                else
                    report(CheckResult.Warn($"{name} points to {string.Join(", ", addrs.Select(a => a.ToString()))}, not your internet address ({publicIp})",
                        "Update the A record at your domain provider (or your dynamic DNS). If you use Cloudflare's orange-cloud proxy, this is expected."));
            }
        }

        // --- Router
        progress?.Invoke("Looking for your router…");
        var gw = await UpnpGateway.DiscoverAsync(3000, ct);
        if (gw is null)
        {
            report(CheckResult.Info("Couldn't talk to your router automatically (UPnP)", "That's fine if you forward ports on your router by hand."));
        }
        else
        {
            string? ext = null;
            try { ext = await gw.GetExternalIpAsync(); } catch { }
            if (IPAddress.TryParse(ext, out var extIp) && NetInfo.IsPrivate(extIp))
                report(CheckResult.Fail($"Your router's internet address ({ext}) is a private address",
                    "Your internet provider probably uses CGNAT, or there's a second router in front of yours. People outside can't reach you directly until that's solved: ask your ISP for a public IP."));
            else if (extIp is not null && publicIp is not null && !extIp.Equals(publicIp))
                report(CheckResult.Warn($"Your router says its address is {ext}, but the internet sees {publicIp}",
                    "This usually means two routers (double NAT) or a VPN. Port forwarding may not work."));
            else
                report(CheckResult.Ok($"Found your router: {gw.FriendlyName}", ext is null ? null : $"Internet address {ext}"));

            // Router forwards made by the port helper: still there, and still pointing at this PC?
            foreach (var rule in c.Ports.Where(r => r.Router))
            {
                foreach (var proto in rule.Protocols())
                {
                    var m = await gw.GetMappingAsync(rule.Port, proto);
                    bool ok = m is not null && IPAddress.TryParse(m.InternalClient, out var client) && client.Equals(gw.LocalAddress);
                    if (ok) continue;
                    var captured = rule;
                    var capturedProto = proto;
                    report(CheckResult.Warn(
                        m is null ? $"Your router forgot the forward for {rule.Name} ({proto} {rule.Range})"
                                  : $"Your router sends {rule.Name} ({proto} {rule.Range}) to {m.InternalClient}, not this PC ({gw.LocalAddress})",
                        "Routers drop forwards after a reboot, and this PC's address can change. (The service also re-checks this every 20 minutes.)",
                        new FixAction("Fix the router forward", async () =>
                        {
                            for (int p = captured.Port; p <= captured.Last && p - captured.Port < PortHelper.MaxRouterPorts; p++)
                                await gw.AddPortMappingAsync(p, capturedProto, $"AnyPortProxy {captured.Name}");
                            return "Router forward fixed.";
                        })));
                }
            }
        }

        // --- Ports opened with the helper
        foreach (var rule in c.Ports.Where(r => r.Firewall))
        {
            foreach (var proto in rule.Protocols())
            {
                if (Firewall.Exists(Firewall.PortRuleName(rule.Range, proto))) continue;
                var captured = rule;
                var capturedProto = proto;
                report(CheckResult.Warn($"The firewall rule for {rule.Name} ({proto} {rule.Range}) is missing",
                    "Someone (or Windows) removed it.",
                    new FixAction("Put it back", () =>
                    {
                        var ok = Firewall.AllowPort(captured.Range, capturedProto, captured.Name, out var err);
                        return Task.FromResult(ok ? "Firewall rule restored." : $"Failed: {err}");
                    })));
            }
        }
    }

    private static string OwnerHint(string process) => process.ToLowerInvariant() switch
    {
        "svchost" => "Probably an old \"netsh portproxy\" rule (IP Helper service). Remove old portproxy rules.",
        "wslrelay" or "vmmem" or "vmmemwsl" or "wsl" => "This is WSL (probably sslh). Stop it with: wsl --shutdown",
        "system" => "This is Windows' built-in web server (IIS or HTTP.sys, used by some apps). Stop IIS or the app using it.",
        "httpd" or "nginx" or "caddy" or "apache" or "traefik" => "Another web server is using it. Move it to a different port (like 8080) and add it as a website here.",
        "skype" or "teams" => "Change the app's settings so it doesn't use port 80/443.",
        _ => "Close that program or change its port, then restart AnyPortProxy.",
    };
}
