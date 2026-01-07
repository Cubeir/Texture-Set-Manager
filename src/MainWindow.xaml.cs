using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Texture_Set_Manager.Core;
using Texture_Set_Manager.Modules;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using static System.Runtime.InteropServices.JavaScript.JSType;
using static Texture_Set_Manager.Core.WindowControlsManager;
using static Texture_Set_Manager.TunerVariables;
using static Texture_Set_Manager.TunerVariables.Persistent;

namespace Texture_Set_Manager;

/*
### GENERAL TODO & IDEAS ###

- Is the lamp halo too weak at rest? it seems inconsistent, during runtime reglar flash halos are very bright
watchya doing?

- Add a way to add custom presets to BetterRTX Manager (e.g. user made presets)
Give it special treatment same as default preset and avoid changing existing logic
they appear at the bottom
expects zips or rtpacks to be passed in, extracts bins and makes a custom preset, name em custom_preset_[increment]
basically, instead of changing the current pipeline, integerate this/build it on top of it
that way it'll surely work without fucking things up

- Do what you promised:
https://github.com/Cubeir/Vanilla-RTX/issues/60

- https://discord.com/channels/721377277480402985/1455281964138369054/1455548708123840604
Does the app stop working if Minecraft, for whatever the reason, is named weirdly?
Should the GDKLocator's behavior be updated to: just find the game's exe?
But then, how do we differentiate preview and release?

and
- https://discord.com/channels/721377277480402985/1453451223599546399
there were some more reports on Discord

Investigate, and after all changes, TEST the whole thing again
locator, manual locator, all steps, on diff drives, deep in subfolders up to 9, on a busy last drive/worst case
And lastly the CACHE invalidator, will it continue to work well with it (betterrtx cache invalidator)

- Update the docs to be less verbose, more accurate and helpful instead, cut off unneeded details.

- Further review PackUpdater and BetterRTX manager codes, ensure no stone is left unturned.
Especially release builds
Game detection and cache invalidation could be improved for both
PackUpdater may have blindspots still, though HIGHLY unlikely, still, review and test, make changes on the go

- Go over Main Window again some time, especially update ToggleControls usage, its... weird to say the least
Be more CONSISTENT with it, and ensure sidebarlogbox NEVER EVER EVER gets disabled on the main window!
Some overrides now disable it while they should not.

- Unify the 4 places hardcoded paths are used into a class
pack updater, pack locator, pack browser, launcher, they deal with hardcoded paths, what else? (Ask copilot to scry the code)

For finding the game, GDKLocator kit handles it system-wide, all good
**For Minecraft's USER DATA however, you better expose those, apparently some third party launchers use different paths!!!**

For GDKLocator, and wherever it is used, you could still expose the SPECIFIC file and folder names it looks for
Actually don't expose anything, the overhead and the risk, instead, make them globally-available constants that can easily be changed
so in the event of Minecraft files restructuing, you can quickly release an update without having to do much testing, make the code clear, basically
This Applies to this older todo below as well:

- Expose as many params as you can to a json in app's root
the URLs the app sends requests to + the hardcoded Minecraft paths
* Resource packs only end up in shared
* Options file is in both shared and non-shared, but non-shared is presumably the one that takes priority, still, we take care of both
* PackLocator, PackUpdater (deployer), Browse Packs, and LaunchMinecraftRTX's options.txt updater are the only things that rely on hardcoded paths on the system
* EXPOSE ALL hardcoded URLs and Tuning parameters

Additionally, while going through params, 
Examine your github usage patterns (caching, and cooldowns) -- especially updater, maximize up-to-dateness with as few requests as possible
All settled there? ensure there isn't a way the app can ddos github AND at the same time there are no unintended Blind spots

- Do the TODOs scattered in the code

- With splash screen here, UpdateUI is useless, getting rid of it is too much work though, just too much...
It is too integerated, previewer class has some funky behavior tied to it, circumvented by it
It's a mess but it works perfectly, so, only fix it once you have an abundance of time...!

In fact, manually calling UpdateUI is NECESSERY, thank GOD you're not using bindings
UpdateUI is VERY NEEDED for Previewer class, it is already implemented everywhere and freezes vessel updates as necessery
You would've had to manually done this anyway

And the smooth transitions are worth it.

- A cool "Gradual logger" -- log texts gradually but very quickly! It helps make it less overwhelming when dumping huge logs
Besides that you're gonna need something to unify the logging
A public variable that gets all text dumped to perhaps, and gradually writes out its contents to sidebarlog whenever it is changed, async
This way direct interaction with non-UI threads will be zero
Long running tasks dump their text, UI thread gradually writes it out on its own.
only concern is performance with large logs
This idea can be a public static method and it won't ever ever block Ui thread
A variable is getting constantly updated with new logs, a worker in main UI thread's only job is to write out its content as it comes along

^ yeah lets dedicate more code clutter to visual things

- Set random preview arts on startup, featuring locations from Vanilla RTX's history (Autumn's End, Pale Horizons, Bevy of Bugs, etc...)
Or simple pixel arts you'd like to make in the same style
Have 5-10 made

- Tuner could, in theory, use the MANIFEST.JSON's metadata (i.e. TOOLS USED param) to MARK packs
e.g. you can preserve their tuning histories there, embed it into the manifest, like for ambient lighting toggle

- Account for different font scalings, windows accessibility settings, etc...
gonna need lots of painstakingly redoing xamls but if one day you have an abundance of time sure why not
*/

