using System.Net;

namespace AnyPortProxy.Core;

public sealed class OpenPortRequest
{
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public int? EndPort { get; set; }
    public PortProtocol Protocol { get; set; } = PortProtocol.Tcp;
    public bool Firewall { get; set; } = true;
    public bool Router { get; set; }
}

/// <summary>Opens, closes and checks ports across AnyPortProxy, Windows Firewall and the router.</summary>
public static class PortHelper
{
    public const int MaxRouterPorts = 50;

    public static string? Validate(AppConfig c, int port, int? endPort, PortProtocol protocol)
    {
        int end = endPort ?? port;
        if (port is < 1 or > 65535 || end is < 1 or > 65535) return "Ports go from 1 to 65535.";
        if (end < port) return "The second port must be bigger than the first.";
        if (end - port > 1000) return "That's a lot of ports. Open at most 1000 at a time.";
        if (protocol != PortProtocol.Udp)
        {
            var sniff = c.Proxy.SniffPorts.FirstOrDefault(p => p >= port && p <= end);
            if (sniff != 0)
                return $"Port {sniff} is already used for website routing. Add it as a website instead (Websites page, or: apx site add).";
        }
        if (c.Proxy.CatchAll.ListenPort >= port && c.Proxy.CatchAll.ListenPort <= end)
            return $"Port {c.Proxy.CatchAll.ListenPort} is used internally by AnyPortProxy. Pick another port.";
        return null;
    }

    public static string Range(int port, int? end) => end is int e && e > port ? $"{port}-{e}" : $"{port}";

    /// <summary>The address to give friends, e.g. "reggilion.com:25565".</summary>
    public static string ConnectAddress(AppConfig c, int port, IPAddress? publicIp = null)
    {
        var host = !string.IsNullOrWhiteSpace(c.Domain) ? c.Domain!.Trim() : publicIp?.ToString() ?? "<your internet address>";
        return $"{host}:{port}";
    }

    public static async Task<List<CheckResult>> OpenAsync(AppConfig c, OpenPortRequest r, Action<string>? progress = null)
    {
        var results = new List<CheckResult>();
        int end = r.EndPort ?? r.Port;
        var range = Range(r.Port, end);
        var name = string.IsNullOrWhiteSpace(r.Name) ? Presets.ForPort(r.Port)?.Name ?? $"Port {range}" : r.Name.Trim();
        var rule = new PortRule
        {
            Name = name,
            Port = r.Port,
            EndPort = end > r.Port ? end : null,
            Protocol = r.Protocol,
        };

        if (Validate(c, r.Port, r.EndPort, r.Protocol) is { } error)
        {
            results.Add(CheckResult.Fail("Can't open this port", error));
            return results;
        }

        // 1. AnyPortProxy's own forwarding (TCP only)
        var ca = c.Proxy.CatchAll;
        if (r.Protocol != PortProtocol.Udp)
        {
            if (ca.Enabled)
            {
                int unblocked = ca.BlockedPorts.RemoveAll(b => b >= r.Port && b <= end);
                bool widened = false;
                if (!PortRanges.ContainsAll(ca.AllowedPorts, r.Port, end))
                {
                    ca.AllowedPorts = PortRanges.Add(ca.AllowedPorts, r.Port, end);
                    widened = true;
                }
                var note = unblocked > 0 ? "Removed it from the blocked list."
                    : widened ? "Added it to the forwarded ports."
                    : "It was already covered by all-ports forwarding.";
                results.Add(CheckResult.Ok($"Internet visitors on TCP {range} are sent to {c.CatchAllHost}", note));
            }
            else
            {
                results.Add(CheckResult.Info($"TCP {range} goes straight to this PC",
                    "All-ports forwarding is off, so make sure your router forwards this port to this PC."));
            }
        }
        if (r.Protocol != PortProtocol.Tcp)
        {
            results.Add(CheckResult.Info($"UDP {range} goes straight to this PC",
                "AnyPortProxy doesn't relay UDP; your router and Windows Firewall handle it directly."));
        }

        // 2. Windows Firewall
        if (r.Firewall)
        {
            progress?.Invoke("Adding Windows Firewall rule…");
            bool allOk = true;
            foreach (var proto in rule.Protocols())
            {
                if (Firewall.AllowPort(range, proto, name, out var fwError))
                {
                    results.Add(CheckResult.Ok($"Windows Firewall allows {proto} {range}", "Devices on your home network can connect too."));
                }
                else
                {
                    allOk = false;
                    results.Add(CheckResult.Fail($"Couldn't add a Windows Firewall rule for {proto} {range}", fwError));
                }
            }
            rule.Firewall = allOk;
        }
        else
        {
            rule.Firewall = false;
        }

        // 3. Router (UPnP)
        if (r.Router)
        {
            results.AddRange(await AddRouterAsync(rule, progress));
        }

        // 4. Is something actually running there?
        progress?.Invoke("Checking what's running on this port…");
        results.AddRange(ListeningChecks(r.Port, end, rule.Protocol));

        c.Ports.RemoveAll(x => x.Port == rule.Port && x.Last == rule.Last && x.Protocol == rule.Protocol);
        c.Ports.Add(rule);
        ConfigStore.Save(c);

        var publicIp = string.IsNullOrWhiteSpace(c.Domain) ? await NetInfo.GetPublicIpAsync() : null;
        results.Add(CheckResult.Info($"Tell people to connect to: {ConnectAddress(c, r.Port, publicIp)}",
            string.IsNullOrWhiteSpace(c.Domain) ? "Tip: set your domain in Settings to show a nicer address." :
            "Any subdomain of your domain works too."));
        return results;
    }

