using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using DoomLauncher.WinUI.Services;
using Microsoft.Data.Sqlite;

internal static class ReliabilityTests
{
    private sealed class Locator(string database) : IDoomLauncherDatabaseLocator { public string FindDatabase() => database; }
    private sealed class Progress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private static void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
    private static async Task RejectAsync(Func<Task> action, string name)
    { try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { Check(true, name); return; } throw new InvalidOperationException(name); }

    public static async Task PrepareUiAsync(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("UI fixture target must be empty.");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "DoomLauncher.sqlite");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.Empty.sqlite"), database);
        var locator = new Locator(database); var library = new SqliteNativeLibraryService(locator);
        var setup = new FirstSetupService(locator, library);
        await setup.EnsureManagedLayoutAsync(); await setup.CompleteWizardAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync();
        for (var index = 0; index < 65; index++)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO GameFiles(FileName,Title,Author,Downloaded,MinutesPlayed,IsSyncNeeded) VALUES($file,$title,'UI test','2020-01-01',0,1);";
            insert.Parameters.AddWithValue("$file", $"Mods/seed-{index}.zip"); insert.Parameters.AddWithValue("$title", $"Seed {index:000}");
            await insert.ExecuteNonQueryAsync();
        }
        var inputs = Path.Combine(root, "UIInputs"); Directory.CreateDirectory(inputs);
        MakeArchive(inputs, "ui-alpha", "UI Alpha"); MakeArchive(inputs, "ui-beta", "UI Beta");
        await File.WriteAllTextAsync(Path.Combine(root, ".ui-test-instance"), "isolated-ui-fixture");
        Console.WriteLine("UI fixture ready: " + root);
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DoomLauncher reliability " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await VerifyRefreshAsync();
            VerifyCollection();
            await VerifySettingsAsync(root);
            await VerifyLeaseAsync(root);
            await VerifyBackupsAndDuplicatesAsync(root);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task VerifyRefreshAsync()
    {
        var coordinator = new RefreshCoordinator(); var count = 0; var last = 0;
        var started = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        var running = coordinator.RequestAsync(async () => { count++; started.SetResult(); await resume.Task; return true; });
        await started.Task;
        var waiters = Enumerable.Range(1, 10).Select(index => coordinator.RequestAsync(() =>
            { count++; last = index; return Task.FromResult(true); })).ToArray();
        resume.SetResult(); await Task.WhenAll(waiters.Append(running));
        Check(count == 2 && last == 10, "Concurrent refresh requests coalesce and replay the newest request");
        await RejectAsync(() => coordinator.RequestAsync(() => throw new IOException("injected")), "Refresh errors propagate");
        Check(await coordinator.RequestAsync(() => Task.FromResult(true)), "Refresh coordinator recovers after failure");
    }

    private static void VerifyCollection()
    {
        var a = new object(); var b = new object(); var c = new object(); var d = new object();
        var values = new ObservableCollection<object> { a, b, c }; var resets = 0;
        values.CollectionChanged += (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
        CollectionReconciler.Apply(values, new[] { c, a, d });
        Check(resets == 0 && values.SequenceEqual(new[] { c, a, d }), "Reconcile preserves collection identity and avoids reset notifications");
    }

    private static async Task VerifySettingsAsync(string root)
    {
        var before = Environment.GetEnvironmentVariable(JsonUserLibraryStateStore.StateEnvironmentVariable);
        var path = Path.Combine(root, "settings", "state.json");
        Environment.SetEnvironmentVariable(JsonUserLibraryStateStore.StateEnvironmentVariable, path);
        try
        {
            var store = new JsonUserLibraryStateStore();
            var original = UserLibraryState.Empty with { Language = "de-DE" };
            await store.SaveAsync(original);
            await store.SaveAsync(original with { Language = "fr-FR" });
            var folder = Path.GetDirectoryName(path)!;
            Directory.Move(folder, folder + ".offline");
            try
            {
                await RejectAsync(() => store.LoadAsync(), "Unavailable settings folder does not return empty defaults");
                await RejectAsync(() => store.SaveAsync(original), "Unavailable settings folder is not recreated on save");
                Check(!Directory.Exists(folder), "Missing settings location remains untouched");
            }
            finally { Directory.Move(folder + ".offline", folder); }
            await File.WriteAllTextAsync(path, "broken JSON");
            Check((await store.LoadAsync()).Language == "de-DE" && store.RecoveryNoticeKey == "StateRecovered", "Corrupt settings recover from last valid backup with a notice");
            await store.SaveAsync(original);
            Check(Directory.GetFiles(folder, "state.json.corrupt-*").Length == 1, "Corrupt settings retained for recovery before replacement");
            File.Delete(path + ".bak"); await File.WriteAllTextAsync(path, "broken again");
            await RejectAsync(() => store.LoadAsync(), "Unreadable settings without backup are not silently reset");
        }
        finally { Environment.SetEnvironmentVariable(JsonUserLibraryStateStore.StateEnvironmentVariable, before); }
    }

    public static async Task VerifyLeaseAsync(string root)
    {
        var ready = Path.Combine(root, "lease-ready");
        var database = Path.Combine(root, "lease-instance", "DoomLauncher.sqlite");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(ReliabilityTests).Assembly.Location);
        info.ArgumentList.Add("--hold-installation-lease"); info.ArgumentList.Add(database); info.ArgumentList.Add(ready);
        using var process = Process.Start(info)!;
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(ready) && !process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
            if (!File.Exists(ready)) throw new InvalidOperationException("Lease child failed: " + await process.StandardError.ReadToEndAsync());
            await RejectAsync(() => { using var lease = InstallationLease.Acquire(database); return Task.CompletedTask; }, "Installation lock excludes a second process");
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var next = InstallationLease.Acquire(database);
            Check(true, "Installation lock releases after normal process exit");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    private static async Task VerifyBackupsAndDuplicatesAsync(string root)
    {
        var installation = Path.Combine(root, "installation");
        Directory.CreateDirectory(Path.Combine(installation, "WinUI"));
        await File.WriteAllTextAsync(Path.Combine(installation, "DoomLauncher667.exe"), "fixture launcher");
        await File.WriteAllTextAsync(Path.Combine(installation, "WinUI", "DoomLauncher.WinUI.exe"), "fixture client");
        var database = Path.Combine(installation, "DoomLauncher.sqlite");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.Empty.sqlite"), database);
        var locator = new Locator(database); var library = new SqliteNativeLibraryService(locator);
        await new FirstSetupService(locator, library).EnsureManagedLayoutAsync();
        var input = Path.Combine(root, "inputs"); Directory.CreateDirectory(input);
        var a = MakeArchive(input, "A", "Alpha"); var b = MakeArchive(input, "B", "Beta");
        var broken = Path.Combine(input, "broken.zip"); await File.WriteAllTextAsync(broken, "broken archive");
        var summary = await new ModImportBatch(library).RunAsync(new[] {
            new ModImportRequest(a, ImportFileConflictResolution.Fail), new(b, ImportFileConflictResolution.Fail),
            new(a, ImportFileConflictResolution.Skip), new(broken, ImportFileConflictResolution.Fail) }, null, default);
        Check(summary.Imported == 2 && summary.Skipped == 1 && summary.Failures.Count == 1, "Batch import reports successes, skips and per-file failures");
        var cancelled = await new ModImportBatch(library).RunAsync(new[] { new ModImportRequest(a, ImportFileConflictResolution.Fail) }, null, new CancellationToken(true));
        Check(cancelled.Cancelled && !cancelled.HasChanges, "Cancelled batch does not import subsequent files");
        var mods = Path.Combine(installation, "Data", "Mods");
        File.Copy(Directory.GetFiles(mods).First(), Path.Combine(mods, "renamed-copy.zip"));
        var duplicates = await new DuplicateModService().FindAsync(mods, null, default);
        Check(duplicates.Count == 1 && duplicates[0].Paths.Count == 2, "Duplicate scan finds identical renamed archives");
        Check(Directory.GetFiles(mods).Length == 3, "Duplicate scan never removes files");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA journal_mode=WAL;"; await command.ExecuteScalarAsync();
            command.CommandText = "UPDATE GameFiles SET Author='committed WAL data';"; await command.ExecuteNonQueryAsync();
            command.CommandText = "UPDATE Configuration SET Value=$folder WHERE Name='GameFileDirectory';";
            command.Parameters.AddWithValue("$folder", Path.Combine(installation, "Data"));
            await command.ExecuteNonQueryAsync();
            var backup = Path.Combine(root, "complete.zip");
            var service = new InstallationBackupService();
            await service.CreateAsync(installation, backup, true, null, default);
            var restored = Path.Combine(root, "restored"); Directory.CreateDirectory(restored);
            var manifest = await service.RestoreAsync(backup, restored, null, default);
            Check(manifest.IncludesMods && Directory.GetFiles(Path.Combine(restored, "Data", "Mods")).Length == 3, "Full backup restores archives and installation files");
            await using var restoredDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(restored, "DoomLauncher.sqlite"), Pooling = false }.ToString());
            await restoredDb.OpenAsync(); await using var check = restoredDb.CreateCommand();
            check.CommandText = "SELECT Author FROM GameFiles LIMIT 1;";
            Check(Convert.ToString(await check.ExecuteScalarAsync()) == "committed WAL data", "Backup includes committed SQLite WAL data");
            check.CommandText = "SELECT Value FROM Configuration WHERE Name='GameFileDirectory';";
            Check(Convert.ToString(await check.ExecuteScalarAsync()) == Path.Combine(restored, "Data"), "Restore rebases installation paths to the new folder");
            await RejectAsync(() => service.RestoreAsync(backup, restored, null, default), "Restore refuses nonempty destination");
            var existing = await File.ReadAllBytesAsync(backup);
            await RejectAsync(() => service.CreateAsync(installation, backup, true, null, new CancellationToken(true)), "Cancelled backup leaves destination untouched");
            var after = await File.ReadAllBytesAsync(backup);
            Check(existing.SequenceEqual(after), "Existing backup survives failed replacement");
            var light = Path.Combine(root, "without-mods.zip"); await service.CreateAsync(installation, light, false, null, default);
            var lightTarget = Path.Combine(root, "light-restore"); Directory.CreateDirectory(lightTarget);
            Check(!(await service.RestoreAsync(light, lightTarget, null, default)).IncludesMods
                && !Directory.Exists(Path.Combine(lightTarget, "Data", "Mods")), "Mod archive exclusion is explicit in backup manifest");
            var tampered = Path.Combine(root, "tampered.zip"); File.Copy(backup, tampered);
            using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("DoomLauncher667.exe")!; entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("DoomLauncher667.exe").Open()); writer.Write("invalid launcher");
            }
            var invalidTarget = Path.Combine(root, "invalid-restore"); Directory.CreateDirectory(invalidTarget);
            await RejectAsync(() => service.RestoreAsync(tampered, invalidTarget, null, default), "Restore rejects corrupted backup content");
            Check(!Directory.EnumerateFileSystemEntries(invalidTarget).Any(), "Failed restore cleans up its own extracted files");
            var traversal = Path.Combine(root, "traversal.zip"); File.Copy(backup, traversal);
            using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("dl667-backup.json")!; InstallationBackupManifest sourceManifest;
                using (var stream = entry.Open()) sourceManifest = JsonSerializer.Deserialize<InstallationBackupManifest>(stream)!;
                entry.Delete();
                using var output = zip.CreateEntry("dl667-backup.json").Open();
                JsonSerializer.Serialize(output, sourceManifest with { Files = new[] { sourceManifest.Files[0] with { Path = "../escaped.exe" } }.Concat(sourceManifest.Files.Skip(1)).ToArray() });
            }
            await RejectAsync(() => service.RestoreAsync(traversal, invalidTarget, null, default), "Restore rejects path traversal before extracting files");
            Check(!File.Exists(Path.Combine(root, "escaped.exe")), "Restore cannot write outside selected folder");
        }
    }

    public static string MakeArchive(string folder, string name, string title)
    {
        var path = Path.Combine(folder, name + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var text = new StreamWriter(zip.CreateEntry("readme.txt").Open());
        text.Write($"Title : {title}\nAuthor : Test\nGame : Doom II\n");
        return path;
    }
}
