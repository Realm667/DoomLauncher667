namespace DoomLauncher.WinUI.Services;

internal sealed record ArchiveInspection(IReadOnlyList<string> Maps, ArchiveTextMetadata Text)
{
    // All archive ingestion paths use the same decoding and exact map counting.
    // Archive libraries also do synchronous decompression; keep that off the UI.
    public static Task<ArchiveInspection> ReadAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(async () => new ArchiveInspection(
            await MapNameExtractor.ExtractAsync(path, cancellationToken),
            await ArchiveTextMetadataReader.ReadAsync(path, cancellationToken)), cancellationToken);
}