    private static async Task<List<CheckResult>> AddRouterAsync(PortRule rule, Action<string>? progress)
    {
        var results = new List<CheckResult>();
        progress?.Invoke("Looking for your router…");
        var gw = await UpnpGateway.DiscoverAsync();
        var lan = NetInfo.GetLanAddress()?.ToString() ?? "this PC";
        if (gw is null)
        {
            results.Add(CheckResult.Warn("Couldn't talk to your router automatically",
                $"Your router may have UPnP turned off. That's fine if you already forward ports by hand: forward {rule.ProtocolText} {rule.Range} to {lan} in your router's settings."));
            return results;
        }
        if (rule.Last - rule.Port + 1 > MaxRouterPorts)
        {
            results.Add(CheckResult.Warn("Too many ports to set up on the router automatically",
                $"Forward {rule.ProtocolText} {rule.Range} to {gw.LocalAddress} in your router's settings."));
            return results;
        }

        try
        {
            progress?.Invoke($"Asking {gw.FriendlyName} to forward {rule.Range}…");
            foreach (var proto in rule.Protocols())
                for (int p = rule.Port; p <= rule.Last; p++)
                    await gw.AddPortMappingAsync(p, proto, $"AnyPortProxy {rule.Name}");
            rule.Router = true;
            results.Add(CheckResult.Ok($"Your router forwards {rule.ProtocolText} {rule.Range} to this PC ({gw.LocalAddress})", gw.FriendlyName));

            var ext = await gw.GetExternalIpAsync();
            if (IPAddress.TryParse(ext, out var extIp) && NetInfo.IsPrivate(extIp))
            {
                results.Add(CheckResult.Warn($"Your router's internet address is {ext}, which is a private address",
                    "Your internet provider probably uses CGNAT (or you have two routers). People outside may not be able to reach you. Ask your ISP for a public IP address."));
            }
        }
        catch (Exception ex) when (ex is UpnpException or HttpRequestException or TaskCanceledException)
        {
            results.Add(CheckResult.Warn("Your router didn't accept the forward",
                $"{ex.Message} You can forward {rule.ProtocolText} {rule.Range} to {gw.LocalAddress} by hand in your router's settings."));
        }
        return results;
    }

