using DoomLauncher.Modern.Core.Launch;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private async Task ShowSavesAsync()
    {
        var game = ViewModel.SelectedGame; if (game.IsPlaceholder) return;
        var archive = await _app.NativeLibraryService.ResolveManagedGameFileAsync(game.GameFileId, _loadCancellation.Token);
        if (archive is null || !File.Exists(archive)) throw new FileNotFoundException(Strings["ArchiveMissing"], archive);
        var hash = await SaveGameService.HashAsync(archive, _loadCancellation.Token); var root = GetPortableRoot();
        while (true)
        {
            var saves = await SaveGameService.LoadAsync(root, game.FileName, hash, _loadCancellation.Token);
            var list = new ListView { ItemsSource = saves, DisplayMemberPath = nameof(SaveGameEntry.Label), Height = 260, SelectionMode = ListViewSelectionMode.Single };
            var details = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 660 };
            var preview = new Image { Width = 380, MaxHeight = 220, Stretch = Stretch.Uniform };
            var temporary = new List<string>(); var generation = 0;
            list.SelectionChanged += async (_, _) =>
            {
                var request = ++generation; if (list.SelectedItem is not SaveGameEntry entry) return;
                details.Text = $"{entry.Path}\n{entry.Metadata?.EngineName ?? "?"} {entry.Metadata?.EngineVersion}\n{(entry.Metadata is null || entry.Metadata.Imported ? Strings["SaveUnverified"] : entry.Metadata.GameHash == hash ? Strings["SaveArchiveMatches"] : Strings["SaveArchiveMismatch"])}";
                preview.Source = null;
                if (entry.Preview is not null)
                {
                    try
                    {
                        var directory = Path.Combine(root, "Data", "Temp", "SavePreviews"); Directory.CreateDirectory(directory);
                        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png"); await File.WriteAllBytesAsync(path, entry.Preview, _loadCancellation.Token); temporary.Add(path);
                        if (request == generation) preview.Source = new BitmapImage(new Uri(path));
                    }
                    catch (OperationCanceledException) { }
                    catch (IOException error) { details.Text += "\n" + error.Message; }
                }
            };
            var panel = new StackPanel { Width = 700, Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = Strings["SaveManagerNotice"], TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(list); panel.Children.Add(details); panel.Children.Add(preview);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            string? action = null; var dialog = FeatureDialog(Strings["SaveGames"], new ScrollViewer { Content = panel, MaxHeight = 720 }, Strings["ResumeSave"]);
            foreach (var key in new[] { "ImportSaves", "BackupSaves", "RestoreSaves" })
            { var button = new Button { Content = Strings[key] }; button.Click += (_, _) => { action = key; dialog.Hide(); }; buttons.Children.Add(button); }
            panel.Children.Add(buttons); if (saves.Count > 0) list.SelectedIndex = 0;
            var result = await dialog.ShowAsync(); generation++;
            foreach (var path in temporary) { try { File.Delete(path); } catch (IOException) { } }
            if (action == "ImportSaves")
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zds"); InitializePicker(picker);
                var files = await picker.PickMultipleFilesAsync(); if (files.Count == 0) continue;
                if (await FeatureDialog(Strings["ImportSaves"], new TextBlock { Text = Strings["SaveImportNotice"] + "\n\n" + game.Title, TextWrapping = TextWrapping.Wrap }, Strings["ImportAction"]).ShowAsync() != ContentDialogResult.Primary) continue;
                var errors = new List<string>();
                foreach (var file in files)
                    try { await SaveGameService.ImportAsync(root, file.Path, game.FileName, hash, _loadCancellation.Token); }
                    catch (Exception error) when (error is IOException or InvalidDataException) { errors.Add(file.Name + ": " + error.Message); }
                if (errors.Count > 0) await ShowActionMessageAsync(Strings["ImportSaves"], string.Join('\n', errors), Strings["Close"]);
                continue;
            }
            if (action == "BackupSaves")
            {
                var directories = saves.Select(save => Path.GetDirectoryName(save.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var results = new List<string>(); foreach (var directory in directories)
                    if (await SaveGameService.BackupAsync(root, directory, _loadCancellation.Token) is { } backup) results.Add(backup);
                await ShowActionMessageAsync(Strings["BackupSaves"], results.Count == 0 ? Strings["NoSaves"] : string.Join('\n', results), Strings["Close"]); continue;
            }
            if (action == "RestoreSaves")
            {
                var picker = new FolderPicker { CommitButtonText = Strings["RestoreSaves"] }; picker.FileTypeFilter.Add("*"); InitializePicker(picker);
                var folder = await picker.PickSingleFolderAsync(); if (folder is null) continue;
                if (await FeatureDialog(Strings["RestoreSaves"], new TextBlock { Text = Strings["SaveRestoreNotice"] + "\n" + folder.Path, TextWrapping = TextWrapping.Wrap }, Strings["Apply"]).ShowAsync() == ContentDialogResult.Primary)
                    await SaveGameService.RestoreBackupAsync(root, folder.Path, _loadCancellation.Token);
                continue;
            }
            if (result != ContentDialogResult.Primary || list.SelectedItem is not SaveGameEntry selected) return;
            var warning = Strings["SaveCompatibilityNotice"] + "\n\n" + selected.Label + "\n" + details.Text;
            if (await FeatureDialog(Strings["ResumeSave"], new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap, MaxWidth = 650 }, Strings["ResumeSave"]).ShowAsync() != ContentDialogResult.Primary) continue;
            int? portId = null;
            if (selected.Metadata is { EngineHash.Length: > 0 } metadata)
            {
                var definitions = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(_loadCancellation.Token);
                foreach (var port in definitions.SourcePorts.Where(port => port.Name == metadata.EngineName))
                {
                    var exe = Path.Combine(Path.GetFullPath(Path.Combine(root, port.Directory)), port.Executable);
                    if (File.Exists(exe) && await SaveGameService.HashAsync(exe, _loadCancellation.Token) == metadata.EngineHash) { portId = port.SourcePortId; break; }
                }
            }
            await ViewModel.LaunchFeatureRequestAsync(new GameLaunchRequest(game.GameFileId, game.Title, SourcePortId: portId,
                SaveGamePath: selected.Path, AllowUnverifiedSave: true), _loadCancellation.Token); return;
        }
    }
}
