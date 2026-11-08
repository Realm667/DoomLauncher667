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
    private async Task ShowLauncherDefinitionsDialogAsync()
    {
        var data = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(
            _loadCancellation.Token);
        var acknowledgedExternalPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var portableWarning = new InfoBar
        {
            IsClosable = true,
            IsOpen = false,
            Severity = InfoBarSeverity.Warning,
            Title = Strings["ExternalPathTitle"],
        };

        var newSourcePort = new NativeSourcePortDefinition(
            null,
            Strings["NewDefinition"],
            string.Empty,
            string.Empty,
            ".wad,.pk3,.ipk3,.pk7,.deh,.bex,.pke",
            "-file",
            string.Empty);
        var sourcePortChoice = new ListView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = CreateSourcePortDefinitionTemplate(),
            ItemsSource = new[] { newSourcePort }.Concat(data.SourcePorts).ToArray(),
            SelectedIndex = 0,
            SelectionMode = ListViewSelectionMode.Single,
        };
        var sourcePortDeleteButton = CreateDefinitionActionButton(
            Strings["DeleteSourcePort"],
            "\uE74D");
        sourcePortDeleteButton.IsEnabled = false;
        AutomationProperties.SetName(sourcePortChoice, Strings.SourcePort);
        var sourcePortName = CreateDefinitionTextBox(Strings["Name"]);
        var sourcePortVersion = CreateDefinitionTextBox(Strings["Version"]);
        var sourcePortDirectory = CreateDefinitionTextBox(Strings["Directory"]);
        var sourcePortExecutable = CreateDefinitionTextBox(Strings["Executable"]);
        var sourcePortExtensions = CreateDefinitionTextBox(Strings["Extensions"]);
        var sourcePortExtra = CreateDefinitionTextBox(Strings["ExtraParameters"]);
        var screenshotSupportOptions = new[]
        {
            new DefinitionOption("Auto", Strings["ScreenshotSupportAuto"]),
            new DefinitionOption("Configured", Strings["ScreenshotSupportConfigured"]),
            new DefinitionOption("None", Strings["CapabilityUnsupported"]),
        };
        var screenshotSupportBox = CreateDefinitionOptionBox(
            Strings["ScreenshotCapability"],
            screenshotSupportOptions);
        var screenshotDirectories = CreateDefinitionTextBox(
            Strings["ScreenshotDirectories"]);
        var screenshotExtensions = CreateDefinitionTextBox(
            Strings["ScreenshotFormats"]);
        var statisticsOptions = new[]
        {
            new DefinitionOption("None", Strings["CapabilityUnsupported"]),
            new DefinitionOption("ZDoomSave", Strings["StatisticsAdapterZDoom"]),
        };
        var statisticsAdapterBox = CreateDefinitionOptionBox(
            Strings["StatisticsCapability"],
            statisticsOptions);
        var statisticsDirectories = CreateDefinitionTextBox(
            Strings["StatisticsDirectories"]);
        var saveGameExtensions = CreateDefinitionTextBox(
            Strings["SaveGameFormats"]);
        var capabilityStatus = new InfoBar
        {
            IsClosable = false,
            IsOpen = true,
            Severity = InfoBarSeverity.Informational,
            Title = Strings["CapabilityStatus"],
        };
        var capabilityTestButton = new Button
        {
            Content = Strings["TestCapabilities"],
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        void UpdateCapabilityStatus()
        {
            var screenshotOption =
                screenshotSupportBox.SelectedItem as DefinitionOption
                ?? screenshotSupportOptions[0];
            var statisticsOption =
                statisticsAdapterBox.SelectedItem as DefinitionOption
                ?? statisticsOptions[0];
            capabilityStatus.Message = _app.Localization.Format(
                "CapabilityStatusMessage",
                string.IsNullOrWhiteSpace(sourcePortVersion.Text)
                    ? Strings["NotSet"]
                    : sourcePortVersion.Text.Trim(),
                screenshotOption.Label,
                statisticsOption.Label);
            capabilityStatus.Title = Strings["CapabilityStatus"];
            capabilityStatus.Severity = InfoBarSeverity.Informational;
        }
        capabilityTestButton.Click += (_, _) =>
        {
            var messages = new List<string>();
            var executablePath = ResolveDefinitionExecutablePath(
                sourcePortDirectory.Text,
                sourcePortExecutable.Text);
            if (!File.Exists(executablePath))
                messages.Add(Strings["ExecutableNotFound"]);
            if ((screenshotSupportBox.SelectedItem as DefinitionOption)?.Code
                    == "Configured"
                && string.IsNullOrWhiteSpace(screenshotDirectories.Text))
            {
                messages.Add(Strings["ScreenshotConfigurationMissing"]);
            }
            if ((statisticsAdapterBox.SelectedItem as DefinitionOption)?.Code
                    == "ZDoomSave"
                && string.IsNullOrWhiteSpace(saveGameExtensions.Text))
            {
                messages.Add(Strings["SaveGameFormatMissing"]);
            }
            capabilityStatus.Severity = messages.Count == 0
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
            capabilityStatus.Title = messages.Count == 0
                ? Strings["CapabilityTestPassed"]
                : Strings["CapabilityTestWarning"];
            capabilityStatus.Message = messages.Count == 0
                ? Strings["CapabilityTestPassedMessage"]
                : string.Join(Environment.NewLine, messages);
        };
        var sourcePortDirectoryField = CreateDefinitionBrowseField(
            sourcePortDirectory,
            async () =>
            {
                var path = await PickDefinitionFolderAsync();
                if (path is not null)
                {
                    sourcePortDirectory.Text = ConvertToPortableReference(
                        path,
                        acknowledgedExternalPaths,
                        portableWarning);
                }
            });
        var sourcePortExecutableField = CreateDefinitionBrowseField(
            sourcePortExecutable,
            async () =>
            {
                var path = await PickDefinitionFileAsync();
                if (path is null)
                    return;
                sourcePortDirectory.Text = ConvertToPortableReference(
                    Path.GetDirectoryName(path)!,
                    acknowledgedExternalPaths,
                    portableWarning);
                sourcePortExecutable.Text = Path.GetFileName(path);
                sourcePortVersion.Text = ReadExecutableVersion(path);
                if (IsZDoomFamilyExecutable(path))
                    statisticsAdapterBox.SelectedItem = statisticsOptions[1];
            });
        var sourcePortVersionField = CreateDefinitionActionField(
            sourcePortVersion,
            Strings["DetectVersion"],
            () =>
            {
                var executablePath = ResolveDefinitionExecutablePath(
                    sourcePortDirectory.Text,
                    sourcePortExecutable.Text);
                sourcePortVersion.Text = ReadExecutableVersion(executablePath);
                return Task.CompletedTask;
            });
        var screenshotDirectoriesField = CreateDefinitionBrowseField(
            screenshotDirectories,
            async () =>
            {
                var path = await PickDefinitionFolderAsync();
                if (path is not null)
                {
                    screenshotDirectories.Text = ConvertToPortableReference(
                        path,
                        acknowledgedExternalPaths,
                        portableWarning);
                }
            });
        var statisticsDirectoriesField = CreateDefinitionBrowseField(
            statisticsDirectories,
            async () =>
            {
                var path = await PickDefinitionFolderAsync();
                if (path is not null)
                {
                    statisticsDirectories.Text = ConvertToPortableReference(
                        path,
                        acknowledgedExternalPaths,
                        portableWarning);
                }
            });
        void UpdateCapabilityFieldVisibility()
        {
            var screenshotsSupported =
                (screenshotSupportBox.SelectedItem as DefinitionOption)?.Code != "None";
            screenshotDirectoriesField.Visibility = screenshotsSupported
                ? Visibility.Visible
                : Visibility.Collapsed;
            screenshotExtensions.Visibility = screenshotsSupported
                ? Visibility.Visible
                : Visibility.Collapsed;
            var statisticsSupported =
                (statisticsAdapterBox.SelectedItem as DefinitionOption)?.Code != "None";
            statisticsDirectoriesField.Visibility = statisticsSupported
                ? Visibility.Visible
                : Visibility.Collapsed;
            saveGameExtensions.Visibility = statisticsSupported
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        void UpdateCapabilityUi()
        {
            UpdateCapabilityStatus();
            UpdateCapabilityFieldVisibility();
        }
        screenshotSupportBox.SelectionChanged += (_, _) => UpdateCapabilityUi();
        statisticsAdapterBox.SelectionChanged += (_, _) => UpdateCapabilityUi();
        sourcePortVersion.TextChanged += (_, _) => UpdateCapabilityStatus();
        sourcePortExtensions.Text = newSourcePort.SupportedExtensions;
        screenshotSupportBox.SelectedItem = screenshotSupportOptions[0];
        screenshotExtensions.Text = newSourcePort.ScreenshotExtensions;
        statisticsAdapterBox.SelectedItem = statisticsOptions[0];
        saveGameExtensions.Text = newSourcePort.SaveGameExtensions;
        sourcePortChoice.SelectionChanged += (_, _) =>
        {
            if (sourcePortChoice.SelectedItem is not NativeSourcePortDefinition value)
                return;
            sourcePortName.Text = value.SourcePortId.HasValue ? value.Name : string.Empty;
            sourcePortVersion.Text = value.Version;
            sourcePortDirectory.Text = value.Directory;
            sourcePortExecutable.Text = value.Executable;
            sourcePortExtensions.Text = value.SupportedExtensions;
            sourcePortExtra.Text = value.ExtraParameters;
            screenshotSupportBox.SelectedItem = screenshotSupportOptions.First(
                item => item.Code.Equals(
                    value.ScreenshotSupport,
                    StringComparison.OrdinalIgnoreCase));
            screenshotDirectories.Text = value.ScreenshotDirectories;
            screenshotExtensions.Text = value.ScreenshotExtensions;
            statisticsAdapterBox.SelectedItem = statisticsOptions.First(
                item => item.Code.Equals(
                    value.StatisticsAdapter,
                    StringComparison.OrdinalIgnoreCase));
            statisticsDirectories.Text = value.StatisticsDirectories;
            saveGameExtensions.Text = value.SaveGameExtensions;
            sourcePortDeleteButton.IsEnabled = value.SourcePortId.HasValue;
            UpdateCapabilityUi();
        };
        sourcePortChoice.SelectedItem = newSourcePort;

        var newIwad = new NativeIwadDefinition(
            null,
            Strings["NewDefinition"],
            string.Empty,
            string.Empty);
        var iwadChoice = new ListView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(NativeIwadDefinition.DisplayLabel),
            ItemsSource = new[] { newIwad }.Concat(data.Iwads).ToArray(),
            SelectedIndex = 0,
            SelectionMode = ListViewSelectionMode.Single,
        };
        var iwadDeleteButton = CreateDefinitionActionButton(
            Strings["DeleteIwad"],
            "\uE74D");
        iwadDeleteButton.IsEnabled = false;
        AutomationProperties.SetName(iwadChoice, Strings.Iwad);
        var iwadName = CreateDefinitionTextBox(Strings["Name"]);
        var iwadVersion = CreateDefinitionTextBox(Strings["Version"]);
        var iwadArchive = CreateDefinitionTextBox(Strings["ArchiveFile"]);
        var iwadInternal = CreateDefinitionTextBox(Strings["InternalIwad"]);
        var iwadMd5 = string.Empty;
        var iwadFileSize = 0L;
        var iwadCatalogLabel = string.Empty;
        var iwadHashStatus = new InfoBar
        {
            IsClosable = false,
            IsOpen = false,
            Severity = InfoBarSeverity.Informational,
            Title = Strings["IwadVersionDetection"],
        };
        async Task DetectIwadAsync()
        {
            try
            {
                var detected = await _app.NativeLibraryService.DetectIwadVersionAsync(
                    iwadArchive.Text,
                    iwadInternal.Text,
                    _loadCancellation.Token);
                iwadMd5 = detected.Md5;
                iwadFileSize = detected.FileSize;
                iwadCatalogLabel = detected.CatalogLabel;
                if (detected.IsKnown)
                    iwadVersion.Text = detected.Version;
                iwadHashStatus.Severity = detected.IsKnown
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning;
                iwadHashStatus.Message = detected.IsKnown
                    ? _app.Localization.Format(
                        "IwadVersionDetected",
                        detected.DisplayVersion,
                        detected.Md5)
                    : _app.Localization.Format(
                        "IwadVersionUnknown",
                        detected.Md5);
                iwadHashStatus.IsOpen = true;
            }
            catch (Exception exception)
            {
                iwadHashStatus.Severity = InfoBarSeverity.Error;
                iwadHashStatus.Message = exception.Message;
                iwadHashStatus.IsOpen = true;
            }
        }
        var iwadVersionField = CreateDefinitionActionField(
            iwadVersion,
            Strings["DetectIwadVersion"],
            DetectIwadAsync);
        var hexddWarning = new InfoBar
        {
            IsClosable = false,
            IsOpen = false,
            Severity = InfoBarSeverity.Informational,
            Title = Strings["HexddDependencyTitle"],
            Message = Strings["HexddDependencyMessage"],
        };
        void UpdateHexddWarning() =>
            hexddWarning.IsOpen = iwadInternal.Text.Trim().Equals(
                "HEXDD.WAD",
                StringComparison.OrdinalIgnoreCase);
        iwadInternal.TextChanged += (_, _) => UpdateHexddWarning();
        var iwadArchiveField = CreateDefinitionBrowseField(
            iwadArchive,
            async () =>
            {
                var path = await PickDefinitionFileAsync();
                if (path is not null)
                {
                    if (Path.GetExtension(path).Equals(
                            ".wad",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        iwadInternal.Text = Path.GetFileName(path);
                    }
                    iwadArchive.Text = ConvertToPortableReference(
                        path,
                        acknowledgedExternalPaths,
                        portableWarning);
                    await DetectIwadAsync();
                }
            });
        iwadChoice.SelectionChanged += (_, _) =>
        {
            if (iwadChoice.SelectedItem is not NativeIwadDefinition value)
                return;
            iwadName.Text = value.IwadId.HasValue ? value.Name : string.Empty;
            iwadVersion.Text = value.Version;
            iwadArchive.Text = value.ArchiveFileName;
            iwadInternal.Text = value.InternalFileName;
            iwadMd5 = value.Md5;
            iwadFileSize = value.FileSize;
            iwadCatalogLabel = value.CatalogLabel;
            iwadHashStatus.IsOpen = !string.IsNullOrWhiteSpace(value.Md5);
            iwadHashStatus.Severity = string.IsNullOrWhiteSpace(value.CatalogLabel)
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            iwadHashStatus.Message = string.IsNullOrWhiteSpace(value.Md5)
                ? string.Empty
                : _app.Localization.Format(
                    string.IsNullOrWhiteSpace(value.CatalogLabel)
                        ? "IwadVersionUnknown"
                        : "IwadStoredVersion",
                    string.IsNullOrWhiteSpace(value.CatalogLabel)
                        ? value.Md5
                        : value.DisplayLabel,
                    value.Md5);
            iwadDeleteButton.IsEnabled = value.IwadId.HasValue;
            UpdateHexddWarning();
        };
        iwadChoice.SelectedItem = newIwad;

        var sourcePortScanButton = CreateDefinitionActionButton(
            Strings["ScanSourcePortDirectory"],
            "\uE8B7");
        var iwadScanButton = CreateDefinitionActionButton(
            Strings["ScanIwadDirectory"],
            "\uE8B7");
        var sourcePortActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        sourcePortActions.Children.Add(sourcePortScanButton);
        sourcePortActions.Children.Add(sourcePortDeleteButton);
        var iwadActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        iwadActions.Children.Add(iwadScanButton);
        iwadActions.Children.Add(iwadDeleteButton);
        var sourceContent = CreateDefinitionTabContent(
            Strings["SourcePortHelp"],
            sourcePortChoice,
            sourcePortActions,
            CreateDefinitionSection(
                Strings["BasicInformation"],
                sourcePortName,
                sourcePortVersionField),
            CreateDefinitionSection(
                Strings["PathsAndArguments"],
                sourcePortDirectoryField,
                sourcePortExecutableField,
                sourcePortExtensions,
                sourcePortExtra),
            CreateDefinitionSection(
                Strings["SourcePortCapabilities"],
                capabilityStatus,
                new TextBlock
                {
                    Text = Strings["ManualCaptureDirectoriesHelp"],
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72,
                },
                screenshotSupportBox,
                screenshotDirectoriesField,
                screenshotExtensions,
                statisticsAdapterBox,
                statisticsDirectoriesField,
                saveGameExtensions,
                capabilityTestButton));
        var iwadContent = CreateDefinitionTabContent(
            Strings["IwadHelp"],
            iwadChoice,
            iwadActions,
            CreateDefinitionSection(
                Strings["BasicInformation"],
                iwadName,
                iwadVersionField),
            CreateDefinitionSection(
                Strings["PathsAndArguments"],
                iwadArchiveField,
                iwadInternal,
                iwadHashStatus,
                hexddWarning));
        var sourceTab = CreateDefinitionTabButton(
            Strings["SourcePortDefinition"],
            "\uE756");
        var iwadTab = CreateDefinitionTabButton(
            Strings["IwadDefinition"],
            "\uE7F1");
        var tabSelector = new Grid
        {
            ColumnSpacing = 8,
        };
        tabSelector.ColumnDefinitions.Add(new ColumnDefinition());
        tabSelector.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(iwadTab, 1);
        tabSelector.Children.Add(sourceTab);
        tabSelector.Children.Add(iwadTab);
        var definitionHost = new ContentControl
        {
            Content = sourceContent,
        };
        var tabs = new Grid
        {
            Width = 880,
            Height = 590,
            RowSpacing = 10,
        };
        tabs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tabs.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(definitionHost, 1);
        tabs.Children.Add(tabSelector);
        tabs.Children.Add(definitionHost);
        void SelectDefinitionTab(ToggleButton selected, UIElement tabContent)
        {
            sourceTab.IsChecked = ReferenceEquals(selected, sourceTab);
            iwadTab.IsChecked = ReferenceEquals(selected, iwadTab);
            definitionHost.Content = tabContent;
        }
        sourceTab.Click += (_, _) => SelectDefinitionTab(sourceTab, sourceContent);
        iwadTab.Click += (_, _) => SelectDefinitionTab(iwadTab, iwadContent);
        SelectDefinitionTab(sourceTab, sourceContent);

        var content = new StackPanel { Width = 880, Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = Strings["DefinitionsIntro"],
            Opacity = 0.78,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(portableWarning);
        content.Children.Add(tabs);

        var saveStatus = new InfoBar
        {
            IsClosable = true,
            IsOpen = false,
            Severity = InfoBarSeverity.Success,
        };
        content.Children.Add(saveStatus);

        var sourcePortDeleteWarning = new TextBlock
        {
            MaxWidth = 360,
            TextWrapping = TextWrapping.Wrap,
        };
        var confirmSourcePortDelete = new Button
        {
            Content = Strings["Delete"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var deleteSourcePortFiles = new CheckBox
        {
            Content = Strings["DeleteSourcePortFiles"],
        };
        var sourcePortDeleteFlyout = CreateDefinitionDeleteFlyout(
            sourcePortDeleteWarning,
            deleteSourcePortFiles,
            confirmSourcePortDelete);
        sourcePortDeleteButton.Click += (_, _) =>
        {
            if (sourcePortChoice.SelectedItem is not NativeSourcePortDefinition
                { SourcePortId: not null } selected)
            {
                return;
            }
            sourcePortDeleteWarning.Text = _app.Localization.Format(
                "DeleteDefinitionWarning",
                selected.DisplayLabel);
            deleteSourcePortFiles.IsChecked = false;
            sourcePortDeleteFlyout.ShowAt(sourcePortDeleteButton);
        };
        confirmSourcePortDelete.Click += async (_, _) =>
        {
            if (sourcePortChoice.SelectedItem is not NativeSourcePortDefinition
                { SourcePortId: not null } selected)
            {
                return;
            }
            try
            {
                await _app.NativeLibraryService.DeleteSourcePortAsync(
                    selected.SourcePortId.Value,
                    deleteSourcePortFiles.IsChecked == true,
                    _loadCancellation.Token);
                data = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(
                    _loadCancellation.Token);
                sourcePortChoice.ItemsSource =
                    new[] { newSourcePort }.Concat(data.SourcePorts).ToArray();
                sourcePortChoice.SelectedItem = data.SourcePorts.FirstOrDefault()
                    ?? newSourcePort;
                saveStatus.Title = Strings["DefinitionDeleted"];
                saveStatus.Message = _app.Localization.Format(
                    "DefinitionDeletedMessage",
                    selected.DisplayLabel);
                saveStatus.Severity = InfoBarSeverity.Success;
                saveStatus.IsOpen = true;
            }
            catch (Exception exception)
            {
                saveStatus.Title = Strings["DeleteFailed"];
                saveStatus.Message = exception.Message;
                saveStatus.Severity = InfoBarSeverity.Error;
                saveStatus.IsOpen = true;
            }
            finally
            {
                sourcePortDeleteFlyout.Hide();
            }
        };

        var iwadDeleteWarning = new TextBlock
        {
            MaxWidth = 360,
            TextWrapping = TextWrapping.Wrap,
        };
        var confirmIwadDelete = new Button
        {
            Content = Strings["Delete"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var deleteIwadFiles = new CheckBox
        {
            Content = Strings["DeleteIwadFiles"],
        };
        var iwadDeleteFlyout = CreateDefinitionDeleteFlyout(
            iwadDeleteWarning,
            deleteIwadFiles,
            confirmIwadDelete);
        iwadDeleteButton.Click += (_, _) =>
        {
            if (iwadChoice.SelectedItem is not NativeIwadDefinition
                { IwadId: not null } selected)
            {
                return;
            }
            iwadDeleteWarning.Text = _app.Localization.Format(
                "DeleteDefinitionWarning",
                selected.DisplayLabel);
            deleteIwadFiles.IsChecked = false;
            iwadDeleteFlyout.ShowAt(iwadDeleteButton);
        };
        confirmIwadDelete.Click += async (_, _) =>
        {
            if (iwadChoice.SelectedItem is not NativeIwadDefinition
                { IwadId: not null } selected)
            {
                return;
            }
            try
            {
                await _app.NativeLibraryService.DeleteIwadAsync(
                    selected.IwadId.Value,
                    deleteIwadFiles.IsChecked == true,
                    _loadCancellation.Token);
                data = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(
                    _loadCancellation.Token);
                iwadChoice.ItemsSource =
                    new[] { newIwad }.Concat(data.Iwads).ToArray();
                iwadChoice.SelectedItem = data.Iwads.FirstOrDefault()
                    ?? newIwad;
                saveStatus.Title = Strings["DefinitionDeleted"];
                saveStatus.Message = _app.Localization.Format(
                    "DefinitionDeletedMessage",
                    selected.DisplayLabel);
                saveStatus.Severity = InfoBarSeverity.Success;
                saveStatus.IsOpen = true;
            }
            catch (Exception exception)
            {
                saveStatus.Title = Strings["DeleteFailed"];
                saveStatus.Message = exception.Message;
                saveStatus.Severity = InfoBarSeverity.Error;
                saveStatus.IsOpen = true;
            }
            finally
            {
                iwadDeleteFlyout.Hide();
            }
        };
        sourcePortScanButton.Click += async (_, _) =>
        {
            await RunDefinitionScanAsync(
                _app.FirstSetupService.ScanSourcePortsAsync,
                async () =>
                {
                    data = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(
                        _loadCancellation.Token);
                    sourcePortChoice.ItemsSource =
                        new[] { newSourcePort }.Concat(data.SourcePorts).ToArray();
                    sourcePortChoice.SelectedItem = data.SourcePorts.FirstOrDefault()
                        ?? newSourcePort;
                },
                saveStatus);
        };
        iwadScanButton.Click += async (_, _) =>
        {
            await RunDefinitionScanAsync(
                _app.FirstSetupService.ScanIwadsAsync,
                async () =>
                {
                    data = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(
                        _loadCancellation.Token);
                    iwadChoice.ItemsSource =
                        new[] { newIwad }.Concat(data.Iwads).ToArray();
                    iwadChoice.SelectedItem = data.Iwads.FirstOrDefault()
                        ?? newIwad;
                },
                saveStatus);
        };
        var saveButton = new Button
        {
            Content = Strings["Save"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var cancelButton = new Button
        {
            Content = Strings["Cancel"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var closeButton = new Button
        {
            Content = Strings["Close"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var footer = new Grid { ColumnSpacing = 10 };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(cancelButton, 1);
        Grid.SetColumn(closeButton, 2);
        footer.Children.Add(saveButton);
        footer.Children.Add(cancelButton);
        footer.Children.Add(closeButton);
        content.Children.Add(footer);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            MinWidth = 940,
            MaxWidth = 980,
            MaxHeight = 900,
            Title = Strings["LauncherDefinitions"],
            Content = content,
        };
        ApplyDialogTheme(dialog);
        dialog.Resources["ContentDialogMinWidth"] = 940d;
        dialog.Resources["ContentDialogMaxWidth"] = 1000d;
        dialog.Resources["ContentDialogMaxHeight"] = 900d;
        saveButton.Click += async (_, _) =>
        {
            try
            {
                if (ReferenceEquals(definitionHost.Content, sourceContent))
                {
                    await WarnIfExternalReferenceAsync(
                        sourcePortDirectory.Text,
                        acknowledgedExternalPaths);
                    await WarnIfExternalReferenceAsync(
                        sourcePortExecutable.Text,
                        acknowledgedExternalPaths);
                    await WarnIfExternalReferencesAsync(
                        screenshotDirectories.Text,
                        acknowledgedExternalPaths);
                    await WarnIfExternalReferencesAsync(
                        statisticsDirectories.Text,
                        acknowledgedExternalPaths);
                    var selected =
                        (NativeSourcePortDefinition)sourcePortChoice.SelectedItem;
                    await _app.NativeLibraryService.SaveSourcePortAsync(
                        selected with
                        {
                            Name = sourcePortName.Text,
                            Directory = sourcePortDirectory.Text,
                            Executable = sourcePortExecutable.Text,
                            SupportedExtensions = sourcePortExtensions.Text,
                            FileOption = "-file",
                            ExtraParameters = sourcePortExtra.Text,
                            Version = sourcePortVersion.Text,
                            ScreenshotSupport =
                                ((DefinitionOption)screenshotSupportBox.SelectedItem).Code,
                            ScreenshotDirectories = screenshotDirectories.Text,
                            ScreenshotExtensions = screenshotExtensions.Text,
                            ScreenshotArgument = string.Empty,
                            StatisticsAdapter =
                                ((DefinitionOption)statisticsAdapterBox.SelectedItem).Code,
                            StatisticsDirectories = statisticsDirectories.Text,
                            SaveGameExtensions = saveGameExtensions.Text,
                        },
                        _loadCancellation.Token);
                    data = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(
                        _loadCancellation.Token);
                    sourcePortChoice.ItemsSource =
                        new[] { newSourcePort }.Concat(data.SourcePorts).ToArray();
                    sourcePortChoice.SelectedItem = data.SourcePorts.FirstOrDefault(
                        item => selected.SourcePortId.HasValue
                            ? item.SourcePortId == selected.SourcePortId
                            : string.Equals(
                                item.Name,
                                sourcePortName.Text,
                                StringComparison.OrdinalIgnoreCase))
                        ?? newSourcePort;
                }
                else
                {
                    await WarnIfExternalReferenceAsync(
                        iwadArchive.Text,
                        acknowledgedExternalPaths);
                    var selected = (NativeIwadDefinition)iwadChoice.SelectedItem;
                    await _app.NativeLibraryService.SaveIwadAsync(
                        selected with
                        {
                            Name = iwadName.Text,
                            ArchiveFileName = iwadArchive.Text,
                            InternalFileName = iwadInternal.Text,
                            Version = iwadVersion.Text,
                            Md5 = iwadMd5,
                            FileSize = iwadFileSize,
                            CatalogLabel = iwadCatalogLabel,
                        },
                        _loadCancellation.Token);
                    data = await _app.NativeLibraryService.LoadLauncherDefinitionsAsync(
                        _loadCancellation.Token);
                    iwadChoice.ItemsSource =
                        new[] { newIwad }.Concat(data.Iwads).ToArray();
                    iwadChoice.SelectedItem = data.Iwads.FirstOrDefault(
                        item => selected.IwadId.HasValue
                            ? item.IwadId == selected.IwadId
                            : string.Equals(
                                item.Name,
                                iwadName.Text,
                                StringComparison.OrdinalIgnoreCase))
                        ?? newIwad;
                }
                saveStatus.Title = Strings["Saved"];
                saveStatus.Message = Strings["DefinitionsSavedOpen"];
                saveStatus.Severity = InfoBarSeverity.Success;
                saveStatus.IsOpen = true;
            }
            catch (Exception exception)
            {
                saveStatus.Title = Strings["SaveFailed"];
                saveStatus.Message = exception.Message;
                saveStatus.Severity = InfoBarSeverity.Error;
                saveStatus.IsOpen = true;
            }
        };
        cancelButton.Click += (_, _) =>
        {
            if (ReferenceEquals(definitionHost.Content, sourceContent))
            {
                var selected =
                    (NativeSourcePortDefinition)sourcePortChoice.SelectedItem;
                sourcePortName.Text = selected.SourcePortId.HasValue
                    ? selected.Name
                    : string.Empty;
                sourcePortVersion.Text = selected.Version;
                sourcePortDirectory.Text = selected.Directory;
                sourcePortExecutable.Text = selected.Executable;
                sourcePortExtensions.Text = selected.SupportedExtensions;
                sourcePortExtra.Text = selected.ExtraParameters;
                screenshotSupportBox.SelectedItem = screenshotSupportOptions.First(
                    item => item.Code.Equals(
                        selected.ScreenshotSupport,
                        StringComparison.OrdinalIgnoreCase));
                screenshotDirectories.Text = selected.ScreenshotDirectories;
                screenshotExtensions.Text = selected.ScreenshotExtensions;
                statisticsAdapterBox.SelectedItem = statisticsOptions.First(
                    item => item.Code.Equals(
                        selected.StatisticsAdapter,
                        StringComparison.OrdinalIgnoreCase));
                statisticsDirectories.Text = selected.StatisticsDirectories;
                saveGameExtensions.Text = selected.SaveGameExtensions;
                UpdateCapabilityUi();
            }
            else
            {
                var selected = (NativeIwadDefinition)iwadChoice.SelectedItem;
                iwadName.Text = selected.IwadId.HasValue ? selected.Name : string.Empty;
                iwadVersion.Text = selected.Version;
                iwadArchive.Text = selected.ArchiveFileName;
                iwadInternal.Text = selected.InternalFileName;
                iwadMd5 = selected.Md5;
                iwadFileSize = selected.FileSize;
                iwadCatalogLabel = selected.CatalogLabel;
                iwadHashStatus.IsOpen = !string.IsNullOrWhiteSpace(selected.Md5);
            }
            saveStatus.IsOpen = false;
        };
        closeButton.Click += (_, _) => dialog.Hide();
        await dialog.ShowAsync();
        await ViewModel.RefreshAsync(_loadCancellation.Token);
    }

    private async Task RunDefinitionScanAsync(
        Func<CancellationToken, Task<SetupScanResult>> scan,
        Func<Task> reload,
        InfoBar status)
    {
        try
        {
            status.Title = Strings["Scanning"];
            status.Message = Strings["ScanningDirectories"];
            status.Severity = InfoBarSeverity.Informational;
            status.IsOpen = true;
            var result = await scan(_loadCancellation.Token);
            await reload();
            status.Title = Strings["ScanComplete"];
            status.Message = _app.Localization.Format(
                "FirstSetupScanResult",
                result.Discovered,
                result.Imported,
                result.Updated,
                result.Removed,
                result.Skipped);
            if (result.RemovedItems.Count > 0)
            {
                status.Message += Environment.NewLine
                    + _app.Localization.Format(
                        "RemovedDefinitions",
                        string.Join(", ", result.RemovedItems));
            }
            if (result.Warnings.Count > 0)
            {
                status.Message += " "
                    + _app.Localization.Format(
                        "FirstSetupWarnings",
                        result.Warnings.Count);
                status.Severity = InfoBarSeverity.Warning;
            }
            else
            {
                status.Severity = result.Removed > 0
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success;
            }
        }
        catch (Exception exception)
        {
            status.Title = Strings["ScanFailed"];
            status.Message = exception.Message;
            status.Severity = InfoBarSeverity.Error;
            status.IsOpen = true;
        }
    }

    private static TextBox CreateDefinitionTextBox(string header)
    {
        var box = new TextBox
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(box, header);
        return box;
    }

    private static ComboBox CreateDefinitionOptionBox(
        string header,
        IReadOnlyList<DefinitionOption> options)
    {
        var box = new ComboBox
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(DefinitionOption.Label),
            ItemsSource = options,
        };
        AutomationProperties.SetName(box, header);
        return box;
    }

    private Grid CreateDefinitionActionField(
        TextBox textBox,
        string actionLabel,
        Func<Task> actionAsync)
    {
        var actionButton = new Button
        {
            Margin = new Thickness(8, 27, 0, 0),
            MinWidth = 104,
            VerticalAlignment = VerticalAlignment.Bottom,
            Content = actionLabel,
        };
        actionButton.Click += async (_, _) => await actionAsync();
        AutomationProperties.SetName(
            actionButton,
            $"{actionLabel}: {textBox.Header}");

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });
        Grid.SetColumn(actionButton, 1);
        layout.Children.Add(textBox);
        layout.Children.Add(actionButton);
        return layout;
    }

    private static string ReadExecutableVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return string.Empty;
            var info = FileVersionInfo.GetVersionInfo(path);
            return DatabaseTextSanitizer.SingleLine(
                string.IsNullOrWhiteSpace(info.ProductVersion)
                    ? info.FileVersion
                    : info.ProductVersion);
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static bool IsZDoomFamilyExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return new[]
        {
            "gzdoom",
            "uzdoom",
            "zdoom",
            "vkdoom",
            "lzdoom",
            "qzdoom",
            "zandronum",
            "skulltag",
        }.Any(value => name.Contains(
            value,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveDefinitionExecutablePath(
        string directory,
        string executable)
    {
        if (string.IsNullOrWhiteSpace(directory)
            || string.IsNullOrWhiteSpace(executable))
        {
            return string.Empty;
        }
        var root = GetPortableRoot();
        var resolvedDirectory = Path.GetFullPath(
            Path.IsPathFullyQualified(directory)
                ? directory
                : Path.Combine(root, directory));
        return Path.Combine(resolvedDirectory, Path.GetFileName(executable));
    }

    private Grid CreateDefinitionBrowseField(
        TextBox textBox,
        Func<Task> browseAsync)
    {
        var browseButton = new Button
        {
            Margin = new Thickness(8, 27, 0, 0),
            MinWidth = 104,
            VerticalAlignment = VerticalAlignment.Bottom,
            Content = Strings["Browse"],
        };
        AutomationProperties.SetName(
            browseButton,
            $"{Strings["Browse"]}: {textBox.Header}");
        browseButton.Click += async (_, _) => await browseAsync();

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });
        Grid.SetColumn(browseButton, 1);
        layout.Children.Add(textBox);
        layout.Children.Add(browseButton);
        return layout;
    }

}
