using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

// Some downloads are only a ZIP around one PK3. Spool that container to disk
// rather than keeping a large nested archive in memory or silently skipping it.
internal sealed class ArchivePayload(string path) : IDisposable
{
    private const long MaximumPayloadSize = 4L * 1024 * 1024 * 1024;
    private readonly List<string> _temporaryFiles = [];
    public string Path { get; private set; } = path;
    public string FileName { get; private set; } = System.IO.Path.GetFileName(path);

    public static async Task<ArchivePayload> OpenAsync(string path, CancellationToken token)
    {
        var payload = new ArchivePayload(System.IO.Path.GetFullPath(path));
        try
        {
            for (var depth = 0; ; depth++)
            {
                token.ThrowIfCancellationRequested();
                if (!new[] { ".zip", ".pk3", ".pk7", ".7z", ".rar" }.Contains(
                        System.IO.Path.GetExtension(payload.Path), StringComparer.OrdinalIgnoreCase))
                    return payload;
                using var archive = ArchiveFactory.OpenArchive(payload.Path);
                var entries = archive.Entries.Where(entry => !entry.IsDirectory).Take(2).ToArray();
                if (entries.Length != 1 || !new[] { ".zip", ".pk3", ".pk7" }.Contains(
                        System.IO.Path.GetExtension(entries[0].Key), StringComparer.OrdinalIgnoreCase))
                    return payload;
                var entry = entries[0];
                if (depth >= 2 || entry.Size <= 0 || entry.Size > MaximumPayloadSize)
                    throw new InvalidDataException("The nested mod archive exceeds the supported size or depth.");
                var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "DoomLauncher-payload-" + Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(entry.Key));
                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                {
                    payload._temporaryFiles.Add(temporary);
                    await using var source = entry.OpenEntryStream();
                    var buffer = new byte[128 * 1024];
                    long length = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, token)) != 0)
                    {
                        length += read;
                        if (length > MaximumPayloadSize || length > entry.Size)
                            throw new InvalidDataException("The nested mod archive exceeds its declared size.");
                        await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (length != entry.Size) throw new InvalidDataException("The nested mod archive is incomplete.");
                }
                payload.Path = temporary;
                payload.FileName = System.IO.Path.GetFileName(entry.Key ?? string.Empty);
            }
        }
        catch (SharpCompress.Common.ArchiveOperationException error)
        {
            payload.Dispose();
            throw new InvalidDataException("The mod archive could not be read.", error);
        }
        catch { payload.Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var file in _temporaryFiles.AsEnumerable().Reverse())
        {
            try { File.Delete(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Temporary archive cleanup: {0}", error.Message); }
        }
    }
}
