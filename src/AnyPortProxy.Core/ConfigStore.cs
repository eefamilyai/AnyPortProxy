using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AnyPortProxy.Core;

/// <summary>Reads and writes config.json. Keeps sections it doesn't know about (e.g. Logging).</summary>
public static class ConfigStore
{
    private const string Header =
        "// AnyPortProxy settings. Easiest way to change them: the AnyPortProxy app or the \"apx\" command.\n" +
        "// You can also edit this file by hand; the proxy picks up changes automatically when you save.\n";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig CreateDefault() => new()
    {
        Proxy = new ProxyOptions
        {
            DefaultTarget = "127.0.0.1",
            SniffPorts = [80, 443],
            CatchAll = new CatchAllOptions { BlockedPorts = [.. CatchAllOptions.DefaultBlocked] },
        },
    };

    public static void EnsureExists(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        if (File.Exists(path)) return;
        Save(CreateDefault(), path);
    }

    public static AppConfig Load(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        try
        {
            EnsureExists(path);
        }
        catch (UnauthorizedAccessException)
        {
            return CreateDefault(); // not admin and nothing installed yet: show defaults read-only
        }
        var text = File.ReadAllText(path);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: DocOptions) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The settings file has a typo and can't be read ({ex.Message}). File: {path}");
        }

        var cfg = new AppConfig
        {
            Raw = root,
            Domain = (string?)root["Domain"],
            Proxy = root["Proxy"]?.Deserialize<ProxyOptions>(Json) ?? CreateDefault().Proxy,
            Ports = root["Ports"]?.Deserialize<List<PortRule>>(Json) ?? new(),
        };
        return cfg;
    }

    public static void Save(AppConfig cfg, string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        var root = cfg.Raw ?? new JsonObject();
        root["Domain"] = string.IsNullOrWhiteSpace(cfg.Domain) ? null : cfg.Domain.Trim();
        root["Proxy"] = JsonSerializer.SerializeToNode(cfg.Proxy, Json);
        root["Ports"] = JsonSerializer.SerializeToNode(cfg.Ports, Json);
        root["Logging"] ??= new JsonObject
        {
            ["LogLevel"] = new JsonObject
            {
                ["Default"] = "Information",
                ["Microsoft"] = "Warning",
                ["Microsoft.Hosting.Lifetime"] = "Information",
            },
        };
        if (root["Domain"] is null) root.Remove("Domain");
        cfg.Raw = root;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = Header + root.ToJsonString(Json) + "\n";

        // The app and the "apx" command may save at the same moment: serialise writers across processes.
        Mutex? mutex = null;
        bool owned = false;
        try
        {
            mutex = new Mutex(false, @"Global\AnyPortProxyConfig");
            owned = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Can't lock (e.g. permissions); still safe thanks to the unique temp file + atomic rename.
        }

        try
        {
            var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true); // atomic, so the service never reads half a file
                    break;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(100); // e.g. antivirus briefly holding the file
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;
                }
            }
        }
        finally
        {
            if (owned) mutex!.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    public static string LogLevel(AppConfig cfg) =>
        (string?)cfg.Raw?["Logging"]?["LogLevel"]?["Default"] ?? "Information";

    public static void SetLogLevel(AppConfig cfg, string level)
    {
        cfg.Raw ??= new JsonObject();
        cfg.Raw["Logging"] ??= new JsonObject();
        cfg.Raw["Logging"]!["LogLevel"] ??= new JsonObject();
        cfg.Raw["Logging"]!["LogLevel"]!["Default"] = level;
    }
}
