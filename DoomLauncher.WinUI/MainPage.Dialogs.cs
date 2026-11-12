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
    private async Task<T> RunProgressDialogAsync<T>(
        string title,
        string status,
        Func<IProgress<double>, Task<T>> operation,
        Func<string>? currentStatus = null,
        CancellationTokenSource? cancellation = null)
    {
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            MinWidth = 0,
            Foreground = (Brush)Application.Current.Resources[
                "DoomControlAccentBrush"],
        };
        var percentage = new TextBlock
        {
            Text = "0%",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var statusText = new TextBlock { Text = status, TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel
        {
            Width = UiLayoutPolicy.DialogWidth(XamlRoot.Size.Width, 480),
            Spacing = 12,
            Children =
            {
                statusText,
                progressBar,
                percentage,
            },
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = title,
            Content = content,
            CloseButtonText = cancellation is null ? string.Empty : Strings["Cancel"],
        };
        dialog.CloseButtonClick += (_, _) => cancellation?.Cancel();
        ApplyDialogTheme(dialog);
        FitDialogToViewport(dialog);

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closing += (_, args) => args.Cancel = !completion.Task.IsCompleted;
        dialog.Opened += async (_, _) =>
        {
            var progress = new Progress<double>(value =>
            {
                var normalized = Math.Clamp(value, 0, 100);
                progressBar.Value = normalized;
                percentage.Text = $"{normalized:0}%";
                if (currentStatus is not null) statusText.Text = currentStatus();
            });
            try
            {
                completion.TrySetResult(await operation(progress));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                dialog.Hide();
            }
        };
        await dialog.ShowAsync();
        return await completion.Task;
    }

    private async Task<IReadOnlyDictionary<string, IwadInModsAction>>
        ResolveIwadsInModsAsync()
    {
        var prompts = await RunProgressDialogAsync(
            Strings["CheckingModsForIwads"],
            Strings["CheckingModsForIwadsStatus"],
            progress => _app.FirstSetupService.FindIwadsInModsAsync(
                _loadCancellation.Token,
                progress));
        var decisions = new Dictionary<string, IwadInModsAction>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var prompt in prompts)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = EffectiveDialogTheme,
                Title = Strings["IwadFoundInModsTitle"],
                Content = new TextBlock
                {
                    MaxWidth = 620,
                    Text = _app.Localization.Format(
                        "IwadFoundInModsMessage",
                        prompt.FileName,
                        string.Join(", ", prompt.DetectedIwads)),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = Strings["MoveAndRegisterIwad"],
                SecondaryButtonText = Strings["KeepAsMod"],
                DefaultButton = ContentDialogButton.Primary,
            };
            ApplyDialogTheme(dialog);
            var result = await dialog.ShowAsync();
            decisions[Path.GetFullPath(prompt.FilePath)] =
                result == ContentDialogResult.Primary
                    ? IwadInModsAction.MoveAndRegister
                    : IwadInModsAction.KeepAsMod;
        }
        return decisions;
    }

    private async Task ShowActionMessageAsync(
        string title,
        string message,
        string action)
    {
        // Setup/wizard Next and Finish remain explicit. Routine acknowledgments
        // no longer interrupt selection, keyboard focus or navigation.
        if (action == Strings["Close"] || action == Strings["Ok"])
        {
            ShowFeedback(title, message);
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            },
            PrimaryButtonText = action,
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyDialogTheme(dialog);
        await dialog.ShowAsync();
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = title,
            Content = new StackPanel { Spacing = 12, Children = {
                new TextBlock { Text = Strings["ActionFailedGuidance"], TextWrapping = TextWrapping.Wrap },
                new Expander { Header = Strings["TechnicalDetails"], HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new ScrollViewer { MaxHeight = 300, Content = new TextBlock { Text = message, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap } } }
            } },
            CloseButtonText = Strings["Close"],
        };
        ApplyDialogTheme(dialog);
        await dialog.ShowAsync();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Strings["Ok"],
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyDialogTheme(dialog);
        await dialog.ShowAsync();
    }

    private async Task<ImportFileConflictResolution> AskImportConflictResolutionAsync(
        string fileName)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = Strings["ImportFileConflictTitle"],
            Content = new TextBlock
            {
                Text = _app.Localization.Format(
                    "ImportFileConflictMessage",
                    fileName),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Strings["Overwrite"],
            CloseButtonText = Strings["Skip"],
            DefaultButton = ContentDialogButton.Close,
        };
        ApplyDialogTheme(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? ImportFileConflictResolution.Overwrite
            : ImportFileConflictResolution.Skip;
    }

}
