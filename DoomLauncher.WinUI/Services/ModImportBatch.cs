namespace DoomLauncher.WinUI.Services;

public sealed record ModImportRequest(string Path, ImportFileConflictResolution Resolution);
public sealed record ModImportProgress(int Completed, int Total, string FileName);
public sealed record ModImportFailure(string FileName, string Message);
public sealed record ModImportSummary(int Imported, int Updated, int Skipped, bool Cancelled,
    IReadOnlyList<ModImportFailure> Failures)
{
    public bool HasChanges => Imported + Updated > 0;
}

public sealed class ModImportBatch(INativeLibraryService library)
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        Array.AsReadOnly(new[] { ".zip", ".7z", ".rar", ".wad", ".pk3", ".ipk3", ".pk7", ".pke" });
    public async Task<ModImportSummary> RunAsync(IReadOnlyList<ModImportRequest> requests,
        IProgress<ModImportProgress>? progress, CancellationToken cancellationToken)
    {
        var imported = 0; var updated = 0; var skipped = 0; var cancelled = false;
        var failures = new List<ModImportFailure>();
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var name = Path.GetFileName(request.Path);
            progress?.Report(new(index, requests.Count, name));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await Task.Run(() => library.ImportAsync(request.Path, request.Resolution, cancellationToken), cancellationToken);
                if (result.WasSkipped) skipped++;
                else if (result.ReusedExisting) updated++;
                else imported++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { cancelled = true; break; }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or NotSupportedException)
            { failures.Add(new(name, error.Message)); }
            progress?.Report(new(index + 1, requests.Count, name));
        }
        return new(imported, updated, skipped, cancelled, failures);
    }
}
