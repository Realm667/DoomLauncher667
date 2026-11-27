using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DoomLauncher.WinUI.Services;

internal sealed record LauncherRelease(string Version, Uri Download, string? Sha256, Uri? Checksum, string Page);
internal sealed record UpdateProgramFile(string Path, string Sha256);
internal sealed record PreparedLauncherUpdate(string Directory, string Manifest);

internal sealed class LauncherUpdateService(HttpClient client)
{
    internal const string Repository = "https://github.com/Realm667/DoomLauncher667";
    public static HttpClient CreateClient()
    {
        var result = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("DoomLauncher667/" + AppVersion.Current);
        result.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return result;
    }
    public async Task<LauncherRelease?> CheckAsync(string currentVersion, string? skippedVersion, CancellationToken token)
    {
        using var response = await client.GetAsync("https://api.github.com/repos/Realm667/DoomLauncher667/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsByteArrayAsync(token);
        if (content.Length > 2 * 1024 * 1024) throw new InvalidDataException("Release response is too large.");
        return ParseRelease(content, currentVersion, skippedVersion);
    }
    internal static LauncherRelease? ParseRelease(byte[] content, string currentVersion, string? skippedVersion)
    {
        using var document = JsonDocument.Parse(content); var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$") || !Version.TryParse(version, out var available)
            || !Version.TryParse(currentVersion.Split('-', '+')[0], out var current)
            || available < current || (available == current && !currentVersion.Contains('-')) || version == skippedVersion) return null;
        var name = $"DoomLauncher667-{version}-win-x64.zip";
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var matches = assets.Where(item => item.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) return null;
        var download = TrustedAsset(matches[0].GetProperty("browser_download_url").GetString()!);
        var digest = matches[0].TryGetProperty("digest", out var value) ? value.GetString() : null;
        var hash = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
        if (hash is not null && !Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("Invalid release checksum.");
        var checksums = assets.Where(item => item.GetProperty("name").GetString() == name + ".sha256").ToArray();
        var checksum = checksums.Length == 1 ? TrustedAsset(checksums[0].GetProperty("browser_download_url").GetString()!) : null;
        if (hash is null && checksum is null) return null;
        return new(version, download, hash, checksum, Repository + "/releases/tag/" + Uri.EscapeDataString(root.GetProperty("tag_name").GetString()!));
    }
    private static Uri TrustedAsset(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
            || !uri.AbsolutePath.StartsWith("/Realm667/DoomLauncher667/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected release download address.");
        return uri;
    }
    public static string StatePath(string portableRoot) => Path.Combine(portableRoot, "Data", "UserState", "launcher-update.json");
    public static async Task<string?> LoadSkippedAsync(string root, CancellationToken token)
    {
        try
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(StatePath(root), token));
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("SkippedVersion", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public static async Task SkipAsync(string root, string version, CancellationToken token)
    {
        var path = StatePath(root); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { SkippedVersion = version }), token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<PreparedLauncherUpdate> PrepareAsync(LauncherRelease release, string target, IProgress<double>? progress, CancellationToken token)
    {
        var userToken = token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10)); token = deadline.Token;
        var directory = Path.Combine(Path.GetTempPath(), "DoomLauncher-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var archive = Path.Combine(directory, "release.zip");
            using (var response = await client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                var buffer = new byte[131072]; long length = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    length += read; if (length > 512L * 1024 * 1024) throw new InvalidDataException("Update exceeds size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    if (response.Content.Headers.ContentLength is long total && total > 0) progress?.Report(Math.Min(65, length * 65d / total));
                }
            }
            var expected = release.Sha256;
            if (expected is null)
            {
                var text = await client.GetStringAsync(release.Checksum!, token);
                var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || parts[1].TrimStart('*') != $"DoomLauncher667-{release.Version}-win-x64.zip") throw new InvalidDataException("Invalid checksum file.");
                expected = parts[0];
            }
            await using (var input = File.OpenRead(archive))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update SHA-256 mismatch.");
            var package = Path.Combine(directory, "package");
            var files = await Task.Run(() => ExtractPackage(archive, package, release.Version, token), token);
            var manifest = Path.Combine(directory, "update.json");
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { Target = Path.GetFullPath(target), Package = package, Files = files }), token);
            await using var helper = typeof(LauncherUpdateService).Assembly.GetManifestResourceStream("DoomLauncher667.ApplyUpdate.ps1") ?? throw new FileNotFoundException("Update helper missing.");
            await using (var output = File.Create(Path.Combine(directory, "ApplyUpdate.ps1"))) await helper.CopyToAsync(output, token);
            progress?.Report(100); return new(directory, manifest);
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        { Directory.Delete(directory, true); throw new TimeoutException("The update download or verification timed out."); }
        catch { Directory.Delete(directory, true); throw; }
    }
    internal static IReadOnlyList<UpdateProgramFile> ExtractPackage(string archive, string package, string version, CancellationToken token)
    {
        Directory.CreateDirectory(package); using var zip = ZipFile.OpenRead(archive);
        var prefix = $"DoomLauncher667-{version}-win-x64/";
        var result = new List<UpdateProgramFile>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0, actualTotal = 0;
        var buffer = new byte[131072];
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested(); var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
            if (name.Length == 0 || name.EndsWith('/')) continue;
            if (!IsProgramPath(name) || !seen.Add(name)) throw new InvalidDataException("Unexpected file in release package: " + name);
            total += entry.Length; if (total > 2L * 1024 * 1024 * 1024 || result.Count >= 10000) throw new InvalidDataException("Expanded update exceeds size limit.");
            var path = Path.GetFullPath(Path.Combine(package, name));
            if (!path.StartsWith(Path.GetFullPath(package) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid update path.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open(); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long extracted = 0; int read;
            while ((read = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested(); extracted += read; actualTotal += read;
                if (actualTotal > 2L * 1024 * 1024 * 1024 || extracted > entry.Length) throw new InvalidDataException("Expanded update exceeds size limit.");
                output.Write(buffer, 0, read); hash.AppendData(buffer, 0, read);
            }
            if (extracted != entry.Length) throw new InvalidDataException("Incomplete update entry.");
            result.Add(new(name, Convert.ToHexString(hash.GetHashAndReset())));
        }
        if (!seen.Contains("DoomLauncher667.exe") || !seen.Contains("WinUI/DoomLauncher.WinUI.exe") || !seen.Contains("VERSION.txt")) throw new InvalidDataException("Incomplete portable release.");
        if (File.ReadAllText(Path.Combine(package, "VERSION.txt")).Trim() != version) throw new InvalidDataException("Package version mismatch.");
        return result;
    }
    internal static bool IsProgramPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':'))) return false;
        return parts.Length == 1
            ? new[] { "DoomLauncher667.exe", "DoomLauncher667-debug.cmd", "DoomLauncher667-reset.cmd", "VERSION.txt", "README.txt", "LICENSE", "THIRD-PARTY-NOTICES.md" }.Contains(path, StringComparer.OrdinalIgnoreCase)
            : new[] { "WinUI", "Licenses", "Documentation", "Tools" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase);
    }
    public static async Task StartHelperAsync(PreparedLauncherUpdate update, CancellationToken token)
    {
        var process = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(update.Directory, "ApplyUpdate.ps1"), "-Manifest", update.Manifest, "-ParentProcessId", Environment.ProcessId.ToString() }) process.ArgumentList.Add(argument);
        using var helper = Process.Start(process) ?? throw new IOException("Update helper could not start.");
        try
        {
            for (var index = 0; index < 600; index++)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(update.Directory, "helper-ready"))) return;
                if (helper.HasExited) throw new IOException("Update helper failed; launcher remains open. Details: " + update.Directory);
                await Task.Delay(100, token);
            }
            throw new IOException("Update helper did not become ready; launcher remains open.");
        }
        catch { if (!helper.HasExited) helper.Kill(); throw; }
    }
}
