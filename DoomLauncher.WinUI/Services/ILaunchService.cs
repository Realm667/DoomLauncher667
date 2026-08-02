using DoomLauncher.Modern.Core.Launch;

namespace DoomLauncher.WinUI.Services;

public interface ILaunchService
{
    Task<GameLaunchResult> LaunchAsync(
        GameLaunchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record GameLaunchResult(
    IGameLaunchSession Session,
    string Message);

public interface IGameLaunchSession
{
    int ProcessId { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}
