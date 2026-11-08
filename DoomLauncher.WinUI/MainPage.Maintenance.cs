using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private async Task ShowMaintenanceDialogAsync()
    {
        if (ViewModel.IsGameRunning || _obsidianGenerationCancellation is not null || _isImporting || _isRescraping)
        {
            await ShowActionMessageAsync(Strings["Maintenance"], Strings["CloseGamesFirst"], Strings["Close"]);
            return;
        }
        var includeMods = new CheckBox { Content = Strings["IncludeModArchives"], IsChecked = true };
        var content = new StackPanel { Spacing = 12, MaxWidth = 550 };
        content.Children.Add(new TextBlock { Text = Strings["MaintenanceDescription"], TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = Strings["BackupScopeNotice"], TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        content.Children.Add(includeMods);
        string? action = null;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Strings["Maintenance"], Content = content, CloseButtonText = Strings["Close"] };
        foreach (var key in new[] { "CreateBackup", "RestoreBackup", "FindDuplicates" })
        {
            var button = new Button { Content = Strings[key], HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => { action = key; dialog.Hide(); };
            content.Children.Add(button);
        }
        ApplyDialogTheme(dialog);
        await dialog.ShowAsync();
        try
        {
            if (action == "CreateBackup") await CreateInstallationBackupAsync(includeMods.IsChecked == true);
            else if (action == "RestoreBackup") await RestoreInstallationBackupAsync();
            else if (action == "FindDuplicates") await ShowDuplicateModsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { await ShowErrorAsync(Strings["Maintenance"], error.Message); }
    }

    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker,
        WinRT.Interop.WindowNative.GetWindowHandle(_app.MainWindow!));

    private async Task CreateInstallationBackupAsync(bool includeMods)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"DoomLauncher667-backup-{DateTime.Now:yyyyMMdd-HHmmss}" };
        picker.FileTypeChoices.Add("Doom Launcher 667 backup", new List<string> { ".zip" });
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
        await RunProgressDialogAsync(Strings["CreateBackup"], Strings["MaintenanceDescription"], async progress =>
        {
            await Task.Run(() => new InstallationBackupService().CreateAsync(GetPortableRoot(), file.Path, includeMods, progress, cancellation.Token));
            return true;
        }, cancellation: cancellation);
        await ShowActionMessageAsync(Strings["CreateBackup"], Strings["BackupReady"] + Environment.NewLine + file.Path, Strings["Close"]);
    }

    private async Task RestoreInstallationBackupAsync()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zip"); InitializePicker(picker);
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        var folders = new FolderPicker { CommitButtonText = Strings["RestoreBackup"] };
        folders.FileTypeFilter.Add("*"); InitializePicker(folders);
        var folder = await folders.PickSingleFolderAsync(); if (folder is null) return;
        var root = Path.GetFullPath(GetPortableRoot()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(folder.Path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore into a separate empty folder outside the running installation.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
        var manifest = await RunProgressDialogAsync(Strings["RestoreBackup"], Strings["MaintenanceDescription"], progress =>
            Task.Run(() => new InstallationBackupService().RestoreAsync(file.Path, folder.Path, progress, cancellation.Token)), cancellation: cancellation);
        var message = Strings["RestoreReady"] + Environment.NewLine + folder.Path;
        if (!manifest.IncludesMods) message += Environment.NewLine + Strings["BackupWithoutMods"];
        await ShowActionMessageAsync(Strings["RestoreBackup"], message, Strings["Close"]);
    }

    private sealed record DuplicateRow(string Path, string Display);

    private async Task ShowDuplicateModsAsync()
    {
        var settings = await ViewModel.LoadNativeSettingsAsync(_loadCancellation.Token);
        var mods = Path.Combine(settings.GameFileDirectory, "Mods");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
        var groups = await RunProgressDialogAsync(Strings["FindDuplicates"], mods, progress =>
            Task.Run(() => new DuplicateModService().FindAsync(mods, progress, cancellation.Token)), cancellation: cancellation);
        if (groups.Count == 0)
        {
            await ShowActionMessageAsync(Strings["FindDuplicates"], Strings["NoDuplicates"], Strings["Close"]);
            return;
        }
        var rows = groups.SelectMany((group, index) => group.Paths.Select(path => new DuplicateRow(path,
            $"{index + 1} · {group.FileSize:N0} bytes · {group.Sha256[..12]} · {Path.GetRelativePath(mods, path)}"))).ToArray();
        var list = new ListView { ItemsSource = rows, DisplayMemberPath = nameof(DuplicateRow.Display), SelectionMode = ListViewSelectionMode.Single, Height = 360 };
        var summary = _app.Localization.Format("DuplicateSummary", groups.Count, groups.Sum(group => group.ReclaimableBytes).ToString("N0"));
        var content = new StackPanel { Spacing = 12, Width = 680 };
        content.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(list);
        var open = new Button { Content = Strings["OpenFolder"], IsEnabled = false };
        list.SelectionChanged += (_, _) => open.IsEnabled = list.SelectedItem is DuplicateRow;
        open.Click += async (_, _) =>
        {
            if (list.SelectedItem is DuplicateRow row)
                await Windows.System.Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(row.Path)!);
        };
        content.Children.Add(open);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Strings["FindDuplicates"], Content = content, CloseButtonText = Strings["Close"] };
        ApplyDialogTheme(dialog);
        dialog.Resources["ContentDialogMinWidth"] = 740d;
        await dialog.ShowAsync();
    }
}