public static class TunerVariables
{
    public static string? appVersion = null;

    public static string VanillaRTXLocation = string.Empty;
    public static string VanillaRTXNormalsLocation = string.Empty;
    public static string VanillaRTXOpusLocation = string.Empty;
    public static string CustomPackLocation = string.Empty;

    public static string VanillaRTXVersion = string.Empty;
    public static string VanillaRTXNormalsVersion = string.Empty;
    public static string VanillaRTXOpusVersion = string.Empty;
    public static string CustomPackDisplayName = string.Empty;
    // We already know names of Vanilla RTX packs so we get version instead, for custom pack, name's enough.
    // We invalidate the retrieved name whenever we want to disable processing of the custom pack, so it has multiple purposes

    // Tied to checkboxes
    public static bool IsVanillaRTXEnabled = false;
    public static bool IsNormalsEnabled = false;
    public static bool IsOpusEnabled = false;

    public static string HaveDeployableCache = "";

    // These variables are saved and loaded, they persist
    public static class Persistent
    {
        public static string AppThemeMode = "Dark";
    }

    // Defaults are backed up to be used as a compass by other classes
    public static class Defaults
    {

    }

    // Set Window size default for all windows
    public const int WindowSizeX = 1105;
    public const int WindowSizeY = 555;
    public const int WindowMinSizeX = 970;
    public const int WindowMinSizeY = 555;

    // Saves persistent variables
    public static void SaveSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);

        foreach (var field in fields)
        {
            var value = field.GetValue(null);
            localSettings.Values[field.Name] = value;
        }
    }

    // Loads persitent variables
    public static void LoadSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var field in fields)
        {
            try
            {
                if (localSettings.Values.ContainsKey(field.Name))
                {
                    var savedValue = localSettings.Values[field.Name];
                    var convertedValue = Convert.ChangeType(savedValue, field.FieldType);
                    field.SetValue(null, convertedValue);
                }
            }
            catch
            {
                Trace.WriteLine($"An issue occured loading settings");
            }
        }
    }
}

// ---------------------------------------\                /-------------------------------------------- \\

