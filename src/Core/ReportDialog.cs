using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Texture_Set_Manager.Core;

/// <summary>
/// A modal "here is a wall of text you probably want to keep" dialog: scrollable monospaced
/// body, a copy-to-clipboard button, and optional links.
///
/// Exists because the sidebar log is a stream – anything long scrolls away or gets skimmed
/// past, and reports the user is meant to act on (a crash from last session, a list of
/// textures to go fix) shouldn't rely on them noticing. The dialog puts it in front of them
/// once; the log still keeps its copy for later.
/// </summary>
public static class ReportDialog
{
    public sealed record Link(string Text, string Uri);

    /// <summary>
    /// Room a ContentDialog needs for its own chrome – title, padding, and the pinned button
    /// row – before any of the height is available to content. Deliberately generous: guessing
    /// too high costs a little scrolling, guessing too low puts controls off-screen.
    /// </summary>
    private const double DialogChromeHeight = 220;

    /// <summary>
    /// The most vertical space content may take, given how tall the window actually is right
    /// now. Anything beyond this scrolls.
    /// </summary>
    private static double AvailableContentHeight(XamlRoot xamlRoot, double preferredMax)
    {
        var windowHeight = xamlRoot.Size.Height;

        // A tiny window still has to show something; 120px of content plus the pinned buttons
        // remains usable, and the ScrollViewer takes care of the rest.
        var usable = Math.Max(120, windowHeight - DialogChromeHeight);
        return Math.Min(preferredMax, usable);
    }

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
        double contentMaxHeight = 420)
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
        panel.Children.Add(new TextBlock
        {
            Text = body,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        });

        // Everything scrolls together inside a height the current window can actually show.
        // Previously only the body scrolled and the buttons lived in the content, so on a short
        // window the dialog simply grew past the bottom of the screen and the close button became
        // unreachable with nothing to scroll – the user had to resize the window to escape.
        var scroller = new ScrollViewer
        {
            Content = panel,
            MaxHeight = AvailableContentHeight(xamlRoot, contentMaxHeight),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // Copy and Close are the dialog's own buttons rather than content: ContentDialog pins its
        // button row, so they can never be scrolled or pushed out of reach however small the
        // window gets or however long the report is.
        var dialog = new ContentDialog
        {
            Title = title,
            Content = scroller,
            PrimaryButtonText = copyButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
            RequestedTheme = theme
        };

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            // Copying shouldn't dismiss the report – the user may well want to read on.
            args.Cancel = true;

            try
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(body);
                Clipboard.SetContent(dataPackage);
                dialog.PrimaryButtonText = "Copied!";
            }
            catch
            {
                dialog.PrimaryButtonText = "Couldn't copy";
            }

            _ = RestoreLabelAsync();

            async Task RestoreLabelAsync()
            {
                await Task.Delay(1500);
                dialog.PrimaryButtonText = copyButtonText;
            }
        };

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
            // Bounded the same way as the report: a long folder path on a short window would
            // otherwise push the buttons off the bottom.
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                MaxHeight = AvailableContentHeight(xamlRoot, 300),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
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
