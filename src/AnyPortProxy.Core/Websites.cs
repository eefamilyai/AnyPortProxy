namespace AnyPortProxy.Core;

/// <summary>
/// Friendly view of the route list: one entry per address with an http and https destination.
/// Route rules for other ports (e.g. Minecraft on 25565) are kept untouched in <see cref="Other"/>.
/// </summary>
public sealed class Website
{
    public string Host { get; set; } = "";
    public string Computer { get; set; } = "";

    /// <summary>Port on the computer that http (80) visitors go to; null = http not routed.</summary>
    public int? HttpPort { get; set; } = 80;

    /// <summary>Port on the computer that https (443) visitors go to; null = https not routed.</summary>
    public int? HttpsPort { get; set; } = 443;

    public List<RouteRule> Other { get; set; } = new();

    public string HttpText => HttpPort is int p ? TargetParser.Format(Computer, p) : "—";
    public string HttpsText => HttpsPort is int p ? TargetParser.Format(Computer, p) : "—";

    public IEnumerable<RouteRule> ToRoutes()
    {
        if (HttpPort == 80 && HttpsPort == 443)
        {
            yield return new RouteRule { Host = Host, Target = TargetParser.Format(Computer, null) };
        }
        else
        {
            if (HttpPort is int hp)
                yield return new RouteRule { Host = Host, Port = 80, Target = TargetParser.Format(Computer, hp == 80 ? null : hp) };
            if (HttpsPort is int sp)
                yield return new RouteRule { Host = Host, Port = 443, Target = TargetParser.Format(Computer, sp == 443 ? null : sp) };
        }
        foreach (var r in Other) yield return r;
    }
}

public static class Websites
{
    private static string Norm(string h) => h.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>A website rule (ports 80/443, or "all web ports"); game addresses on other ports are separate.</summary>
    public static bool IsWebRule(RouteRule r) => r.Port is null or 80 or 443;

    public static List<Website> List(ProxyOptions p)
    {
        var result = new List<Website>();
        foreach (var group in p.Routes.Where(IsWebRule).GroupBy(r => Norm(r.Host)))
        {
            var rules = group.ToList();
            var general = rules.FirstOrDefault(r => r.Port is null);
            var r80 = rules.FirstOrDefault(r => r.Port == 80);
            var r443 = rules.FirstOrDefault(r => r.Port == 443);
            var main = r443 ?? r80 ?? general ?? rules[0];
            TargetParser.TryParse(main.Target, out var computer, out _);

            int? PortFor(RouteRule? specific, int incoming)
            {
                var rule = specific ?? general;
                if (rule is null) return null;
                TargetParser.TryParse(rule.Target, out _, out var port);
                return port ?? incoming;
            }

            result.Add(new Website
            {
                Host = rules[0].Host.Trim(),
                Computer = computer,
                HttpPort = PortFor(r80, 80),
                HttpsPort = PortFor(r443, 443),
            });
        }
        return result;
    }

    /// <summary>Adds or replaces the website (matched by originalHost, or its own host). Game addresses are left alone.</summary>
    public static void Upsert(ProxyOptions p, Website w, string? originalHost = null)
    {
        var key = Norm(originalHost ?? w.Host);
        int index = p.Routes.FindIndex(r => IsWebRule(r) && Norm(r.Host) == key);
        p.Routes.RemoveAll(r => IsWebRule(r) && (Norm(r.Host) == key || Norm(r.Host) == Norm(w.Host)));
        var newRules = w.ToRoutes().Where(IsWebRule).Select(r => { r.Host = w.Host.Trim(); return r; }).ToList();
        if (index < 0 || index > p.Routes.Count) p.Routes.AddRange(newRules);
        else p.Routes.InsertRange(index, newRules);
    }

    public static bool Remove(ProxyOptions p, string host) => p.Routes.RemoveAll(r => IsWebRule(r) && Norm(r.Host) == Norm(host)) > 0;

    /// <summary>"nas" → "nas.example.com" when a domain is set; strips http:// and trailing slashes.</summary>
    public static string Expand(string? domain, string text)
    {
        var h = text.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var prefix in new[] { "http://", "https://" })
            if (h.StartsWith(prefix)) h = h[prefix.Length..];
        h = h.TrimEnd('/');
        if (h.Length > 0 && !h.Contains('.') && h != "*" && !string.IsNullOrWhiteSpace(domain)) h = $"{h}.{domain.Trim().TrimEnd('.')}";
        return h;
    }

    public static string? ValidateHost(string host)
    {
        host = host.Trim().TrimEnd('.');
        if (host.Length == 0) return "Type the address people will visit, like nas.example.com";
        if (host.Contains("://")) return "Leave out http:// or https:// — just the name, like nas.example.com";
        if (host.Contains('/')) return "Just the name please, without any /path";
        if (host.Contains(':')) return "Leave out the :port — choose ports below instead";
        var check = host.StartsWith("*.") ? host[2..] : host == "*" ? "x" : host;
        if (!check.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            return "Addresses can only contain letters, numbers, dots and dashes";
        return null;
    }

    public static string? ValidateComputer(string computer)
    {
        computer = computer.Trim();
        if (computer.Length == 0) return "Type the IP address of the computer (like 192.168.1.20), or click \"This PC\"";
        if (computer.Contains("://") || computer.Contains('/')) return "Just the IP address or computer name, like 192.168.1.20";
        if (!TargetParser.TryParseValid(computer, out _, out var port)) return "That doesn't look like an IP address or computer name";
        if (port is not null) return "Put the port in the port boxes instead of here";
        return null;
    }
}
