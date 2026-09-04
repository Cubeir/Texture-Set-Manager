using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Texture_Set_Manager.Core;

/// <summary>
/// A modal "here is a wall of text you probably want to keep" dialog: scrollable monospaced
/// body, a copy-to-clipboard button, and optional links.
///
/// Exists because the sidebar log is a stream — anything long scrolls away or gets skimmed
/// past, and reports the user is meant to act on (a crash from last session, a list of
/// textures to go fix) shouldn't rely on them noticing. The dialog puts it in front of them
/// once; the log still keeps its copy for later.
/// </summary>
public static class ReportDialog
{
    public sealed record Link(string Text, string Uri);

    public static async Task ShowAsync(
        XamlRoot? xamlRoot,
        ElementTheme theme,
        string title,
        string intro,
        string body,
        string copyButtonText = "Copy Report",
        string closeButtonText = "Close",
        string? linksHeader = null,
        IReadOnlyList<Link>? links = null,
        double bodyMaxHeight = 260)
    {
        if (xamlRoot == null) return;

        var panel = new StackPanel { Spacing = 12 };

        if (!string.IsNullOrWhiteSpace(intro))
        {
            panel.Children.Add(new TextBlock
            {
                Text = intro,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (links is { Count: > 0 })
        {
            var linksPanel = new StackPanel { Spacing = 2 };

            if (!string.IsNullOrWhiteSpace(linksHeader))
                linksPanel.Children.Add(new TextBlock { Text = linksHeader });

            foreach (var link in links)
            {
                linksPanel.Children.Add(new HyperlinkButton
                {
                    Content = link.Text,
                    NavigateUri = new Uri(link.Uri),
                    Padding = new Thickness(4)
                });
            }

            panel.Children.Add(linksPanel);
        }

        // Monospaced so paths and columns line up; selectable so the user can grab one line
        // instead of the whole report when that's all they need.
        var bodyBlock = new TextBlock
        {
            Text = body,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };

        panel.Children.Add(new ScrollViewer
        {
            Content = bodyBlock,
            MaxHeight = bodyMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        var copyButton = new Button
        {
            Content = copyButtonText,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(copyButton);

        var dismissButton = new Button
        {
            Content = closeButtonText,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(dismissButton);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            XamlRoot = xamlRoot,
            RequestedTheme = theme
        };

        copyButton.Click += async (_, _) =>
        {
            try
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(body);
                Clipboard.SetContent(dataPackage);

                copyButton.Content = "Copied!";
                await Task.Delay(1500);
                copyButton.Content = copyButtonText;
            }
            catch
            {
                copyButton.Content = "Couldn't copy";
            }
        };

        dismissButton.Click += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
    }

    /// <summary>
    /// Three-way scope prompt used before a destructive folder operation. The choice is the
    /// confirmation: there is no "just do it" path, and Cancel is the default button so a
    /// stray Enter backs out instead of committing.
    /// </summary>
    public enum ScopeChoice { Cancelled, ThisFolderOnly, IncludeSubfolders }

    public static async Task<ScopeChoice> AskScopeAsync(
        XamlRoot? xamlRoot,
        ElementTheme theme,
        string title,
        string message,
        string thisFolderText = "This folder only",
        string recursiveText = "Include subfolders")
    {
        if (xamlRoot == null) return ScopeChoice.Cancelled;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = thisFolderText,
            SecondaryButtonText = recursiveText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
            RequestedTheme = theme
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => ScopeChoice.ThisFolderOnly,
            ContentDialogResult.Secondary => ScopeChoice.IncludeSubfolders,
            _ => ScopeChoice.Cancelled
        };
    }
}
