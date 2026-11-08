namespace DoomLauncher.Modern.Core.Launch;

public sealed record GameLaunchRequest(
    int GameFileId,
    string DisplayName,
    int? SourcePortId = null,
    int? IwadId = null,
    string? Map = null,
    string? Skill = null,
    string? SaveGamePath = null,
    bool AllowUnverifiedSave = false,
    MultiplayerLaunchRequest? Multiplayer = null);

public sealed record MultiplayerLaunchRequest(string Mode, string? Address, int Port = 10666, int Players = 2,
    string GameMode = "coop", IReadOnlyList<MultiplayerModFile>? Mods = null);
public sealed record MultiplayerModFile(int GameFileId, string InternalName);
