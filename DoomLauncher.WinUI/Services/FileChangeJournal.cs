namespace DoomLauncher.WinUI.Services;

// Tracks only files owned by the current operation. Until Commit, an exception
// (including cancellation) restores replaced files and removes new files.
internal sealed class FileChangeJournal : IDisposable
{
    private readonly List<(string Path, string? Backup)> changes = [];
    private bool committed;

    public void Track(string path)
    {
        path = Path.GetFullPath(path);
        if (changes.Any(change => change.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;
        string? backup = null;
        if (File.Exists(path))
        {
            backup = path + $".rollback-{Guid.NewGuid():N}";
            File.Copy(path, backup, overwrite: false);
        }
        changes.Add((path, backup));
    }

    public void Commit() => committed = true;

    public void Dispose()
    {
        foreach (var change in changes.AsEnumerable().Reverse())
        {
            if (!committed)
            {
                if (change.Backup is not null)
                    File.Move(change.Backup, change.Path, overwrite: true);
                else if (File.Exists(change.Path))
                    File.Delete(change.Path);
            }
            else if (change.Backup is not null)
            {
                try { File.Delete(change.Backup); }
                catch (IOException) { /* Retain the recovery copy if still in use. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
