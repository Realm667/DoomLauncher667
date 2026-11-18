using System.Diagnostics;
using System.Text.Json;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using DoomLauncher.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    // Explicit, marked synthetic installations only. Includes the real ItemsSource,
    // selection bindings and native layout, rather than timing a detached LINQ query.
    private async Task RunPerformanceChecksAsync()
    {
        if (!Environment.GetCommandLineArgs().Any(arg => arg is "--ui-performance-test" or "--ui-startup-test" or "--ui-collection-test" or "--ui-refresh-visual-test")) return;
        var root = Environment.GetEnvironmentVariable("DOOMLAUNCHER_UI_TEST_ROOT");
        if (root is null || !File.Exists(Path.Combine(root, ".ui-test-instance"))
            || !Path.GetFullPath(GetPortableRoot()).TrimEnd('\\').Equals(Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Performance tests require an isolated marked installation.");
        var results = new Dictionary<string, List<double>>();
        var checks = new List<string>();
        var failedChecks = new List<string>();
        var diagnostics = new Dictionary<string, double> { ["rasterization-scale"] = XamlRoot.RasterizationScale };
        void Check(bool condition, string name)
        { if (!condition) failedChecks.Add(name); else checks.Add(name); }
        async Task Layout()
        {
            for (var i = 0; i < 3; i++)
            {
                var done = new TaskCompletionSource();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                { GameList.UpdateLayout(); GameGrid.UpdateLayout(); done.SetResult(); });
                await done.Task;
            }
            // Dispatcher-idle alone precedes native scroll/layout commits.
            // Observe two real frames before timing completion or reading state.
            for (var i = 0; i < 2; i++)
            {
                var frame = new TaskCompletionSource();
                EventHandler<object>? rendered = null;
                rendered = (_, _) => frame.TrySetResult();
                CompositionTarget.Rendering += rendered;
                try { await frame.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { CompositionTarget.Rendering -= rendered; }
            }
        }
        async Task Measure(string name, Func<Task> action)
        {
            var clock = Stopwatch.StartNew();
            await action(); await Layout();
            if (!results.TryGetValue(name, out var values)) results[name] = values = [];
            values.Add(clock.Elapsed.TotalMilliseconds);
        }
        async Task Navigate(LibrarySection section)
        {
            ShellNavigation.SelectedItem = ShellNavigation.MenuItems.OfType<NavigationViewItem>()
                .Single(item => item.Tag?.ToString() == section.ToString());
            await Layout();
        }
        string? error = null;
        try
        {
            await Layout();
            results["process-start-to-library"] = [(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds];
            var count = ViewModel.Games.Count;
            Check(count >= 65 && GameList.ContainerFromIndex(0) is not null, "Library is rendered");
            var realized = Enumerable.Range(0, count).Count(i => GameList.ContainerFromIndex(i) is not null);
            Check(realized < Math.Min(100, count), $"List virtualization: {realized}/{count}");
            if (Environment.GetCommandLineArgs().Contains("--ui-startup-test")) return;
            if (Environment.GetCommandLineArgs().Contains("--ui-refresh-visual-test"))
            {
                _app.MainWindow!.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)(1440 * XamlRoot.RasterizationScale), (int)(900 * XamlRoot.RasterizationScale)));
                await Layout();
                ViewModel.SelectedGame = ViewModel.Games[2]; SyncSelection();
                FindDescendant<ScrollViewer>(GameList)?.ChangeView(null, 0, null, true); await Layout();
                var visualSelectedId = ViewModel.SelectedGame.GameFileId;
                await ViewModel.RefreshAsync(); await Layout();
                Check(ViewModel.SelectedGame.GameFileId == visualSelectedId && ReferenceEquals(GameList.SelectedItem, ViewModel.SelectedGame),
                    "Refresh retains native selection");
                FindDescendant<ScrollViewer>(GameList)?.ChangeView(null, 0, null, true); await Layout();
                await CapturePolishAsync(Path.Combine(root, "refresh-selection.png"), 1);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--ui-collection-test"))
            {
                await Measure("open-collection", () => Navigate(LibrarySection.Collections));
                var lists = Descendants(CollectionListHost).OfType<ListView>().ToArray();
                diagnostics["collection-realized"] = lists.Sum(list => Enumerable.Range(0, list.Items.Count).Count(i => list.ContainerFromIndex(i) is not null));
                diagnostics["collection-items"] = lists.Sum(list => list.Items.Count);
                if (Environment.GetCommandLineArgs().Contains("--assert-collection-virtualization"))
                    Check(diagnostics["collection-realized"] < Math.Min(100, diagnostics["collection-items"]), "Collection rows are virtualized");
                return;
            }
            var collectionGroups = ViewModel.CollectionGroups;
            for (var run = 0; run < 3; run++)
            {
                await Measure("search", () => { ViewModel.Filter("Seed 010"); SyncSelection(); return Task.CompletedTask; });
                Check(ViewModel.Games.Count > 0 && ViewModel.Games.All(g => g.Title.Contains("Seed 010")), "Search results");
                await Measure("clear-search", () => { ViewModel.Filter(""); SyncSelection(); return Task.CompletedTask; });
                Check(ViewModel.Games.Count == count, "Clear restores all entries");
                await Measure("reverse-sort", () => { ViewModel.SetSortOrder(LibrarySortOrder.Title, true); SyncSelection(); return Task.CompletedTask; });
                var selected = ViewModel.Games[20]; ViewModel.SelectedGame = selected; SyncSelection();
                FindDescendant<ScrollViewer>(GameList)?.ChangeView(null, 500, null, true); await Layout();
                var offset = FindDescendant<ScrollViewer>(GameList)?.VerticalOffset ?? 0;
                await Measure("collections-return", async () =>
                {
                    await Navigate(LibrarySection.Collections);
                    var lists = Descendants(CollectionListHost).OfType<ListView>().ToArray();
                    diagnostics["collection-realized"] = lists.Sum(list => Enumerable.Range(0, list.Items.Count).Count(i => list.ContainerFromIndex(i) is not null));
                    diagnostics["collection-items"] = lists.Sum(list => list.Items.Count);
                    await Navigate(LibrarySection.Library);
                });
                Check(ReferenceEquals(ViewModel.SelectedGame, selected), "Navigation retains selected instance");
                Check(Math.Abs((FindDescendant<ScrollViewer>(GameList)?.VerticalOffset ?? 0) - offset) < 5, "Navigation retains scroll");
                await Measure("details-options", () => ViewModel.LoadLaunchOptionsAsync());
            }
            diagnostics["collection-groups-retained"] = ReferenceEquals(collectionGroups, ViewModel.CollectionGroups) ? 1 : 0;
            var selectedId = ViewModel.SelectedGame.GameFileId;
            var refreshOffset = FindDescendant<ScrollViewer>(GameList)?.VerticalOffset ?? 0;
            await Measure("refresh-catalog", () => ViewModel.RefreshAsync());
            Check(ViewModel.SelectedGame.GameFileId == selectedId && ReferenceEquals(GameList.SelectedItem, ViewModel.SelectedGame), "Refresh retains native selection");
            Check(Math.Abs((FindDescendant<ScrollViewer>(GameList)?.VerticalOffset ?? 0) - refreshOffset) < 5, "Refresh retains viewport");
            ViewModel.SelectedGame = ViewModel.Games[10]; SyncSelection();
            GameList.ScrollIntoView(ViewModel.SelectedGame); await Layout();
            ((Control)GameList.ContainerFromItem(ViewModel.SelectedGame)).Focus(FocusState.Keyboard);
            // ScrollIntoView/focus can animate after UpdateLayout has returned.
            // Start the state-retention check from a settled, visible selection.
            await Task.Delay(250); await Layout();
            var focusedId = ViewModel.SelectedGame.GameFileId;
            var focusOffset = FindDescendant<ScrollViewer>(GameList)?.VerticalOffset ?? 0;
            diagnostics["sort-viewport-before"] = focusOffset;
            ViewModel.SetSortOrder(LibrarySortOrder.Title, true); SyncSelection(); await Layout();
            await Task.Delay(250); await Layout();
            diagnostics["sort-viewport-after"] = FindDescendant<ScrollViewer>(GameList)?.VerticalOffset ?? 0;
            var focusInList = false;
            for (var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                 focused is not null; focused = VisualTreeHelper.GetParent(focused))
                if (ReferenceEquals(focused, GameList)) { focusInList = true; break; }
            Check(focusInList && ViewModel.SelectedGame.GameFileId == focusedId, "Sort retains keyboard context and selected mod");
            // Reordering can move the visible anchor. Selection and keyboard
            // context remain invariant; navigation and refresh have separate
            // strict viewport checks.
            await Measure("import-archive", async () =>
            {
                var result = await ViewModel.ImportFilesAsync([new(Path.Combine(root, "UIInputs", "ui-alpha.zip"), ImportFileConflictResolution.Fail)], null, default);
                Check(result.Imported == 1, "Archive import succeeds");
            });
            for (var run = 0; run < 3; run++)
                await Measure("refresh-metadata-artwork", () => ViewModel.ApplySelectedIdGamesMetadataAsync(new IdGamesItem
                { Id = 667, Title = "UI Alpha", Author = "Synthetic mapper", Description = "Deterministic metadata payload", Directory = "levels/doom2/", FileName = "ui-alpha.zip" }));
            await Navigate(LibrarySection.Library);
            ViewModel.Filter(""); ViewModel.SetSortOrder(LibrarySortOrder.Title); SyncSelection(); await Layout();
            ViewModeButton_Click(this, new RoutedEventArgs()); await Layout();
            Check(diagnostics["collection-realized"] < Math.Min(100, diagnostics["collection-items"]), "Collection overview remains virtualized");
            Check(GameGrid.ContainerFromIndex(0) is not null && Enumerable.Range(0, ViewModel.Games.Count).Count(i => GameGrid.ContainerFromIndex(i) is not null) < 100,
                "Tile virtualization is retained");
            ViewModeButton_Click(this, new RoutedEventArgs()); await Layout();
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            var report = Path.Combine(root, "performance-report.json");
            await File.WriteAllTextAsync(report + ".tmp", JsonSerializer.Serialize(new
            { Passed = error is null && failedChecks.Count == 0, Error = error, FailedChecks = failedChecks, Checks = checks, Diagnostics = diagnostics, MeasurementsMs = results,
              MediansMs = results.ToDictionary(p => p.Key, p => p.Value.Order().ElementAt(p.Value.Count / 2)) }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(report + ".tmp", report, true);
            if (!Environment.GetCommandLineArgs().Contains("--keep-test-window")) _app.MainWindow!.Close();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
