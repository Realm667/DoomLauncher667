namespace DoomLauncher.WinUI.Services;

/// <summary>Server-enforced file sharing lock: works for drive-letter and UNC access to the same installation.</summary>
internal sealed class InstallationLease : IDisposable
{
    private readonly FileStream _stream;
    private InstallationLease(FileStream stream) => _stream = stream;

    public static InstallationLease Acquire(string databasePath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "Data", "UserState");
        Directory.CreateDirectory(directory);
        try
        {
            // Do not delete the file on exit: deleting/recreating it could defeat another owner's lock.
            return new InstallationLease(new FileStream(Path.Combine(directory, "installation.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new IOException("This installation is already open, or its storage is unavailable. Close the other launcher instance and check the connection before trying again.", exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
