using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnyPortProxy.Core;

public sealed class UpdateException(string message) : Exception(message);

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string AssetName, Uri AssetUrl, long Size, string? Sha256, Uri Page);

/// <summary>
/// Updates from the GitHub repository's releases: finds the newest release with an AnyPortProxySetup-*.exe,
/// downloads it, verifies it, and runs it silently (it keeps all settings).
/// </summary>
public static partial class Updater
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"AnyPortProxy-Updater/{AppPaths.Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoRegex();

    public static bool IsValidRepo(string repo) => RepoRegex().IsMatch(repo);

    /// <summary>Where updates come from (settings override, else the repo baked in at build time).</summary>
    public static string? Repo(AppConfig? c) =>
        c?.Updates?.Repo is { } r && IsValidRepo(r) ? r : AppPaths.UpdateRepo;

    public static Version Current => ParseVersion(AppPaths.Version) ?? new Version(0, 0, 0);

    public static Version? ParseVersion(string text)
    {
        text = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>The newest release if it's newer than this version; null when up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string repo, CancellationToken ct = default)
    {
        if (!IsValidRepo(repo)) throw new UpdateException($"\"{repo}\" isn't a GitHub repository name (owner/repo).");
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new UpdateException($"No published release found at github.com/{repo}. (The repository must be public, and have at least one release.)");
        if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
            throw new UpdateException("GitHub is limiting update checks right now. Try again in an hour.");
        if (!resp.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)resp.StatusCode}.");
        var info = Parse(await resp.Content.ReadAsStringAsync(ct));
        return info.Version > Current ? info : null;
    }

    /// <summary>Reads a GitHub "release" JSON document.</summary>
    public static UpdateInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag) ?? throw new UpdateException($"The latest release's tag \"{tag}\" isn't a version number (use tags like v1.5.0).");
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = new Uri(root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "https://github.com" : "https://github.com");

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("AnyPortProxySetup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var url = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "");
            if (url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The installer link doesn't point to github.com; refusing to download it.");
            string? sha = null;
            if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = digest[7..].ToLowerInvariant();
            return new UpdateInfo(version, tag, notes, name, url, asset.GetProperty("size").GetInt64(), sha, page);
        }
        throw new UpdateException($"Release {tag} has no AnyPortProxySetup-*.exe attached.");
    }

    /// <summary>Downloads and verifies the installer (size, SHA-256 when GitHub provides it, product name and version).</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AnyPortProxyUpdate");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(u.AssetName));
        var tmp = path + ".part";

        using (var resp = await Http.GetAsync(u.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.AppendData(buf, 0, n);
                total += n;
                if (u.Size > 0) progress?.Report(Math.Min(1.0, total / (double)u.Size));
            }
            if (u.Size > 0 && total != u.Size) throw new UpdateException($"The download was incomplete ({total:N0} of {u.Size:N0} bytes). Try again.");
            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (u.Sha256 is not null && actual != u.Sha256)
                throw new UpdateException("The downloaded installer doesn't match GitHub's checksum. It was not installed.");
        }

        var vi = FileVersionInfo.GetVersionInfo(tmp);
        if (vi.ProductName != "AnyPortProxy" || ParseVersion(vi.ProductVersion ?? "") != u.Version)
        {
            File.Delete(tmp);
            throw new UpdateException("The downloaded file isn't the AnyPortProxy installer it claims to be. It was not installed.");
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>Runs the installer silently; with <paramref name="reopenApp"/> it opens the app again when done.</summary>
    public static void RunInstaller(string path, bool reopenApp) =>
        Process.Start(new ProcessStartInfo(path, reopenApp ? "/S /launch" : "/S") { UseShellExecute = true });
}
