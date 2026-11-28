using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private async Task VerifyVersionGroupsAsync(string root, Action<bool,string> check, Func<Task> layout)
    {
        var added = new List<int>();
        var original = ViewModel.SelectedGame;
        var state = await _app.UserLibraryStateStore.LoadAsync();
        try
        {
            // Synthetic metadata fixtures, not checksum validation of commercial data.
            foreach (var (value, version, md5) in new[] { ((byte)17,"1.666","54978d12de87f162b9bcc011676cb3c0"), ((byte)18,"1.8","11e1cd216801ea2657723abc86ecb01f") })
            {
                var input = Path.Combine(root,"version-group-" + value + ".wad");
                using (var memory=new MemoryStream())
                {
                    using var writer=new BinaryWriter(memory,System.Text.Encoding.ASCII,true);
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("IWAD"));writer.Write(1);writer.Write(13);writer.Write(value);
                    writer.Write(12);writer.Write(1);writer.Write(System.Text.Encoding.ASCII.GetBytes("TESTDATA"));
                    await File.WriteAllBytesAsync(input,memory.ToArray());
                }
                var imported=await _app.NativeLibraryService.ImportIwadAsync(input);
                var definition=(await _app.NativeLibraryService.LoadLauncherDefinitionsAsync()).Iwads.Single(i=>i.ArchiveFileName==imported.FileName);
                added.Add(definition.IwadId!.Value);
                await _app.NativeLibraryService.SaveIwadAsync(definition with {Name="Custom " + version,Version=version,Md5=md5,FileSize=11159840});
            }
            await ViewModel.RefreshAsync();
            await ViewModel.SetVersionGroupingAsync(false); SyncSelection(); await layout();
            await CapturePolishAsync(Path.Combine(root,"visual-qa","version-groups-before.png"),1);
            await ViewModel.SetVersionGroupingAsync(true); SyncSelection(); await layout();
            check(ViewModel.Games.Count==3 && ViewModel.ListRows.Count==1,"Three concrete IWADs collapse to one row without merging library entries");
            check(GameList.KeyboardAccelerators.Any(key=>key.Key==Windows.System.VirtualKey.Left)
                && GameList.KeyboardAccelerators.Any(key=>key.Key==Windows.System.VirtualKey.Right),"Version arrow keys are registered in the native list keyboard scope");
            var header=ViewModel.ListRows.Single();
            check(header.Title=="Doom Registered" && header.Version=="1.9" && header.VersionMembers.Select(g=>g.Version).SequenceEqual(new[]{"1.9","1.8","1.666"}),
                "Group heading uses canonical edition and latest verified release; children follow curated chronology");
            await CapturePolishAsync(Path.Combine(root,"visual-qa","version-groups-collapsed.png"),1);
            await ViewModel.ToggleVersionGroupAsync(header,true); SyncSelection(); await layout();
            check(ViewModel.ListRows.Count==4 && ViewModel.ListRows.Skip(1).All(g=>!g.IsVersionGroup),"Expanded versions remain flat virtualized ListView rows");
            var older=header.VersionMembers.Single(g=>g.Version=="1.666");
            ViewModel.SelectedGame=older;SyncSelection();await layout();
            var targetBefore=ViewModel.SelectedGame;
            var scroll=FindDescendant<ScrollViewer>(GameList);var horizontal=scroll?.HorizontalOffset??0;
            await CapturePolishAsync(Path.Combine(root,"visual-qa","version-groups-expanded.png"),1);
            var versionButtons=FindDescendants<Button>(GameList,"VersionToggle").ToArray();
            check(versionButtons.Count(button=>button.Visibility==Visibility.Visible && button.Tag is LibraryItem {IsVersionGroup:true})==1
                && versionButtons.All(button=>button.Tag is LibraryItem {IsVersionGroup:true}),
                "Only the group header exposes an expand/collapse button, including after column layout: " + string.Join("; ",versionButtons.Select(b=>$"{b.Visibility}/{b.Tag?.GetType().Name ?? "null"}")));
            var toggle=versionButtons.Single(button=>ReferenceEquals(button.Tag,header));
            var peer=FrameworkElementAutomationPeer.CreatePeerForElement(toggle) ?? new ButtonAutomationPeer(toggle);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await layout(); await Task.Delay(100); await layout();
            check(!header.VersionsExpanded && ReferenceEquals(ViewModel.SelectedGame,targetBefore) && ReferenceEquals(GameList.SelectedItem,header),
                "Collapsing through the real chevron preserves the older launch target and highlights its group");
            check(header.SelectedVersionLabel.Contains("1.666") && ViewModel.LaunchButtonText.EndsWith("1.666"),"Collapsed older selection and Play action explicitly name the retained version");
            check(Math.Abs((scroll?.HorizontalOffset??0)-horizontal)<1,"Collapsing retains the horizontal viewport");
            await CapturePolishAsync(Path.Combine(root,"visual-qa","version-groups-selected-older.png"),1);
            ViewModel.Filter("1.8");SyncSelection();await layout();
            check(ViewModel.ListRows.Count==2 && header.VersionsExpanded && ViewModel.ListRows[1].Version=="1.8"
                && ReferenceEquals(ViewModel.SelectedGame,older) && header.VersionGroupSummary.Contains("1/3"),"Version search temporarily reveals matching children, reports match count and preserves launch target");
            ViewModel.Filter("");SyncSelection();await layout();
            check(!header.VersionsExpanded && ViewModel.ListRows.Count==1 && ReferenceEquals(ViewModel.SelectedGame,older),"Clearing search restores collapsed state and selected concrete version");
            await ViewModel.ToggleVersionGroupAsync(header,true);SyncSelection();await layout();
            check((await _app.UserLibraryStateStore.LoadAsync()).ExpandedIwadVersionGroups!.Contains("Doom Registered"),"Expanded group keys are persisted in portable user state");
            await ViewModel.RefreshAsync();SyncSelection();await layout();
            check(ViewModel.ListRows.Count==4 && ViewModel.SelectedGame.GameFileId==older.GameFileId,"Catalog refresh retains group expansion and the selected entry ID");
            header=ViewModel.ListRows.First();
            ViewModel.SelectedGame=header;SyncSelection();await layout();
            check(ReferenceEquals(ViewModel.SelectedGame,header.VersionGroupTarget) && ViewModel.SelectedGame.Version=="1.9","Selecting the group explicitly selects its concrete latest entry");
            CategoryFilter_Click(ModsFilterButton,new RoutedEventArgs());await layout();
            CategoryFilter_Click(IwadFilterButton,new RoutedEventArgs());await layout();
            check(ViewModel.ListRows.Count==4,"Returning from Mods restores the expanded IWAD groups");
            _app.Localization.SetLanguage("de-DE"); await ViewModel.RefreshLocalizationAsync(); DataContext=null;DataContext=Strings;SyncSelection();
            Width=800;ApplyResponsiveLayout();await layout();
            await CapturePolishAsync(Path.Combine(root,"visual-qa","version-groups-de-DE-narrow.png"),1);
            check(GroupVersionsButton.ActualWidth<=40 && GroupVersionsButton.Visibility==Visibility.Visible,"Grouping control fits narrow windows with German accessibility labels");
            check(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(GroupVersionsButton)=="Versionen gruppieren",
                "Grouping toggle exposes its translated accessibility name");
            Width=1440;ApplyResponsiveLayout();await layout();
        }
        finally
        {
            _app.Localization.SetLanguage(state.Language); await ViewModel.RefreshLocalizationAsync(); DataContext=null;DataContext=Strings;
            ViewModel.Filter("");
            foreach(var id in added)await _app.NativeLibraryService.DeleteIwadAsync(id,deletePhysicalFiles:true);
            await ViewModel.RefreshAsync();
            await ViewModel.SetVersionGroupingAsync(state.GroupIwadVersions);
            await _app.UserLibraryStateStore.SaveAsync(state);
            ViewModel.SelectGame(original.GameFileId);SyncSelection();await layout();
        }
    }
}
