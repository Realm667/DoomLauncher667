using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using Microsoft.Data.Sqlite;
using System.IO.Compression;

internal static class PerformanceTests
{
    public static async Task PrepareCollectionsAndArchiveAsync(string root, int count)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "DoomLauncher.sqlite")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Tags(Name,HasTab) VALUES('Eine lange deutsche Sammlung für den Leistungstest',1); INSERT INTO TagMapping(FileID,TagID) SELECT GameFileID,(SELECT MAX(TagID) FROM Tags) FROM GameFiles ORDER BY GameFileID LIMIT $count;";
        command.Parameters.AddWithValue("$count", Math.Min(count, 250)); await command.ExecuteNonQueryAsync();
        // A deterministic local WAD + title picture avoids network latency in
        // import/metadata timings. No commercial game data is included.
        using var archive = ZipFile.Open(Path.Combine(root, "UIInputs", "ui-alpha.zip"), ZipArchiveMode.Update);
        using (var output = archive.CreateEntry("TITLEPIC.png").Open())
        using (var bitmap = new System.Drawing.Bitmap(16, 16)) bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        using var writer = new BinaryWriter(archive.CreateEntry("fixture.wad").Open());
        var lumps = new[] { "MAP01", "THINGS", "LINEDEFS", "SIDEDEFS", "VERTEXES", "SEGS", "SSECTORS", "NODES", "SECTORS", "REJECT", "BLOCKMAP" };
        writer.Write(System.Text.Encoding.ASCII.GetBytes("PWAD")); writer.Write(lumps.Length); writer.Write(12);
        foreach (var lump in lumps) { writer.Write(12); writer.Write(0); writer.Write(System.Text.Encoding.ASCII.GetBytes(lump.PadRight(8, '\0'))); }
    }
    public static void VerifyReconciliation()
    {
        var items = Enumerable.Range(0, 11000).Select(_ => new object()).ToArray();
        var visible = new ReconciledCollection<object>();
        var notifications = 0;
        var resets = 0;
        visible.CollectionChanged += (_, args) => { notifications++; if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
        CollectionReconciler.Apply(visible, items);
        if (notifications > 1 || !visible.SequenceEqual(items))
            throw new InvalidOperationException($"Initial 11000-entry publication emits {notifications} notifications, expected one.");
        notifications = 0;
        CollectionReconciler.Apply(visible, items);
        if (notifications != 0) throw new InvalidOperationException("No-op reload changed the collection.");
        CollectionReconciler.Apply(visible, items.Reverse().ToArray());
        if (notifications != 1 || !visible.SequenceEqual(items.Reverse())) throw new InvalidOperationException("Large sort must retain references/order with one notification.");
        notifications = 0; resets = 0;
        CollectionReconciler.Apply(visible, items.Reverse().Skip(1).ToArray());
        if (notifications != 1 || resets != 0) throw new InvalidOperationException("Single removal must remain incremental.");
        var random = new Random(667);
        for (var run = 0; run < 25; run++)
        {
            var desired = items.Take(30).OrderBy(_ => random.Next()).Take(random.Next(31)).ToArray();
            CollectionReconciler.Apply(visible, desired);
            if (!visible.SequenceEqual(desired)) throw new InvalidOperationException("Reconciliation lost references or order.");
        }
        Console.WriteLine("PASS Large initial load, no-op reload, reverse sort and incremental removal retain references with bounded notifications");
    }
    private sealed class Locator(string path) : IDoomLauncherDatabaseLocator { public string FindDatabase() => path; }
    public static async Task RunAsync(string root)
    {
        if (!File.Exists(Path.Combine(root, ".ui-test-instance"))) throw new InvalidOperationException("Marked synthetic fixture required.");
        var catalog = new SqliteLibraryCatalog(new Locator(Path.Combine(root, "DoomLauncher.sqlite")), new UiLocalization());
        var library = new SqliteNativeLibraryService(new Locator(Path.Combine(root, "DoomLauncher.sqlite")));
        var results = new Dictionary<string, List<double>>();
        async Task Measure(string name, Func<Task> action)
        {
            var clock = Stopwatch.StartNew(); await action();
            if (!results.TryGetValue(name, out var values)) results[name] = values = [];
            values.Add(clock.Elapsed.TotalMilliseconds);
        }
        LibraryCatalogResult? loaded = null;
        await Measure("catalog-first", async () => loaded = await catalog.LoadAsync());
        for (var i = 0; i < 5; i++) await Measure("catalog-warm", async () => loaded = await catalog.LoadAsync());
        var all = loaded!.Entries.Select(_ => new object()).ToArray();
        var visible = new ReconciledCollection<object>();
        CollectionReconciler.Apply(visible, all.Take(1).ToArray());
        for (var i = 0; i < 5; i++)
        {
            CollectionReconciler.Apply(visible, all.Take(1).ToArray());
            await Measure("restore-full-list", () => { CollectionReconciler.Apply(visible, all); return Task.CompletedTask; });
            if (!visible.SequenceEqual(all)) throw new InvalidOperationException("Reconciliation changed entries/order.");
        }
        for (var i = 0; i < 5; i++)
        {
            var archive = Path.Combine(root, "UIInputs", $"service-{Guid.NewGuid():N}.zip");
            File.Copy(Path.Combine(root, "UIInputs", "ui-alpha.zip"), archive);
            NativeImportResult? imported = null;
            await Measure("import-archive", async () => imported = await library.ImportAsync(archive));
            await Measure("metadata-local-artwork", async () =>
            {
                await library.UpdateGameFromIdGamesAsync(imported!.GameFileId, new IdGamesItem
                { Id = 667, Title = "UI Alpha refreshed", Author = "Synthetic mapper", Description = "Deterministic metadata payload", Directory = "levels/doom2/", FileName = "ui-alpha.zip" });
                await library.TryImportTitlePicAsync(imported.GameFileId, imported.DestinationPath);
            });
        }
        var report = JsonSerializer.Serialize(new { Entries = all.Length, MeasurementsMs = results,
            MediansMs = results.ToDictionary(p => p.Key, p => p.Value.Order().ElementAt(p.Value.Count / 2)) }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(root, "service-performance.json"), report);
        Console.WriteLine(report);
    }
}