public sealed partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly WindowStateManager _windowStateManager;

    private readonly ProgressBarManager _progressManager;


    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    private Dictionary<FrameworkElement, string> _originalTexts = new();
    private bool _shiftPressed = false;

    // ---------------------------------------| | | | | | | | | | |-------------------------------------------- \\

    public MainWindow()
    {
        // Properties to set before it is rendered
        SetMainWindowProperties();
        InitializeComponent();

        // Titlebar drag region
        SetTitleBar(TitleBarDragArea);

        // Show splash screen immedietly
        if (SplashOverlay != null)
        {
            SplashOverlay.Visibility = Visibility.Visible;
        }

        _windowStateManager = new WindowStateManager(this, false, msg => Log(msg));
        _progressManager = new ProgressBarManager(ProgressBar);

        Instance = this;

        var defaultSize = new SizeInt32(WindowSizeX, WindowSizeY);
        _windowStateManager.ApplySavedStateOrDefaults();

        // Version, title and initial logs
        var version = Windows.ApplicationModel.Package.Current.Id.Version;
        var versionString = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        appVersion = versionString;
        Log($"App Version: {versionString}");

        // Do upon app closure
        this.Closed += (s, e) =>
        {
            SaveSettings();
            App.CleanupMutex();
        };


        // Things to do after mainwindow is initialized
        this.Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        // Unsubscribe to avoid running this again
        this.Activated -= MainWindow_Activated;

        // Give the window time to render for the first time
        // If one day something goes on the background that needs waiting, increase this, it delays the flash
        await Task.Delay(50);

        InitializeShadows();

        LoadSettings();

        // APPLY THEME if it isn't a button click they won't cycle and apply the loaded setting instead
        CycleThemeButton_Click(null, null);



        // lazy credits and PSA retriever, credits are saved for donate hover event, PSA is shown when ready
        _ = CreditsUpdater.GetCredits(false);
        _ = Task.Run(async () =>
        {
            var psa = await PSAUpdater.GetPSAAsync();
            if (!string.IsNullOrWhiteSpace(psa))
            {
                Log(psa, LogLevel.Informational);
            }
        });


        // Brief delay to ensure everything is fully rendered, then fade out splash screen
        await Task.Delay(750);
        // ================ Do all UI updates you DON'T want to be seen BEFORE here, and what you want seen AFTER ======================= 
        await FadeOutSplashScreen();

        // Show Leave a Review prompt, has a 10 sec cd built in
        _ = ReviewPromptManager.InitializeAsync(MainGrid);

        async Task FadeOutSplashScreen()
        {
            if (SplashOverlay == null) return;

            var fadeOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(fadeOut, SplashOverlay);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            storyboard.Children.Add(fadeOut);

            var tcs = new TaskCompletionSource<bool>();
            storyboard.Completed += (s, e) =>
            {
                SplashOverlay.Visibility = Visibility.Collapsed;
                tcs.SetResult(true);
            };

            storyboard.Begin();
            await tcs.Task;
        }
    }


    #region Main Window properties and essential components used throughout the app
    private void SetMainWindowProperties()
    {
        ExtendsContentIntoTitleBar = true;
        this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;

            var dpi = GetDpiForWindow(hWnd);
            var scaleFactor = dpi / 96.0;
            presenter.PreferredMinimumWidth = (int)(WindowMinSizeX * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(WindowMinSizeY * scaleFactor);
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        appWindow.SetTaskbarIcon(iconPath);
        appWindow.SetTitleBarIcon(iconPath);

        // Watches theme changes and adjusts based on theme
        // use only for stuff that can be altered before mainwindow initlization
        ThemeWatcher(this, theme =>
        {
            var titleBar = appWindow.TitleBar;
            if (titleBar == null) return;

            bool isLight = theme == ElementTheme.Light;

            titleBar.ButtonForegroundColor = isLight ? Colors.Black : Colors.White;
            titleBar.ButtonHoverForegroundColor = isLight ? Colors.Black : Colors.White;
            titleBar.ButtonPressedForegroundColor = isLight ? Colors.Black : Colors.White;
            titleBar.ButtonInactiveForegroundColor = isLight
                ? Color.FromArgb(255, 100, 100, 100)
                : Color.FromArgb(255, 160, 160, 160);

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = isLight
                ? Color.FromArgb(20, 0, 0, 0)
                : Color.FromArgb(40, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = isLight
                ? Color.FromArgb(40, 0, 0, 0)
                : Color.FromArgb(60, 255, 255, 255);
        });


    }
    public static void ThemeWatcher(Window window, Action<ElementTheme> onThemeChanged)
    {
        void HookThemeChangeListener()
        {
            if (window.Content is FrameworkElement root)
            {
                root.ActualThemeChanged += (_, __) =>
                {
                    onThemeChanged(root.ActualTheme);
                };

                // also call once now
                onThemeChanged(root.ActualTheme);
            }
        }

        // Safe way to defer until content is ready
        window.Activated += (_, __) =>
        {
            HookThemeChangeListener();
        };
    }


    private void InitializeShadows()
    {
        TitleBarShadow.Receivers.Add(TitleBarShadowReceiver);
    }


    public enum LogLevel
    {
        Success, Informational, Warning, Error, Network, Lengthy, Debug
    }
    public static void Log(string message, LogLevel? level = null)
    {
        void Prepend()
        {
            var textBox = Instance.SidebarLog;

            string prefix = level switch
            {
                LogLevel.Success => "✅ ",
                LogLevel.Informational => "ℹ️ ",
                LogLevel.Warning => "⚠️ ",
                LogLevel.Error => "❌ ",
                LogLevel.Network => "🛜 ",
                LogLevel.Lengthy => "⏳ ",
                LogLevel.Debug => "🔍 ",
                _ => ""
            };

            string prefixedMessage = $"{prefix}{message}";
            string separator = "";

            if (string.IsNullOrWhiteSpace(textBox.Text))
            {
                textBox.Text = prefixedMessage + "\n";
            }
            else
            {
                var sb = new StringBuilder(prefixedMessage.Length + textBox.Text.Length + separator.Length + 2);
                sb.Append(prefixedMessage)
                  .Append('\n')
                  .Append(separator)
                  .Append('\n')
                  .Append(textBox.Text);
                textBox.Text = sb.ToString();
            }

            // Scroll to top
            textBox.UpdateLayout();
            var sv = GetScrollViewer(textBox);
            sv?.ChangeView(null, 0, null);
        }

        if (Instance.DispatcherQueue.HasThreadAccess)
            Prepend();
        else
            Instance.DispatcherQueue.TryEnqueue(Prepend);
    }
    public static ScrollViewer GetScrollViewer(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv)
                return sv;

            var result = GetScrollViewer(child);
            if (result != null)
                return result;
        }
        return null;
    }



    public static void OpenUrl(string url)
    {
#if DEBUG
        Log("OpenUrl is disabled in debug builds.", LogLevel.Informational);
        return;
#else
    try
    {
        if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            throw new ArgumentException("Malformed URL.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Log($"Details: {ex.Message}", LogLevel.Informational);
        Log("Failed to open URL. Make sure you have a browser installed and associated with web links.", LogLevel.Warning); 
    }
#endif
    }


    #endregion -------------------------------

    private void ChatButton_Click(object sender, RoutedEventArgs e)
    {
        Log("Here is the invitation!\nDiscord.gg/A4wv4wwYud", LogLevel.Informational);
        OpenUrl("https://discord.gg/A4wv4wwYud");
    }


    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        Log("Find helpful resources in the README file, launching in your default browser shortly.", LogLevel.Informational);
        OpenUrl("https://github.com/Cubeir/Vanilla-RTX-App/blob/main/README.md");
    }
    private void HelpButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        HelpButton.Content = "\uF167";
        if (RuntimeFlags.Set("Wrote_Info_Thingy"))
        {
            Log("Open a page with full documentation of the app and a how-to guide.", LogLevel.Informational);
        }
    }
    private void HelpButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HelpButton.Content = "\uE946";
    }


    private void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        DonateButton.Content = "\uEB52";
        var credits = CreditsUpdater.GetCredits(true);
        if (!string.IsNullOrEmpty(credits) && RuntimeFlags.Set("Wrote_Supporter_Shoutout"))
        {
            Log(credits);
        }

        OpenUrl("https://ko-fi.com/cubeir");
    }
    private void DonateButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB52";
        var credits = CreditsUpdater.GetCredits(true);
        if (!string.IsNullOrEmpty(credits) && RuntimeFlags.Set("Wrote_Supporter_Shoutout"))
        {
            Log(credits);
        }
    }
    private void DonateButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB51";
        var credits = CreditsUpdater.GetCredits(true);
        if (!string.IsNullOrEmpty(credits) && RuntimeFlags.Set("Wrote_Supporter_Shoutout"))
        {
            Log(credits);
        }
    }


    public void CycleThemeButton_Click(object? sender, RoutedEventArgs? e)
    {
        bool invokedByClick = sender is Button;
        string mode = TunerVariables.Persistent.AppThemeMode;

        if (invokedByClick)
        {
            mode = mode switch
            {
                "System" => "Light",
                "Light" => "Dark",
                _ => "System"
            };
            TunerVariables.Persistent.AppThemeMode = mode;
        }

        var root = MainWindow.Instance.Content as FrameworkElement;
        root.RequestedTheme = mode switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        Button btn = (sender as Button) ?? CycleThemeButton;

        // Visual Feedback
        if (mode == "System")
        {
            btn.Content = new TextBlock
            {
                Text = "A",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 15
            };
        }
        else
        {
            btn.Content = mode switch
            {
                "Light" => "\uE706",
                "Dark" => "\uEC46",
                _ => "A",
            };
        }

        ToolTipService.SetToolTip(btn, "Theme: " + mode);
    }


}
