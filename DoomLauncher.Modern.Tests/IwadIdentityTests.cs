using System.IO.Compression;
using System.Text;
using DoomLauncher.WinUI.Services;

internal static class IwadIdentityTests
{
    public static async Task VerifyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DL667-identities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Require(!MapTrackingService.IsUnsupportedLogProtocol("UZDoom", "5.0.0-rc.1")
                && MapTrackingService.IsUnsupportedLogProtocol("UZDoom", "5.0.3")
                && MapTrackingService.IsUnsupportedLogProtocol("UZDoom", "5.1.0")
                && !MapTrackingService.IsUnsupportedLogProtocol("GZDoom", "4.14.2"), "Unvalidated UZDoom log protocols must not enable automatic progress.");
            await IwadVersionDetector.EnsureLoadedAsync();
            using var hashes = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "iwad-hashes.json")));
            var legacyMd5 = new[] { "54978d12de87f162b9bcc011676cb3c0", "11e1cd216801ea2657723abc86ecb01f", "740901119ba2953e3c7f3764eca6e128", "dae9b1eea1a8e090fdfa5707187f4a43", "b6afa12a8b22e2726a8ff5bd249223de", "9c877480b8ef33b7074f1f0c07ed6487", "049e32f18d9c9529630366cfc72726ea", "981b03e6d1dc033301aa3095acc437ce", "792fd1fea023d61210857089a7c1e351", "c4fe9fd920207691a9f493668e0a2083", "1cd63c5ddff1bf8ce844237f580e9cf3", "fb35c4a5a9fd49ec29ab6e900572c524", "e4f120eab6fb410a5b6e11c947832357", "0c8758f102ccafe26a3040bee8ba5021", "f0cefca49926d00903cf57551d901abe", "25e1459ca71d321525f84628f45ca8cd", "c3bea40570c23e511a7ed3ebcd9865f7", "4e158d9953c79ccf97bd0663244cc6b6", "75c8cf89566741fa9d22447604053bd7", "023b52175d2f260c3bdc5528df5d0a8c", "ae779722390ec32fa37b0d361f7d82f8", "3117e399cdb4298eaa3941625f4b2923", "1e4cb4ef075ad344dd63971637307e04", "66d686b1ed6d35ff103f15dbd30e0341", "876a5a44c7b68f04b3bb9bc7a5bd69d6", "b2543a03521365261d0a0f74d5dd90f0", "abb033caf81e26f12a2103e1fa25453f", "1077432e2690d390c256ac908b5f4efa", "78d5898e99e220e4de64edaa0e479593", "2fed2031a5b03892106e0f117f17901f", "25485721882b050afa96a56e5758dd52" };
            Require(legacyMd5.All(md5 => hashes.RootElement.EnumerateArray().Any(e => e.GetProperty("md5").GetString() == md5)), "All 31 previously supported content identities must survive catalog expansion.");
            Require(hashes.RootElement.EnumerateArray().Where(e => e.TryGetProperty("sha1",out _)).All(e => System.Text.RegularExpressions.Regex.IsMatch(e.GetProperty("sha1").GetString()!, "^[a-f0-9]{40}$")), "Catalog SHA-1 fingerprints must be well formed.");
            var registered = IwadVersionDetector.IdentifyHash("", "7742089b4468a736cadb659a7deca3320fe6dcbd", 11159840);
            var ultimate = IwadVersionDetector.IdentifyHash("", "9b07b02ab3c275a6a7570c3f73cc20d63a0e3833", 12408292);
            Require(registered == ("Doom Registered", "1.9") && ultimate == ("The Ultimate Doom", "1.9"), "Identical doom.wad names must retain their exact edition and common 1.9 version.");
            var alias = new NativeIwadDefinition(1, "My Doom", "base.zip", "DOOM.WAD", "1.9", "1cd63c5ddff1bf8ce844237f580e9cf3");
            Require(alias.Name == "My Doom" && alias.DisplayLabel == "My Doom · Doom Registered 1.9", "Detected labels must preserve custom aliases while disambiguating the edition.");
            using var identities = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "iwad-identities.json")));
            var rules = identities.RootElement.GetProperty("rules");
            Require(rules.GetArrayLength() == 45 && identities.RootElement.GetProperty("names").EnumerateArray().Any(n => n.GetString() == "square.pk3"), "Both UZDoom's Names and IWad blocks must be represented, including Square's extra alias.");
            foreach (var rule in rules.EnumerateArray())
            {
                var bytes = Wad(rule.GetProperty("mustContain").EnumerateArray().Select(n => n.GetString()!).ToArray()); bytes[0] = (byte)'I';
                var fixture = Path.Combine(root, "rule.wad"); await File.WriteAllBytesAsync(fixture, bytes);
                var candidate = (await IwadVersionDetector.ScanArchiveAsync(fixture, default)).Single();
                Require(candidate.SuggestedName == rule.GetProperty("edition").GetString() && candidate.Version == "", "Ordered UZDoom rule failed: " + rule.GetProperty("edition").GetString());
            }
            var package = Path.Combine(root, "renamed.ipk3");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("SQU-IWAD").Open())) writer.Write("marker");
                using (var writer = new StreamWriter(zip.CreateEntry("maps/E1A1.wad").Open())) writer.Write("map");
            }
            var identified = await IwadVersionDetector.ScanArchiveAsync(package, default);
            Require(identified.Count == 1 && identified[0].SuggestedName == "The Adventures of Square"
                && identified[0].Version == "", "Renamed IPK3 must identify its edition from contents without inventing a version.");
            var renamed = Path.Combine(root, "doom.wad");
            await File.WriteAllBytesAsync(renamed, Wad("E1M1", "CYCLA1", "FLMBA1", "MAPINFO"));
            var chex = await IwadVersionDetector.ScanArchiveAsync(renamed, default);
            Require(chex.Count == 1 && chex[0].SuggestedName.Contains("Chex"), "A standalone PWAD must be recognized by its specific content, not its misleading filename.");
            await File.WriteAllBytesAsync(renamed, Wad("MAP01"));
            Require((await IwadVersionDetector.ScanArchiveAsync(renamed, default)).Count == 0,
                "An ordinary MAP01 patch must not become a Doom II IWAD.");
            var database = Path.Combine(root, "DoomLauncher.sqlite");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.Empty.sqlite"), database);
            var locator = new Locator(database); var library = new SqliteNativeLibraryService(locator);
            await new FirstSetupService(locator, library).EnsureManagedLayoutAsync();
            var importedPackage = await library.ImportIwadAsync(package);
            using (var wrapper = ZipFile.OpenRead(importedPackage.DestinationPath))
            {
                Require(wrapper.Entries.Count == 1 && wrapper.Entries[0].Name == "renamed.ipk3", "Import must wrap an IPK3 intact, not flatten its contents.");
                using var bytes = new MemoryStream(); using var input = wrapper.Entries[0].Open(); input.CopyTo(bytes);
                var original = await File.ReadAllBytesAsync(package);
                Require(bytes.ToArray().SequenceEqual(original), "Original package bytes must survive import.");
            }
            var sve = Path.Combine(root, "renamed-veteran.wad");
            await File.WriteAllBytesAsync(sve, Wad("MAP35", "I_RELB", "FXAA_F"));
            try { await library.ImportIwadAsync(sve); throw new Exception("Veteran component accepted without Strife base."); }
            catch (InvalidOperationException) { }
            Require((await library.LoadLauncherDefinitionsAsync()).Iwads.Count == 1, "Missing dependency must roll back all component rows.");
            var strife = Path.Combine(root, "my-base.wad"); var baseBytes = Wad("MAP01", "MAP33", "ENDSTRF");
            await File.WriteAllBytesAsync(strife, baseBytes);
            await library.ImportIwadAsync(strife);
            var component = await library.ImportIwadAsync(sve);
            var portDirectory = Path.Combine(root, "port"); Directory.CreateDirectory(portDirectory);
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(portDirectory, "TestPort.exe"));
            await library.SaveSourcePortAsync(new(null, "Test", portDirectory, "TestPort.exe", ".wad,.pk3,.ipk3", "-file", ""));
            var definitions = await library.LoadLauncherDefinitionsAsync();
            var componentDefinition = definitions.Iwads.Single(i => i.ArchiveFileName == component.FileName);
            var launcher = new NativeGameLaunchService(locator, new CapturingStarter(info =>
            {
                var arguments = info.ArgumentList.ToList();
                var basePath = arguments[arguments.IndexOf("-iwad") + 1];
                Require(File.ReadAllBytes(basePath).SequenceEqual(baseBytes), "Renamed Strife base must be used as -iwad for SVE.");
                var fileIndex = arguments.IndexOf("-file");
                Require(fileIndex >= 0 && arguments.Skip(fileIndex + 1).Any(p => File.Exists(p) && File.ReadAllBytes(p).SequenceEqual(File.ReadAllBytes(sve))), "SVE must remain a separate -file dependency.");
            }));
            try
            {
                await launcher.LaunchAsync(new DoomLauncher.Modern.Core.Launch.GameLaunchRequest(component.GameFileId, "SVE", SourcePortId: definitions.SourcePorts.Single().SourcePortId, IwadId: componentDefinition.IwadId));
                throw new Exception("Launch capture did not run.");
            }
            catch (CapturedLaunch) { }
            Console.WriteLine("PASS 45 ordered UZDoom identities, exact edition/version and aliases; renamed IPK3 and standalone PWAD identity; ordinary map patch excluded; unknown version remains blank");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    internal static byte[] Wad(params string[] names)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(Encoding.ASCII.GetBytes("PWAD")); writer.Write(names.Length); writer.Write(12);
        foreach (var name in names)
        {
            writer.Write(12); writer.Write(0);
            writer.Write(Encoding.ASCII.GetBytes(name.PadRight(8, '\0')));
        }
        return memory.ToArray();
    }
    private sealed class CapturedLaunch : Exception { }
    private sealed class CapturingStarter(Action<System.Diagnostics.ProcessStartInfo> verify) : IProcessStarter
    {
        public IGameLaunchSession Start(System.Diagnostics.ProcessStartInfo info) { verify(info); throw new CapturedLaunch(); }
    }
    private sealed class Locator(string database) : IDoomLauncherDatabaseLocator { public string FindDatabase() => database; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
