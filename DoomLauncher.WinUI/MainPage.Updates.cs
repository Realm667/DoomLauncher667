using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private bool _updateChecked;
    private async Task CheckForLauncherUpdatesAsync()
    {
        if (_updateChecked || Environment.GetCommandLineArgs().Any(argument => argument.StartsWith("--") && argument.Contains("test", StringComparison.OrdinalIgnoreCase))) return;
        _updateChecked = true;
        var userAction = false;
        try
        {
            using var client = LauncherUpdateService.CreateClient();
            var updater = new LauncherUpdateService(client);
            var root = GetPortableRoot();
            var release = await updater.CheckAsync(AppVersion.Current,
                await LauncherUpdateService.LoadSkippedAsync(root, _loadCancellation.Token), _loadCancellation.Token);
            if (release is null) return;
            while (_isImporting || ViewModel.IsLaunching || ViewModel.IsGameRunning || _obsidianGenerationCancellation is not null || _isRescraping
                || VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0)
                await Task.Delay(1000, _loadCancellation.Token);
            var result = await CreateLauncherUpdateDialog(release).ShowAsync();
            userAction = result != ContentDialogResult.None;
            if (result == ContentDialogResult.Secondary)
                await LauncherUpdateService.SkipAsync(root, release.Version, _loadCancellation.Token);
            else if (result == ContentDialogResult.Primary)
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
                await RunProgressDialogAsync(Strings["LauncherUpdateTitle"], Strings["UpdatePreparing"], async progress =>
                {
                    var prepared = await updater.PrepareAsync(release, root, progress, cancellation.Token);
                    await LauncherUpdateService.StartHelperAsync(prepared, cancellation.Token);
                    return prepared;
                }, () => Strings["UpdatePreparing"], cancellation);
                _app.MainWindow!.Close();
            }
            // Close/Escape and "later" deliberately persist nothing: check again next launch.
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // Offline startup and API rate limits must never block the Library.
            System.Diagnostics.Trace.TraceWarning("Launcher update: {0}", error);
            if (userAction && !_loadCancellation.IsCancellationRequested)
                await ShowErrorAsync(Strings["LauncherUpdateTitle"], error.Message);
        }
    }

    private ContentDialog CreateLauncherUpdateDialog(LauncherRelease release)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = Strings["LauncherUpdateTitle"],
            Content = new TextBlock { Text = _app.Localization.Format("LauncherUpdateAvailable", release.Version, AppVersion.Current) + "\n\n" + Strings["UpdateChoiceHelp"], TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            PrimaryButtonText = Strings["UpdateNow"], SecondaryButtonText = Strings["UpdateSkip"], CloseButtonText = Strings["UpdateLater"],
            DefaultButton = ContentDialogButton.Close,
        };
        ApplyDialogTheme(dialog); return dialog;
    }
}
