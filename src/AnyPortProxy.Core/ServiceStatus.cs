using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnyPortProxy.Core;

/// <summary>Live state the service writes to status.json every couple of seconds.</summary>
public sealed class ServiceStatus
{
    public DateTime UpdatedUtc { get; set; }
    public DateTime StartedUtc { get; set; }
    public int Pid { get; set; }
    public string Version { get; set; } = "";
    public List<ListenerStatus> SniffPorts { get; set; } = new();
    public CatchAllStatus CatchAll { get; set; } = new();
    public long ActiveConnections { get; set; }
    public long TotalConnections { get; set; }
    public long FailedConnections { get; set; }

    /// <summary>Connections refused by the flood limits.</summary>
    public long RejectedConnections { get; set; }

    public long TotalBytes { get; set; }
    public double BytesPerSec { get; set; }
    public double ConnectionsPerSec { get; set; }
    public double PacketsPerSec { get; set; }

    /// <summary>Probes to ports where nothing runs, ignored without using any resources (smart routing).</summary>
    public long IgnoredProbes { get; set; }

    /// <summary>Connections handed straight to an app (smart routing), bypassing the proxy.</summary>
    public long DirectConnections { get; set; }

    /// <summary>Set when config.json can't be read; the proxy keeps running on the last good settings.</summary>
    public string? ConfigError { get; set; }

    /// <summary>Settings that were ignored or corrected because they were invalid.</summary>
    public List<string> ConfigNotes { get; set; } = new();

    /// <summary>Most recent things the service repaired by itself (firewall rules, router forwards…).</summary>
    public List<string> Repairs { get; set; } = new();

    [JsonIgnore] public bool IsFresh => DateTime.UtcNow - UpdatedUtc < TimeSpan.FromSeconds(10);

    public IEnumerable<string> Problems()
    {
        if (ConfigError is not null)
            yield return $"Settings file problem (still running on the last good settings): {ConfigError}";
        foreach (var l in SniffPorts.Where(l => !l.Listening))
            yield return $"Port {l.Port} couldn't be opened: {l.Error}";
        if (CatchAll.State == "Error")
            yield return $"All-ports forwarding failed: {CatchAll.Message}";
    }

    public static string FormatRate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1_000_000_000 => $"{bytesPerSec / 1_000_000_000:0.0} GB/s",
        >= 1_000_000 => $"{bytesPerSec / 1_000_000:0.0} MB/s",
        >= 1_000 => $"{bytesPerSec / 1_000:0} KB/s",
        _ => $"{bytesPerSec:0} B/s",
    };

    public static ServiceStatus? Read()
    {
        try
        {
            using var fs = new FileStream(AppPaths.StatusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ServiceStatus>(fs, ConfigStore.Json);
        }
        catch
        {
            return null;
        }
    }

    public void Write()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var tmp = AppPaths.StatusFile + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, ConfigStore.Json));
        File.Move(tmp, AppPaths.StatusFile, overwrite: true);
    }
}

public sealed class ListenerStatus
{
    public int Port { get; set; }
    public bool Listening { get; set; }
    public string? Error { get; set; }
}

public sealed class CatchAllStatus
{
    /// <summary>Off, Running or Error.</summary>
    public string State { get; set; } = "Off";
    public string? Message { get; set; }
    public int Flows { get; set; }
}

public static class LogReader
{
    public static string? CurrentFile() =>
        Directory.Exists(AppPaths.LogDir)
            ? Directory.GetFiles(AppPaths.LogDir, "anyportproxy-*.log").OrderBy(f => f).LastOrDefault()
            : null;

    public static List<string> Tail(int lines)
    {
        var file = CurrentFile();
        if (file is null) return new();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var q = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            q.Enqueue(line);
            if (q.Count > lines) q.Dequeue();
        }
        return q.ToList();
    }

    /// <summary>Returns lines appended since the last call (follows daily file rollover).</summary>
    public sealed class Follower
    {
        private string? _file;
        private long _pos;

        public Follower(bool fromEnd = true)
        {
            _file = CurrentFile();
            if (fromEnd && _file is not null) _pos = new FileInfo(_file).Length;
        }

        public List<string> ReadNew()
        {
            var result = new List<string>();
            var current = CurrentFile();
            if (current is null) return result;
            if (current != _file)
            {
                _file = current;
                _pos = 0;
            }
            try
            {
                using var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _pos) _pos = 0;
                fs.Seek(_pos, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                var text = reader.ReadToEnd();
                int lastNewline = text.LastIndexOf('\n');
                if (lastNewline < 0) return result;
                _pos += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNewline + 1));
                result.AddRange(text[..lastNewline].Split('\n').Select(l => l.TrimEnd('\r')));
            }
            catch (IOException)
            {
            }
            return result;
        }
    }
}
