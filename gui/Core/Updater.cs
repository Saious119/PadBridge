using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace PadBridge.Gui.Core;

public sealed record UpdateInfo(
    Version Version,
    string TarballUrl,
    string TarballName,
    string? ChecksumUrl);

/// <summary>
/// Checks GitHub releases for a newer PadBridge, downloads the tarball
/// (verifying its checksum when the release publishes one) and runs the
/// bundled installer in update mode.
/// </summary>
public static class Updater
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Saious119/PadBridge/releases/latest";
    private const string AssetSuffix = "linux-x64.tar.gz";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API rejects requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PadBridge-updater");
        return c;
    }

    private static string CacheDir =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            "padbridge");

    /// <summary>Returns the newer release, or null when already up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestReleaseApi));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) ||
            !Version.TryParse(currentVersion, out var current) ||
            latest <= current)
            return null;

        string? url = null, name = null, checksum = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var assetName = asset.GetProperty("name").GetString() ?? "";
            var download = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (assetName.EndsWith(AssetSuffix)) { url = download; name = assetName; }
            if (assetName.EndsWith(AssetSuffix + ".sha256")) checksum = download;
        }
        return url == null ? null : new UpdateInfo(latest, url, name!, checksum);
    }

    /// <summary>Downloads and verifies the release tarball; returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update)
    {
        Directory.CreateDirectory(CacheDir);
        var path = Path.Combine(CacheDir, update.TarballName);

        var bytes = await Http.GetByteArrayAsync(update.TarballUrl);
        if (update.ChecksumUrl != null)
        {
            var expected = (await Http.GetStringAsync(update.ChecksumUrl))
                .Split(' ', '\t')[0].Trim();
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Downloaded file failed checksum verification; not installing.");
        }
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    /// <summary>Extracts the tarball and runs its installer in update mode.</summary>
    public static async Task InstallAsync(string tarballPath)
    {
        // Extract fully before touching the existing install, so a corrupt
        // archive can never leave a half-replaced installation behind.
        var stage = Path.Combine(CacheDir, "update-stage");
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(stage);

        await RunAsync("tar", $"xf \"{tarballPath}\" -C \"{stage}\"");

        var dir = Directory.GetDirectories(stage).FirstOrDefault()
                  ?? throw new InvalidOperationException("Archive contained no release directory.");
        await RunAsync("bash", $"\"{Path.Combine(dir, "install.sh")}\" --update");

        Directory.Delete(stage, recursive: true);
        File.Delete(tarballPath);
    }

    /// <summary>Replaces this process with the freshly installed app.</summary>
    public static void RestartApp()
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Cannot determine app path.");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        Environment.Exit(0);
    }

    private static async Task RunAsync(string cmd, string args)
    {
        var psi = new ProcessStartInfo(cmd, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Failed to start {cmd}.");
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"{cmd} failed (exit {p.ExitCode}): {stderr.Trim()}");
    }
}
