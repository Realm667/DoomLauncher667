namespace DoomLauncher.WinUI.Services;

internal sealed class RefreshCoordinator
{
    private readonly object _gate = new();
    private Func<Task<bool>>? _pending;
    private Task<bool>? _running;

    public Task<bool> RequestAsync(Func<Task<bool>> refresh)
    {
        lock (_gate)
        {
            _pending = refresh;
            return _running ??= DrainAsync();
        }
    }

    private async Task<bool> DrainAsync()
    {
        await Task.Yield(); // Publish the shared task before executing callbacks, even for synchronous mocks.
        var result = false;
        try
        {
            while (true)
            {
                Func<Task<bool>>? operation;
                lock (_gate)
                {
                    operation = _pending;
                    _pending = null;
                    if (operation is null) { _running = null; return result; }
                }
                result = await operation();
            }
        }
        catch
        {
            lock (_gate) { _running = null; _pending = null; }
            throw;
        }
    }
}
