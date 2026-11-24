namespace DoomLauncher.WinUI.Services;

internal sealed record ArchiveInspection(IReadOnlyList<string> Maps, ArchiveTextMetadata Text)
{
    // All archive ingestion paths use the same decoding and exact map counting.
    // Archive libraries also do synchronous decompression; keep that off the UI.
    public static Task<ArchiveInspection> ReadAsync(string path, CancellationToken cancellationToken,
        string? expectedFileName = null, string? expectedTitle = null) =>
        Task.Run(async () =>
        {
            using var payload = await ArchivePayload.OpenAsync(path, cancellationToken);
            return new ArchiveInspection(
                await MapNameExtractor.ExtractAsync(payload.Path, cancellationToken),
                await ArchiveTextMetadataReader.ReadAsync(payload.Path, cancellationToken, expectedFileName ?? payload.FileName, expectedTitle));
        }, cancellationToken);
}
