using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private bool _isImporting;

    private async Task ImportModPathsAsync(IReadOnlyList<string> paths)
    {
        if (_isImporting || paths.Count == 0) return;
        _isImporting = true;
        try
        {
            var requests = new List<ModImportRequest>();
            ImportFileConflictResolution? remainingResolution = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                var conflict = await ViewModel.FindImportConflictAsync(name, _loadCancellation.Token);
                var resolution = ImportFileConflictResolution.Fail;
                if (conflict is not null || !seen.Add(name))
                {
                    if (remainingResolution.HasValue) resolution = remainingResolution.Value;
                    else
                    {
                        var all = new CheckBox { Content = Strings["ApplyRemainingConflicts"] };
                        var dialog = new ContentDialog
                        {
                            XamlRoot = XamlRoot, Title = Strings["ImportFileConflictTitle"],
                            Content = new StackPanel { Spacing = 12, Children = {
                                new TextBlock { Text = _app.Localization.Format("ImportFileConflictMessage", name), TextWrapping = TextWrapping.Wrap }, all } },
                            PrimaryButtonText = Strings["Overwrite"], CloseButtonText = Strings["Skip"],
                            DefaultButton = ContentDialogButton.Close,
                        };
                        ApplyDialogTheme(dialog);
                        resolution = await dialog.ShowAsync() == ContentDialogResult.Primary
                            ? ImportFileConflictResolution.Overwrite : ImportFileConflictResolution.Skip;
                        if (all.IsChecked == true) remainingResolution = resolution;
                    }
                }
                requests.Add(new(path, resolution));
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
            var status = string.Empty;
            var result = await RunProgressDialogAsync(Strings["ImportBatchTitle"], Strings["ImportBatchPreparing"], progress =>
                ViewModel.ImportFilesAsync(requests, new Progress<ModImportProgress>(update =>
                {
                    status = $"{update.Completed + (update.Completed < update.Total ? 1 : 0)} / {update.Total} · {update.FileName}";
                    progress.Report(update.Total == 0 ? 100 : 100d * update.Completed / update.Total);
                }), cancellation.Token), () => status, cancellation);
            SyncSelection();
            var message = _app.Localization.Format("ImportBatchSummary", result.Imported, result.Updated, result.Skipped, result.Failures.Count);
            if (result.Cancelled) message += Environment.NewLine + Strings["ImportBatchCancelled"];
            foreach (var failure in result.Failures)
                message += $"{Environment.NewLine}• {failure.FileName}: {failure.Message}";
            await ShowActionMessageAsync(Strings["ImportBatchTitle"], message, Strings["Close"]);
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested) { }
        catch (Exception error) { await ShowErrorAsync(Strings["ImportBatchTitle"], error.Message); }
        finally { _isImporting = false; }
    }
}
