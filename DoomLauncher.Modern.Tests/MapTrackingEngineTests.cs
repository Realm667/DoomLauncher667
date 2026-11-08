using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DoomLauncher.WinUI.Services;

internal static class MapTrackingEngineTests
{
    // Explicit opt-in: uses an already installed engine and the user's own IWAD
    // only in a new isolated temporary folder. Neither is modified or published.
    internal static async Task RunAsync(string engine, string iwadArchive)
    {
        var root = Path.Combine(Path.GetTempPath(), "DL667-tracker-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Console.WriteLine("TRACKER FIXTURE " + root);
        var iwad = Path.Combine(root, "doom2.wad");
        if (Path.GetExtension(iwadArchive).Equals(".wad", StringComparison.OrdinalIgnoreCase)) File.Copy(iwadArchive, iwad);
        else
        {
            using var zip = ZipFile.OpenRead(iwadArchive);
            zip.Entries.Single(item => Path.GetFileName(item.FullName).Equals("doom2.wad", StringComparison.OrdinalIgnoreCase)).ExtractToFile(iwad);
        }
        if (!MapTrackingService.SupportsEngine(engine)) throw new Exception("Engine API probe failed.");
        foreach (var scenario in new[] { "normal", "secret", "hub", "finale", "nointermission" })
        {
            var folder = Path.Combine(root, scenario); Directory.CreateDirectory(folder);
            var context = await MapTrackingService.PrepareAsync(engine, folder, iwad, iwad, default) ?? throw new Exception("Tracker unavailable.");
            var driver = Path.Combine(folder, "fixture.pk3");
            using (var zip = ZipFile.Open(driver, ZipArchiveMode.Create))
            {
                void Add(string name, string content) { using var output = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); output.Write(content); }
                var mapInfo = "gameinfo { AddEventHandlers = \"DL667TestDriver\" }\nmap MAP01 \"Test\" { next=\"MAP02\" secretnext=\"MAP02\" cluster=1 "
                    + (scenario == "finale" ? "next=\"EndGameC\" " : "") + (scenario == "nointermission" ? "nointermission " : "")
                    + "}\nmap MAP02 \"Test 2\" { next=\"MAP03\" cluster=1 }\ncluster 1 { " + (scenario == "hub" ? "hub" : "") + " }";
                Add("MAPINFO", mapInfo);
                var exit = scenario == "secret" ? "level.SecretExitLevel(0);" : "level.ExitLevel(0,false);";
                var shouldExit = scenario is "normal" or "secret" or "hub" or "finale" or "nointermission";
                Add("ZSCRIPT", "version \"4.0\"\nclass DL667TestDriver : StaticEventHandler { override void WorldTick() { "
                    + (shouldExit ? "if(level.MapName == \"MAP01\" && level.maptime == 20) { Console.Printf(\"DL667_TEST_ACTION\"); " + exit + " }" : "") + " } }");
            }
            var info = new ProcessStartInfo(engine) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "-iwad", iwad, "-noautoload", "-nosound", "-nomusic", "-nojoy", "-window", "-width", "640", "-height", "480", "-config", Path.Combine(folder, "test.ini"),
                "-file", context.HelperPath, driver, "-warp", "1", "+logfile", context.LogPath, "+set", "dl667_track_session", context.SessionToken,
                "+vid_activeinbackground", "1", "+cl_capfps", "true" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new Exception("Test engine did not start.");
            try
            {
                {
                    for (var tick = 0; tick < 100 && !process.HasExited; tick++)
                    {
                        await Task.Delay(100);
                        if (File.Exists(context.LogPath))
                        {
                            using var input = new FileStream(context.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var reader = new StreamReader(input);
                            if ((await reader.ReadToEndAsync()).Contains("DL667_TEST_ACTION")) { await Task.Delay(1500); break; }
                        }
                    }
                    // Terminate only this disposable, save-free fixture process.
                    // This also verifies completion evidence survives an engine crash.
                    if (!process.HasExited) process.Kill(true);
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch (Exception error) { if (!process.HasExited) process.Kill(true); throw new Exception("Engine test failed: " + folder, error); }
            string[] lines = [];
            if (File.Exists(context.LogPath))
            {
                using var log = new StreamReader(new FileStream(context.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                lines = (await log.ReadToEndAsync()).Split('\n');
            }
            if (lines.Any(line => line.Contains("Script error", StringComparison.OrdinalIgnoreCase) || line.Contains("compilation failed", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Tracker script compilation failed: " + context.LogPath + "\n" + string.Join('\n', lines.TakeLast(24)));
            var completed = MapTrackingService.ParseCompletions(lines, context.SessionToken, context.Maps);
            if (!lines.Any(line => line.Contains("DL667_TEST_ACTION"))) throw new Exception("Engine test action did not run: " + context.LogPath);
            var expected = scenario is "normal" or "secret" or "finale" or "nointermission";
            if (!lines.Any(line => line.Contains("|" + context.SessionToken + "|ready")) || completed.Contains("MAP01") != expected)
                throw new Exception($"Tracker {scenario}: expected={expected}, maps={string.Join(',', completed)}. Log: {context.LogPath}");
            Console.WriteLine($"PASS Engine {Path.GetFileName(engine)} {scenario}: automatically validated={completed.Contains("MAP01")}");
        }
    }
}
