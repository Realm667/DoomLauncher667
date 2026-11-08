using DoomLauncher.Modern.Core.Launch;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private CancellationTokenSource? _multiplayerCancellation;
    private IReadOnlyList<MultiplayerServer> _servers = [];
    private DateTimeOffset _lastMasterQuery = DateTimeOffset.MinValue;

    private MultiplayerServerTable? _multiplayerTable;
    private int _multiplayerGeneration;

    private async Task InitializeMultiplayerAsync()
    {
        var generation = ++_multiplayerGeneration;
        _multiplayerCancellation?.Cancel();
        var previous = _multiplayerTable?.CaptureState();
        var definitions = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(_loadCancellation.Token);
        if (generation != _multiplayerGeneration) return;

        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(0, 4, 0, 20) };
        TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        Border Card(UIElement content) => new() { Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(10),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DoomSurfaceBrush"] };
        Grid Fields(params FrameworkElement[] controls)
        {
            var grid = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
            foreach (var control in controls) { control.HorizontalAlignment = HorizontalAlignment.Stretch; grid.Children.Add(control); }
            void Arrange(double width)
            {
                var columns = width >= 720 ? controls.Length : 1;
                if (grid.ColumnDefinitions.Count == columns) return;
                grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
                for (var index = 0; index < columns; index++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (var index = 0; index < (controls.Length + columns - 1) / columns; index++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (var index = 0; index < controls.Length; index++) { Grid.SetColumn(controls[index], index % columns); Grid.SetRow(controls[index], index / columns); }
            }
            Arrange(1000); grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width); return grid;
        }
        panel.Children.Add(Note(Strings["ServerBrowserIntro"]));
        var master = new TextBox { Header = Strings["MasterServer"], Text = MultiplayerService.DefaultMaster, MaxLength = 255 };
        var scanPort = new NumberBox { Header = Strings["LanScanPort"], Value = 10666, Minimum = 1, Maximum = 65535, SmallChange = 1 };
        var discoverySettings = new StackPanel { Spacing = 12, Children = { Fields(master, scanPort), Note(Strings["MultiplayerNotice"]) } };
        panel.Children.Add(new Expander { Header = Strings["ConnectionSettings"], Content = discoverySettings, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        var internet = new Button { Content = Strings["BrowseInternet"] };
        var lan = new Button { Content = Strings["BrowseLan"] };
        var cancel = new Button { Content = Strings["Cancel"], IsEnabled = false };
        var status = Note(Strings["ServerBrowserEmpty"]); Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var errors = new InfoBar { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
        var progress = new ProgressBar { IsIndeterminate = true, Height = 3, Visibility = Visibility.Collapsed,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DoomControlAccentBrush"] };
        var browseActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { internet, lan, cancel } };
        panel.Children.Add(browseActions); panel.Children.Add(status); panel.Children.Add(progress); panel.Children.Add(errors);

        var table = new MultiplayerServerTable(); _multiplayerTable = table; table.Configure(Strings, previous);
        table.SetServers(_servers, previous?.SelectedAddress); panel.Children.Add(table);
        var selectedTitle = Heading(Strings["SelectServer"]);
        var selectedSummary = Note(Strings["ServerSelectionHint"]);
        var requiredNames = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var technicalDetails = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        var required = new Expander { Header = Strings["RequiredMods"], Content = new ScrollViewer { Content = requiredNames, MaxHeight = 150 }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var technical = new Expander { Header = Strings["TechnicalDetails"], Content = new ScrollViewer { Content = technicalDetails, MaxHeight = 180 }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var ports = new ComboBox { Header = Strings["SourcePort"], ItemsSource = definitions.SourcePorts.Where(item => SaveGameService.SupportsManagedSaves(item.Executable)).ToArray(), DisplayMemberPath = "DisplayLabel", SelectedIndex = 0 };
        var iwads = new ComboBox { Header = "IWAD", ItemsSource = definitions.Iwads, DisplayMemberPath = "DisplayLabel", SelectedIndex = 0 };
        var join = new Button { Content = Strings["JoinServer"], Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var checkFiles = new Button { Content = Strings["CheckServerFiles"] };
        var getFiles = new Button { Content = Strings["GetRequiredMods"] };
        var definitionHint = Note(Strings["MultiplayerSetupRequired"]);
        var joinActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { join, checkFiles, getFiles } };
        var joinPanel = new StackPanel { Spacing = 12, Children = { selectedTitle, selectedSummary, required, technical,
            Heading(Strings["LaunchConfiguration"]), Fields(ports, iwads), definitionHint, joinActions } };
        panel.Children.Add(Card(joinPanel));
        var browsing = false; string? selectedAddress = null;
        void UpdateActions()
        {
            var selected = table.SelectedServer is not null;
            join.IsEnabled = selected && !browsing;
            checkFiles.IsEnabled = getFiles.IsEnabled = selected && !browsing;
            definitionHint.Visibility = ports.SelectedItem is null || iwads.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;
        }
        void SelectServer()
        {
            if (table.SelectedServer is not MultiplayerServer server)
            {
                selectedTitle.Text = Strings["SelectServer"]; selectedSummary.Text = Strings["ServerSelectionHint"];
                required.Visibility = technical.Visibility = Visibility.Collapsed; selectedAddress = null; UpdateActions(); return;
            }
            selectedTitle.Text = server.Name;
            selectedSummary.Text = $"{server.PlayersText} {Strings["Players"]} · {server.GameMode} · {server.Map}\n{server.Iwad} · Zandronum {server.Version} · {server.PingText}"
                + (server.PasswordRequired ? "\n" + Strings["ServerPasswordNotice"] : "");
            required.Header = $"{Strings["RequiredMods"]} ({server.Files.Count})";
            requiredNames.Text = server.Files.Count == 0 ? Strings["NoAdditionalMods"] : string.Join('\n', server.Files.Select(file => file.Name));
            technicalDetails.Text = $"{Strings["Address"]}: {server.Endpoint}\n\n" + string.Join('\n', server.Files.Select(file => file.Name + "\nMD5: " + (file.Md5 ?? Strings["ChecksumUnavailable"])));
            required.Visibility = technical.Visibility = Visibility.Visible;
            if (selectedAddress != server.AddressText)
            {
                iwads.SelectedItem = definitions.Iwads.FirstOrDefault(item => item.InternalFileName.Equals(server.Iwad, StringComparison.OrdinalIgnoreCase));
                var compatible = definitions.SourcePorts.Where(item => Path.GetFileNameWithoutExtension(item.Executable).Equals("zandronum", StringComparison.OrdinalIgnoreCase)).ToArray();
                ports.SelectedItem = compatible.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Version) && server.Version.StartsWith(item.Version, StringComparison.OrdinalIgnoreCase))
                    ?? compatible.OrderByDescending(item => Version.TryParse(item.Version, out var version) ? version : new Version(0, 0)).FirstOrDefault();
            }
            selectedAddress = server.AddressText; UpdateActions();
        }
        table.SelectedServerChanged += (_, _) => SelectServer();
        ports.SelectionChanged += (_, _) => UpdateActions(); iwads.SelectionChanged += (_, _) => UpdateActions();
        SelectServer();
        async Task Browse(bool local)
        {
            if (!local && DateTimeOffset.UtcNow - _lastMasterQuery < TimeSpan.FromSeconds(10)) { status.Text = Strings["ServerQueryCooldown"]; return; }
            _multiplayerCancellation?.Cancel(); _multiplayerCancellation?.Dispose();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token); _multiplayerCancellation = cancellation;
            browsing = true; internet.IsEnabled = lan.IsEnabled = false; cancel.IsEnabled = true; progress.Visibility = Visibility.Visible; errors.IsOpen = false; UpdateActions();
            try
            {
                if (!local) _lastMasterQuery = DateTimeOffset.UtcNow;
                status.Text = Strings[local ? "FindingLanServers" : "FindingInternetServers"];
                var info = new Progress<string>(text => { if (generation == _multiplayerGeneration) status.Text = Strings["QueryingServers"] + " · " + text; });
                var result = local ? await MultiplayerService.BrowseLanAsync((int)scanPort.Value, cancellation.Token)
                    : await MultiplayerService.BrowseInternetAsync(master.Text, info, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != _multiplayerGeneration) return;
                _servers = result; table.SetServers(result); status.Text = result.Count + " " + Strings["ServersFound"] + " · " + DateTime.Now.ToString("t");
            }
            catch (OperationCanceledException) { status.Text = Strings["Cancelled"]; }
            catch (Exception error) { errors.Message = error.Message; errors.IsOpen = true; status.Text = Strings["ServerQueryFailed"]; }
            finally { browsing = false; internet.IsEnabled = lan.IsEnabled = true; cancel.IsEnabled = false; progress.Visibility = Visibility.Collapsed; UpdateActions(); }
        }
        internet.Click += async (_, _) => await Browse(false); lan.Click += async (_, _) => await Browse(true); cancel.Click += (_, _) => _multiplayerCancellation?.Cancel();

        var hostPort = new NumberBox { Header = Strings["ServerPort"], Value = 10666, Minimum = 1, Maximum = 65535, SmallChange = 1 };
        var players = new NumberBox { Header = Strings["Players"], Minimum = 2, Maximum = 64, Value = 2, SmallChange = 1 };
        var mode = new ComboBox { Header = Strings["GameMode"], ItemsSource = new[] { "coop", "deathmatch", "teamplay" }, SelectedIndex = 0 };
        var direct = new TextBox { Header = Strings["ServerAddress"], PlaceholderText = "192.168.1.10:10666", MaxLength = 255 };
        var joinDirect = new Button { Content = Strings["DirectConnect"], VerticalAlignment = VerticalAlignment.Bottom };
        var host = new Button { Content = Strings["HostLan"] };
        var allMods = ViewModel.CatalogItems.Where(item => item.Category == "Mod").OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var selectedMods = new HashSet<int>(); var replacingMods = false;
        var modFilter = new TextBox { PlaceholderText = Strings["Filter"] };
        var mods = new ListView { ItemsSource = allMods, DisplayMemberPath = nameof(LibraryItem.Title), SelectionMode = ListViewSelectionMode.Multiple, Height = 180 };
        var modCount = Note(string.Format(Strings["SelectedModsCount"], 0));
        mods.SelectionChanged += (_, args) =>
        {
            if (replacingMods) return;
            foreach (var item in args.AddedItems.OfType<LibraryItem>()) selectedMods.Add(item.GameFileId);
            foreach (var item in args.RemovedItems.OfType<LibraryItem>()) selectedMods.Remove(item.GameFileId);
            modCount.Text = string.Format(Strings["SelectedModsCount"], selectedMods.Count);
        };
        modFilter.TextChanged += (_, _) =>
        {
            replacingMods = true; mods.ItemsSource = allMods.Where(item => item.Title.Contains(modFilter.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            foreach (var item in mods.Items.OfType<LibraryItem>().Where(item => selectedMods.Contains(item.GameFileId))) mods.SelectedItems.Add(item);
            replacingMods = false;
        };
        LibraryItem[] ChosenMods() => allMods.Where(item => selectedMods.Contains(item.GameFileId)).ToArray();
        var optionalMods = new Expander { Header = Strings["HostModsNotice"], HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { modFilter, mods, modCount } } };
        var hostPanel = new StackPanel { Spacing = 12, Children = { Note(Strings["SharedLaunchConfiguration"]), optionalMods,
            Fields(hostPort, players, mode), host, Heading(Strings["DirectConnect"]), Fields(direct, joinDirect) } };
        panel.Children.Add(new Expander { Header = Strings["HostAndDirectConnect"], Content = hostPanel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });

        async Task InspectFiles(MultiplayerServer server, bool download)
        {
            var matches = await MatchServerFilesAsync(server);
            if (download)
            {
                foreach (var missing in matches.Where(item => item.File is null)) await DownloadServerFileAsync(missing.Required);
                matches = await MatchServerFilesAsync(server);
            }
            var missingFiles = matches.Where(item => item.File is null).Select(item => item.Required.Name).ToArray();
            await ShowActionMessageAsync(Strings["CheckServerFiles"], missingFiles.Length == 0 ? Strings["ServerFilesReady"]
                : Strings["ServerFilesMissing"] + "\n\n" + string.Join('\n', missingFiles), Strings["Close"]);
        }
        async Task Join(MultiplayerServer? server)
        {
            if (ports.SelectedItem is not NativeSourcePortDefinition source || iwads.SelectedItem is not NativeIwadDefinition selectedIwad) throw new InvalidOperationException(Strings["MultiplayerSetupRequired"]);
            if (server is not null && !Path.GetFileNameWithoutExtension(source.Executable).Equals("zandronum", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(Strings["ZandronumRequired"]);
            if (server is not null && !server.Iwad.Equals(selectedIwad.InternalFileName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(Strings["ServerIwadMismatch"]);
            var endpoint = server?.Endpoint ?? await MultiplayerService.ResolveAddressAsync(direct.Text, (int)hostPort.Value, _loadCancellation.Token);
            var files = server is null ? ChosenMods().Select(item => new MultiplayerFileMatch(new(item.FileName, null), new(item.GameFileId, ""), null)).ToArray() : await MatchServerFilesAsync(server);
            if (files.Any(item => item.File is null))
            {
                foreach (var missing in files.Where(item => item.File is null)) await DownloadServerFileAsync(missing.Required);
                files = await MatchServerFilesAsync(server!);
                if (files.Any(item => item.File is null)) throw new InvalidOperationException(Strings["ServerFilesMissing"] + "\n" + string.Join('\n', files.Where(item => item.File is null).Select(item => item.Required.Name)));
            }
            var notice = Strings["JoinServerNotice"] + "\n" + endpoint + "\n" + (server?.Version ?? "") + "\n" + source.DisplayLabel;
            if (server?.Files.Any(file => file.Md5 is null) == true) notice += "\n" + Strings["ServerChecksumsMissing"];
            if (await FeatureDialog(Strings["JoinServer"], Note(notice), Strings["JoinServer"]).ShowAsync() != ContentDialogResult.Primary) return;
            var baseGame = ViewModel.CatalogItems.FirstOrDefault(item => item.FileName.Equals(selectedIwad.ArchiveFileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(Strings["MultiplayerSetupRequired"]);
            await ViewModel.LaunchFeatureRequestAsync(new GameLaunchRequest(baseGame.GameFileId, server?.Name ?? endpoint.ToString(), SourcePortId: source.SourcePortId,
                IwadId: selectedIwad.IwadId, Map: "", Skill: "", Multiplayer: new("join", endpoint.Address.ToString(), endpoint.Port, Mods: files.Select(item => item.File!).ToArray())), _loadCancellation.Token);
        }
        join.Click += async (_, _) => await FeatureActionAsync(() => Join(table.SelectedServer ?? throw new InvalidOperationException(Strings["SelectServer"])));
        checkFiles.Click += async (_, _) => await FeatureActionAsync(() => InspectFiles(table.SelectedServer ?? throw new InvalidOperationException(Strings["SelectServer"]), false));
        getFiles.Click += async (_, _) => await FeatureActionAsync(() => InspectFiles(table.SelectedServer ?? throw new InvalidOperationException(Strings["SelectServer"]), true));
        joinDirect.Click += async (_, _) => await FeatureActionAsync(() => Join(null));
        host.Click += async (_, _) => await FeatureActionAsync(async () =>
        {
            if (ports.SelectedItem is not NativeSourcePortDefinition source || iwads.SelectedItem is not NativeIwadDefinition selectedIwad) throw new InvalidOperationException(Strings["MultiplayerSetupRequired"]);
            var baseGame = ViewModel.CatalogItems.FirstOrDefault(item => item.FileName.Equals(selectedIwad.ArchiveFileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(Strings["MultiplayerSetupRequired"]);
            var chosen = ChosenMods();
            if (await FeatureDialog(Strings["HostLan"], Note(Strings["HostLanNotice"] + "\n" + string.Join('\n', chosen.Select(item => item.Title))), Strings["HostLan"]).ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.LaunchFeatureRequestAsync(new GameLaunchRequest(baseGame.GameFileId, "LAN", SourcePortId: source.SourcePortId, IwadId: selectedIwad.IwadId,
                Multiplayer: new("host", null, (int)hostPort.Value, (int)players.Value, mode.SelectedItem?.ToString() ?? "coop", chosen.Select(item => new MultiplayerModFile(item.GameFileId, "")).ToArray())), _loadCancellation.Token);
        });
        MultiplayerHost.Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
