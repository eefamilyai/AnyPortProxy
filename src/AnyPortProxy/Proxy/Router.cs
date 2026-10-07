using AnyPortProxy.Core;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy.Proxy;

public sealed record RouteDecision(string Host, int Port, string Rule);

/// <summary>
/// Hostname → destination lookup. Routes are compiled into hash tables whenever settings change,
/// so each connection costs one dictionary lookup (plus a short wildcard scan), with no allocations.
/// </summary>
public sealed class Router
{
    private readonly ILogger<Router> _log;
    private volatile RouteTable _table;

    public Router(ConfigMonitor config, ILogger<Router> log)
    {
        _log = log;
        _table = RouteTable.Build(config.Current);
        config.Changed += o =>
        {
            _table = RouteTable.Build(o);
            LogRoutes(o);
        };
    }

    public RouteDecision Resolve(string? hostname, int incomingPort) => _table.Resolve(hostname, incomingPort);

    public void LogRoutes(ProxyOptions o)
    {
        _log.LogInformation("Default destination: {Target}", o.DefaultTarget);
        foreach (var r in o.Routes)
            _log.LogInformation("Website {Host}{Port} -> {Target}", r.Host, r.Port is int p ? $" (port {p})" : "", r.Target);
    }

    private sealed class RouteTable
    {
        private readonly record struct Target(string Host, int? Port, string Rule);

        private sealed class Entry
        {
            public Target? Any;
            public Dictionary<int, Target>? ByPort;

            public bool TryPick(int port, out Target t)
            {
                if (ByPort is not null && ByPort.TryGetValue(port, out t)) return true;
                // Rules without a port are website rules: they cover 80/443 only, never game ports.
                if (Any is { } any && port is 80 or 443)
                {
                    t = any;
                    return true;
                }
                t = default;
                return false;
            }
        }

        private readonly Dictionary<string, Entry> _exact = new(StringComparer.Ordinal);
        private readonly List<(string Suffix, Entry Entry)> _wildcards = new();
        private Entry? _star;
        private Target _default;

        public static RouteTable Build(ProxyOptions o)
        {
            var t = new RouteTable();
            TargetParser.TryParse(o.DefaultTarget, out var dh, out var dp);
            t._default = new Target(dh, dp, "default");
            var wild = new Dictionary<string, Entry>(StringComparer.Ordinal);

            foreach (var r in o.Routes)
            {
                if (!TargetParser.TryParse(r.Target, out var host, out var port)) continue;
                var name = Normalize(r.Host);
                Entry entry;
                if (name == "*") entry = t._star ??= new Entry();
                else if (name.StartsWith("*.", StringComparison.Ordinal))
                {
                    var suffix = name[1..];
                    if (!wild.TryGetValue(suffix, out entry!)) wild[suffix] = entry = new Entry();
                }
                else if (!t._exact.TryGetValue(name, out entry!)) t._exact[name] = entry = new Entry();

                var target = new Target(host, port, r.Port is int rp ? $"{r.Host}@{rp}" : r.Host);
                if (r.Port is int p) (entry.ByPort ??= new())[p] = target;
                else entry.Any ??= target; // first rule wins, like before
            }
            // Longest suffix first = most specific wildcard wins.
            t._wildcards.AddRange(wild.OrderByDescending(kv => kv.Key.Length).Select(kv => (kv.Key, kv.Value)));
            return t;
        }

        public RouteDecision Resolve(string? hostname, int port)
        {
            if (!string.IsNullOrEmpty(hostname))
            {
                var name = Normalize(hostname);
                if (_exact.TryGetValue(name, out var e) && e.TryPick(port, out var t)) return Decide(t, port);
                foreach (var (suffix, entry) in _wildcards)
                {
                    if (name.EndsWith(suffix, StringComparison.Ordinal) && entry.TryPick(port, out t)) return Decide(t, port);
                }
            }
            // "*" means everything — including clients that don't send a hostname at all.
            if (_star is not null && _star.TryPick(port, out var any)) return Decide(any, port);
            return Decide(_default, port);
        }

        private static RouteDecision Decide(Target t, int incomingPort) => new(t.Host, t.Port ?? incomingPort, t.Rule);

        private static string Normalize(string s)
        {
            s = s.Trim().TrimEnd('.');
            foreach (char c in s)
                if (char.IsAsciiLetterUpper(c)) return s.ToLowerInvariant();
            return s;
        }
    }
}
