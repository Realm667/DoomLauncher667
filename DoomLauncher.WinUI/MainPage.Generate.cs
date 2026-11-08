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
    private async Task RefreshObsidianDetectionAsync()
    {
        try
        {
            _obsidianInstallation = await _app.ObsidianService.DetectAsync(
                _loadCancellation.Token);
            ObsidianPathText.Text = _obsidianInstallation.InstallDirectory;
            ObsidianVersionText.Text = _obsidianInstallation.IsAvailable
                ? string.IsNullOrWhiteSpace(_obsidianInstallation.DisplayVersion)
                    ? Strings["ObsidianDetected"]
                    : _obsidianInstallation.DisplayVersion
                : Strings["ObsidianNotDetected"];
            ObsidianStatusIcon.Foreground = (Brush)Application.Current.Resources[
                _obsidianInstallation.IsAvailable
                    ? "DoomSuccessBrush"
                    : "DoomAccentBrush"];
            GenerateObsidianButton.IsEnabled = _obsidianInstallation.IsAvailable
                && _obsidianGenerationCancellation is null;
            ViewModel.SetGenerateSummary(_obsidianInstallation.IsAvailable
                ? _app.Localization.Format(
                    "GenerateSummaryReady",
                    _obsidianInstallation.DisplayVersion)
                : Strings["GenerateSummaryMissing"]);
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ObsidianVersionText.Text = Strings["ObsidianNotDetected"];
            GenerateObsidianButton.IsEnabled = false;
            ViewModel.SetGenerateSummary(exception.Message);
        }
    }

    private async void RefreshObsidianButton_Click(
        object sender,
        RoutedEventArgs args) => await RefreshObsidianDetectionAsync();

    private void OpenObsidianFolderButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        var directory = _app.ObsidianService.InstallDirectory;
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        };
        start.ArgumentList.Add(directory);
        Process.Start(start);
    }

    private void RandomizeObsidianButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        foreach (var box in new[]
                 {
                     ObsidianGameBox,
                     ObsidianPortBox,
                     ObsidianLengthBox,
                     ObsidianThemeBox,
                     ObsidianMapSizeBox,
                     ObsidianOutdoorsBox,
                     ObsidianCavesBox,
                     ObsidianLiquidsBox,
                     ObsidianHallwaysBox,
                     ObsidianSteepnessBox,
                     ObsidianMonstersBox,
                     ObsidianStrengthBox,
                     ObsidianBossesBox,
                     ObsidianTrapsBox,
                     ObsidianCagesBox,
                     ObsidianHealthBox,
                     ObsidianAmmoBox,
                     ObsidianWeaponsBox,
                 })
        {
            if (box.Items.Count > 0)
                box.SelectedIndex = Random.Shared.Next(box.Items.Count);
        }
        ObsidianSeedBox.Value = Random.Shared.Next(1, int.MaxValue);
        ObsidianSkySwitch.IsOn = Random.Shared.Next(2) == 1;
        ObsidianMusicSwitch.IsOn = Random.Shared.Next(2) == 1;
    }

    private async void GenerateObsidianButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_obsidianGenerationCancellation is not null)
            return;
        await RefreshObsidianDetectionAsync();
        if (_obsidianGenerationCancellation is not null)
            return;
        if (_obsidianInstallation?.IsAvailable != true)
        {
            await ShowErrorAsync(
                Strings["ObsidianMissingTitle"],
                _app.Localization.Format(
                    "ObsidianMissingMessage",
                    _app.ObsidianService.InstallDirectory));
            return;
        }

        _obsidianGenerationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _loadCancellation.Token);
        GenerateObsidianButton.IsEnabled = false;
        ObsidianOptionsPanel.IsEnabled = false;
        RandomizeObsidianButton.IsEnabled = false;
        ObsidianProgressPanel.Visibility = Visibility.Visible;
        ObsidianProgressBar.Value = 0;
        ObsidianProgressText.Text = Strings["GenerationReady"];
        var progress = new Progress<ObsidianGenerationProgress>(update =>
        {
            ObsidianProgressBar.Value = Math.Clamp(update.Value, 0, 1) * 100;
            ObsidianProgressText.Text = update.Status;
        });
        try
        {
            var result = await _app.ObsidianService.GenerateAsync(
                BuildObsidianOptions(),
                progress,
                _obsidianGenerationCancellation.Token);
            await EnsureGeneratedLibraryFilterAsync();
            await ViewModel.RefreshAsync(_loadCancellation.Token);
            ObsidianProgressBar.Value = 100;
            ObsidianProgressText.Text = _app.Localization.Format(
                "GenerationCompleted",
                result.FileName);
            ViewModel.ShowLatestImports();
        }
        catch (OperationCanceledException)
        {
            ObsidianProgressText.Text = Strings["CancelGeneration"];
        }
        catch (Exception exception)
        {
            ObsidianProgressText.Text = exception.Message;
            await ShowErrorAsync(Strings["GenerationFailed"], exception.Message);
        }
        finally
        {
            _obsidianGenerationCancellation.Dispose();
            _obsidianGenerationCancellation = null;
            GenerateObsidianButton.IsEnabled = _obsidianInstallation?.IsAvailable == true;
            ObsidianOptionsPanel.IsEnabled = true;
            RandomizeObsidianButton.IsEnabled = true;
        }
    }

    private void CancelObsidianGenerationButton_Click(
        object sender,
        RoutedEventArgs args) => _obsidianGenerationCancellation?.Cancel();

    private ObsidianGenerationOptions BuildObsidianOptions() => new(
        ObsidianOutputNameBox.Text,
        SelectedTag(ObsidianGameBox),
        SelectedTag(ObsidianPortBox),
        SelectedTag(ObsidianLengthBox),
        SelectedTag(ObsidianThemeBox),
        double.IsNaN(ObsidianSeedBox.Value)
            ? 0
            : (int)Math.Clamp(ObsidianSeedBox.Value, 0, int.MaxValue),
        SelectedTag(ObsidianMapSizeBox),
        SelectedTag(ObsidianOutdoorsBox),
        SelectedTag(ObsidianCavesBox),
        SelectedTag(ObsidianLiquidsBox),
        SelectedTag(ObsidianHallwaysBox),
        SelectedTag(ObsidianSteepnessBox),
        SelectedTag(ObsidianMonstersBox),
        SelectedTag(ObsidianStrengthBox),
        SelectedTag(ObsidianBossesBox),
        SelectedTag(ObsidianTrapsBox),
        SelectedTag(ObsidianCagesBox),
        SelectedTag(ObsidianHealthBox),
        SelectedTag(ObsidianAmmoBox),
        SelectedTag(ObsidianWeaponsBox),
        ObsidianSkySwitch.IsOn,
        ObsidianMusicSwitch.IsOn);

    private static string SelectedTag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private async Task EnsureGeneratedLibraryFilterAsync()
    {
        var tags = _userState.LibraryFilterTags.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (!tags.Add("Generated"))
            return;
        _userState = _userState with
        {
            LibraryFilterTags = tags
                .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
        };
        await _app.UserLibraryStateStore.SaveAsync(
            _userState,
            _loadCancellation.Token);
        RebuildCollectionFilterButtons();
    }

}
