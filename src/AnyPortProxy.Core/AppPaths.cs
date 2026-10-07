using System.Reflection;

namespace AnyPortProxy.Core;

public static class AppPaths
{
    public const string ServiceName = "AnyPortProxy";
    public const string DisplayName = "AnyPort Proxy";
    public const string CliExe = "AnyPortProxy.exe";
    public const string GuiExe = "AnyPortProxyGui.exe";

    /// <summary>Settings, logs and live status. Override with ANYPORTPROXY_DATA for testing.</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("ANYPORTPROXY_DATA") is { Length: > 0 } d
        ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AnyPortProxy");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string StatusFile => Path.Combine(DataDir, "status.json");

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AnyPortProxy");

    public static string AppDir => AppContext.BaseDirectory;

    public static string Version { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(AppPaths).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>
    /// GitHub "owner/repo" that updates come from, baked in at build time (build.ps1 reads it from the local git remote).
    /// Null when this copy was built without one.
    /// </summary>
    public static string? UpdateRepo { get; } =
        typeof(AppPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value is { } repo && Updater.IsValidRepo(repo) ? repo : null;

    public static bool SameDir(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder the installed service runs from, or this app's folder if not installed.</summary>
    public static string ServiceDir()
    {
        var image = ServiceManager.ImagePath();
        if (image is not null)
        {
            var exe = image.StartsWith('"') ? image[1..image.IndexOf('"', 1)] : image.Split(' ')[0];
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return AppDir;
    }
}
