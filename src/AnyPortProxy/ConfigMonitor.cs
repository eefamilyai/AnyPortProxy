using System.Text.Json;
using AnyPortProxy.Core;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy;

/// <summary>
/// Watches config.json and publishes validated settings. A typo, a half-written file or a locked file
/// never takes effect: the proxy keeps running on the last good settings and reports the problem.
/// </summary>
public sealed class ConfigMonitor : IDisposable
{
    private readonly ILogger<ConfigMonitor> _log;
    private readonly Timer _debounce;
    private readonly Timer _poll;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private volatile ProxyOptions _current;
    private volatile IReadOnlyList<PortRule> _ports = Array.Empty<PortRule>();
    private string _currentJson;
    private DateTime _lastWrite;

    public ConfigMonitor(ILogger<ConfigMonitor> log)
    {
        _log = log;
        try
        {
            ConfigStore.EnsureExists();
        }
        catch (Exception ex)
        {
            _log.LogError("Couldn't create the settings file: {Error}", ex.Message);
        }

        if (TryLoad(out var initial, out var ports, out var notes, out var error))
        {
            Notes = notes;
            _ports = ports;
        }
        else
        {
            initial = ConfigStore.CreateDefault().Proxy;
            ConfigSanitizer.Sanitize(initial);
            Error = error;
            _log.LogError("Settings file can't be read ({Error}). Starting with safe defaults until it's fixed.", error);
        }
        _current = initial;
        _currentJson = Fingerprint(initial, _ports);
        _lastWrite = LastWriteTime();
        foreach (var n in Notes) _log.LogWarning("Settings: {Note}", n);

        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _poll = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        StartWatcher();
    }

    public ProxyOptions Current => _current;

    /// <summary>Ports opened with the port helper (used for smart routing).</summary>
    public IReadOnlyList<PortRule> Ports => _ports;

    private static string Fingerprint(ProxyOptions p, IReadOnlyList<PortRule> ports) =>
        JsonSerializer.Serialize(p, ConfigStore.Json) + JsonSerializer.Serialize(ports, ConfigStore.Json);

    /// <summary>Why the settings file couldn't be used (null when fine).</summary>
    public string? Error { get; private set; }

    /// <summary>Invalid entries that were ignored or corrected.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = Array.Empty<string>();

    /// <summary>Raised (on a background thread) with the new settings after a valid change.</summary>
    public event Action<ProxyOptions>? Changed;

    private void StartWatcher()
    {
        try
        {
            _watcher?.Dispose();
            var w = new FileSystemWatcher(AppPaths.DataDir, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler kick = (_, _) => _debounce.Change(300, Timeout.Infinite);
            w.Changed += kick;
            w.Created += kick;
            w.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
            w.Error += (_, e) =>
            {
                _log.LogWarning("Settings watcher error ({Error}); restarting it", e.GetException().Message);
                StartWatcher();
                _debounce.Change(300, Timeout.Infinite);
            };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            // Polling still picks up changes.
            _log.LogWarning("Can't watch the settings file ({Error}); checking it every 15 seconds instead", ex.Message);
        }
    }

    private static DateTime LastWriteTime()
    {
        try { return File.GetLastWriteTimeUtc(AppPaths.ConfigFile); } catch { return default; }
    }

    // Safety net: file events can be missed (network drives, buffer overflows, sleeping PCs).
    private void Poll()
    {
        try
        {
            if (LastWriteTime() != _lastWrite) Reload();
        }
        catch (Exception ex)
        {
            _log.LogDebug("Settings poll failed: {Error}", ex.Message);
        }
    }

    private bool TryLoad(out ProxyOptions options, out IReadOnlyList<PortRule> ports, out List<string> notes, out string? error)
    {
        options = null!;
        ports = Array.Empty<PortRule>();
        notes = new();
        error = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(AppPaths.ConfigFile))
                {
                    // Editors sometimes delete + recreate when saving; never replace the user's settings with defaults.
                    if (attempt < 3)
                    {
                        Thread.Sleep(300);
                        continue;
                    }
                    error = "the settings file is missing (it's recreated as soon as you change any setting in the app or with apx)";
                    return false;
                }
                var cfg = ConfigStore.Load();
                options = cfg.Proxy;
                ports = cfg.Ports.Where(p => p is not null).ToList();
                notes = ConfigSanitizer.Sanitize(options);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
            {
                Thread.Sleep(150); // file being written / locked by an editor or antivirus
            }
            catch (Exception ex)
            {
                // A half-written file looks like a typo; give the writer a moment before reporting it.
                if (attempt < 3 && ex is InvalidDataException)
                {
                    Thread.Sleep(250);
                    continue;
                }
                error = ex.Message;
                return false;
            }
        }
    }

    private void Reload()
    {
        lock (_gate)
        {
            try
            {
                _lastWrite = LastWriteTime();
                if (!TryLoad(out var next, out var ports, out var notes, out var error))
                {
                    if (Error != error) _log.LogError("Settings file problem — still running on the last good settings: {Error}", error);
                    Error = error;
                    return;
                }
                if (Error is not null) _log.LogInformation("Settings file is readable again");
                Error = null;

                var json = Fingerprint(next, ports);
                if (json == _currentJson) return;
                foreach (var n in notes.Except(Notes)) _log.LogWarning("Settings: {Note}", n);
                Notes = notes;
                _ports = ports;
                _current = next;
                _currentJson = json;
                _log.LogInformation("Settings changed — applying");

                foreach (var handler in Changed?.GetInvocationList() ?? [])
                {
                    try
                    {
                        ((Action<ProxyOptions>)handler)(next);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Applying new settings failed in {Handler}", handler.Method.DeclaringType?.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Settings reload failed; keeping the previous settings");
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
        _poll.Dispose();
    }
}
