using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Texture_Set_Manager.Core;

/// <summary>
/// Single place where theme-dependent colors are decided. Nothing here subscribes to
/// anything – callers own their subscriptions and are responsible for unhooking them,
/// which is precisely why this replaced the old fire-and-forget ThemeWatcher that kept
/// poking at a window long after it had been closed.
/// </summary>
public static class ThemeService
{
    public static event Action<ElementTheme>? ThemeChanged;

    public static void Broadcast(ElementTheme theme) => ThemeChanged?.Invoke(theme);

    public static void ApplyTitleBarColors(AppWindow appWindow, ElementTheme theme)
    {
        var titleBar = appWindow?.TitleBar;
        if (titleBar == null) return;

        var isLight = theme == ElementTheme.Light;
        titleBar.ButtonForegroundColor = isLight ? Colors.Black : Colors.White;
        titleBar.ButtonHoverForegroundColor = isLight ? Colors.Black : Colors.White;
        titleBar.ButtonPressedForegroundColor = isLight ? Colors.Black : Colors.White;
        titleBar.ButtonInactiveForegroundColor = isLight
            ? Color.FromArgb(255, 128, 128, 128)
            : Color.FromArgb(255, 160, 160, 160);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = isLight
            ? Color.FromArgb(20, 0, 0, 0)
            : Color.FromArgb(40, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = isLight
            ? Color.FromArgb(40, 0, 0, 0)
            : Color.FromArgb(60, 255, 255, 255);
    }

    public static ElementTheme ResolveInitialTheme() =>
        (EnvironmentVariables.Persistent.AppThemeMode ?? "System") switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

    public enum BevelEdge { Left, Right }

    /// <summary>
    /// Color for one edge of the "fake split button" bevel decoration drawn between
    /// adjacent buttons. Left edge always reads the "bright" source, right edge the
    /// "dark" source – accented=true when the button the bevel belongs to represents an
    /// active/highlighted state (a checked toggle), accented=false for the resting state.
    /// </summary>
    public static Color GetBevelColor(ElementTheme theme, BevelEdge edge, bool accented, bool isEnabled = true)
    {
        // Disabled state always falls back to the resting (non-accented) bevel, dimmed,
        // regardless of what the enabled state would've shown – this way re-enabling just
        // re-runs the normal accented/resting logic and the bevel snaps back exactly as
        // if nothing had happened.
        if (!isEnabled)
        {
            var restingColor = GetRestingBevelColor(theme, edge);
            return Color.FromArgb(90, restingColor.R, restingColor.G, restingColor.B); // ~35% opacity, matches typical WinUI disabled dimming
        }

        if (accented)
        {
            var key = edge == BevelEdge.Left
                ? (theme == ElementTheme.Light ? "SystemAccentColorLight1" : "SystemAccentColorLight3")
                : (theme == ElementTheme.Light ? "SystemAccentColorDark2" : "SystemAccentColorDark1");
            return (Color)Application.Current.Resources[key];
        }

        return GetRestingBevelColor(theme, edge);
    }

    private static Color GetRestingBevelColor(ElementTheme theme, BevelEdge edge)
    {
        var themeKey = theme == ElementTheme.Light ? "Light" : "Dark";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var themeDictObj)
            && themeDictObj is ResourceDictionary dict)
        {
            var resKey = edge == BevelEdge.Left ? "FakeSplitButtonBrightBorderColor" : "FakeSplitButtonDarkBorderColor";
            if (dict.TryGetValue(resKey, out var colorObj) && colorObj is Color color)
                return color;
        }
        return Colors.Transparent;
    }
}
