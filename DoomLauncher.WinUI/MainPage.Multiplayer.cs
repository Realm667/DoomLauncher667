using DoomLauncher.Modern.Core.Launch;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Net;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private CancellationTokenSource? _multiplayerCancellation;
    private IReadOnlyList<MultiplayerServer> _servers = [];
    private DateTimeOffset _lastMasterQuery = DateTimeOffset.MinValue;

    private async Task InitializeMultiplayerAsync()
    {
        _multiplayerCancellation?.Cancel();
        MultiplayerHost.Content = null; // Re-read definitions and localized labels on each entry.
        var settings = await ViewModel.LoadNativeSettingsAsync(_loadCancellation.Token);
        var definitions = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(_loadCancellation.Token);
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 8, 0, 20) };
        panel.Children.Add(new TextBlock { Text = Strings["MultiplayerNotice"], TextWrapping = TextWrapping.Wrap, MaxWidth = 1100 });
        var master = new TextBox { Header = Strings["MasterServer"], Text = MultiplayerService.DefaultMaster, Width = 350 };
        var port = new NumberBox { Header = Strings["ServerPort"], Value = 10666, Minimum = 1, Maximum = 65535, Width = 160 };
        var internet = new Button { Content = Strings["BrowseInternet"] }; var lan = new Button { Content = Strings["BrowseLan"] }; var cancel = new Button { Content = Strings["Cancel"], IsEnabled = false };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { master, port } });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { internet, lan, cancel } });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        progress.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DoomControlAccentBrush"];
        panel.Children.Add(status); panel.Children.Add(progress);
        var filter = new TextBox { PlaceholderText = Strings["ServerFilter"], MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left };
        var list = new ListView { DisplayMemberPath = nameof(MultiplayerServer.Label), Height = 340, SelectionMode = ListViewSelectionMode.Single };
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 1100 };
        void FilterServers() => list.ItemsSource = _servers.Where(server => $"{server.Name} {server.GameMode} {server.Map} {server.Iwad} {server.Endpoint}".Contains(filter.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        filter.TextChanged += (_, _) => FilterServers();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is MultiplayerServer server) details.Text = $"{server.Endpoint} · Zandronum {server.Version} · IWAD: {server.Iwad}\n{string.Join('\n', server.Files.Select(file => file.Name + (file.Md5 is null ? " · ?" : " · " + file.Md5)))}\n{(server.PasswordRequired ? Strings["ServerPasswordNotice"] : "")}"; };
        panel.Children.Add(filter); panel.Children.Add(list); panel.Children.Add(details);
        async Task Browse(bool local)
        {
            if (!local && DateTimeOffset.UtcNow - _lastMasterQuery < TimeSpan.FromSeconds(10)) { status.Text = Strings["ServerQueryCooldown"]; return; }
            _multiplayerCancellation?.Cancel(); _multiplayerCancellation?.Dispose(); _multiplayerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
            internet.IsEnabled = lan.IsEnabled = false; cancel.IsEnabled = true; progress.Visibility = Visibility.Visible;
            try
            {
                if (!local) _lastMasterQuery = DateTimeOffset.UtcNow;
                var info = new Progress<string>(text => status.Text = text);
                _servers = local ? await MultiplayerService.BrowseLanAsync((int)port.Value, _multiplayerCancellation.Token)
                    : await MultiplayerService.BrowseInternetAsync(master.Text, info, _multiplayerCancellation.Token);
                FilterServers(); status.Text = _servers.Count + " " + Strings["ServersFound"];
            }
            catch (OperationCanceledException) { status.Text = Strings["Cancelled"]; }
            catch (Exception error) { status.Text = error.Message; }
            finally { internet.IsEnabled = lan.IsEnabled = true; cancel.IsEnabled = false; progress.Visibility = Visibility.Collapsed; }
        }
        internet.Click += async (_, _) => await Browse(false); lan.Click += async (_, _) => await Browse(true); cancel.Click += (_, _) => _multiplayerCancellation?.Cancel();
        var ports = new ComboBox { Header = Strings["SourcePort"], ItemsSource = definitions.SourcePorts.Where(item => SaveGameService.SupportsManagedSaves(item.Executable)).ToArray(), DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 300 };
        var iwads = new ComboBox { Header = "IWAD", ItemsSource = definitions.Iwads, DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 300 };
        var direct = new TextBox { Header = Strings["ServerAddress"], PlaceholderText = "192.168.1.10:10666", MinWidth = 300 };
        var join = new Button { Content = Strings["JoinServer"] }; var joinDirect = new Button { Content = Strings["DirectConnect"] }; var checkFiles = new Button { Content = Strings["CheckServerFiles"] };
        var mods = new ListView { Header = Strings["HostModsNotice"], ItemsSource = ViewModel.CatalogItems.Where(item => item.Category == "Mod").OrderBy(item => item.Title).ToArray(), DisplayMemberPath = nameof(LibraryItem.Title), SelectionMode = ListViewSelectionMode.Multiple, Height = 160 };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not MultiplayerServer server) return;
            iwads.SelectedItem = definitions.Iwads.FirstOrDefault(item => item.InternalFileName.Equals(server.Iwad, StringComparison.OrdinalIgnoreCase));
            var compatible = definitions.SourcePorts.Where(item => Path.GetFileNameWithoutExtension(item.Executable).Equals("zandronum", StringComparison.OrdinalIgnoreCase)).ToArray();
            ports.SelectedItem = compatible.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Version) && server.Version.StartsWith(item.Version, StringComparison.OrdinalIgnoreCase))
                ?? compatible.OrderByDescending(item => Version.TryParse(item.Version, out var version) ? version : new Version(0, 0)).FirstOrDefault();
        };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { ports, iwads } });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { join, checkFiles } });
        panel.Children.Add(direct); panel.Children.Add(joinDirect);
        async Task Join(MultiplayerServer? server, bool launch)
        {
            if (ports.SelectedItem is not NativeSourcePortDefinition source || iwads.SelectedItem is not NativeIwadDefinition selectedIwad) throw new InvalidOperationException(Strings["MultiplayerSetupRequired"]);
            if (server is not null && !Path.GetFileNameWithoutExtension(source.Executable).Equals("zandronum", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(Strings["ZandronumRequired"]);
            if (server is not null && !server.Iwad.Equals(selectedIwad.InternalFileName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(Strings["ServerIwadMismatch"]);
            var endpoint = server?.Endpoint ?? await MultiplayerService.ResolveAddressAsync(direct.Text, (int)port.Value, _loadCancellation.Token);
            var files = server is null ? mods.SelectedItems.OfType<LibraryItem>().Select(item => new MultiplayerFileMatch(new(item.FileName, null), new(item.GameFileId, ""), null)).ToArray() : await MatchServerFilesAsync(server);
            if (files.Any(item => item.File is null))
            {
                foreach (var missing in files.Where(item => item.File is null)) await DownloadServerFileAsync(missing.Required);
                files = await MatchServerFilesAsync(server!);
                if (files.Any(item => item.File is null)) throw new InvalidOperationException(Strings["ServerFilesMissing"] + "\n" + string.Join('\n', files.Where(item => item.File is null).Select(item => item.Required.Name)));
            }
            if (!launch) { await ShowActionMessageAsync(Strings["CheckServerFiles"], Strings["ServerFilesReady"], Strings["Close"]); return; }
            var notice = Strings["JoinServerNotice"] + "\n" + endpoint + "\n" + (server?.Version ?? "") + "\n" + source.Name + " " + source.Version;
            if (server?.Files.Any(file => file.Md5 is null) == true) notice += "\n" + Strings["ServerChecksumsMissing"];
            if (await FeatureDialog(Strings["JoinServer"], new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap }, Strings["JoinServer"]).ShowAsync() != ContentDialogResult.Primary) return;
            var baseGame = ViewModel.CatalogItems.FirstOrDefault(item => item.FileName.Equals(selectedIwad.ArchiveFileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(Strings["MultiplayerSetupRequired"]);
            await ViewModel.LaunchFeatureRequestAsync(new GameLaunchRequest(baseGame.GameFileId, server?.Name ?? endpoint.ToString(), SourcePortId: source.SourcePortId,
                IwadId: selectedIwad.IwadId, Map: "", Skill: "", Multiplayer: new("join", endpoint.Address.ToString(), endpoint.Port, Mods: files.Select(item => item.File!).ToArray())), _loadCancellation.Token);
        }
        join.Click += async (_, _) => await FeatureActionAsync(() => Join(list.SelectedItem as MultiplayerServer ?? throw new InvalidOperationException(Strings["SelectServer"]), true));
        checkFiles.Click += async (_, _) => await FeatureActionAsync(() => Join(list.SelectedItem as MultiplayerServer ?? throw new InvalidOperationException(Strings["SelectServer"]), false));
        joinDirect.Click += async (_, _) => await FeatureActionAsync(() => Join(null, true));
        var players = new NumberBox { Header = Strings["Players"], Minimum = 2, Maximum = 64, Value = 2 };
        var mode = new ComboBox { Header = Strings["GameMode"], ItemsSource = new[] { "coop", "deathmatch", "teamplay" }, SelectedIndex = 0 };
        var host = new Button { Content = Strings["HostLan"] };
        panel.Children.Add(new Expander { Header = Strings["HostLan"], Content = new StackPanel { Spacing = 10, Children = { players, mode, mods, host } } });
        host.Click += async (_, _) => await FeatureActionAsync(async () =>
        {
            if (ports.SelectedItem is not NativeSourcePortDefinition source || iwads.SelectedItem is not NativeIwadDefinition selectedIwad) throw new InvalidOperationException(Strings["MultiplayerSetupRequired"]);
            var baseGame = ViewModel.CatalogItems.First(item => item.FileName.Equals(selectedIwad.ArchiveFileName, StringComparison.OrdinalIgnoreCase));
            var files = mods.SelectedItems.OfType<LibraryItem>().Select(item => new MultiplayerModFile(item.GameFileId, "")).ToArray();
            if (await FeatureDialog(Strings["HostLan"], new TextBlock { Text = Strings["HostLanNotice"] + "\n" + string.Join('\n', mods.SelectedItems.OfType<LibraryItem>().Select(item => item.Title)), TextWrapping = TextWrapping.Wrap }, Strings["HostLan"]).ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.LaunchFeatureRequestAsync(new GameLaunchRequest(baseGame.GameFileId, "LAN", SourcePortId: source.SourcePortId, IwadId: selectedIwad.IwadId,
                Multiplayer: new("host", null, (int)port.Value, (int)players.Value, mode.SelectedItem?.ToString() ?? "coop", files)), _loadCancellation.Token);
        });
        MultiplayerHost.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task<MultiplayerFileMatch[]> MatchServerFilesAsync(MultiplayerServer server)
    {
        var settings = await ViewModel.LoadNativeSettingsAsync(_loadCancellation.Token);
        var root = Path.GetFullPath(Path.Combine(GetPortableRoot(), settings.GameFileDirectory));
        var candidates = ViewModel.CatalogItems.Where(item => item.Category == "Mod").Select(item => (item.GameFileId, Path.Combine(root, item.FileName))).ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
        var result = await RunProgressDialogAsync(Strings["CheckServerFiles"], Strings["CheckingArchives"], progress => Task.Run(async () =>
        { var matches = await MultiplayerService.MatchFilesAsync(server, candidates, cancellation.Token); progress.Report(100); return matches; }, cancellation.Token), cancellation: cancellation);
        return result.ToArray();
    }

    private async Task DownloadServerFileAsync(ServerModFile required)
    {
        if (MultiplayerService.IsProtectedIwad(required.Name)) throw new InvalidOperationException(Strings["NoIwadDownload"]);
        var url = new TextBox { Header = "HTTPS URL", PlaceholderText = "https://…", Width = 570 };
        var panel = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = required.Name + "\n" + Strings["ServerDownloadNotice"], TextWrapping = TextWrapping.Wrap, MaxWidth = 570 }, url } };
        var find = new Button { Content = Strings["SearchIdgames"] }; panel.Children.Add(find);
        var links = new ListView { DisplayMemberPath = nameof(IdGamesItem.MatchLabel), Height = 140 };
        find.Click += async (_, _) =>
        {
            find.IsEnabled = false;
            try { links.ItemsSource = await _app.IdGamesService.FindMatchesAsync(required.Name, Path.GetFileNameWithoutExtension(required.Name), _loadCancellation.Token); }
            catch (Exception error) { panel.Children.Add(new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap }); }
            finally { find.IsEnabled = true; }
        };
        panel.Children.Add(links);
        var dialog = FeatureDialog(Strings["DownloadRequiredMod"], panel, Strings["DownloadRequiredMod"]);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
        var directory = Path.Combine(GetPortableRoot(), "Data", "Temp", "Multiplayer", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var downloaded = Path.Combine(directory, required.Name);
        try
        {
            await RunProgressDialogAsync(Strings["DownloadRequiredMod"], required.Name, async progress =>
            {
                if (links.SelectedItem is IdGamesItem match) await _app.IdGamesService.DownloadAsync(match, downloaded, progress, cancellation.Token);
                else await MultiplayerDownloadService.DownloadAsync(url.Text, downloaded, progress, cancellation.Token);
                return true;
            }, cancellation: cancellation);
            var prepared = await MultiplayerDownloadService.VerifyAndPrepareAsync(downloaded, required, cancellation.Token);
            await _app.NativeLibraryService.ImportAsync(prepared, ImportFileConflictResolution.Fail, cancellation.Token);
            await ViewModel.RefreshAsync(_loadCancellation.Token);
        }
        finally { foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }
}
