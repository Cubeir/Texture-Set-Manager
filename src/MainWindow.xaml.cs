using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Texture_Set_Manager.Core;
using Texture_Set_Manager.Modules;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUIEx;
using static Texture_Set_Manager.Core.WindowControlsManager;
using static Texture_Set_Manager.EnvironmentVariables;
using static Texture_Set_Manager.EnvironmentVariables.Persistent;

namespace Texture_Set_Manager;

public static class EnvironmentVariables
{
    private static readonly Windows.ApplicationModel.PackageVersion _version = App.GetPackageVersion();
    public static readonly string appVersion = $"{_version.Major}.{_version.Minor}.{_version.Build}.{_version.Revision}";
    public static readonly string appVersionMajorMinor = $"{_version.Major}.{_version.Minor}";

    public static string[]? selectedFiles = null;
    public static string? selectedFolder = null;

    public static readonly string[] supportedFileExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

    // These variables are saved and loaded, they persist
    public static class Persistent
    {
        public static bool enableSSS = Defaults.enableSSS;
        public static string SecondaryPBRMapType = Defaults.SecondaryPBRMapType;

        public static bool ProcessSubfolders = Defaults.ProcessSubfolders;
        public static bool SmartFilters = Defaults.SmartFilters;
        public static bool ConvertToTarga = Defaults.ConvertToTarga;
        public static bool CreateBackup = Defaults.CreateBackup;
        public static bool CreateNewFolders = Defaults.CreateNewFolders;

        public static string AppThemeMode = Defaults.AppThemeMode;
    }

    // Defaults are backed up to be used as a compass
    public static class Defaults
    {
        public const bool enableSSS = false;
        public const string SecondaryPBRMapType = "none";

        public const bool ProcessSubfolders = false;
        public const bool SmartFilters = true;
        public const bool ConvertToTarga = false;
        public const bool CreateBackup = true;
        public const bool CreateNewFolders = false;

        public const string AppThemeMode = "Dark";
    }

    // Set Window size default for all windows
    public const int WindowSizeX = 750;
    public const int WindowSizeY = 500;
    public const int WindowMinSizeX = 640;
    public const int WindowMinSizeY = 400;

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

    // Everything that keeps running after the window is gone has to be told to stop, and
    // every handler hung off the content tree has to be unhooked – leaving a theme listener
    // (or a DispatcherTimer) alive past Closed is exactly what used to spit a crash minidump
    // into the temp folder on every single exit.
    private bool _isClosing = false;
    private FrameworkElement? _rootElement;
    private CancellationTokenSource? _analysisCts;

    // Drives the thin indeterminate bar at the top of the log while a long operation runs.
    // Reference-counted, so overlapping callers can't switch each other's indicator off.
    private readonly ProgressBarManager _progressManager;


    // ---------------------------------------| | | | | | | | | | |-------------------------------------------- \\

