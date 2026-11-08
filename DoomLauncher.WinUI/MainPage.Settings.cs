using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using DoomLauncher.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private async Task ShowSettingsDialogAsync()
    {
        try
        {
            var settings = await ViewModel.LoadNativeSettingsAsync(_loadCancellation.Token);
            var directoryBox = new TextBox
            {
                Header = Strings["GameDirectory"],
                Text = settings.GameFileDirectory,
            };
            AutomationProperties.SetName(directoryBox, Strings["GameDirectory"]);
            var sourcePortBox = CreateChoiceBox(
                Strings["DefaultSourcePort"],
                settings.SourcePorts,
                settings.DefaultSourcePortId);
            var iwadBox = CreateChoiceBox(
                Strings["DefaultIwad"],
                settings.Iwads,
                settings.DefaultIwadId);
            var playDialogSwitch = new ToggleSwitch
            {
                Header = Strings["ShowPlayDialog"],
                IsOn = settings.ShowPlayDialog,
            };
            var screenshotSwitch = new ToggleSwitch
            {
                Header = Strings["ImportScreenshots"],
                IsOn = settings.ImportScreenshots,
            };
            var pageSizeBox = new NumberBox
            {
                Header = Strings["ItemsPerPage"],
                Minimum = 20,
                Maximum = 250,
                SmallChange = 10,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = settings.ItemsPerPage,
            };
            var homeItemsBox = new NumberBox
            {
                Header = Strings["HomeItemsPerGroup"],
                Minimum = 1,
                Maximum = 20,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = settings.HomeItemsPerGroup,
            };
            var themeItems = ThemeManager.GetAvailableThemes();
            var themeBox = new ComboBox
            {
                Header = Strings["Theme"],
                Height = 68,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                DisplayMemberPath = nameof(LauncherThemeInfo.Name),
                ItemsSource = themeItems,
            };
            themeBox.SelectedIndex = Math.Max(
                themeItems
                    .Select((item, index) => new { item.Id, Index = index })
                    .FirstOrDefault(item => item.Id.Equals(
                        _userState.Theme,
                        StringComparison.OrdinalIgnoreCase))
                    ?.Index
                    ?? -1,
                0);
            var languageValues = new[]
            {
                "en-US", "de-DE", "fr-FR", "es-ES",
            };
            var languageLabels = new[]
            {
                Strings["English"],
                Strings["German"],
                Strings["French"],
                Strings["Spanish"],
            };
            var languageBox = new ComboBox
            {
                Header = Strings["Language"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = languageLabels,
            };
            languageBox.SelectedItem = languageLabels[Math.Max(
                Array.IndexOf(languageValues, _userState.Language),
                0)];
            var densityValues = new[] { "Normal", "Compact", "UltraCompact" };
            var densityBox = new ComboBox
            {
                Header = Strings["ListDensity"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[]
                {
                    Strings["NormalDensity"],
                    Strings["CompactDensity"],
                    Strings["UltraCompactDensity"],
                },
                SelectedIndex = _userState.ListDensity switch
                {
                    "Compact" => 1,
                    "UltraCompact" => 2,
                    _ => 0,
                },
            };
            var accordionDensityBox = new ComboBox
            {
                Header = Strings["AccordionDensity"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[]
                {
                    Strings["NormalDensity"],
                    Strings["CompactDensity"],
                    Strings["UltraCompactDensity"],
                },
                SelectedIndex = _userState.AccordionDensity switch
                {
                    "Compact" => 1,
                    "UltraCompact" => 2,
                    _ => 0,
                },
            };
            var placeholderArtworkValues = new[] { "Grayscale", "Colored" };
            var placeholderArtworkBox = new ComboBox
            {
                Header = Strings["PlaceholderArtworkStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[]
                {
                    Strings["GrayscaleArtwork"],
                    Strings["ColoredArtwork"],
                },
                SelectedIndex = string.Equals(
                    settings.PlaceholderArtworkStyle,
                    "Colored",
                    StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0,
            };
            AutomationProperties.SetName(playDialogSwitch, Strings["ShowPlayDialog"]);
            AutomationProperties.SetName(screenshotSwitch, Strings["ImportScreenshots"]);
            AutomationProperties.SetName(pageSizeBox, Strings["ItemsPerPage"]);
            AutomationProperties.SetName(homeItemsBox, Strings["HomeItemsPerGroup"]);
            AutomationProperties.SetName(themeBox, Strings["Theme"]);
            AutomationProperties.SetName(languageBox, Strings["Language"]);
            AutomationProperties.SetName(densityBox, Strings["ListDensity"]);
            AutomationProperties.SetName(
                accordionDensityBox,
                Strings["AccordionDensity"]);
            AutomationProperties.SetName(
                placeholderArtworkBox,
                Strings["PlaceholderArtworkStyle"]);
            var generalPrimarySettings = new StackPanel
            {
                Spacing = 12,
            };
            generalPrimarySettings.Children.Add(directoryBox);
            generalPrimarySettings.Children.Add(sourcePortBox);
            generalPrimarySettings.Children.Add(iwadBox);
            var generalBehaviorSettings = new StackPanel
            {
                Spacing = 12,
            };
            generalBehaviorSettings.Children.Add(playDialogSwitch);
            generalBehaviorSettings.Children.Add(screenshotSwitch);
            generalBehaviorSettings.Children.Add(pageSizeBox);
            generalBehaviorSettings.Children.Add(homeItemsBox);
            var appearancePrimarySettings = new StackPanel
            {
                Spacing = 12,
            };
            appearancePrimarySettings.Children.Add(themeBox);
            appearancePrimarySettings.Children.Add(languageBox);
            var appearanceDetailSettings = new StackPanel
            {
                Spacing = 12,
            };
            appearanceDetailSettings.Children.Add(densityBox);
            appearanceDetailSettings.Children.Add(accordionDensityBox);
            appearanceDetailSettings.Children.Add(placeholderArtworkBox);

            var generalContent = CreateSettingsTabContent(
                generalPrimarySettings,
                generalBehaviorSettings);
            var appearanceContent = CreateSettingsTabContent(
                appearancePrimarySettings,
                appearanceDetailSettings);
            var generalTab = CreateDefinitionTabButton(
                Strings["GeneralSettingsTab"],
                "\uE713");
            var appearanceTab = CreateDefinitionTabButton(
                Strings["AppearanceSettingsTab"],
                "\uE790");
            var tabSelector = new Grid
            {
                ColumnSpacing = 8,
            };
            tabSelector.ColumnDefinitions.Add(new ColumnDefinition());
            tabSelector.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(appearanceTab, 1);
            tabSelector.Children.Add(generalTab);
            tabSelector.Children.Add(appearanceTab);
            var settingsHost = new ContentControl
            {
                Content = generalContent,
            };
            var settingsScrollViewer = new ScrollViewer
            {
                MaxHeight = 500,
                Padding = new Thickness(0, 0, 12, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled,
                Content = settingsHost,
            };
            var content = new Grid
            {
                Width = 800,
                RowSpacing = 14,
            };
            content.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(settingsScrollViewer, 1);
            content.Children.Add(tabSelector);
            content.Children.Add(settingsScrollViewer);
            void SelectSettingsTab(
                ToggleButton selected,
                UIElement selectedContent)
            {
                generalTab.IsChecked = ReferenceEquals(selected, generalTab);
                appearanceTab.IsChecked =
                    ReferenceEquals(selected, appearanceTab);
                settingsHost.Content = selectedContent;
            }
            generalTab.Click += (_, _) =>
                SelectSettingsTab(generalTab, generalContent);
            appearanceTab.Click += (_, _) =>
                SelectSettingsTab(appearanceTab, appearanceContent);
            SelectSettingsTab(generalTab, generalContent);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = EffectiveDialogTheme,
                MinWidth = 840,
                MaxWidth = 900,
                Title = Strings["SettingsTitle"],
                Content = content,
                PrimaryButtonText = Strings["Save"],
                SecondaryButtonText = Strings["Maintenance"],
                CloseButtonText = Strings["Cancel"],
                DefaultButton = ContentDialogButton.Primary,
            };
            ApplyDialogTheme(dialog);
            dialog.Resources["ContentDialogMinWidth"] = 840d;
            dialog.Resources["ContentDialogMaxWidth"] = 900d;
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                await ShowMaintenanceDialogAsync();
                return;
            }
            if (result != ContentDialogResult.Primary)
                return;

            var theme = (themeBox.SelectedItem as LauncherThemeInfo)?.Id
                ?? themeItems.FirstOrDefault()?.Id
                ?? "Dark";
            var language = languageBox.SelectedIndex >= 0
                ? languageValues[languageBox.SelectedIndex]
                : "en-US";
            var listDensity = densityBox.SelectedIndex >= 0
                ? densityValues[densityBox.SelectedIndex]
                : "Normal";
            var accordionDensity = accordionDensityBox.SelectedIndex >= 0
                ? densityValues[accordionDensityBox.SelectedIndex]
                : "Normal";
            var placeholderArtworkStyle =
                placeholderArtworkBox.SelectedIndex >= 0
                    ? placeholderArtworkValues[
                        placeholderArtworkBox.SelectedIndex]
                    : "Grayscale";
            await ViewModel.SaveNativeSettingsAsync(
                settings with
                {
                    GameFileDirectory = directoryBox.Text,
                    DefaultSourcePortId = (sourcePortBox.SelectedItem as NativeChoice)?.Id,
                    DefaultIwadId = (iwadBox.SelectedItem as NativeChoice)?.Id,
                    ShowPlayDialog = playDialogSwitch.IsOn,
                    ImportScreenshots = screenshotSwitch.IsOn,
                    ItemsPerPage = checked((int)pageSizeBox.Value),
                    HomeItemsPerGroup = checked((int)homeItemsBox.Value),
                    ColorTheme = theme,
                    PlaceholderArtworkStyle = placeholderArtworkStyle,
                },
                _loadCancellation.Token);
            var currentState =
                await _app.UserLibraryStateStore.LoadAsync(_loadCancellation.Token);
            var testedThemesAfterSave = currentState.TestedThemes.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            testedThemesAfterSave.Add(theme);
            _userState = currentState with
            {
                Theme = theme,
                Language = language,
                ListDensity = listDensity,
                AccordionDensity = accordionDensity,
                TestedThemes = testedThemesAfterSave,
            };
            await _app.UserLibraryStateStore.SaveAsync(_userState, _loadCancellation.Token);
            _app.Localization.SetLanguage(language);
            DataContext = null;
            DataContext = Strings;
            ApplyGeneratorLocalization();
            ThemeManager.Apply(this, theme);
            (_app.MainWindow as MainWindow)?.ApplyTitleBarTheme();
            ListDensity.Apply(listDensity);
            AccordionDensity.Apply(accordionDensity);
            await ViewModel.RefreshLocalizationAsync(_loadCancellation.Token);
            SyncSelection();
            UpdateSortHeaders();
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(_app.Localization.Get("SettingsSaveFailed"), exception.Message);
        }
    }

    private static ComboBox CreateChoiceBox(
        string header,
        IReadOnlyList<NativeChoice> choices,
        int? selectedId)
    {
        var box = new ComboBox
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(NativeChoice.Name),
            ItemsSource = choices,
        };
        box.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedId)
            ?? choices.FirstOrDefault();
        AutomationProperties.SetName(box, header);
        return box;
    }

    private static Grid CreateSettingsTabContent(
        FrameworkElement primaryColumn,
        FrameworkElement secondaryColumn)
    {
        var grid = new Grid
        {
            Width = 780,
            Padding = new Thickness(4, 4, 4, 8),
            ColumnSpacing = 24,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(secondaryColumn, 1);
        grid.Children.Add(primaryColumn);
        grid.Children.Add(secondaryColumn);
        return grid;
    }

}
