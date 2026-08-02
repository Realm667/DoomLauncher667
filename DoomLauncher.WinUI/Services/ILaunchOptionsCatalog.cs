namespace DoomLauncher.WinUI.Services;

public interface ILaunchOptionsCatalog
{
    Task<LaunchOptionsResult> LoadAsync(
        int gameFileId,
        CancellationToken cancellationToken = default);
}

public sealed record LaunchOptionsResult(
    IReadOnlyList<LaunchOptionChoice> SourcePorts,
    IReadOnlyList<LaunchOptionChoice> Iwads,
    IReadOnlyList<LaunchValueChoice> Maps,
    IReadOnlyList<LaunchValueChoice> Skills);

public sealed record LaunchOptionChoice(
    int? Id,
    string Name);

public sealed record LaunchValueChoice(
    string? Value,
    string Name);