    public static async Task<List<CheckResult>> CloseAsync(AppConfig c, PortRule rule, bool blockInProxy, Action<string>? progress = null)
    {
        var results = new List<CheckResult>();
        foreach (var proto in rule.Protocols())
        {
            Firewall.Delete(Firewall.PortRuleName(rule.Range, proto));
        }
        if (rule.Firewall) results.Add(CheckResult.Ok($"Removed the Windows Firewall rule for {rule.Range}"));

        if (rule.Router)
        {
            progress?.Invoke("Removing the forward from your router…");
            var gw = await UpnpGateway.DiscoverAsync();
            if (gw is null)
            {
                results.Add(CheckResult.Warn("Couldn't reach your router to remove the forward",
                    $"Remove the forward for {rule.Range} in your router's settings if it's still there."));
            }
            else
            {
                try
                {
                    foreach (var proto in rule.Protocols())
                        for (int p = rule.Port; p <= rule.Last; p++)
                            await gw.DeletePortMappingAsync(p, proto);
                    results.Add(CheckResult.Ok($"Removed the router forward for {rule.Range}"));
                }
                catch (Exception ex) when (ex is UpnpException or HttpRequestException or TaskCanceledException)
                {
                    results.Add(CheckResult.Warn("The router didn't remove the forward", ex.Message));
                }
            }
        }

        var ca = c.Proxy.CatchAll;
        if (blockInProxy && rule.Protocol != PortProtocol.Udp && ca.Enabled)
        {
            for (int p = rule.Port; p <= rule.Last; p++)
                if (!ca.BlockedPorts.Contains(p)) ca.BlockedPorts.Add(p);
            ca.BlockedPorts.Sort();
            results.Add(CheckResult.Ok($"Internet visitors can no longer reach TCP {rule.Range}", "Added to the blocked list."));
        }
        else if (rule.Protocol != PortProtocol.Udp && ca.Enabled && PortRanges.ContainsAll(ca.AllowedPorts, rule.Port, rule.Last))
        {
            results.Add(CheckResult.Info($"TCP {rule.Range} is still reachable from the internet through all-ports forwarding",
                "Block it too if you want it completely closed."));
        }

        c.Ports.Remove(rule);
        ConfigStore.Save(c);
        return results;
    }

    public static List<CheckResult> ListeningChecks(int port, int end, PortProtocol protocol)
    {
        var results = new List<CheckResult>();
        bool tcp = protocol != PortProtocol.Udp, udp = protocol != PortProtocol.Tcp;
        var range = Range(port, end);
        var found = NetInfo.GetListeners()
            .Where(l => l.Port >= port && l.Port <= end && ((tcp && l.Protocol == "TCP") || (udp && l.Protocol == "UDP")))
            .ToList();
        if (found.Count == 0)
        {
            results.Add(CheckResult.Warn($"Nothing is running on port {range} on this PC yet",
                "Start your game server or app. It will be reachable as soon as it's running. (Ignore this if it runs on another computer.)"));
            return results;
        }

        var procs = string.Join(", ", found.Select(l => l.Process).Distinct());
        results.Add(CheckResult.Ok($"{procs} is running on port {range}"));
        if (found.All(l => IPAddress.IsLoopback(l.Address)))
        {
            results.Add(CheckResult.Info("It only accepts connections from this PC (127.0.0.1)",
                "Internet visitors still get in through AnyPortProxy, but other devices at home can't connect directly. To fix that, configure the app to listen on 0.0.0.0."));
        }
        return results;
    }

