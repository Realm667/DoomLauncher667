using System.Text.Json;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using DoomLauncher.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    // Opt-in native integration checks, executable only against an explicitly marked isolated fixture.
    private async Task RunNativeUiChecksAsync()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke-test")) return;
        var root = Environment.GetEnvironmentVariable("DOOMLAUNCHER_UI_TEST_ROOT");
        if (root is null || !File.Exists(Path.Combine(root, ".ui-test-instance"))
            || !Path.GetFullPath(GetPortableRoot()).TrimEnd('\\').Equals(Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native UI tests require an isolated marked installation.");
        var checks = new List<string>(); string? failure = null;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        async Task Layout()
        {
            for (var index = 0; index < 3; index++)
            {
                var tick = new TaskCompletionSource();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                { GameList.UpdateLayout(); GameGrid.UpdateLayout(); tick.SetResult(); });
                await tick.Task;
            }
        }
        async Task Navigate(LibrarySection section)
        {
            ShellNavigation.SelectedItem = ShellNavigation.MenuItems.OfType<NavigationViewItem>()
                .Single(item => item.Tag?.ToString() == section.ToString());
            await Layout();
        }
        try
        {
            var nativeTestScale = XamlRoot.RasterizationScale;
            _app.MainWindow!.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1440*nativeTestScale),(int)(950*nativeTestScale)));
            await Layout();
            ApplyResponsiveLayout(); await Layout();
            Check(GameList.IsLoaded && LibraryDetailsPane.IsLoaded, "Native list and shared details are loaded");
            ((Expander)LibraryDetailsPane.FindName("EntryToolsExpander")).IsExpanded = true;
            await Layout();
            var iconWindow = WinRT.Interop.WindowNative.GetWindowHandle(_app.MainWindow!);
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.ico");
            Check(WindowIconMatchesAsset(iconWindow, 0, iconPath) && WindowIconMatchesAsset(iconWindow, 1, iconPath),
                "Native small and large window icons match the shipped launcher artwork");
            var navigationItems = ShellNavigation.MenuItems.ToList();
            var multiplayerIndex = navigationItems.FindIndex(item => item is NavigationViewItem { Tag: "Multiplayer" });
            Check(multiplayerIndex >= 0 && navigationItems[multiplayerIndex + 1] is NavigationViewItemSeparator
                && navigationItems[multiplayerIndex + 2] is NavigationViewItem { Tag: "Achievements" }
                && navigationItems[multiplayerIndex - 1] is NavigationViewItemSeparator
                && navigationItems[multiplayerIndex - 2] is NavigationViewItem { Tag: "Downloads" },
                "Multiplayer is separated from both Latest and Achievements");
            var ratingRow = (Grid)LibraryDetailsPane.FindName("PersonalRatingRow");
            var ratingLabel = (TextBlock)LibraryDetailsPane.FindName("PersonalRatingLabel");
            var ratingStars = (RatingControl)LibraryDetailsPane.FindName("PersonalRatingStars");
            var labelTop = ratingLabel.TransformToVisual(ratingRow).TransformPoint(new Windows.Foundation.Point()).Y;
            var starsTop = ratingStars.TransformToVisual(ratingRow).TransformPoint(new Windows.Foundation.Point()).Y;
            Check(Grid.GetColumn(ratingLabel) == 0 && Grid.GetColumn(ratingStars) == 1
                && Math.Abs(labelTop + ratingLabel.ActualHeight / 2 - starsTop - ratingStars.ActualHeight / 2) < 1,
                "My rating label and stars share one vertically centered native row");
            var actions = (Grid)LibraryDetailsPane.FindName("CollectionActionsRow");
            var queueButton = (Button)LibraryDetailsPane.FindName("PlayLaterButton");
            var collectionButton = (Button)LibraryDetailsPane.FindName("AddToCollectionButton");
            Check(actions.ColumnDefinitions.Count == 2 && actions.ColumnDefinitions.All(column => column.Width.IsStar)
                && Grid.GetColumn(queueButton) == 0 && Grid.GetColumn(collectionButton) == 1
                && Math.Abs(queueButton.ActualWidth - collectionButton.ActualWidth) < 1,
                "Play later and collection actions share an equal-width native row");
            Check(KeyboardAccelerators.Any(item => item.Key == Windows.System.VirtualKey.I)
                && KeyboardAccelerators.Any(item => item.Key == Windows.System.VirtualKey.Escape), "Import/search/back keyboard navigation is registered");
            ViewModel.Filter("Seed"); SearchBox.Text = "Seed";
            ViewModel.SetSortOrder(LibrarySortOrder.Title, toggleDirection: true);
            await Layout();
            var selection = ViewModel.Games[3].GameFileId; ViewModel.SelectGame(selection); SyncSelection();
            FindDescendant<ScrollViewer>(GameList)?.ChangeView(null, 220, null, true); await Layout();
            var offset = FindDescendant<ScrollViewer>(GameList)?.VerticalOffset ?? 0;
            await Navigate(LibrarySection.Favorites); await Navigate(LibrarySection.Library);
            Check(SearchBox.Text == "Seed" && ViewModel.SortDescending && ViewModel.SelectedGame.GameFileId == selection,
                "Navigation restores search, sort and selected item");
            Check(Math.Abs((FindDescendant<ScrollViewer>(GameList)?.VerticalOffset ?? 0) - offset) < 5,
                "Navigation restores list scroll position");
            var result = await ViewModel.ImportFilesAsync(new[] {
                new ModImportRequest(Path.Combine(root, "UIInputs", "ui-alpha.zip"), ImportFileConflictResolution.Fail),
                new ModImportRequest(Path.Combine(root, "UIInputs", "ui-beta.zip"), ImportFileConflictResolution.Fail) }, null, default);
            await Layout();
            Check(result.Imported == 2 && ViewModel.ActiveSection == LibrarySection.Downloads && SearchBox.Text.Length == 0,
                "Batch import reveals Latest and clears hiding filters");
            Check(ViewModel.Games[0].Title == "UI Beta" && ViewModel.SelectedGame.GameFileId == ViewModel.Games[0].GameFileId
                && (FindDescendant<ScrollViewer>(GameList)?.VerticalOffset ?? 0) < 1, "Latest selects newest entry and scrolls to top");
            Check(ViewModel.Games[0].NewBadgeVisibility == Visibility.Visible && ViewModel.Games[0].ImportedOnText.Length > 0,
                "New badge and import-date tooltip are available");
            var menu = (MenuFlyout)Resources["LibraryItemContextMenu"];
            menu.ShowAt(GameList); await Layout();
            Check(menu.Items.OfType<MenuFlyoutItem>().All(item => !string.IsNullOrWhiteSpace(item.Text)), "Context menu labels populate without restart");
            menu.Hide();
            ViewModeButton_Click(this, new RoutedEventArgs());
            await Layout();
            Check(GameGrid.Items.Count == ViewModel.Games.Count && GameGrid.ContainerFromIndex(0) is not null, "Tile view renders imported entries");
            await ViewModel.LoadLaunchOptionsAsync();
            Check(ViewModel.SourcePortOptions.Count > 0 && ViewModel.IwadOptions.Count > 0, "Launch choices populate in the shared details workflow");
            await ViewModel.LaunchSelectedAsync();
            Check(ViewModel.LaunchErrorMessage == Strings["MissingSourcePortAction"], "Missing launch definition has localized actionable error");
            var portDirectory = Path.Combine(root, "Data", "Sourceports", "UI-test-port");
            Directory.CreateDirectory(portDirectory);
            await File.WriteAllTextAsync(Path.Combine(portDirectory, "ui-test.exe"), "test fixture; never executed");
            await _app.NativeLibraryService.SaveSourcePortAsync(new NativeSourcePortDefinition(null, "UI test port",
                portDirectory, "ui-test.exe", ".wad", "-file", string.Empty));
            await ViewModel.RefreshAsync(); await ViewModel.LoadLaunchOptionsAsync();
            ViewModel.SelectedSourcePortOption = ViewModel.SourcePortOptions.First(choice => choice.Id.HasValue);
            var chosenPort = ViewModel.SelectedSourcePortOption.Id;
            var chosenGame = ViewModel.SelectedGame.GameFileId;
            await ViewModel.SaveLaunchDefinitionsAsync();
            Check((await _app.NativeLibraryService.LoadGameAsync(chosenGame)).SourcePortId == chosenPort,
                "Details launch assignment persists without altering mod metadata");
            await ViewModel.SetPersonalRatingAsync(chosenGame, 4); await Layout();
            Check(ViewModel.PersonalRatingValue == 4 && FindDescendant<RatingControl>(LibraryDetailsPane)?.Value == 4,
                "Personal stars persist and bind to the native details control");
            await ViewModel.ToggleQueueAsync(); await Navigate(LibrarySection.Queue);
            Check(ViewModel.Games.Any(game => game.GameFileId == chosenGame), "Play later sidebar displays queued entries");
            await _app.LibraryFeatures.RecordValidatedMapsAsync(chosenGame, new HashSet<string> { "MAP01" }); await ViewModel.ReloadPersonalDataAsync();
            // PropertyChanged queues native binding work; await the dispatcher before inspecting controls.
            await Layout();
            Check(ViewModel.SelectedPersonalData.CompletedMaps.Contains("MAP01"), "Map progress reloads into shared details");
            Check(!ViewModel.CanTrackSelectedGame && LibraryDetailsPane.FindName("MapProgressSummary") is TextBlock { Visibility: Visibility.Collapsed }
                && LibraryDetailsPane.FindName("MapsProgressButton") is Button { Content: var mapLabel } && (string?)mapLabel == Strings["Maps"],
                $"Unsupported source port hides progress and retains a plain Maps button (tracking={ViewModel.CanTrackSelectedGame}, visibility={((TextBlock)LibraryDetailsPane.FindName("MapProgressSummary")).Visibility}, label={((Button)LibraryDetailsPane.FindName("MapsProgressButton")).Content})");
            var apiPath = Path.Combine(portDirectory, "ui-test.pk3");
            using (var zip = System.IO.Compression.ZipFile.Open(apiPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("zscript/doombase.zs").Open())) writer.Write("LevelInfo info int clusterflags");
                using (var writer = new StreamWriter(zip.CreateEntry("zscript/events.zs").Open())) writer.Write("UiTick() NextMap OnEngineInitialize()");
            }
            await ViewModel.RefreshAsync(); await Layout();
            Check(ViewModel.CanTrackSelectedGame && LibraryDetailsPane.FindName("MapProgressSummary") is TextBlock { Visibility: Visibility.Visible }
                && LibraryDetailsPane.FindName("MapsProgressButton") is Button { Content: var trackingLabel } && (string?)trackingLabel == Strings["MapsAndProgress"],
                "Compatible engine API reveals progress without restarting the application");
            File.Delete(apiPath); await ViewModel.RefreshAsync(); await Layout();
            Check(!ViewModel.CanTrackSelectedGame && ViewModel.SelectedPersonalData.CompletedMaps.Contains("MAP01")
                && LibraryDetailsPane.FindName("MapProgressSummary") is TextBlock { Visibility: Visibility.Collapsed },
                "Lost compatibility hides tracking immediately but preserves recorded completions");
            Check(CreateMapProgressItem(new MapDocument("MAP01", "Test", "fixture.wad", "UDMF", [], [], 0, 0, null, null, null), true, false).Content is TextBlock { Text: "MAP01 · Test" },
                "Unsupported map browser omits completion symbols");
            Check(CreateMapProgressItem(new MapDocument("MAP01", "Test", "fixture.wad", "UDMF", [], [], 0, 0, null, null, null), true).Content is TextBlock
                && typeof(LibraryFeatureStore).GetMethod("SetMapFinishedAsync") is null,
                "Map progress has read-only status rows and no manual completion API");
            await Navigate(LibrarySection.Library); await Layout();
            LibraryToolsButton.Flyout.ShowAt(LibraryToolsButton); await Layout();
            Check(LibraryToolsButton.IsLoaded && ((MenuFlyout)LibraryToolsButton.Flyout).Items.OfType<MenuFlyoutItem>().All(item => !string.IsNullOrWhiteSpace(item.Text)),
                "Library tools are visible and all feature actions are labeled");
            LibraryToolsButton.Flyout.Hide();
            Check(((MenuFlyout)LibraryToolsButton.Flyout).Items.OfType<MenuFlyoutItem>().Select(item => item.Tag?.ToString())
                .SequenceEqual(new[] { "RandomMod", "BulkEdit", "PlayLater" })
                && typeof(MainPage).Assembly.GetType("DoomLauncher.WinUI.Services.PersonalSyncService") is null,
                "Retired synchronization is absent from native tools and compiled application");
            Check(Strings["ImportBatchTitle"] != "ImportBatchTitle" && Strings["Maintenance"] != "Maintenance", "New workflow labels resolve");
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "DoomLauncher.sqlite"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync(); await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO IWads(Name,FileName,GameFileID) VALUES('UI IWAD','DOOM.WAD',$game); INSERT INTO WinUI_IdGamesMetadata(GameFileID,IdGamesID,ArchiveDirectory,ScrapedAt) VALUES($game,16276,'levels/doom/','2026-07-29');";
                command.Parameters.AddWithValue("$game", chosenGame); await command.ExecuteNonQueryAsync();
            }
            await ViewModel.RefreshAsync(); await Navigate(LibrarySection.Library);
            ViewModel.Filter(string.Empty); ViewModel.SelectGame(chosenGame); SyncSelection(); await Layout();
            Check(!ViewModel.CanRefreshModMetadata && LibraryDetailsPane.FindName("RefreshModMetadataButton") is Button { IsEnabled: false },
                "IWAD details disable mod metadata refresh");
            try { await ViewModel.FindSelectedIdGamesMatchesAsync(); throw new Exception("IWAD search was allowed"); }
            catch (InvalidOperationException error) { Check(error.Message == Strings["IwadMetadataProtected"], "IWAD metadata search is blocked with localized guidance"); }
            var bulk = await ViewModel.RefreshLinkedIdGamesMetadataAsync();
            Check(bulk.Total == 0, "Bulk mod refresh excludes linked IWADs before network requests");
            var featuredFavorites = ViewModel.CatalogItems.Where(game => game.Category == "Mod").Take(3).ToArray();
            foreach (var favorite in featuredFavorites)
            {
                ViewModel.SelectGame(favorite.GameFileId);
                if (!favorite.IsFavorite) await ViewModel.ToggleSelectedFavoriteAsync();
            }
            await Navigate(LibrarySection.Home);
            Check(featuredFavorites.Any(game => game.GameFileId == ViewModel.HomeHero.GameFileId), "Home Featured selects a favorite");
            var previousFeatured = ViewModel.HomeHero.GameFileId;
            for (var index = 0; index < 6; index++)
            {
                await Navigate(LibrarySection.Library); await Navigate(LibrarySection.Home);
                Check(ViewModel.HomeHero.GameFileId != previousFeatured
                    && featuredFavorites.Any(game => game.GameFileId == ViewModel.HomeHero.GameFileId),
                    $"Home return {index + 1} rotates Featured to another favorite");
                previousFeatured = ViewModel.HomeHero.GameFileId;
            }
            await Navigate(LibrarySection.Multiplayer);
            await InitializeMultiplayerAsync(); await Layout();
            var table = _multiplayerTable!;
            var fixtureServers = new[] {
                new MultiplayerServer(new(System.Net.IPAddress.Loopback, 10666), "Alpha", "MAP01", "Cooperative", "doom2.wad", "3.2", 2, 8, false, 15, []),
                new MultiplayerServer(new(System.Net.IPAddress.Loopback, 10667), "Beta", "MAP02", "Deathmatch", "doom2.wad", "3.2", 10, 16, true, 100, []) };
            table.SetServers(fixtureServers); await Layout();
            Check(((TextBox)table.FindName("Search")).PlaceholderText == Strings["ServerFilter"]
                && ((CheckBox)table.FindName("PopulatedOnly")).Content?.ToString() == Strings["PopulatedServers"]
                && ((CheckBox)table.FindName("HideFull")).Content?.ToString() == Strings["HideFullServers"],
                "Native server search and occupancy filters display translated labels");
            Check(table.HeaderButtons.Count == 9 && table.Rows.Items.Count == 2
                && table.Rows.ContainerFromIndex(0) is not null, "Native multiplayer table renders nine labeled columns and server rows");
            table.SelectAddress(fixtureServers[0].AddressText); table.SortBy(ServerSortColumn.Ping); await Layout();
            Check(((MultiplayerServer)table.Rows.Items[0]).Ping == 15 && table.SelectedServer?.Name == "Alpha",
                "Server table sorts ping numerically and retains selection");
            table.FilterText = "BETA map02"; await Layout();
            Check(table.Rows.Items.Count == 1 && table.SelectedServer is null,
                "Server filter matches multiple terms and clears hidden selection");
            table.FilterText = ""; table.SelectAddress(fixtureServers[1].AddressText);
            var tableState = table.CaptureState();
            table.Configure(Strings, tableState); table.SetServers(fixtureServers, tableState.SelectedAddress); await Layout();
            Check(table.SelectedServer?.Name == "Beta" && Strings["ServerName"] != "ServerName"
                && Strings["HostAndDirectConnect"] != "HostAndDirectConnect", "Server selection and translated table settings restore without network requests");
            await RunPolishUiChecksAsync(root, Check, Layout);
        }
        catch (Exception error) { failure = error.ToString(); }
        var report = Path.Combine(root, "ui-test-report.json");
        await File.WriteAllTextAsync(report + ".tmp", JsonSerializer.Serialize(new { Passed = failure is null, Checks = checks, Error = failure }));
        File.Move(report + ".tmp", report);
        _app.MainWindow!.Close();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint ReadWindowIcon(nint window, uint message, nint kind, nint parameter);

    private static bool WindowIconMatchesAsset(nint window, int kind, string path)
    {
        var handle = ReadWindowIcon(window, 0x007F, kind, 0); // WM_GETICON; ICON_SMALL/ICON_BIG
        if (handle == 0) return false;
        using var borrowed = System.Drawing.Icon.FromHandle(handle); // Does not own the native HICON.
        using var expected = new System.Drawing.Icon(path, borrowed.Size);
        using var actualBitmap = borrowed.ToBitmap();
        using var expectedBitmap = expected.ToBitmap();
        if (actualBitmap.Size != expectedBitmap.Size) return false;
        for (var y = 0; y < actualBitmap.Height; y++)
            for (var x = 0; x < actualBitmap.Width; x++)
            {
                var actual = actualBitmap.GetPixel(x, y); var target = expectedBitmap.GetPixel(x, y);
                if (actual.A != target.A || (actual.A != 0 && actual.ToArgb() != target.ToArgb())) return false;
            }
        return true;
    }
}
