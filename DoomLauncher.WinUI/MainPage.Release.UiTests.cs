using System.Diagnostics;
using System.Text.Json;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private Task? _firstSetupAcceptance;
    private readonly List<string> _firstSetupActions=[];

    private void StartFirstSetupAcceptance()
    {
        if(!Environment.GetCommandLineArgs().Contains("--release-acceptance"))return;
        var root=Environment.GetEnvironmentVariable("DOOMLAUNCHER_UI_TEST_ROOT");
        if(root is null || !File.Exists(Path.Combine(root,".first-setup-test")))return;
        if(!File.Exists(Path.Combine(root,".release-test-instance")) || !Path.GetFullPath(root).TrimEnd('\\').Equals(Path.GetFullPath(GetPortableRoot()).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("First-setup acceptance requires a marked isolated installation.");
        _firstSetupAcceptance=DriveFirstSetupAsync(root);
    }

    private async Task DriveFirstSetupAsync(string root)
    {
        var handled=new HashSet<ContentDialog>();var watch=Stopwatch.StartNew();
        try
        {
            while(watch.Elapsed.TotalSeconds<120)
            {
                await Task.Delay(50);
                var dialog=_activeThemedDialog;
                if(dialog is null || handled.Contains(dialog))continue;
                string? buttonName=null;string? action=null;
                if(dialog.PrimaryButtonText==Strings["Migrate"] && dialog.CloseButtonText==Strings["Skip"]){buttonName="CloseButton";action=Strings["Skip"];}
                else if(new[]{Strings["StartSetup"],Strings["ScanAndContinue"],Strings["Next"],Strings["Finish"]}.Contains(dialog.PrimaryButtonText))
                {buttonName="PrimaryButton";action=dialog.PrimaryButtonText;}
                else if(!string.IsNullOrEmpty(dialog.PrimaryButtonText)||!string.IsNullOrEmpty(dialog.CloseButtonText))
                    throw new InvalidOperationException("Unexpected setup dialog buttons: "+dialog.PrimaryButtonText+" / "+dialog.CloseButtonText);
                if(buttonName is null)continue;
                dialog.UpdateLayout();
                var button=VisualDescendants<Button>(dialog).SingleOrDefault(b=>b.Name==buttonName && b.IsEnabled);
                if(button is null)continue;
                handled.Add(dialog);_firstSetupActions.Add(action!);
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                if(action==Strings["Finish"])return;
            }
            throw new TimeoutException("First-setup dialogs did not finish.");
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(root,"release-test-report.json"),JsonSerializer.Serialize(new{Passed=false,Checks=_firstSetupActions,Error=error.ToString()}));
            _app.MainWindow!.Close();
        }
    }

    private async Task CheckExternalDefinitionSaveAsync(string root, Action<bool, string> check, Func<Task> layout)
    {
        var external = Path.Combine(Path.GetTempPath(), "DL667-external-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "fixture.exe"), "Synthetic fixture; never executed.");
        var showing = ShowLauncherDefinitionsDialogAsync();
        ContentDialog? dialog = null;
        try
        {
            for (var attempt=0; attempt<200 && _activeThemedDialog is null && !showing.IsCompleted; attempt++) await Task.Delay(50);
            if (showing.IsFaulted) await showing;
            dialog = _activeThemedDialog ?? throw new InvalidOperationException("Definition dialog did not open.");
            await layout();
            T Control<T>(string name) where T: FrameworkElement => VisualDescendants<T>(dialog).Single(c => c.Name == name);
            var choices=Control<ListView>("DefinitionSourcePortChoice"); choices.SelectedIndex=0;
            var name=Control<TextBox>("DefinitionSourcePortName");
            var version=Control<TextBox>("DefinitionSourcePortVersion");
            Control<TextBox>("DefinitionSourcePortDirectory").Text=external;
            Control<TextBox>("DefinitionSourcePortExecutable").Text=Path.Combine(external,"fixture.exe");
            var save=Control<Button>("DefinitionSaveButton");
            var status=Control<InfoBar>("DefinitionSaveStatus");
            foreach (var revision in new[] { "1.2", "2.0" })
            {
                name.Text="ZZZ External regression " + revision; version.Text=revision; status.IsOpen=false;
                ((IInvokeProvider)new ButtonAutomationPeer(save).GetPattern(PatternInterface.Invoke)).Invoke();
                for(var attempt=0;attempt<200 && (!status.IsOpen || !save.IsEnabled);attempt++) await Task.Delay(50);
                await layout();
                check(status.IsOpen && status.Severity==InfoBarSeverity.Success && ReferenceEquals(_activeThemedDialog,dialog),
                    "External source-port create/rename saves inside the existing dialog: " + revision + " / " + status.Message);
                var saved=(await _app.NativeLibraryService.LoadLauncherDefinitionsAsync()).SourcePorts.Single(p=>p.Name==name.Text);
                check(saved.Version==revision && Path.GetFullPath(saved.Directory)==external,
                    "External source-port name, version and directory persist: " + revision);
            }
            check(Control<InfoBar>("DefinitionPortableWarning").IsOpen,
                "External references retain an inline portability warning, without a nested ContentDialog");
        }
        finally
        {
            dialog?.Hide(); await showing;
            File.Delete(Path.Combine(external,"fixture.exe")); Directory.Delete(external);
        }
    }

    // A separate opt-in mode allows upgrade and large-migration tests to use
    // their own databases, never a user's running installation.
    private async Task RunReleaseAcceptanceAsync()
    {
        var args=Environment.GetCommandLineArgs();
        if(!args.Contains("--release-acceptance")) return;
        var root=Environment.GetEnvironmentVariable("DOOMLAUNCHER_UI_TEST_ROOT");
        if(root is null || !File.Exists(Path.Combine(root,".release-test-instance"))
            || !Path.GetFullPath(root).TrimEnd('\\').Equals(Path.GetFullPath(GetPortableRoot()).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Release acceptance requires a marked isolated installation.");
        var checks=new List<string>();string? failure=null;
        void Check(bool condition,string text) { if(!condition)throw new InvalidOperationException(text);checks.Add(text); }
        async Task Layout() { await Task.Delay(50);UpdateLayout();await Task.Delay(50); }
        try
        {
            Check(!ViewModel.HasError,"Portable installation starts and loads its catalog without errors");
            if(File.Exists(Path.Combine(root,".first-setup-test")))
            {
                Check(_firstSetupActions.Count(a=>a==Strings["ScanAndContinue"])==4,"Fresh installation completes all four real setup scans");
                Check(_firstSetupActions.Count(a=>a==Strings["Next"])==4 && _firstSetupActions.Contains(Strings["Finish"]),"Fresh setup scan results use Next and final completion uses Finish");
                Check(!await _app.FirstSetupService.ShouldRunWizardAsync(),"Completed first setup is persisted and will not repeat on restart");
            }
            // Keep upgrade fixtures read-only: source-port editing is covered by
            // the separate native smoke fixture, not mixed with preservation checks.
            var expectedPath=Path.Combine(root,"expected-entry-count.txt");
            if(File.Exists(expectedPath))
            {
                var expected=int.Parse(await File.ReadAllTextAsync(expectedPath));
                Check(ViewModel.CatalogItems.Count==expected,"All " + expected + " migrated library entries load");
                ShellNavigation.SelectedItem=ShellNavigation.MenuItems.OfType<NavigationViewItem>().Single(i=>i.Tag?.ToString()=="Collections");
                ViewModel.SetSection(LibrarySection.Collections);_showCollectionList=true;UpdateCollectionsViewControls();await Layout();
                Check(ViewModel.CollectionGroups.Sum(g=>g.Items.Count)==expected,"Collection memberships survive large-library migration");
                var rows=VisualDescendants<ListView>(CollectionListHost).Where(l=>l.Items.Count>100).ToArray();
                Check(rows.Length>0,"Migrated accordion collection rows are rendered");
                foreach(var list in rows)
                {
                    var realized=Enumerable.Range(0,list.Items.Count).Count(i=>list.ContainerFromIndex(i) is not null);
                    Check(list.ActualHeight<=560 && realized<200,"Collection accordion recycles rows: " + realized + "/"+list.Items.Count);
                    list.ScrollIntoView(list.Items[list.Items.Count-1]);await Layout();
                    Check(list.ContainerFromIndex(list.Items.Count-1) is not null,"Last accordion mod remains reachable without realizing the whole collection");
                }
                var group=ViewModel.CollectionGroups.OrderByDescending(g=>g.Items.Count).First();
                OpenCollectionGroup(group);_showCollectionDetailList=true;UpdateCollectionsViewControls();await Layout();
                var detailRealized=Enumerable.Range(0,group.Items.Count).Count(i=>CollectionDetailGameList.ContainerFromIndex(i) is not null);
                Check(detailRealized>0 && detailRealized<200,"Collection detail table also recycles rows: "+detailRealized+"/"+group.Items.Count);
                CollectionDetailGameList.ScrollIntoView(group.Items[^1]);await Layout();
                Check(CollectionDetailGameList.ContainerFromIndex(group.Items.Count-1) is not null,"Last detail-table mod is reachable");
                _showCollectionDetailList=false;UpdateCollectionsViewControls();await Layout();
                CollectionDetailGameGrid.ScrollIntoView(group.Items[^1]);await Layout();
                Check(CollectionDetailGameGrid.ContainerFromIndex(group.Items.Count-1) is not null,"Large collection tile view reaches its last mod");
                CollectionBackButton_Click(this,new RoutedEventArgs());_showCollectionList=false;UpdateCollectionsViewControls();await Layout();
                Check(CollectionOverviewHost.Visibility==Visibility.Visible,"Collection overview remains usable after repeated table/tile navigation");
            }
            using var process=Process.GetCurrentProcess();
            var modules=process.Modules.Cast<ProcessModule>().Where(m=>m.ModuleName is "coreclr.dll" or "Microsoft.ui.xaml.dll").ToArray();
            Check(modules.Any(m=>m.ModuleName=="coreclr.dll" && Path.GetFullPath(m.FileName).StartsWith(Path.Combine(root,"WinUI"),StringComparison.OrdinalIgnoreCase)),
                "Application loads its bundled .NET runtime");
            await File.WriteAllTextAsync(Path.Combine(root,"runtime-evidence.json"),JsonSerializer.Serialize(new { OS=Environment.OSVersion.ToString(),PeakWorkingSet=process.PeakWorkingSet64,Modules=modules.Select(m=>new{m.ModuleName,m.FileName}) }));
        }
        catch(Exception error) { failure=error.ToString(); }
        var report=Path.Combine(root,"release-test-report.json");
        await File.WriteAllTextAsync(report+".tmp",JsonSerializer.Serialize(new{Passed=failure is null,Checks=checks,Error=failure}));
        File.Move(report+".tmp",report,true);_app.MainWindow!.Close();
    }
}