    public static async Task<List<CheckResult>> CheckAsync(AppConfig c, int port, PortProtocol protocol, bool checkRouter, Action<string>? progress = null)
    {
        var results = new List<CheckResult>();
        bool tcp = protocol != PortProtocol.Udp;
        var ca = c.Proxy.CatchAll;

        if (Presets.Describe(port) is { } what) results.Add(CheckResult.Info(what));
        if (Presets.Warning(port) is { } warn)
            results.Add(warn.Risk == Risk.High ? CheckResult.Fail("Dangerous to open", warn.Why) : CheckResult.Warn("Be careful opening this", warn.Why));

        if (tcp && c.Proxy.SniffPorts.Contains(port))
        {
            results.Add(CheckResult.Ok($"Port {port} is used for website routing", "Visitors are sent to computers based on the address they type (see Websites)."));
            return results;
        }

        if (tcp)
        {
            if (!ca.Enabled)
            {
                results.Add(CheckResult.Info("All-ports forwarding is off", "Internet visitors reach this port only if your router forwards it straight to a computer."));
            }
            else if (ca.BlockedPorts.Contains(port))
            {
                results.Add(CheckResult.Fail($"Port {port} is blocked in AnyPortProxy", "Internet visitors can't reach it.",
                    new FixAction("Unblock it", () =>
                    {
                        ca.BlockedPorts.Remove(port);
                        ConfigStore.Save(c);
                        return Task.FromResult($"Port {port} unblocked.");
                    })));
            }
            else if (!PortRanges.ContainsAll(ca.AllowedPorts, port, port))
            {
                results.Add(CheckResult.Fail($"Port {port} isn't in the forwarded ports ({ca.AllowedPorts})", null,
                    new FixAction("Forward it", () =>
                    {
                        ca.AllowedPorts = PortRanges.Add(ca.AllowedPorts, port, port);
                        ConfigStore.Save(c);
                        return Task.FromResult($"Port {port} is now forwarded.");
                    })));
            }
            else
            {
                results.Add(CheckResult.Ok($"Internet visitors on TCP {port} are sent to {c.CatchAllHost}"));
            }
        }

        var protos = protocol switch { PortProtocol.Udp => new[] { "UDP" }, PortProtocol.Both => new[] { "TCP", "UDP" }, _ => new[] { "TCP" } };
        var rule = c.Ports.FirstOrDefault(r => port >= r.Port && port <= r.Last);
        foreach (var proto in protos)
        {
            bool hasRule = rule is not null && rule.Protocols().Contains(proto) && Firewall.Exists(Firewall.PortRuleName(rule.Range, proto));
            if (hasRule)
            {
                results.Add(CheckResult.Ok($"Windows Firewall allows {proto} {port}"));
            }
            else
            {
                results.Add(CheckResult.Warn($"No Windows Firewall rule from AnyPortProxy for {proto} {port}",
                    proto == "TCP" && ca.Enabled
                        ? "Internet visitors are fine (they come through AnyPortProxy), but devices on your home network may be blocked."
                        : "Windows Firewall may block people from connecting.",
                    new FixAction("Add firewall rule", async () =>
                    {
                        var res = await OpenAsync(c, new OpenPortRequest
                        {
                            Name = rule?.Name ?? Presets.ForPort(port)?.Name ?? "",
                            Port = port,
                            Protocol = proto == "UDP" ? PortProtocol.Udp : PortProtocol.Tcp,
                            Firewall = true,
                        });
                        return res.Any(x => x.Status == CheckStatus.Fail) ? "Couldn't add the rule." : "Firewall rule added.";
                    })));
            }
        }

        results.AddRange(ListeningChecks(port, port, protocol));

        if (checkRouter)
        {
            progress?.Invoke("Asking your router…");
            var gw = await UpnpGateway.DiscoverAsync();
            if (gw is null)
            {
                results.Add(CheckResult.Info("Couldn't ask your router (UPnP is off or unsupported)", "If you forward ports by hand on your router, that's fine."));
            }
            else
            {
                foreach (var proto in protos)
                {
                    var m = await gw.GetMappingAsync(port, proto);
                    results.Add(m is null
                        ? CheckResult.Info($"Your router has no automatic forward for {proto} {port}", "That's fine if you forwarded ports by hand (or all ports) in your router.")
                        : CheckResult.Ok($"Your router forwards {proto} {port} to {m.InternalClient}", m.Description));
                }
            }
        }

        results.Add(CheckResult.Info($"From the internet, use: {ConnectAddress(c, port)}",
            "To test from outside your home, use your phone on mobile data, or a site like canyouseeme.org."));
        return results;
    }
}
