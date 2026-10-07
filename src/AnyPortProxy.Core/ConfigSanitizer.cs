namespace AnyPortProxy.Core;

/// <summary>
/// Makes any settings file safe to run: drops invalid entries and clamps numbers to sane ranges,
/// so a typo can never take the proxy down. Returns a plain-English note for everything it changed.
/// </summary>
public static class ConfigSanitizer
{
    public static List<string> Sanitize(ProxyOptions p)
    {
        var notes = new List<string>();
        static bool ValidPort(int x) => x is >= 1 and <= 65535;

        p.SniffPorts = (p.SniffPorts ?? new()).Where(ValidPort).Distinct().ToList();

        var routes = new List<RouteRule>();
        foreach (var r in p.Routes ?? new())
        {
            if (r is null) continue;
            if (Websites.ValidateHost(r.Host ?? "") is { } hostError)
            {
                notes.Add($"Ignored website \"{r.Host}\": {hostError}");
                continue;
            }
            if (!TargetParser.TryParseValid(r.Target, out _, out _))
            {
                notes.Add($"Ignored website \"{r.Host}\": destination \"{r.Target}\" isn't valid");
                continue;
            }
            if (r.Port is int rp && !ValidPort(rp))
            {
                notes.Add($"Ignored website \"{r.Host}\": port {rp} isn't valid");
                continue;
            }
            routes.Add(r);
        }
        p.Routes = routes;

        if (!TargetParser.TryParseValid(p.DefaultTarget, out _, out _))
        {
            notes.Add($"Default destination \"{p.DefaultTarget}\" isn't valid; using 127.0.0.1");
            p.DefaultTarget = "127.0.0.1";
        }
        p.SniffTimeoutMs = Math.Clamp(p.SniffTimeoutMs, 500, 60_000);
        p.ConnectTimeoutMs = Math.Clamp(p.ConnectTimeoutMs, 500, 60_000);

        var ca = p.CatchAll ??= new CatchAllOptions();
        if (!ValidPort(ca.ListenPort) || ca.ListenPort < 1024 || p.SniffPorts.Contains(ca.ListenPort))
        {
            notes.Add($"Internal port {ca.ListenPort} can't be used; using 34010");
            ca.ListenPort = p.SniffPorts.Contains(34010) ? 34011 : 34010;
        }
        ca.BlockedPorts = (ca.BlockedPorts ?? new()).Where(ValidPort).Distinct().Order().ToList();
        ca.AllowedPorts ??= "1-49151";
        if (ca.Target is not null && !TargetParser.TryParseValid(ca.Target, out _, out _))
        {
            notes.Add($"Forwarding destination \"{ca.Target}\" isn't valid; using the default destination");
            ca.Target = null;
        }
        ca.Workers = Math.Clamp(ca.Workers, 0, 64);

        var forwards = (p.Forwards ?? new()).Where(f => f is not null).ToList();
        p.Forwards = new List<PortForward>();
        foreach (var f in forwards)
        {
            f.Name ??= "";
            f.Target ??= "";
            if (PortForwards.Validate(p, f) is { } why)
            {
                notes.Add($"Ignored port rule {f.Range} → {f.Target}: {why}");
                continue;
            }
            p.Forwards.Add(f);
        }

        var l = p.Limits ??= new LimitOptions();
        l.MaxConnections = Math.Clamp(l.MaxConnections, 100, 1_000_000);
        l.MaxConnectionsPerIp = Math.Clamp(l.MaxConnectionsPerIp, 10, l.MaxConnections);
        return notes;
    }
}
