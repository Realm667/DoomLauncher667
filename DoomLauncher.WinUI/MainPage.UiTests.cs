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
            await Layout();
            Check(GameList.IsLoaded && LibraryDetailsPane.IsLoaded, "Native list and shared details are loaded");
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
        }
        catch (Exception error) { failure = error.ToString(); }
        var report = Path.Combine(root, "ui-test-report.json");
        await File.WriteAllTextAsync(report + ".tmp", JsonSerializer.Serialize(new { Passed = failure is null, Checks = checks, Error = failure }));
        File.Move(report + ".tmp", report);
        _app.MainWindow!.Close();
    }
}
