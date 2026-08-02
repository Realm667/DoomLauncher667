using System.Diagnostics;
using DoomLauncher.Modern.Core.Launch;

namespace DoomLauncher.WinUI.Services;

public interface IProcessStarter
{
    IGameLaunchSession Start(ProcessStartInfo startInfo);
}

public sealed class SystemProcessStarter : IProcessStarter
{
    public IGameLaunchSession Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The game process could not be started.");
        return new SystemGameLaunchSession(process);
    }
}

public sealed class SystemGameLaunchSession(Process process) : IGameLaunchSession
{
    public int ProcessId => process.Id;

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            process.Dispose();
        }
    }
}
