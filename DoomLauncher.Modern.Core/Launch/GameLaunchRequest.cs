namespace DoomLauncher.Modern.Core.Launch;

public sealed record GameLaunchRequest(
    int GameFileId,
    string DisplayName,
    int? SourcePortId = null,
    int? IwadId = null,
    string? Map = null,
    string? Skill = null);
