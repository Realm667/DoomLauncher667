using System.Security.Cryptography;

namespace DoomLauncher.WinUI.Services;

public sealed record DuplicateModGroup(string Sha256, long FileSize, IReadOnlyList<string> Paths)
{
    public long ReclaimableBytes => FileSize * (Paths.Count - 1);
}

public sealed class DuplicateModService
{
    public async Task<IReadOnlyList<DuplicateModGroup>> FindAsync(string modsDirectory,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modsDirectory)) throw new DirectoryNotFoundException(modsDirectory);
        var extensions = new HashSet<string>(ModImportBatch.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(modsDirectory, "*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(path => extensions.Contains(Path.GetExtension(path))).Select(path => new FileInfo(path)).ToArray();
        var candidates = files.GroupBy(file => file.Length).Where(group => group.Count() > 1).SelectMany(group => group).ToArray();
        var total = Math.Max(1, candidates.Sum(file => file.Length)); long completed = 0;
        var groups = new Dictionary<(long, string), List<string>>();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = file.Length; var modified = file.LastWriteTimeUtc;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            var buffer = new byte[131072]; int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read); completed += read;
                progress?.Report(100d * completed / total);
            }
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != modified)
                throw new IOException($"Mod changed during comparison: {file.FullName}. Please retry.");
            var key = (length, Convert.ToHexString(hash.GetHashAndReset()));
            if (!groups.TryGetValue(key, out var matches)) groups[key] = matches = [];
            matches.Add(file.FullName);
        }
        progress?.Report(100);
        return groups.Where(pair => pair.Value.Count > 1)
            .Select(pair => new DuplicateModGroup(pair.Key.Item2, pair.Key.Item1,
                pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderByDescending(group => group.ReclaimableBytes).ToArray();
    }
}
