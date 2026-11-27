using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoomLauncher.WinUI.Services;

internal static class LauncherUpdateTests
{
    public static async Task VerifyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DL667-update-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var payload = new Dictionary<string, string> { ["DoomLauncher667.exe"] = "new bootstrap", ["WinUI/new.dll"] = "new runtime", ["WinUI/DoomLauncher.WinUI.exe"] = "new app", ["VERSION.txt"] = "Doom Launcher 667 1.1.0\nPlatform: Windows x64\nSource revision: test\nSource: https://github.com/Realm667/DoomLauncher667/tree/v1.1.0\n" };
            var zipPath = Path.Combine(root, "release.zip");
            MakeZip(zipPath, payload, "DoomLauncher667-1.1.0-win-x64/");
            var zipBytes = await File.ReadAllBytesAsync(zipPath); var hash = Convert.ToHexString(SHA256.HashData(zipBytes));
            var response = Release("v1.1.0", hash);
            Require(LauncherUpdateService.ParseRelease(response, "1.0.0", null)?.Version == "1.1.0", "New stable releases must be offered.");
            Require(LauncherUpdateService.ParseRelease(response, "1.1.0", null) is null && LauncherUpdateService.ParseRelease(response, "1.2.0", null) is null, "Never offer equal versions or downgrades.");
            Require(LauncherUpdateService.ParseRelease(response, "1.1.0-rc.1", null) is not null, "A stable release supersedes its prerelease.");
            Require(LauncherUpdateService.ParseRelease(response, "1.0.0", "1.1.0") is null, "Skip hides only the chosen version.");
            Require(LauncherUpdateService.ParseRelease(Release("v1.2.0", hash), "1.0.0", "1.1.0") is not null, "A newer release is still offered after skipping an older one.");
            Require(LauncherUpdateService.ParseRelease(Release("v1.1.0", hash, prerelease: true), "1.0.0", null) is null, "Prereleases are not offered on the stable channel.");
            Require(LauncherUpdateService.ParseRelease(Release("v1.1.0", hash, draft: true), "1.0.0", null) is null, "Drafts are not offered.");
            foreach (var path in new[] { "DoomLauncher.sqlite", "Data/Mods/a.zip", "Data/SaveGames/save.zds", "../DoomLauncher667.exe", "WinUI/../../Data/save", "WinUI/evil:stream" })
                Require(!LauncherUpdateService.IsProgramPath(path), "User data and traversal must be rejected: " + path);
            await LauncherUpdateService.SkipAsync(root, "1.1.0", default);
            Require(await LauncherUpdateService.LoadSkippedAsync(root, default) == "1.1.0", "Skipped version survives a restart.");
            await File.WriteAllTextAsync(LauncherUpdateService.StatePath(root), "{\"SkippedVersion\":123}");
            Require(await LauncherUpdateService.LoadSkippedAsync(root, default) is null, "Corrupt skip settings must not disable update checks.");
            using var client = new HttpClient(new FakeHttp(response, zipBytes));
            var service = new LauncherUpdateService(client);
            var release = await service.CheckAsync("1.0.0", null, default) ?? throw new Exception("Missing synthetic update.");
            var target = MakeTarget("success");
            var prepared = await service.PrepareAsync(release, target, null, default);
            try
            {
                Require(await RunHelper(prepared.Manifest, prepared.Directory, -1) == 0, "Successful update helper failed.");
                Require(File.ReadAllText(Path.Combine(target, "WinUI", "DoomLauncher.WinUI.exe")) == "new app", "New app was not installed.");
                CheckUserData(target);
            }
            finally { Directory.Delete(prepared.Directory, true); }
            var rollbackTarget = MakeTarget("rollback");
            var rollback = await service.PrepareAsync(release, rollbackTarget, null, default);
            try
            {
                Require(await RunHelper(rollback.Manifest, rollback.Directory, 2) != 0, "Injected failure did not fail.");
                Require(File.ReadAllText(Path.Combine(rollbackTarget, "DoomLauncher667.exe")) == "old bootstrap", "Rollback did not restore the old bootstrap.");
                Require(!File.Exists(Path.Combine(rollbackTarget, "WinUI", "new.dll")), "Rollback left a newly introduced file.");
                CheckUserData(rollbackTarget);
            }
            finally { Directory.Delete(rollback.Directory, true); }
            try { await service.PrepareAsync(release with { Sha256 = new string('0', 64) }, target, null, default); throw new Exception("Corrupt checksum accepted."); }
            catch (InvalidDataException) { }
            var malicious = Path.Combine(root, "malicious.zip"); MakeZip(malicious, payload.Concat(new[] { new KeyValuePair<string, string>("Data/Mods/overwrite.zip", "bad") }).ToDictionary(), "");
            try { LauncherUpdateService.ExtractPackage(malicious, Path.Combine(root, "rejected"), "1.1.0", default); throw new Exception("Package containing user data accepted."); }
            catch (InvalidDataException) { }
            Console.WriteLine("PASS GitHub update comparison, exact-version skip persistence, verified package download, data-preserving overlay and rollback");

            string MakeTarget(string name)
            {
                var targetRoot = Path.Combine(root, name); Directory.CreateDirectory(Path.Combine(targetRoot, "WinUI"));
                File.WriteAllText(Path.Combine(targetRoot, "DoomLauncher667.exe"), "old bootstrap");
                File.WriteAllText(Path.Combine(targetRoot, "WinUI", "DoomLauncher.WinUI.exe"), "old app");
                File.WriteAllText(Path.Combine(targetRoot, ".dl667-update-test"), "isolated test");
                foreach (var path in UserDataPaths) { var file = Path.Combine(targetRoot, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "USER DATA " + path); }
                return targetRoot;
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static readonly string[] UserDataPaths = ["DoomLauncher.sqlite", "Data/Mods/game.zip", "Data/SaveGames/save.zds", "Data/TileImages/custom.png", "Data/UserState/library.json"];
    private static void CheckUserData(string root) { foreach (var path in UserDataPaths) Require(File.ReadAllText(Path.Combine(root, path)) == "USER DATA " + path, "Update modified user data: " + path); }
    private static void MakeZip(string path, IDictionary<string, string> files, string prefix)
    { using var zip = ZipFile.Open(path, ZipArchiveMode.Create); foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(prefix + file.Key).Open()); writer.Write(file.Value); } }
    private static byte[] Release(string tag, string hash, bool prerelease = false, bool draft = false) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        tag_name = tag, prerelease, draft, assets = new[] { new { name = $"DoomLauncher667-{tag.TrimStart('v')}-win-x64.zip", digest = "sha256:" + hash,
            browser_download_url = LauncherUpdateService.Repository + $"/releases/download/{tag}/DoomLauncher667-{tag.TrimStart('v')}-win-x64.zip" } }
    });
    private static async Task<int> RunHelper(string manifest, string directory, int failAfter)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(directory, "ApplyUpdate.ps1"), "-Manifest", manifest, "-ParentProcessId", "0", "-TestMode", "-TestFailAfter", failAfter.ToString() }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(); throw; }
        var message = await error + await output;
        if (process.ExitCode != 0 && failAfter < 0)
        {
            Console.WriteLine(message);
            var result = Path.Combine(directory, "update-result.json");
            if (File.Exists(result)) Console.WriteLine(await File.ReadAllTextAsync(result));
        }
        return process.ExitCode;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FakeHttp(byte[] release, byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.Host == "api.github.com" ? release : archive) }); }
    }
}
