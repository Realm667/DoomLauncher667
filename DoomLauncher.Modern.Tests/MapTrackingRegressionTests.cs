using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DoomLauncher.WinUI.Services;

internal static class MapTrackingRegressionTests
{
    private sealed class Locator(string path) : IDoomLauncherDatabaseLocator { public string FindDatabase() => path; }
    private sealed record Result(string Scenario, string Log, string Token, bool Passed, string? Error);

    // Explicit opt-in; engines and the legal IWAD are never bundled with tests.
    // Six requested cases plus direct -loadgame, the launcher's resume path.
    internal static async Task RunAsync(string engine, string iwadArchive)
    {
        var root = Path.Combine(Path.GetTempPath(), "DL667-tracking-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Console.WriteLine("REGRESSION FIXTURE " + root);
        var iwad = Path.Combine(root, "doom2.wad");
        if (Path.GetExtension(iwadArchive).Equals(".wad", StringComparison.OrdinalIgnoreCase)) File.Copy(iwadArchive, iwad);
        else
        {
            using var archive = ZipFile.OpenRead(iwadArchive);
            archive.Entries.Single(entry => Path.GetFileName(entry.FullName).Equals("doom2.wad", StringComparison.OrdinalIgnoreCase)).ExtractToFile(iwad);
        }
        var results = new List<Result>(); string? previousSave = null;
        foreach (var scenario in new[] { "warp", "changemap", "load", "load-exit", "resume", "resume-exit", "direct-resume-exit" })
        {
            var folder = Path.Combine(root, scenario); Directory.CreateDirectory(folder);
            var context = await MapTrackingService.PrepareAsync(engine, folder, iwad, iwad, default)
                ?? throw new Exception("Engine API probe failed: " + engine);
            try
            {
                var resumed = scenario is "resume" or "resume-exit" or "direct-resume-exit";
                var exit = scenario.EndsWith("-exit", StringComparison.Ordinal);
                var save = Path.Combine(folder, "before.zds");
                if (resumed) File.Copy(previousSave ?? throw new Exception("Missing previous-session save."), save);
                var driver = Path.Combine(folder, "probe.pk3");
                using (var zip = ZipFile.Open(driver, ZipArchiveMode.Create))
                {
                    void Add(string name, string text)
                    { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
                    var noIntermission = scenario == "changemap" ? "nointermission" : "";
                    Add("MAPINFO", "gameinfo { AddEventHandlers=\"DL667RegressionProbe\" }\n"
                        + $"map MAP01 \"Regression 1\" {{ next=\"MAP02\" {noIntermission} }}\n"
                        + $"map MAP02 \"Regression 2\" {{ next=\"MAP03\" {noIntermission} }}\n"
                        + $"map MAP03 \"Regression 3\" {{ next=\"MAP04\" {noIntermission} }}\n");
                    Add("ZSCRIPT", """
                        version "4.0"
                        class DL667RegressionProbe : StaticEventHandler
                        {
                            bool LoadedSave; int AfterLoadTics;
                            override void WorldLoaded(WorldEvent e)
                            {
                                LoadedSave=e.IsSaveGame; AfterLoadTics=0;
                                Console.Printf("DL667_TEST|loaded|%s|save=%d|visited=%d", level.MapName, e.IsSaveGame, (level.info.flags & LEVEL_VISITED)!=0);
                            }
                            override void WorldUnloaded(WorldEvent e)
                            { Console.Printf("DL667_TEST|unloaded|%s|next=%s|save=%d", level.MapName, e.NextMap, e.IsSaveGame); }
                            override void WorldTick()
                            {
                        """ + (exit ? "if(LoadedSave && ++AfterLoadTics==20) { Console.Printf(\"DL667_TEST|real_exit_after_load|%s\",level.MapName); level.ExitLevel(0,false); }" : "") + "\n} }");
                }
                var config = Path.Combine(folder, "test.ini");
                await File.WriteAllTextAsync(config, "[LastRun]\nVersion=225\n[GlobalSettings]\nvid_fullscreen=false\ni_pauseinbackground=false\n");
                var commands = scenario switch
                {
                    "warp" => "wait 35; echo DL667_TEST_ACTION_MAP; map MAP02; wait 70; echo DL667_TEST_FINISH; quit",
                    "changemap" => "wait 35; echo DL667_TEST_ACTION_CHANGEMAP; changemap MAP03; wait 70; echo DL667_TEST_FINISH; quit",
                    "load" or "load-exit" => "wait 35; echo DL667_TEST_ACTION_SAVE; save before.zds; wait 35; echo DL667_TEST_ACTION_LOAD; load before.zds; wait 70; echo DL667_TEST_FINISH; quit",
                    "direct-resume-exit" => "wait 105; echo DL667_TEST_FINISH; quit",
                    _ => "wait 35; echo DL667_TEST_ACTION_LOAD; load before.zds; wait 70; echo DL667_TEST_FINISH; quit",
                };
                var script = Path.Combine(folder, "commands.cfg"); await File.WriteAllTextAsync(script, commands + "\n");
                var info = new ProcessStartInfo(engine) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var argument in new[] { "-iwad", iwad, "-noautoload", "-noautoexec", "-nosound", "-nomusic", "-nojoy", "-nomonsters", "-window", "-config", config,
                    "-savedir", folder, "-file", context.HelperPath, driver, "-exec", script, "+logfile", context.LogPath,
                    "+set", "dl667_track_session", context.SessionToken, "+i_pauseinbackground", "false", "+fullscreen", "false" }) info.ArgumentList.Add(argument);
                foreach (var argument in scenario == "direct-resume-exit" ? new[] { "-loadgame", SaveGameService.BuildLoadGameArgument(folder, save) } : new[] { "-warp", "1" }) info.ArgumentList.Add(argument);
                using (var process = Process.Start(info) ?? throw new Exception("Engine did not start."))
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
                    catch { if (!process.HasExited) process.Kill(true); throw; } // only this disposable fixture
                    if (process.ExitCode != 0) throw new Exception("Engine exit code " + process.ExitCode);
                }
                using var log = new StreamReader(new FileStream(context.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                var lines = (await log.ReadToEndAsync()).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
                void Require(bool condition, string label) { if (!condition) throw new Exception(label); }
                Require(!lines.Any(line => line.Contains("Script error", StringComparison.OrdinalIgnoreCase) || line.Contains("VM execution aborted", StringComparison.OrdinalIgnoreCase)), "Engine script error");
                Require(lines.Contains("DL667_TEST_FINISH"), "Test script did not finish");
                if (scenario == "warp") Require(lines.Contains("DL667_TEST_ACTION_MAP") && lines.Contains("DL667_TEST|loaded|MAP02|save=0|visited=0"), "Map warp not executed");
                if (scenario == "changemap") Require(lines.Contains("DL667_TEST_ACTION_CHANGEMAP") && lines.Contains("DL667_TEST|unloaded|MAP01|next=MAP03|save=0") && lines.Contains("DL667_TEST|loaded|MAP03|save=0|visited=0"), "Changemap not executed");
                if (scenario is not ("warp" or "changemap")) Require(lines.Contains("DL667_TEST|loaded|MAP01|save=1|visited=0") && File.Exists(save), "Save was not actually loaded");
                if (scenario is "load" or "load-exit") Require(lines.Contains("DL667_TEST_ACTION_SAVE"), "Save action missing");
                var completed = MapTrackingService.ParseCompletions(lines, context.SessionToken, context.Maps);
                var expected = new HashSet<string>(exit ? new[] { "MAP01" } : [], StringComparer.OrdinalIgnoreCase);
                Require(completed.SetEquals(expected), "Wrong current-session completion: " + string.Join(',', completed));
                Require(!lines.Any(line => line.StartsWith("DL667_TRACK_V1|") && line.Contains("|complete|") && !line.StartsWith("DL667_TRACK_V1|" + context.SessionToken + "|")), "Foreign-session completion");
                if (exit)
                {
                    var loadIndex = Array.IndexOf(lines, "DL667_TEST|loaded|MAP01|save=1|visited=0");
                    var exitIndex = Array.IndexOf(lines, "DL667_TEST|real_exit_after_load|MAP01");
                    var completeIndex = Array.FindIndex(lines, line => line.StartsWith("DL667_TRACK_V1|" + context.SessionToken + "|complete|MAP01"));
                    Require(loadIndex < exitIndex && exitIndex < completeIndex, "Completion precedes actual load/exit");
                }
                // Exercise the real importer, not just matching text in the log.
                var database = Path.Combine(folder, "DoomLauncher.sqlite"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.Empty.sqlite"), database);
                var locator = new Locator(database); var library = new SqliteNativeLibraryService(locator);
                var game = await library.ImportAsync(iwad); var store = new LibraryFeatureStore(locator);
                for (var count = 0; count < 2; count++)
                {
                    await MapTrackingService.ImportAsync(context, store, game.GameFileId);
                    var actual = (await store.LoadAsync()).GetValueOrDefault(game.GameFileId)?.CompletedMaps ?? new HashSet<string>();
                    Require(actual.SetEquals(expected), "Database import did not match engine evidence");
                }
                if (scenario == "load") previousSave = save;
                results.Add(new(scenario, context.LogPath, context.SessionToken, true, null));
                Console.WriteLine($"PASS {Path.GetFileName(engine)}/{scenario}: engine evidence + database import + repeat import");
            }
            catch (Exception error)
            {
                results.Add(new(scenario, context.LogPath, context.SessionToken, false, error.ToString()));
                Console.WriteLine($"FAIL {Path.GetFileName(engine)}/{scenario}: {error.Message}; {context.LogPath}");
            }
        }
        var report = Path.Combine(root, "report.json"); await File.WriteAllTextAsync(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("REPORT " + report);
        if (results.Any(result => !result.Passed)) throw new Exception("Tracking regression failed. " + report);
    }
}
