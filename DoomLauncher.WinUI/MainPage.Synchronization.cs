using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private async Task ShowSynchronizationAsync()
    {
        var token = _loadCancellation.Token;
        var remembered = await _app.LibraryFeatures.GetOptionAsync("SyncFolder", token);
        var folder = new TextBox { Header = Strings["SyncFolder"], Text = remembered ?? "", TextWrapping = TextWrapping.Wrap };
        var browse = new Button { Content = Strings["ChooseFolder"] };
        var settings = new CheckBox { Content = Strings["SyncSettings"] };
        var saves = new CheckBox { Content = Strings["SyncSaves"] };
        var verify = new CheckBox { Content = Strings["VerifyArchives"] };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var notice = new TextBlock { Text = Strings["SyncNotice"], TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Width = 650, Spacing = 12, Children = { notice, folder, browse, settings, saves, verify, status } };
        var options = FeatureDialog(Strings["Synchronization"], panel, Strings["PreviewChanges"]);
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker { CommitButtonText = Strings["Synchronization"] }; picker.FileTypeFilter.Add("*"); InitializePicker(picker);
                var selected = await picker.PickSingleFolderAsync(); if (selected is not null) folder.Text = selected.Path;
            }
            catch (Exception error) { status.Text = error.Message; }
        };
        options.PrimaryButtonClick += (_, args) =>
        { if (string.IsNullOrWhiteSpace(folder.Text)) { args.Cancel = true; status.Text = Strings["ChooseFolder"]; } };
        if (await options.ShowAsync() != ContentDialogResult.Primary) return;
        var service = new PersonalSyncService(_app.LibraryFeatures, _app.UserLibraryStateStore);
        PersonalSyncPlan? plan = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var progressText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { IsIndeterminate = true, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DoomControlAccentBrush"] };
        var progressDialog = FeatureDialog(Strings["Synchronization"], new StackPanel { Width = 650, Spacing = 12, Children = { progress, progressText } });
        progressDialog.CloseButtonText = Strings["Cancel"];
        var finished = false;
        progressDialog.Closing += (_, args) => { if (!finished) { args.Cancel = true; cancellation.Cancel(); } };
        var shown = progressDialog.ShowAsync();
        Exception? failure = null;
        try { plan = await service.PreviewAsync(GetPortableRoot(), folder.Text, settings.IsChecked == true, saves.IsChecked == true, verify.IsChecked == true, new Progress<string>(text => progressText.Text = text), cancellation.Token); }
        catch (Exception error) { failure = error; }
        finally { finished = true; progressDialog.Hide(); }
        await shown;
        if (failure is OperationCanceledException) return;
        if (failure is not null) throw failure;
        if (plan is null) return;
        await _app.LibraryFeatures.SetOptionAsync("SyncFolder", folder.Text, token);
        var list = new ListView { ItemsSource = plan.Differences, DisplayMemberPath = nameof(SyncDifference.Label), Height = 300, SelectionMode = ListViewSelectionMode.Single };
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 180 };
        var choice = new ComboBox { Header = Strings["ConflictResolution"], ItemsSource = new[] { Strings["ChooseResolution"], Strings["UseLocal"], Strings["UseRemote"] }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void UpdateSummary() => summary.Text = string.Format(Strings["SyncSummary"], plan.Differences.Count, plan.Differences.Count(item => item.Resolution == SyncResolution.Unresolved), plan.MissingLocalMods);
        UpdateSummary();
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not SyncDifference change) return;
            details.Text = $"{change.Label}\n\n{Strings["UseLocal"]}:\n{change.Local ?? "—"}\n\n{Strings["UseRemote"]}:\n{change.Remote ?? "—"}";
            choice.SelectedIndex = (int)change.Resolution;
        };
        choice.SelectionChanged += (_, _) => { if (list.SelectedItem is SyncDifference change) { change.Resolution = (SyncResolution)choice.SelectedIndex; UpdateSummary(); } };
        var local = new Button { Content = Strings["ResolveAllLocal"] }; var remote = new Button { Content = Strings["ResolveAllRemote"] };
        void ResolveAll(SyncResolution resolution) { foreach (var change in plan.Differences.Where(change => change.Conflict)) change.Resolution = resolution; if (list.SelectedItem is SyncDifference selected) choice.SelectedIndex = (int)selected.Resolution; UpdateSummary(); }
        local.Click += (_, _) => ResolveAll(SyncResolution.Local); remote.Click += (_, _) => ResolveAll(SyncResolution.Remote);
        var review = FeatureDialog(Strings["PreviewChanges"], new ScrollViewer { MaxHeight = 720, Content = new StackPanel { Width = 730, Spacing = 10, Children = { summary, list, details, choice, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { local, remote } } } } }, Strings["Synchronize"]);
        review.PrimaryButtonClick += (_, args) => { if (plan.Differences.Any(change => change.Resolution == SyncResolution.Unresolved)) args.Cancel = true; };
        if (await review.ShowAsync() != ContentDialogResult.Primary) return;
        using var applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        finished = false; progressDialog.CloseButtonText = Strings["Cancel"];
        // Use a fresh dialog: WinUI dialogs must not overlap and cancellation is scoped to this operation.
        var apply = FeatureDialog(Strings["Synchronization"], new StackPanel { Width = 650, Spacing = 12, Children = { new ProgressBar { IsIndeterminate = true, Foreground = progress.Foreground }, new TextBlock { Text = Strings["ApplyingSync"], TextWrapping = TextWrapping.Wrap } } });
        apply.CloseButtonText = Strings["Cancel"];
        apply.Closing += (_, args) => { if (!finished) { args.Cancel = true; applyCancellation.Cancel(); } };
        var applying = apply.ShowAsync(); failure = null;
        try { await service.ApplyAsync(plan, null, applyCancellation.Token); }
        catch (Exception error) { failure = error; }
        finally { finished = true; apply.Hide(); }
        await applying;
        await ViewModel.RefreshAsync(token); SyncSelection();
        _userState = await _app.UserLibraryStateStore.LoadAsync(token);
        if (failure is not null) throw failure;
        await ShowActionMessageAsync(Strings["Synchronization"], Strings["SyncComplete"], Strings["Close"]);
    }
}
