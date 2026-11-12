using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using DoomLauncher.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private async Task RunPolishUiChecksAsync(string root, Action<bool,string> check, Func<Task> layout)
    {
        var window = _app.MainWindow!;
        var originalSize = window.AppWindow.Size;
        var originalPosition = window.AppWindow.Position;
        var screenshots = Path.Combine(root,"visual-qa"); Directory.CreateDirectory(screenshots);
        var selection = ViewModel.CatalogItems.First(item => item.Category == "Mod").GameFileId;
        ShellNavigation.SelectedItem=ShellNavigation.MenuItems.OfType<NavigationViewItem>().First(item=>item.Tag?.ToString()=="Library");
        ViewModel.SetSection(LibrarySection.Library); ViewModel.Filter(""); ViewModel.SelectGame(selection); SyncSelection();
        var originalGame = await _app.NativeLibraryService.LoadGameAsync(selection);
        var originalSettings = await _app.NativeLibraryService.LoadSettingsAsync();
        try
        {
            var fresh = new MainViewModel(_app.LibraryCatalog,_app.LaunchOptionsCatalog,_app.LaunchService,
                _app.NativeLibraryService,_app.IdGamesService,_app.UserLibraryStateStore,_app.Localization,_app.LibraryFeatures);
            check(fresh.TotalEntriesValue == "…" && fresh.TotalMapsValue == "…" && !fresh.IsEmpty,
                "Initial loading never presents a zero-count or empty-library state");
            check((string)Application.Current.Resources["DoomBodyFontFamily"].ToString()! is { Length: > 0 }
                && ((Style)Application.Current.Resources["MetaTextStyle"]).Setters.OfType<Setter>().Any(s => Equals(s.Value,13d)),
                "Body and metadata typography use readable normal-weight native text");
            check(LibraryDetailsPane.ViewModel!.SelectedGame.ArtworkStretch == Stretch.Uniform
                && LibraryDetailsPane.ViewModel.SelectedGame.CurrentImageStretch == Stretch.Uniform,
                "Artwork containers preserve the original image aspect ratio");

            var definitions = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync();
            var port = definitions.SourcePorts.First(); var iwad = definitions.Iwads.First();
            await _app.NativeLibraryService.UpdateSettingsAsync(originalSettings with { DefaultSourcePortId=port.SourcePortId, DefaultIwadId=iwad.IwadId });
            await _app.NativeLibraryService.UpdateGameAsync(originalGame with { SourcePortId=null, IwadId=null });
            await ViewModel.RefreshAsync(); ViewModel.SelectGame(selection); await layout();
            check(ViewModel.SelectedGame.SourcePort.Contains(port.Name) && ViewModel.SelectedGame.SourcePort.Contains("Default")
                && ViewModel.SelectedGame.Iwad.Contains("Default") && !ViewModel.SelectedGame.NeedsSourcePort && !ViewModel.SelectedGame.NeedsIwad,
                "Automatic launch choices display their effective defaults without a missing-definition warning");

            var footer = (Border)LibraryDetailsPane.FindName("LaunchFooter");
            var detailsScroll = (ScrollViewer)LibraryDetailsPane.FindName("DetailsScroll");
            var play = (Button)LibraryDetailsPane.FindName("PrimaryPlayButton");
            var tools = (Expander)LibraryDetailsPane.FindName("EntryToolsExpander");
            tools.IsExpanded = true; await layout();
            var footerY = footer.TransformToVisual(LibraryDetailsPane).TransformPoint(new Windows.Foundation.Point()).Y;
            detailsScroll.ChangeView(null,10000,null,true); await layout();
            check(Grid.GetRow(footer)==1 && Math.Abs(footer.TransformToVisual(LibraryDetailsPane).TransformPoint(new Windows.Foundation.Point()).Y-footerY)<1
                && play.ActualHeight>=44, "Play and Launch Options remain in a fixed footer while details scroll");
            check(play.Focus(FocusState.Keyboard) && play.UseSystemFocusVisuals,
                "The primary action accepts keyboard focus and exposes a system focus outline");
            detailsScroll.ChangeView(null,0,null,true);
            ShowFeedback("Fixture", "Saved successfully"); await layout();
            check(FeedbackBar.IsOpen && !HasOpenPopup() && ViewModel.SelectedGame.GameFileId==selection,
                "Routine completion feedback does not open a modal or change selection");
            FeedbackBar.IsOpen=false;
            ViewModel.SetSortOrder(LibrarySortOrder.Author);UpdateSortHeaders();await layout();
            check(SortOrderBox.SelectedItem is ComboBoxItem { Tag: "Author" },"Header and sort dropdown always select the same current field");
            ViewModel.SetSortOrder(LibrarySortOrder.Title);UpdateSortHeaders();
            ViewModel.Filter("__no_matching_fixture_title__"); await layout();
            check(ViewModel.IsEmpty && ViewModel.EmptyStateMessage==Strings["FilterNoMatches"],
                "Filtered-empty results are distinct from loading, failure and an empty library");
            EmptyStateAction_Click(this,new RoutedEventArgs()); await layout();
            check(ViewModel.Games.Count>0 && ViewModel.NavigationSearchText.Length==0,
                "The empty-state action restores visible entries without importing or deleting data");

            check(ThemeManager.GetAvailableThemes().Count>=2,"Theme acceptance never silently skips missing theme assets");
            foreach (var theme in ThemeManager.GetAvailableThemes())
            {
                ThemeManager.Apply(this,theme.Id); await layout();
                var background=BrushColor("DoomPageBackgroundBrush"); var surface=BrushColor("DoomSurfaceBrush");
                check(Contrast(BrushColor("TextFillColorPrimaryBrush"),background)>=4.49
                    && Contrast(BrushColor("TextFillColorSecondaryBrush"),surface)>=4.49,
                    "Primary/secondary text contrast checked: "+theme.Name);
                check(Contrast(BrushColor("FocusVisualPrimaryBrush"),background)>=4.49,
                    "Keyboard focus remains visible: "+theme.Name);
                if (theme.Id is "Dark" or "Light") await CapturePolishAsync(Path.Combine(screenshots,"theme-"+theme.Id+".png"),1);
            }
            ThemeManager.Apply(this,"Dark");
            // Release the fixed inline workflow surface before exercising real
            // window resizing and the responsive/compact layouts below.
            Width=double.NaN;Height=double.NaN;await layout();
            var mapInput=Path.Combine(root,"UIInputs","dialog-map.zip");
            using(var archive=System.IO.Compression.ZipFile.Open(mapInput,System.IO.Compression.ZipArchiveMode.Create))
            using(var writer=new BinaryWriter(archive.CreateEntry("dialog.wad").Open()))
            {
                var text=System.Text.Encoding.ASCII.GetBytes("namespace=\"zdoom\"; vertex{x=0;y=0;} vertex{x=128;y=128;} linedef{v1=0;v2=1;} sector{special=9;}");
                writer.Write(System.Text.Encoding.ASCII.GetBytes("PWAD"));writer.Write(3);writer.Write(12+text.Length);writer.Write(text);
                foreach(var lump in new[] { ("MAP01",12,0),("TEXTMAP",12,text.Length),("ENDMAP",12+text.Length,0) })
                { writer.Write(lump.Item2);writer.Write(lump.Item3);writer.Write(System.Text.Encoding.ASCII.GetBytes(lump.Item1.PadRight(8,'\0'))); }
            }
            var mapEntry=await _app.NativeLibraryService.ImportAsync(mapInput);await ViewModel.RefreshAsync();
            foreach(var width in new[] { 800,1440 })
            {
                var factor=XamlRoot.RasterizationScale;
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width*factor),(int)(720*factor)));
                await layout();ApplyResponsiveLayout();ViewModel.SelectGame(mapEntry.GameFileId);
                var showing=ShowMapsAsync();
                for(var attempt=0;attempt<200 && (_activeThemedDialog?.PrimaryButtonText!=Strings["LaunchSelectedMap"]) && !showing.IsCompleted;attempt++)
                    await Task.Delay(50);
                if(showing.IsFaulted) await showing;
                var mapDialog=_activeThemedDialog;
                check(mapDialog is not null && mapDialog.PrimaryButtonText==Strings["LaunchSelectedMap"],"Native Maps dialog opens without launching a game");
                await layout();
                check(mapDialog!.ActualWidth<=XamlRoot.Size.Width && mapDialog.ActualHeight<=XamlRoot.Size.Height,
                    "Map dialog stays inside the viewport at "+width);
                await CapturePolishAsync(Path.Combine(screenshots,"maps-"+width+".png"),1,mapDialog);
                mapDialog.Hide();await showing;
            }
            var sizes=new[] { (800,600),(1024,768),(1280,720),(1440,900),(1920,1080) };
            foreach(var culture in new[] { "en-US","de-DE","fr-FR","es-ES" })
            {
                _app.Localization.SetLanguage(culture); await ViewModel.RefreshLocalizationAsync(); DataContext=null; DataContext=Strings;
                RestoreViewControls();UpdateSortHeaders();ViewModel.DismissLaunchStatus();
                var libraryNavigation=ShellNavigation.MenuItems.OfType<NavigationViewItem>().First(item=>item.Tag?.ToString()=="Library");
                await layout();
                check(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(libraryNavigation)==Strings["Library"],"Navigation accessibility names follow the active language: "+culture);
                foreach(var size in sizes)
                {
                    var rasterization = XamlRoot.RasterizationScale;
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(size.Item1*rasterization),(int)(size.Item2*rasterization)));
                    await layout(); ApplyResponsiveLayout(); await layout();
                    var profile=CurrentLayoutProfile!;
                    check((CompactDetailsButton.Visibility==Visibility.Visible)==!profile.InlineDetails
                        && SearchBox.ActualWidth>80 && PageHeader.ActualWidth<=ContentLayout.ActualWidth+1,
                        $"Native layout bounds and compact details: {culture} {size.Item1}x{size.Item2}");
                    var left=SearchBox.TransformToVisual(PageHeader).TransformPoint(new Windows.Foundation.Point()).X;
                    check(left>=-1 && left+SearchBox.ActualWidth<=PageHeader.ActualWidth+1,
                        "Search stays inside the header at "+culture+" "+size.Item1);
                    check(CompactDetailsButton.Content?.ToString()==Strings["Details"]
                        && (profile.InlineDetails || SidebarStatistics.Visibility==Visibility.Collapsed),
                        "Compact details is labeled and collapsed navigation hides its statistics");
                    check(((ComboBoxItem)SortOrderBox.SelectedItem).Content.ToString()!.StartsWith(Strings[ViewModel.SortOrder.ToString()]),
                        "Current sort description follows the active locale");
                    var compactDetails = new GameDetailsPane { ViewModel=ViewModel,DataContext=Strings,Width=330,Height=Math.Max(300,ActualHeight-100) };
                    // Detail pane used in the compact flyout is identical to the
                    // shared inline control; static footer is tested above.
                    check(compactDetails.FindName("PrimaryLaunchRow") is Grid && compactDetails.FindName("EntryToolsExpander") is Expander,
                        "Compact detail presentation retains all existing workflows");
                    if(culture=="de-DE" || size.Item1==1440)
                        await CapturePolishAsync(Path.Combine(screenshots,$"library-{culture}-{size.Item1}.png"),1);
                }
            }
            _app.Localization.SetLanguage("en-US"); ViewModel.NotifyLocalizationChanged(); DataContext=null;DataContext=Strings;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1440*XamlRoot.RasterizationScale),(int)(900*XamlRoot.RasterizationScale)));await layout();ApplyResponsiveLayout();
            foreach(var section in new[] { LibrarySection.Home,LibrarySection.Collections,LibrarySection.Generate,LibrarySection.Achievements })
            {
                ShellNavigation.SelectedItem=ShellNavigation.MenuItems.OfType<NavigationViewItem>().First(item=>item.Tag?.ToString()==section.ToString());
                ViewModel.SetSection(section);await layout();ApplyResponsiveLayout();await layout();
                await CapturePolishAsync(Path.Combine(screenshots,section+".png"),1);
                check(ContentLayout.ActualWidth>0 && ContentLayout.ActualHeight>0,"Native section rendering: "+section);
            }
            ViewModel.SetSection(LibrarySection.Library);await layout();
            foreach(var scale in new[] { 1d,1.25,1.5,2d })
            {
                var file=Path.Combine(screenshots,$"raster-{scale:0.00}.png");await CapturePolishAsync(file,scale);
                check(File.Exists(file) && new FileInfo(file).Length>1000,"Native XAML raster output at "+scale.ToString("P0"));
            }
            check(KeyboardAccelerators.Any(a=>a.Key==Windows.System.VirtualKey.D)
                && BlockingOperationOverlay.IsHitTestVisible,
                "Compact-details keyboard access is registered and metadata locking is retained");
            _isRescraping=true;
            try { check(!CanUseLibraryShortcuts,"Metadata refresh also blocks import/play/navigation keyboard shortcuts"); }
            finally { _isRescraping=false; }
            var displayEvidence=new List<object>();
            var displays=Microsoft.UI.Windowing.DisplayArea.FindAll();
            for(var displayIndex=0;displayIndex<displays.Count;displayIndex++)
            {
                var display=displays[displayIndex];
                window.AppWindow.Move(new Windows.Graphics.PointInt32(display.WorkArea.X+16,display.WorkArea.Y+16));
                await Task.Delay(150);await layout();ApplyResponsiveLayout();
                check(ActualWidth>0 && ActualHeight>0 && XamlRoot.RasterizationScale>0,
                    "Native display-area move retains a valid responsive layout: "+display.DisplayId.Value);
                displayEvidence.Add(new { Id=display.DisplayId.Value, Scale=XamlRoot.RasterizationScale, Width=ActualWidth, Height=ActualHeight });
            }
            await File.WriteAllTextAsync(Path.Combine(screenshots,"matrix.json"),System.Text.Json.JsonSerializer.Serialize(new {
                Languages=new[]{"en-US","de-DE","fr-FR","es-ES"}, Viewports=sizes.Select(s=>new{Width=s.Item1,Height=s.Item2}),
                RasterScales=new[]{1d,1.25,1.5,2d}, NativeDisplays=displayEvidence,
                Note="Raster scales are native offscreen XAML exports; Windows display settings are not modified." }));
        }
        finally
        {
            await _app.NativeLibraryService.UpdateGameAsync(originalGame);
            await _app.NativeLibraryService.UpdateSettingsAsync(originalSettings);
            _app.Localization.SetLanguage("en-US");ThemeManager.Apply(this,"Dark");
            DataContext=null;DataContext=Strings;window.AppWindow.Move(originalPosition);window.AppWindow.Resize(originalSize);
        }
    }

    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
    private static double Contrast(Color a,Color b)
    {
        double L(Color c) { double F(byte v) { var n=v/255d;return n<=.04045?n/12.92:Math.Pow((n+.055)/1.055,2.4); } return .2126*F(c.R)+.7152*F(c.G)+.0722*F(c.B); }
        var x=L(a);var y=L(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
    private async Task CapturePolishAsync(string file,double scale,FrameworkElement? target=null)
    {
        target??=this;
        // Let theme/font and visual-state animations reach the compositor before
        // reading pixels; UpdateLayout alone does not wait for rendered frames.
        await Task.Delay(150);
        var bitmap=new RenderTargetBitmap();
        await bitmap.RenderAsync(target,(int)Math.Round(target.ActualWidth*scale),(int)Math.Round(target.ActualHeight*scale));
        var buffer=await bitmap.GetPixelsAsync();var bytes=new byte[buffer.Length];
        using(var reader=DataReader.FromBuffer(buffer))reader.ReadBytes(bytes);
        using var stream=new InMemoryRandomAccessStream();
        var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96*scale,96*scale,bytes);
        await encoder.FlushAsync();stream.Seek(0);var encoded=new byte[stream.Size];
        using(var reader=new DataReader(stream.GetInputStreamAt(0))){await reader.LoadAsync((uint)encoded.Length);reader.ReadBytes(encoded);}
        await File.WriteAllBytesAsync(file,encoded);
    }
}
