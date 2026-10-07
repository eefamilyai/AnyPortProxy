using System.Net;
using AnyPortProxy.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy;

/// <summary>
/// Quietly repairs things that break over time: Windows Firewall rules that got removed (Windows updates,
/// security tools) and router forwards that disappeared (router reboots, this PC getting a new IP).
/// </summary>
public sealed class SelfHealService(StatusTracker status, ILogger<SelfHealService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Elevation.IsAdmin) return; // firewall/router changes need admin; the service always has it
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }

        int round = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (round % 3 == 0) await Task.Run(CheckFirewall, ct); // every hour
                await CheckRouterAsync(ct);                           // every 20 minutes
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogDebug("Self-check failed: {Error}", ex.Message);
            }
            round++;
            try { await Task.Delay(TimeSpan.FromMinutes(20), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void Repaired(string what)
    {
        log.LogWarning("Repaired: {What}", what);
        status.Repaired(what);
    }

    private void CheckFirewall()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null && !Firewall.Exists(Firewall.ProxyRuleName) && Firewall.AllowProgram(exe, out _))
            Repaired("Windows Firewall rule for AnyPortProxy was missing — put it back");

        AppConfig cfg;
        try { cfg = ConfigStore.Load(); } catch { return; }
        foreach (var rule in cfg.Ports.Where(r => r.Firewall))
        {
            foreach (var proto in rule.Protocols())
            {
                if (!Firewall.Exists(Firewall.PortRuleName(rule.Range, proto)) && Firewall.AllowPort(rule.Range, proto, rule.Name, out _))
                    Repaired($"Windows Firewall rule for {rule.Name} ({proto} {rule.Range}) was missing — put it back");
            }
        }
    }

    private async Task CheckRouterAsync(CancellationToken ct)
    {
        AppConfig cfg;
        try { cfg = ConfigStore.Load(); } catch { return; }
        var rules = cfg.Ports.Where(r => r.Router && r.Last - r.Port < PortHelper.MaxRouterPorts).ToList();
        if (rules.Count == 0) return;

        var gw = await UpnpGateway.DiscoverAsync(4000, ct);
        if (gw is null) return; // router not reachable right now; try again next round
        foreach (var rule in rules)
        {
            foreach (var proto in rule.Protocols())
            {
                for (int p = rule.Port; p <= rule.Last; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    var m = await gw.GetMappingAsync(p, proto);
                    if (m is not null && IPAddress.TryParse(m.InternalClient, out var client) && client.Equals(gw.LocalAddress)) continue;
                    try
                    {
                        await gw.AddPortMappingAsync(p, proto, $"AnyPortProxy {rule.Name}");
                        Repaired(m is null
                            ? $"Router forward for {rule.Name} ({proto} {p}) had disappeared — added it again"
                            : $"Router forward for {rule.Name} ({proto} {p}) pointed to {m.InternalClient}; updated to this PC ({gw.LocalAddress})");
                    }
                    catch (Exception ex) when (ex is UpnpException or HttpRequestException or TaskCanceledException)
                    {
                        log.LogDebug("Couldn't repair router forward {Port}: {Error}", p, ex.Message);
                    }
                }
            }
        }
    }
}
