namespace AnyPortProxy.Core;

public static class TargetParser
{
    /// <summary>Parses "host", "host:port", "[v6]" or "[v6]:port".</summary>
    public static bool TryParse(string? value, out string host, out int? port)
    {
        value = value?.Trim() ?? "";
        host = value;
        port = null;
        if (value.Length == 0) return false;

        if (value[0] == '[')
        {
            int end = value.IndexOf(']');
            if (end < 0) return false;
            host = value[1..end];
            var rest = value[(end + 1)..];
            if (rest.Length == 0) return host.Length > 0;
            if (rest[0] != ':' || !TryPort(rest[1..], out var p6)) return false;
            port = p6;
            return host.Length > 0;
        }

        int colon = value.LastIndexOf(':');
        if (colon < 0) return true;
        if (value.IndexOf(':') != colon) return true; // bare IPv6 literal

        host = value[..colon];
        if (!TryPort(value[(colon + 1)..], out var p)) return false;
        port = p;
        return host.Length > 0;
    }

    public static string Format(string host, int? port)
    {
        var h = host.Contains(':') ? $"[{host}]" : host;
        return port is int p ? $"{h}:{p}" : h;
    }

    private static bool TryPort(string s, out int port) =>
        int.TryParse(s, out port) && port is >= 1 and <= 65535;
}

public static class PortRanges
{
    public static List<(int Lo, int Hi)> Parse(string spec)
    {
        var result = new List<(int, int)>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('-', StringSplitOptions.TrimEntries);
            if (bits.Length > 2 || !int.TryParse(bits[0], out int lo) || !int.TryParse(bits[^1], out int hi)
                || lo < 1 || hi > 65535 || lo > hi)
            {
                throw new FormatException($"'{part}' is not a valid port or range (examples: 8080, 3000-3999)");
            }
            result.Add((lo, hi));
        }
        if (result.Count == 0) throw new FormatException("No ports given");
        return result;
    }

    public static bool TryParse(string spec, out List<(int Lo, int Hi)> ranges, out string? error)
    {
        try
        {
            ranges = Parse(spec);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            ranges = new();
            error = ex.Message;
            return false;
        }
    }

    public static bool ContainsAll(string spec, int lo, int hi) =>
        TryParse(spec, out var r, out _) && Enumerable.Range(lo, hi - lo + 1).All(p => r.Any(x => p >= x.Lo && p <= x.Hi));

    /// <summary>Adds lo-hi to the spec and returns a merged, normalised spec.</summary>
    public static string Add(string spec, int lo, int hi)
    {
        TryParse(spec, out var ranges, out _);
        ranges.Add((lo, hi));
        var merged = new List<(int Lo, int Hi)>();
        foreach (var r in ranges.OrderBy(r => r.Lo))
        {
            if (merged.Count > 0 && r.Lo <= merged[^1].Hi + 1)
                merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, r.Hi));
            else
                merged.Add(r);
        }
        return string.Join(", ", merged.Select(r => r.Lo == r.Hi ? $"{r.Lo}" : $"{r.Lo}-{r.Hi}"));
    }

    /// <summary>Parses "25565" or "2456-2458".</summary>
    public static bool TryParseOne(string text, out int lo, out int hi)
    {
        lo = hi = 0;
        var bits = text.Trim().Split('-', StringSplitOptions.TrimEntries);
        if (bits.Length > 2 || !int.TryParse(bits[0], out lo)) return false;
        hi = lo;
        if (bits.Length == 2 && !int.TryParse(bits[1], out hi)) return false;
        return lo is >= 1 and <= 65535 && hi >= lo && hi <= 65535;
    }
}