    public MainWindow()
    {
        // Properties to set before it is rendered
        SetMainWindowProperties();
        InitializeComponent();

        InitializeLogTypewriter();

        // Titlebar drag region
        SetTitleBar(TitleBarDragArea);

        // Show splash screen immedietly
        if (SplashOverlay != null)
        {
            SplashOverlay.Visibility = Visibility.Visible;
        }

        Instance = this;

        _progressManager = new ProgressBarManager(SidelogProgressBar);

        Log($"Version: {appVersion}");

        // Do upon app closure
        this.Closed += MainWindow_Closed;

        // Fake titlebar buttons aren't real caption buttons, so nothing dims them automatically –
        // mirror the system's inactive-titlebar look by hand.
        this.Activated += MainWindow_ActivationChanged;

        // Things to do after mainwindow is initialized
        if (Content is FrameworkElement root)
        {
            _rootElement = root;
            root.Loaded += MainWindow_Loaded;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        // Set first: every guarded callback checks this, so anything already queued becomes a no-op
        // the moment closure begins rather than reaching into a half-torn-down window.
        _isClosing = true;

        // Each step is isolated. A throw partway through used to abandon the rest of the teardown
        // (leaving timers running and the mutex held) which is how a clean exit turned into a
        // minidump in the temp folder.
        Safely(SaveSettings);

        // Stop every timer BEFORE the content tree goes away – a DispatcherTimer that ticks into
        // a dead window doesn't raise a catchable exception, it takes the process with it.
        Safely(() => { _typewriterTimer?.Stop(); _typewriterTimer = null; });
        Safely(() => { rotationTimer?.Stop(); rotationTimer = null; });
        Safely(() => { speedIncrementTimer?.Stop(); speedIncrementTimer = null; });
        Safely(() => { _analysisCts?.Cancel(); _analysisCts?.Dispose(); _analysisCts = null; });
        Safely(() => _progressManager?.ForceHide());

        // Unhook everything hanging off the content tree. The theme listener in particular is the
        // one that used to outlive the window and keep firing at it.
        Safely(() =>
        {
            if (_rootElement != null)
            {
                _rootElement.ActualThemeChanged -= Root_ActualThemeChanged;
                _rootElement = null;
            }
        });
        Safely(() => IncludeSubsurfaceScatteringToggle.IsEnabledChanged -= BevelOwner_IsEnabledChanged);
        Safely(() => SecondaryPBRMapDropDown.IsEnabledChanged -= BevelOwner_IsEnabledChanged);

        Safely(() => this.Activated -= MainWindow_ActivationChanged);
        Safely(() => this.Closed -= MainWindow_Closed);

        Safely(App.CleanupMutex);

        static void Safely(Action step)
        {
            try { step(); }
            catch (Exception ex) { Trace.WriteLine($"[MainWindow] Teardown step failed: {ex.Message}"); }
        }
    }

    private void MainWindow_ActivationChanged(object sender, WindowActivatedEventArgs e)
    {
        if (_isClosing) return;

        var opacity = e.WindowActivationState != WindowActivationState.Deactivated ? 1.0 : 0.5;

        ChatButton.Opacity = opacity;
        DonateButton.Opacity = opacity;
        CycleThemeButton.Opacity = opacity;
        TextureSetToolsButton.Opacity = opacity;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = Content as FrameworkElement;
            if (root != null) root.Loaded -= MainWindow_Loaded;

            // Load variables back in from the previous session
            LoadSettings();

            // APPLY THEME [passing nulls means it isn't a button click, so instead of cycling it applies the loaded setting]
            CycleThemeButton_Click(null, null);

            // Give the window time to render for the first time
            await Task.Delay(50);

            // Apply theme-driven colors once, then keep them in sync. Both subscriptions are
            // torn down in MainWindow_Closed – see the note on _isClosing.
            if (root != null)
            {
                ThemeService.ApplyTitleBarColors(this.AppWindow, root.ActualTheme);
                ApplySSSBevelColors(root.ActualTheme);

                IncludeSubsurfaceScatteringToggle.IsEnabledChanged += BevelOwner_IsEnabledChanged;
                SecondaryPBRMapDropDown.IsEnabledChanged += BevelOwner_IsEnabledChanged;
                root.ActualThemeChanged += Root_ActualThemeChanged;
            }

            // Might summon a ContentDialog about last session's crash
            await CheckForCrashLog();

            // Update the UI
            UpdateUI();

            ToolTipService.SetToolTip(TitleBarText, $"Version: {appVersion}");

            // Brief delay to ensure everything is fully rendered, then fade out splash screen
            await Task.Delay(500);
            // ================ Do all UI updates you DON'T want to be seen BEFORE here, and what you want seen AFTER =======================
            await FadeOutSplashScreen();

            // Show Leave a Review prompt, has a cooldown built in
            _ = ReviewPromptManager.InitializeAsync(MainGrid);

            await Task.Delay(50);
            StartLogoSpinner();

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
                    tcs.TrySetResult(true);
                };

                storyboard.Begin();
                await tcs.Task;
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("MainWindow_Loaded", ex.Message, ex.ToString());
        }
    }

    private void Root_ActualThemeChanged(FrameworkElement sender, object args)
    {
        // Setting properties on a window that's already been torn down throws deep in the
        // native layer, so bail the moment closure starts.
        if (_isClosing) return;

        ThemeService.ApplyTitleBarColors(this.AppWindow, sender.ActualTheme);
        ApplySSSBevelColors(sender.ActualTheme);
        ThemeService.Broadcast(sender.ActualTheme);
    }

    private void BevelOwner_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_isClosing || _rootElement == null) return;
        ApplySSSBevelColors(_rootElement.ActualTheme);
    }

    /// <summary>
    /// The two halves of the bevelled seam between the secondary-PBR dropdown and the Subsurface
    /// Scattering button. Each half belongs to the button it touches: only the SSS side takes the
    /// accent when the toggle is checked, because the dropdown has no accented state to represent
    /// and tinting its edge would read as if the dropdown itself were active. Both still dim when
    /// their own button is disabled and both follow the theme.
    /// </summary>
    private void ApplySSSBevelColors(ElementTheme theme)
    {
        LeftEdgeOfSSSButton.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Left,
                accented: enableSSS, isEnabled: IncludeSubsurfaceScatteringToggle.IsEnabled));

        RightEdgeOfSecondaryPBRDropDown.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Right,
                accented: false, isEnabled: SecondaryPBRMapDropDown.IsEnabled));
    }


    #region Main Window properties and essential components used throughout the app
    private void SetMainWindowProperties()
    {
        ExtendsContentIntoTitleBar = true;
        this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        // WinUIEx owns sizing, minimum size and position persistence now. Setting PersistenceId
        // is the whole of the save/restore story: it writes to its own "WinUIEx" LocalSettings
        // container on close and reapplies on launch, including clamping a restored position back
        // onto a monitor that still exists. Its Width/Height are DIPs and it scales them
        // internally, so none of the DPI arithmetic the old manager carried is needed.
        var manager = WindowManager.Get(this);
        manager.PersistenceId = "MainWindow";
        manager.Width = WindowSizeX;
        manager.Height = WindowSizeY;
        manager.MinWidth = WindowMinSizeX;
        manager.MinHeight = WindowMinSizeY;
        manager.IsResizable = true;
        manager.IsMaximizable = true;

        // First launch (or first launch after this migration) has nothing saved yet - WinUIEx
        // only creates its settings container once it has a position to store, so its absence is
        // the signal to centre rather than let Windows place the window wherever it likes.
        var settings = ApplicationData.Current.LocalSettings;
        if (!settings.Containers.ContainsKey("WinUIEx"))
        {
            this.CenterOnScreen();

            // The hand-rolled window state manager this replaced wrote its own four top-level
            // keys. Nothing reads them any more, so sweep them out on the way past rather than
            // leaving dead entries in every existing user's settings forever.
            foreach (var staleKey in new[] { "WindowX", "WindowY", "WindowWidth", "WindowHeight" })
                settings.Values.Remove(staleKey);
        }

        this.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        // Titlebar colors are applied (and kept in sync) from MainWindow_Loaded, where there's
        // a content tree to hang a properly-unsubscribed listener off of.
    }

    private async Task CheckForCrashLog()
    {
        try
        {
            var logPath = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "last_session_crash_log.txt");

            if (!File.Exists(logPath)) return;

            var content = File.ReadAllText(logPath);
            File.Delete(logPath);

            await ReportDialog.ShowAsync(
                Content.XamlRoot,
                ((FrameworkElement)Content).ActualTheme,
                title: "Previous Session Crash Report",
                intro: "Oh no! Looks like a crash occurred during the previous session. You may continue to use the app, "
                     + "but it would be better if you report it to the developer to see it patched up soon!",
                body: content,
                copyButtonText: "Copy Crash Logs",
                closeButtonText: "Continue Using the App (dismisses the report)",
                linksHeader: "Report using one of the following methods:",
                links: new[]
                {
                    new ReportDialog.Link("Create an issue on GitHub", "https://github.com/Cubeir/Texture-Set-Manager/issues"),
                    new ReportDialog.Link("Create a post on the Vanilla RTX Discord Server", "https://discord.gg/A4wv4wwYud"),
                },
                contentMaxHeight: 320);
        }
        catch { /* a failed crash report must never itself become a crash */ }
    }


    private double rotationAngle = 0.0;
    private DispatcherTimer? rotationTimer;
    private DispatcherTimer? speedIncrementTimer;
    private double currentSpeedDegreesPerSecond = 0.0;
    private const int AccelerationIntervalMs = 1000; // How frequently acceleration happens
    private const double SpeedIncrementDegreesPerMinute = 1.0; // How much acceleration (in extra degrees to spin per min)
    private const int AnimationFrameIntervalMs = 7; // (1000/X ≈ FPS)
    private void StartLogoSpinner()
    {
        var random = new Random();
        var directionMultiplier = random.Next(2) == 0 ? 1.0 : -1.0;

        // Rotation happens around the element's own middle via RenderTransformOrigin="0.5,0.5"
        // in XAML, so no hand-computed CenterX/CenterY that has to be kept in step with the
        // icon's pixel size (getting those even half a pixel wrong is what made the logo look
        // like it drifted off-axis as it sped up).
        var rotateTransform = new RotateTransform { Angle = rotationAngle };
        iconImageBox.RenderTransform = rotateTransform;

        // Integrate against the real elapsed time rather than the nominal interval: DispatcherTimer
        // ticks are best-effort, and assuming a perfect 7ms made the spin visibly stutter once it
        // got fast enough for a dropped frame to matter.
        var stopwatch = Stopwatch.StartNew();
        var lastElapsed = stopwatch.Elapsed;

        rotationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AnimationFrameIntervalMs)
        };
        rotationTimer.Tick += (s, e) =>
        {
            if (_isClosing) return;

            var now = stopwatch.Elapsed;
            var deltaSeconds = (now - lastElapsed).TotalSeconds;
            lastElapsed = now;

            rotationAngle = (rotationAngle + currentSpeedDegreesPerSecond * deltaSeconds) % 360.0;
            rotateTransform.Angle = rotationAngle;
        };
        rotationTimer.Start();

        speedIncrementTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AccelerationIntervalMs)
        };
        speedIncrementTimer.Tick += (s, e) =>
        {
            if (_isClosing) return;
            currentSpeedDegreesPerSecond += SpeedIncrementDegreesPerMinute * directionMultiplier;
        };
        speedIncrementTimer.Start();
    }



    public void UpdateUI()
    {
        // Match bool-based UI elements to their current bools
        ProcessSubfoldersToggle.IsOn = ProcessSubfolders;
        SmartFiltersToggle.IsOn = SmartFilters;
        ConvertToTGAToggle.IsOn = ConvertToTarga;
        CreateBackupToggle.IsOn = CreateBackup;
        CreateNewFoldersToggle.IsOn = CreateNewFolders;

        // Dropdown and SSS
        IncludeSubsurfaceScatteringToggle.IsChecked = enableSSS;
        var displayText = SecondaryPBRMapType switch
        {
            "none" => "None",
            "normalmap" => "Normal Map",
            "heightmap" => "Heightmap",
            _ => "None"
        };
        SecondaryPBRMapDropDown.Content = $"Secondary PBR texture: {displayText}";
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




    private void ChatButton_Click(object sender, RoutedEventArgs e)
    {
        Log("Here is the invitation: https://Discord.gg/A4wv4wwYud", LogLevel.Informational);
        OpenUrl("https://discord.gg/A4wv4wwYud");
    }
    private void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        DonateButton.Content = "\uEB52";
        OpenUrl("https://ko-fi.com/cubeir");
    }
    private void DonateButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB52";
    }
    private void DonateButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB51";
    }
    public void CycleThemeButton_Click(object? sender, RoutedEventArgs? e)
    {
        var invokedByClick = sender is Button;
        var mode = Persistent.AppThemeMode;

        if (invokedByClick)
        {
            mode = mode switch
            {
                "System" => "Light",
                "Light" => "Dark",
                _ => "System"
            };
            Persistent.AppThemeMode = mode;
        }

        var root = Instance!.Content as FrameworkElement;

        var targetTheme = mode switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (root!.RequestedTheme != targetTheme)
            root.RequestedTheme = targetTheme;

        var btn = (sender as Button) ?? CycleThemeButton;

        // Visual Feedback
        btn.Content = mode == "System"
            ? new TextBlock
            {
                Text = "A",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 15
            }
            : mode switch
            {
                "Light" => "\uE706",
                "Dark" => "\uEC46",
                _ => "A",
            };

        ToolTipService.SetToolTip(btn, "Theme: " + mode);
    }

    #endregion -------------------------------
    private async void SelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            try
            {
                // disable the button to avoid double-clicking
                button.IsEnabled = false;

                selectedFolder = null;

                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(button.XamlRoot.ContentIslandEnvironment.AppWindowId);

                picker.CommitButtonText = "Pick a folder";
                picker.SuggestedStartLocation = (Microsoft.Windows.Storage.Pickers.PickerLocationId)PickerLocationId.Desktop;
                picker.ViewMode = (Microsoft.Windows.Storage.Pickers.PickerViewMode)PickerViewMode.Thumbnail;

                // Show the picker dialog window
                var folder = await picker.PickSingleFolderAsync();
                selectedFolder = folder.Path;

                Log("Selected folder path: " + selectedFolder, LogLevel.Success);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

    }
    private void SelectFolderButton_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Link;

        // Check if the dragged items contain folders
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                // Get the storage items to check if they're folders
                var items = e.DataView.GetStorageItemsAsync().AsTask().Result;

                // If any item is a folder, allow the drop
                var hasFolder = false;
                foreach (var item in items)
                {
                    if (item is StorageFolder)
                    {
                        hasFolder = true;
                        break;
                    }
                }

                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }
    private async void SelectFolderButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            try
            {
                // Disable the button to avoid double-clicking
                button.IsEnabled = false;

                selectedFolder = null;

                var items = await e.DataView.GetStorageItemsAsync();

                // Check if we have any items and if the first one is a folder
                if (items.Count > 0)
                {
                    var item = items[0];
                    if (item is StorageFolder folder)
                    {
                        selectedFolder = folder.Path;
                        Log("Selected: " + selectedFolder, LogLevel.Success);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }


    private async void SelectFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            try
            {
                // Disable the button to avoid double-clicking
                button.IsEnabled = false;

                selectedFiles = null;

                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(button.XamlRoot.ContentIslandEnvironment.AppWindowId);

                picker.CommitButtonText = "Pick color textures";
                picker.SuggestedStartLocation = (Microsoft.Windows.Storage.Pickers.PickerLocationId)PickerLocationId.Desktop;
                picker.ViewMode = (Microsoft.Windows.Storage.Pickers.PickerViewMode)PickerViewMode.Thumbnail;
                foreach (var filetype in supportedFileExtensions)
                {
                    picker.FileTypeFilter.Add(filetype);
                }


                // Show the picker dialog window
                var files = await picker.PickMultipleFilesAsync();

                if (files.Count > 0)
                {
                    // Convert StorageFile objects to file paths (strings)
                    var filePaths = new List<string>();
                    foreach (var file in files)
                    {
                        filePaths.Add(file.Path);
                    }
                    selectedFiles = filePaths.ToArray();
                    var fileOrFiles = filePaths.Count > 1 ? "files" : "file";
                    Log($"Selected {files.Count} {fileOrFiles}.");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }
    private void SelectFilesButton_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Link;

        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                // Get the storage items to check file extensions
                var items = e.DataView.GetStorageItemsAsync().AsTask().Result;

                var isValidDrop = false;

                foreach (var item in items)
                {
                    if (item is StorageFile file)
                    {
                        var fileExtension = Path.GetExtension(file.Name).ToLowerInvariant();
                        if (supportedFileExtensions.Contains(fileExtension))
                        {
                            isValidDrop = true;
                            break;
                        }
                    }
                }

                // Only allow the drop if we have valid files
                if (isValidDrop)
                {
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }
    private async void SelectFilesButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            try
            {
                // Disable the button to avoid double-clicking
                button.IsEnabled = false;

                selectedFiles = null;

                var items = await e.DataView.GetStorageItemsAsync();

                if (items.Count > 0)
                {
                    var filePaths = new List<string>();

                    foreach (var item in items)
                    {
                        if (item is StorageFile file)
                        {
                            var fileExtension = Path.GetExtension(file.Name).ToLowerInvariant();

                            // Only process files with supported extensions
                            if (supportedFileExtensions.Contains(fileExtension))
                            {
                                filePaths.Add(file.Path);
                            }
                        }
                    }

                    if (filePaths.Count > 0)
                    {
                        selectedFiles = filePaths.ToArray();
                        var fileOrFiles = filePaths.Count > 1 ? "files" : "file";
                        Log($"Selected {filePaths.Count} valid {fileOrFiles}.");
                    }
                    else
                    {
                        // Optionally show a message that no valid files were dropped
                        Log("No valid files were dropped.", LogLevel.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }


    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        selectedFiles = null;
        selectedFolder = null;
        Log("Folder and file selections cleared.", LogLevel.Informational);
    }




    private void IncludeSubsurfaceScatteringToggle_Checked(object sender, RoutedEventArgs e)
    {
        enableSSS = true;
        Log("Enabled Subsurface Scattering", LogLevel.Informational);
        ApplySSSBevelColors(IncludeSubsurfaceScatteringToggle.ActualTheme);
    }
    private void IncludeSubsurfaceScatteringToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        enableSSS = false;
        Log("Disabled Subsurface Scattering", LogLevel.Informational);
        ApplySSSBevelColors(IncludeSubsurfaceScatteringToggle.ActualTheme);
    }


    private void SecondaryPBRMapOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item)
        {
            var selectedValue = item.Text.ToLowerInvariant(); // normalize input
            var mapType = selectedValue switch
            {
                "none" => "none",
                "normal map" => "normalmap",
                "heightmap" => "heightmap",
                _ => "none"
            };
            Persistent.SecondaryPBRMapType = mapType;
            Log($"Selected secondary PBR map type: {mapType}", LogLevel.Informational);
            SaveSettings();

            // For consistency should have manually updated the text here, but this is faster
            // Generally updateUI should only be used when variables change in the background WITHOUT the control itself being touched
            // Because it's whole job is to refresh ALL CONTROLS at once based on their persistent memory variables
            UpdateUI();
        }
    }


    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BeginLongOperation();
            var (success, message) = await Generate.GenerateTextureSetsAsync();

            Log(message, success ? LogLevel.Success : LogLevel.Error);
        }
        catch (Exception ex)
        {
            Log("Error during generation: " + ex.Message, LogLevel.Error);
        }
        finally
        {
            selectedFiles = null;
            selectedFolder = null;
            EndLongOperation();
        }
    }


    /// <summary>
    /// Locks the window and starts the busy indicator for the duration of a long operation.
    /// Every one of these is destructive or file-touching, and letting a second one start while
    /// the first is mid-run is how a folder gets stripped out from under an in-flight generate.
    /// Always pair with <see cref="EndLongOperation"/> in a finally.
    /// </summary>
    private void BeginLongOperation()
    {
        ToggleControls(this, false);
        _progressManager.ShowProgress();
    }

    private void EndLongOperation()
    {
        _progressManager.HideProgress();
        ToggleControls(this, true);
    }


    /// <summary>
    /// Shared folder picker for the texture set tools. Returns null when the user backs out.
    /// </summary>
    private async Task<string?> PickFolderAsync(string commitText)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(Content.XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                CommitButtonText = commitText,
                SuggestedStartLocation = (Microsoft.Windows.Storage.Pickers.PickerLocationId)PickerLocationId.Desktop,
                ViewMode = (Microsoft.Windows.Storage.Pickers.PickerViewMode)PickerViewMode.Thumbnail,
            };

            var picked = await picker.PickSingleFolderAsync();
            return picked?.Path;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            return null;
        }
    }


    /// <summary>
    /// Picks a folder and reports every PBR texture in it that's still a byte-for-byte copy of
    /// its own color texture – i.e. the templates nobody ever got around to painting. Read-only:
    /// this never touches a file.
    /// </summary>
    private async void AnalyzeTextureSetsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Inspect this folder");
        if (folder == null) return;

        try
        {
            BeginLongOperation();
            Log($"Inspecting texture sets in {folder}...", LogLevel.Lengthy);

            _analysisCts?.Cancel();
            _analysisCts?.Dispose();
            _analysisCts = new CancellationTokenSource();

            var report = await TextureSetAnalyzer.AnalyzeAsync(folder, _analysisCts.Token);

            // The uncapped listing goes to trace so the sidebar stays readable even on a
            // pack with thousands of texture sets.
            Trace.WriteLine(TextureSetAnalyzer.BuildTraceReport(report));

            if (report.JsonFilesFound == 0)
            {
                Log("No .texture_set.json files were found in that folder (subfolders included).", LogLevel.Warning);
                return;
            }

            var flagged = report.FlaggedSets.Any();
            var summary = TextureSetAnalyzer.BuildLogReport(report);

            // Log it for the record, then put it in front of the user – a report they're meant
            // to act on is far too easy to scroll past in the sidebar.
            Log(summary, flagged ? LogLevel.Warning : LogLevel.Success);

            // The work is done; reading the report isn't "busy", so stop the indicator before
            // the dialog goes up. Controls stay locked until the dialog is dismissed.
            _progressManager.HideProgress();

            await ReportDialog.ShowAsync(
                Content.XamlRoot,
                ((FrameworkElement)Content).ActualTheme,
                title: "Texture Set Report",
                intro: flagged
                    ? "Some PBR textures are still identical to their color texture. Those are almost always template "
                    + "copies that were never painted – worth going back to, or removing from the pack."
                    : "Nothing looks left over from a template. Here's the full result:",
                body: summary,
                copyButtonText: "Copy Report");
        }
        catch (OperationCanceledException)
        {
            Log("Texture set inspection cancelled.", LogLevel.Informational);
        }
        catch (Exception ex)
        {
            Log($"Error while inspecting texture sets: {ex.Message}", LogLevel.Error);
            Trace.WriteLine(ex);
        }
        finally
        {
            EndLongOperation();
        }
    }


    /// <summary>
    /// Picks a folder and deletes every texture set in it along with the PBR textures those sets
    /// reference, leaving the color textures alone.
    ///
    /// This is destructive and there is no undo, so the scope prompt is deliberately mandatory:
    /// it names the folder, and Cancel is its default button. That prompt is the confirmation –
    /// there is no path from clicking the menu item to deleting a file without answering it.
    /// </summary>
    private async void StripTextureSetsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Strip this folder");
        if (folder == null) return;

        var scope = await ReportDialog.AskScopeAsync(
            Content.XamlRoot,
            ((FrameworkElement)Content).ActualTheme,
            title: "Strip all texture sets?",
            message: $"Every .texture_set.json in:\n\n{folder}\n\n"
                   + "will be deleted, along with the MER/MERS, normal and heightmap textures they reference. "
                   + "Color textures are never touched.\n\n"
                   + "This cannot be undone. How far should it reach?");

        if (scope == ReportDialog.ScopeChoice.Cancelled)
        {
            Log("Texture set stripping cancelled.", LogLevel.Informational);
            return;
        }

        var searchOption = scope == ReportDialog.ScopeChoice.IncludeSubfolders
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        try
        {
            BeginLongOperation();
            Log($"Stripping texture sets from {folder} ({(searchOption == SearchOption.AllDirectories ? "including subfolders" : "this folder only")})...", LogLevel.Lengthy);

            var result = await Task.Run(() => PbrStripper.Strip(folder, searchOption));

            if (result.TextureSetsDeleted == 0 && result.TexturesDeleted == 0)
            {
                Log("Nothing to strip – no texture sets were found in that scope.", LogLevel.Informational);
                return;
            }

            var message = $"Removed {result.TextureSetsDeleted} texture set(s) and {result.TexturesDeleted} PBR texture(s). Color textures were left untouched.";
            if (result.Failed > 0)
                message += $" {result.Failed} file(s) could not be deleted – they may be open in another program.";

            Log(message, result.Failed > 0 ? LogLevel.Warning : LogLevel.Success);
        }
        catch (Exception ex)
        {
            Log($"Error while stripping texture sets: {ex.Message}", LogLevel.Error);
            Trace.WriteLine(ex);
        }
        finally
        {
            EndLongOperation();
        }
    }


    private void LogCopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();

            AppendSystemInfo(sb);

            sb.AppendLine($"===== Sidebar Log (Last {MaxLogChars} Chars)");
            string logSnapshot;
            lock (_logGate) logSnapshot = LogText;
            sb.AppendLine(logSnapshot.Replace(EntrySentinel, Environment.NewLine));
            sb.AppendLine();

            // Environment variables
            sb.AppendLine("===== Environment Variables");
            var fields = typeof(EnvironmentVariables).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (var field in fields)
            {
                var value = field.GetValue(null);

                if (value is System.Collections.IEnumerable enumerable && value is not string)
                {
                    var items = enumerable.Cast<object>().ToList();
                    sb.AppendLine(items.Count == 0 ? $"{field.Name}: (empty)" : $"{field.Name}:");
                    foreach (var item in items)
                        sb.AppendLine($"  {FormatValue(item)}");
                    continue;
                }

                sb.AppendLine($"{field.Name}: {value ?? "null"}");
            }
            sb.AppendLine();

            // Persistent variables
            sb.AppendLine("===== Persistent Variables");
            var persistentFields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (var field in persistentFields)
            {
                var value = field.GetValue(null);
                sb.AppendLine($"{field.Name}: {value ?? "null"}");
            }
            sb.AppendLine();

            // Trace logs
            sb.AppendLine(TraceManager.GetAllTraceLogs());

            // UI Controls State
            sb.AppendLine();
            sb.AppendLine("===== UI Controls State");
            CollectUIControlsState(sb);

            var dataPackage = new DataPackage();
            dataPackage.SetText(sb.ToString());
            Clipboard.SetContent(dataPackage);
            Log("Copied logs to clipboard.", LogLevel.Success);
        }
        catch (Exception ex)
        {
            Log($"Error during debug log copy: {ex}", LogLevel.Error);
        }

        static string FormatValue(object? value)
        {
            if (value is null) return "null";
            if (value is System.Runtime.CompilerServices.ITuple tuple)
            {
                var items = new object?[tuple.Length];
                for (var i = 0; i < tuple.Length; i++)
                    items[i] = tuple[i]?.ToString() ?? "null";
                return string.Join(", ", items);
            }
            return value.ToString() ?? "null";
        }

        void CollectUIControlsState(StringBuilder sb)
        {
            var fields = this.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            foreach (var field in fields)
            {
                var value = field.GetValue(this);
                if (value == null) continue;

                var name = field.Name;

                // Toggle-type controls
                if (value is ToggleButton toggleBtn)
                {
                    sb.AppendLine($"{name} (ToggleButton): {toggleBtn.IsChecked?.ToString() ?? "null"}");
                }
                else if (value is CheckBox checkBox)
                {
                    sb.AppendLine($"{name} (CheckBox): {checkBox.IsChecked?.ToString() ?? "null"}");
                }
                else if (value is ToggleSwitch toggleSwitch)
                {
                    sb.AppendLine($"{name} (ToggleSwitch): {toggleSwitch.IsOn}");
                }
                else if (value is RadioButton radioBtn)
                {
                    sb.AppendLine($"{name} (RadioButton): {radioBtn.IsChecked?.ToString() ?? "null"}");
                }
                // Value controls
                else if (value is Slider slider)
                {
                    sb.AppendLine($"{name} (Slider): {slider.Value}");
                }
                else if (value is NumberBox numberBox)
                {
                    sb.AppendLine($"{name} (NumberBox): {numberBox.Value}");
                }
                else if (value is ComboBox comboBox)
                {
                    sb.AppendLine($"{name} (ComboBox): SelectedIndex={comboBox.SelectedIndex}, SelectedItem={comboBox.SelectedItem?.ToString() ?? "null"}");
                }
                else if (value is TextBox textBox)
                {
                    var text = textBox.Text;
                    if (!string.IsNullOrEmpty(text) && text.Length > 50)
                        text = text.Substring(0, 50) + "...";
                    sb.AppendLine($"{name} (TextBox): \"{text}\"");
                }
                else if (value is RatingControl rating)
                {
                    sb.AppendLine($"{name} (RatingControl): {rating.Value}");
                }
                else if (value is ColorPicker colorPicker)
                {
                    sb.AppendLine($"{name} (ColorPicker): {colorPicker.Color}");
                }
                else if (value is DatePicker datePicker)
                {
                    sb.AppendLine($"{name} (DatePicker): {datePicker.Date}");
                }
                else if (value is TimePicker timePicker)
                {
                    sb.AppendLine($"{name} (TimePicker): {timePicker.Time}");
                }
            }
        }

        static void AppendSystemInfo(StringBuilder sb)
        {
            sb.AppendLine("===== System Info");

            // Process architecture = what's actually executing right now
            sb.AppendLine($"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
            // OS architecture = the machine's native architecture (differs from above if running under emulation)
            sb.AppendLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"Is Emulated (x64-on-ARM64): {RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture}");

            sb.AppendLine($"OS Version: {RuntimeInformation.OSDescription}");
            sb.AppendLine($".NET Runtime: {RuntimeInformation.FrameworkDescription}");

            sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");
            sb.AppendLine($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
            sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");

            try
            {
                var package = Windows.ApplicationModel.Package.Current;
                var v = package.Id.Version;
                sb.AppendLine($"Package Version: {v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
                sb.AppendLine($"Package Architecture: {package.Id.Architecture}");
                sb.AppendLine($"Package Full Name: {package.Id.FullName}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Package Info: unavailable ({ex.Message})");
            }

            try
            {
                sb.AppendLine($"UI Culture: {CultureInfo.CurrentUICulture.Name}");
            }
            catch { /* non-critical */ }

            sb.AppendLine();
        }
    }

    private void ProcessSubfoldersToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle) ProcessSubfolders = toggle.IsOn;
    }

    private void SmartFiltersToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle) SmartFilters = toggle.IsOn;
    }

    private void ConvertToTGAToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle) ConvertToTarga = toggle.IsOn;
    }

    private void CreateBackupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle) CreateBackup = toggle.IsOn;
    }

    private void CreateNewFoldersToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;

        CreateNewFolders = toggle.IsOn;

        if (CreateNewFolders && RuntimeFlags.Set("Explained_Create_New_Folders"))
        {
            Log($"Generated texture sets and templates will be collected in a \"{Generate.SecondaryPBRFolderName(SecondaryPBRMapType)}\" " +
                "subfolder of each processed folder, rather than sitting next to the color textures.", LogLevel.Informational);
        }
    }


    #region =============== UI LOGGER ===============

    public enum LogLevel
    {
        Success, Informational, Warning, Error, Network, Lengthy, Debug, Report
    }

    // The single source of truth, Log() only ever writes here
    internal static string LogText = "";
    private static readonly Lock _logGate = new();

    // Typewriter state, only ever touched on the UI thread, inside TypewriterTick().
    // Logger writes fast; typewriter reveals it to the UI on its own schedule – always the
    // oldest not-yet-shown entry first, left-to-right within it – so chronology holds up
    // AND each message types start-to-finish instead of finish-to-start.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _typewriterTimer;
    private ScrollViewer? _logScrollViewer;
    private int _settledLength = 0;   // trailing chars of LogText already fully shown & final
    private int _activeRevealed = 0;  // chars revealed so far of the current (oldest-pending) entry
    private string? _lastRenderedText;

    private const int MaxLogChars = 4000;

    private const double BaselineCharsPerTick = 2.0; // relaxed pace for small/no backlog
    private const double CatchUpFraction = 0.10;     // reveal % of the backlog each tick
    private static readonly int TickIntervalMs = ((Func<int>)(() => // speed based on corecount, since this really does affect cpu usage! it's the main lever
    {
        try
        {
            if (Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On)
                return 64;

            return Environment.ProcessorCount switch
            {
                >= 24 => 4,
                >= 16 => 8,
                >= 8 => 16,
                >= 5 => 32,
                _ => 64,
            };
        }
        catch { return 16; }
    }))();

    // Structural marker ONLY – never rendered, never typed character-by-character
    private const string EntrySentinel = "\uE000\uE001";

    // Idle/typing cursor – sits at the current write-head
    private const bool ShowTypingCursor = false;
    private const int CursorBlinkMs = 750;
    private const string CursorOnGlyph = " |";
    private const string CursorOffGlyph = "  ";

    /// <summary>
    /// Thread-safe from anywhere: this only appends to a string behind a lock. Nothing here
    /// touches the UI, so there's no dispatcher hop and no ordering surprise – the typewriter
    /// picks the text up on its own schedule.
    /// </summary>
    public static void Log(string message, LogLevel? level = null)
    {
        var prefix = level switch
        {
            LogLevel.Success => "✅ ",
            LogLevel.Informational => "ℹ️ ",
            LogLevel.Warning => "⚠️ ",
            LogLevel.Error => "❌ ",
            LogLevel.Network => "🛜 ",
            LogLevel.Lengthy => "⏳ ",
            LogLevel.Report => "📋 ",
            LogLevel.Debug => "🔍 ",
            null => "",
            _ => "💩 "
        };

        var entry = $"{prefix}{message}";

        lock (_logGate)
        {
            if (!string.IsNullOrEmpty(LogText))
            {
                var firstSentinel = LogText.IndexOf(EntrySentinel, StringComparison.Ordinal);
                var lastEntry = firstSentinel >= 0 ? LogText[..firstSentinel] : LogText;

                if (lastEntry == entry) // identical to previous entry? drop it
                    return;
            }

            LogText = string.IsNullOrEmpty(LogText) ? entry : $"{entry}{EntrySentinel}{LogText}";
        }
    }

    private void InitializeLogTypewriter()
    {
        SidebarLog.Loaded += (_, _) => _logScrollViewer ??= GetScrollViewer(SidebarLog);
        if (SidebarLog.IsLoaded) _logScrollViewer ??= GetScrollViewer(SidebarLog);

        _typewriterTimer = DispatcherQueue.CreateTimer();
        _typewriterTimer.Interval = TimeSpan.FromMilliseconds(TickIntervalMs);
        _typewriterTimer.Tick += (_, _) => TypewriterTick();
        _typewriterTimer.Start();
    }

    private void TypewriterTick()
    {
        if (_isClosing) return;

        string current;
        lock (_logGate)
        {
            current = LogText;

            if (current.Length > MaxLogChars)
            {
                // Cut on a sentinel boundary so we drop whole oldest entries, never mid-message.
                var cut = current.LastIndexOf(EntrySentinel, MaxLogChars - 1, MaxLogChars, StringComparison.Ordinal);
                if (cut > 0)
                {
                    var trimmedAmount = current.Length - cut;
                    current = current[..cut];
                    LogText = current;

                    // Trimmed content came off the tail – exactly where _settledLength measures
                    // from – so shrink it by the same amount. If the cut reached into content that
                    // wasn't fully settled yet (only possible under an extreme backlog), just reset
                    // both – the next tick starts clean against the trimmed text.
                    if (trimmedAmount > _settledLength)
                    {
                        _settledLength = 0;
                        _activeRevealed = 0;
                    }
                    else
                    {
                        _settledLength -= trimmedAmount;
                    }
                }
            }
        }

        var unshownLength = current.Length - _settledLength;

        if (unshownLength > 0)
        {
            // The oldest not-yet-shown entry sits adjacent to the settled region. Its own
            // trailing sentinel (connecting it to whatever follows) isn't a real boundary
            // between two DIFFERENT pending entries, so exclude it before searching.
            var trailingConnector = _settledLength > 0 ? EntrySentinel.Length : 0;
            var searchLength = Math.Max(0, unshownLength - trailingConnector);

            var sepIndex = searchLength > 0
                ? current.LastIndexOf(EntrySentinel, searchLength - 1, searchLength, StringComparison.Ordinal)
                : -1;

            var activeStart = sepIndex >= 0 ? sepIndex + EntrySentinel.Length : 0;
            var activeTextLength = searchLength - activeStart; // entry's OWN text only, sentinel excluded

            var remaining = unshownLength - _activeRevealed; // whole backlog left – drives speed-up
            var charsThisTick = (int)Math.Max(BaselineCharsPerTick, Math.Ceiling(remaining * CatchUpFraction));

            _activeRevealed = Math.Min(activeTextLength, _activeRevealed + charsThisTick);
            _activeRevealed = SnapForward(current, activeStart, _activeRevealed);

            if (_activeRevealed >= activeTextLength)
            {
                // Entry fully typed – fold it (and its sentinel, converted to a real blank
                // line) into settled INSTANTLY. The separator is never itself "typed."
                _settledLength = current.Length - activeStart;
                _activeRevealed = 0;

                SidebarLog.UpdateLayout();
                _logScrollViewer?.ChangeView(null, 0, null, true); // once, per finished entry
            }
            else
            {
                RenderFrame(current, activeStart, _activeRevealed);
                return;
            }
        }

        RenderFrame(current, 0, 0);
    }

    private void RenderFrame(string current, int activeStart, int activeRevealed)
    {
        var revealedPrefix = activeRevealed > 0 ? current.Substring(activeStart, activeRevealed) : "";
        var settledDisplay = _settledLength > 0
            ? current.Substring(current.Length - _settledLength).Replace(EntrySentinel, "\n\n")
            : "";

        string headText, tailText;
        if (revealedPrefix.Length > 0)
        {
            headText = revealedPrefix;
            tailText = settledDisplay.Length > 0 ? "\n\n" + settledDisplay : "";
        }
        else
        {
            var firstBoundary = settledDisplay.IndexOf("\n\n", StringComparison.Ordinal);
            headText = firstBoundary >= 0 ? settledDisplay[..firstBoundary] : settledDisplay;
            tailText = firstBoundary >= 0 ? settledDisplay[firstBoundary..] : "";
        }

        var cursor = ShowTypingCursor
            ? ((Environment.TickCount64 / CursorBlinkMs) % 2 == 0 ? CursorOnGlyph : CursorOffGlyph)
            : "";

        var newText = headText + cursor + tailText;
        if (newText == _lastRenderedText) return; // nothing visually changed, skip the relayout entirely

        _lastRenderedText = newText;
        SidebarLog.Text = newText;
    }

    // Never reveal a cut that splits a surrogate pair or strands an emoji's
    // variation-selector/combining mark – grows past them instead of stopping mid-glyph.
    private static int SnapForward(string s, int rangeStart, int localIndex)
    {
        var i = rangeStart + localIndex;
        if (i <= rangeStart || i >= s.Length) return localIndex;

        if (char.IsHighSurrogate(s[i - 1]) && char.IsLowSurrogate(s[i]))
            i++;

        while (i < s.Length && IsJoiningMark(s[i]))
            i++;

        return i - rangeStart;
    }
    private static bool IsJoiningMark(char c) =>
        c is '\uFE0F' or '\uFE0E' ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    public static ScrollViewer? GetScrollViewer(DependencyObject obj)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv) return sv;
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }
    #endregion
}
