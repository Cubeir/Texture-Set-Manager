using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;
using Texture_Set_Manager.Core;
using Texture_Set_Manager.Modules;
using Windows.Graphics;
using Windows.UI;
using static Texture_Set_Manager.EnvironmentVariables;
using static Texture_Set_Manager.EnvironmentVariables.Persistent;
using System.Linq.Expressions;

namespace Texture_Set_Manager;

/*
 * let the rainbow gear be the icon and have it constantly spin VERY slowly ramping up with time
 * e.g. if app is left on for 30 minutes, it gets VERY FAST, its a good easter egg because this app is usually opened and closed quickly
 * 
 * All remainin implementaion detes
 * 
 * Finish the layout, hook everything up right
 * Got folders, got files, they remain stored separately, cleared by the clear button, not persistent
 * 
 * Actually they get cleared automatically upon generation completion
 * its just that, make sure the design is able and safe to MEND existing packs!
 * 
 * Select files/folders are able to POOL UP files indefinitely and independantly, i.e. able to recieve arrays yes, can store arrays-of-arrays
 * or just append the god damn arrays, lmao, dont convolute it!
 * The whole pool gets passed down for processing
 *
 * 
 * process subfolders becomes persistent // update updateui
 * 
 * the actual texture set maker, use the current code in ToolKit
 * 
 * It generates with latest format version, the texture set, and the desginated PBR maps in the same directory as the color texture!!!
 * No more extra folder creation!
 * 
 * If the color texture already ends with _mer, _mers, _heightmap or _normal (but not _normal_normal, have that smart thing copied from vrtx app's processors)
 * Use that to AVOID generating a texture set for it! call it "Smart Filters" place it next to  process subfolders, make space in the same row
 * enabled by default same as process subfolders
 * 
 * ACTUALLY, move clear selection to be a smaller button next to GENERATE button, 3-way fake split, new!
 * This opens space for ANOTHER toggle switch: Create Backup, it backs up the SOURCE folder into 
 * 
 * Add another toggle switch (that makes 4), to "Convert to TGA" on by default, converts all images to TGA, both the original color texture, and the PBR sets
*/

public static class EnvironmentVariables
{
    public static string? appVersion = null;

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

        public static string AppThemeMode = Defaults.AppThemeMode;
    }

    // Defaults are backed up to be used as a compass
    public static class Defaults
    {
        public const bool enableSSS = false;
        public const string SecondaryPBRMapType = "none";

        public const bool ProcessSubfolders = true;
        public const bool SmartFilters = true;
        public const bool ConvertToTarga = true;
        public const bool CreateBackup = true;

        public const string AppThemeMode = "Dark";
    }

    // Set Window size default for all windows
    public const int WindowSizeX = 640;
    public const int WindowSizeY = 384;
    public const int WindowMinSizeX = 640;
    public const int WindowMinSizeY = 384;

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

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);


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

        Instance = this;

        var defaultSize = new SizeInt32(WindowSizeX, WindowSizeY);
        _windowStateManager.ApplySavedStateOrDefaults();

        // Version, title and initial logs
        var version = Windows.ApplicationModel.Package.Current.Id.Version;
        var versionString = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        appVersion = versionString;
        Log($"Version: {versionString}");

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


        LoadSettings();

        // APPLY THEME if it isn't a button click they won't cycle and apply the loaded setting instead
        CycleThemeButton_Click(null, null);

        UpdateUI(0.001);

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
        await Task.Delay(640);
        // ================ Do all UI updates you DON'T want to be seen BEFORE here, and what you want seen AFTER ======================= 
        await FadeOutSplashScreen();

        // Show Leave a Review prompt, has a 10 sec cd built in
        _ = ReviewPromptManager.InitializeAsync(MainGrid);

        await Task.Delay(50);
        StartLogoSpinner();
        await Task.Delay(50);
        if (iconImageBox?.RenderTransform is RotateTransform rotateTransform)
        {
            rotateTransform.Angle = rotationAngle;
        }

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


            // Color of that little border next to the button 🍝
            if (enableSSS)
            {
                LeftEdgeOfSSSButton.BorderBrush = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColorLight3"]);
            }
            else
            {
                var themeKey = theme == ElementTheme.Light ? "Light" : "Dark";
                var themeDictionaries = Application.Current.Resources.ThemeDictionaries;
                if (themeDictionaries.TryGetValue(themeKey, out var themeDict) && themeDict is ResourceDictionary dict)
                {
                    if (dict.TryGetValue("FakeSplitButtonBrightBorderColor", out var colorObj) && colorObj is Color color)
                    {
                        LeftEdgeOfSSSButton.BorderBrush = new SolidColorBrush(color);
                    }
                }
            }
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



    private double rotationAngle = 0.0;
    private DispatcherTimer rotationTimer;
    private DispatcherTimer speedIncrementTimer;
    private double currentSpeedDegreesPerSecond = 0.0;
    private const int AccelerationIntervalMs = 500; // How frequently acceleration happens
    private const double SpeedIncrementDegreesPerMinute = 1.0; // How much acceleration (in extra degrees per min)
    private const int AnimationFrameIntervalMs = 7; // (1000/X ≈ FPS)
    private void StartLogoSpinner()
    {
        var random = new Random();
        double directionMultiplier = random.Next(2) == 0 ? 1.0 : -1.0;

        // Create a RotateTransform on the image with center rotation
        var rotateTransform = new RotateTransform();

        // Set the center point to the center of the image (10,10 for 20x20 image)
        rotateTransform.CenterX = 10;  // Half of image width
        rotateTransform.CenterY = 10;  // Half of image height

        iconImageBox.RenderTransform = rotateTransform;

        // Timer for updating rotation angle (30+ FPS)
        rotationTimer = new DispatcherTimer();
        rotationTimer.Interval = TimeSpan.FromMilliseconds(AnimationFrameIntervalMs);
        rotationTimer.Tick += (s, e) =>
        {
            // Update rotation angle based on current speed
            rotationAngle += currentSpeedDegreesPerSecond * (AnimationFrameIntervalMs / 1000.0);

            // Apply transform to image
            rotateTransform.Angle = rotationAngle;
        };
        rotationTimer.Start();

        // Timer for incrementing speed every minute
        speedIncrementTimer = new DispatcherTimer();
        speedIncrementTimer.Interval = TimeSpan.FromMilliseconds(AccelerationIntervalMs);
        speedIncrementTimer.Tick += (s, e) =>
        {
            currentSpeedDegreesPerSecond += SpeedIncrementDegreesPerMinute * directionMultiplier;
        };
        speedIncrementTimer.Start();
    }



    public async void UpdateUI(double animationDurationSeconds = 0.15)
    {
        // Match bool-based UI elements to their current bools
        ProcessSubfoldersToggle.IsOn = Persistent.ProcessSubfolders;
        SmartFiltersToggle.IsOn = Persistent.SmartFilters;
        ConvertToTGAToggle.IsOn = Persistent.ConvertToTarga;
        CreateBackupToggle.IsOn = Persistent.CreateBackup;

        // Dropdwon and SSS
        IncludeSubsurfaceScatteringToggle.IsChecked = Persistent.enableSSS;
        string displayText = Persistent.SecondaryPBRMapType switch
        {
            "none" => "None",
            "normalmap" => "Normal Map",
            "heightmap" => "Heightmap",
            _ => "None"
        };
        SecondaryPBRMapDropDown.Content = $"Secondary PBR texture: {displayText}";
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




    private void ChatButton_Click(object sender, RoutedEventArgs e)
    {
        Log("Here is the invitation!\nDiscord.gg/A4wv4wwYud", LogLevel.Informational);
        OpenUrl("https://discord.gg/A4wv4wwYud");
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
        string mode = EnvironmentVariables.Persistent.AppThemeMode;

        if (invokedByClick)
        {
            mode = mode switch
            {
                "System" => "Light",
                "Light" => "Dark",
                _ => "System"
            };
            EnvironmentVariables.Persistent.AppThemeMode = mode;
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

                Log("Selected: " + selectedFolder, LogLevel.Success);
            }
            catch(Exception ex)
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
                bool hasFolder = false;
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
                foreach (string filetype in supportedFileExtensions)
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
                    string fileOrFiles = filePaths.Count > 1 ? "files" : "file";
                    Log($"Selected {files.Count} {fileOrFiles}");
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

                bool isValidDrop = false;

                foreach (var item in items)
                {
                    if (item is StorageFile file)
                    {
                        string fileExtension = Path.GetExtension(file.Name).ToLowerInvariant();
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
                            string fileExtension = Path.GetExtension(file.Name).ToLowerInvariant();

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
                        string fileOrFiles = filePaths.Count > 1 ? "files" : "file"; 
                        Log($"Selected {filePaths.Count} valid {fileOrFiles}");
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

        LeftEdgeOfSSSButton.BorderBrush = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColorLight3"]);
    }
    private void IncludeSubsurfaceScatteringToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        enableSSS = false;
        Log("Disabled Subsurface Scattering", LogLevel.Informational);

        // Color of that little border next to the button
        var theme = LeftEdgeOfSSSButton.ActualTheme;
        var themeKey = theme == ElementTheme.Light ? "Light" : "Dark";
        var themeDictionaries = Application.Current.Resources.ThemeDictionaries;
        if (themeDictionaries.TryGetValue(themeKey, out var themeDict) && themeDict is ResourceDictionary dict)
        {
            if (dict.TryGetValue("FakeSplitButtonBrightBorderColor", out var colorObj) && colorObj is Color color)
            {
                LeftEdgeOfSSSButton.BorderBrush = new SolidColorBrush(color);
            }
        }
    }


    private void SecondaryPBRMapOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item)
        {
            string selectedValue = item.Text.ToLowerInvariant(); // normalize input
            var mapType = selectedValue switch
            {
                "none" => "none",
                "normal map" => "normalmap",
                "heightmap" => "heightmap",
                _ => "none"
            };
            EnvironmentVariables.Persistent.SecondaryPBRMapType = mapType;
            Log($"Selected secondary PBR map type: {mapType}", LogLevel.Informational);
            EnvironmentVariables.SaveSettings();

            // For consistency should have manually updated the text here, but this is faster
            // Generally updateUI should only be used when variables change in the background WITHOUT the control itself being touched
            // Because it's whole job is to refresh ALL CONTROLS at once based on their persistent memory variables
            UpdateUI();
        }
    }


    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {

    }


    private void SidebarLogCopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(SidebarLog.Text))
            {
                var sb = new StringBuilder();
                // Original sidebar log (important status messages)
                sb.AppendLine("===== Sidebar Log (UI-shown Messages)");
                sb.AppendLine(SidebarLog.Text);
                sb.AppendLine();
                // Tuner variables
                sb.AppendLine("===== Tuner Variables");
                var fields = typeof(EnvironmentVariables).GetFields(BindingFlags.Public | BindingFlags.Static);
                foreach (var field in fields)
                {
                    var value = field.GetValue(null);
                    sb.AppendLine($"{field.Name}: {value ?? "null"}");
                }
                sb.AppendLine();
                // Persistent variables
                sb.AppendLine("===== Persistent Variables");
                var persistentFields = typeof(EnvironmentVariables.Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
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
                Log("Copied debug logs to clipboard.", LogLevel.Success);
            }
        }
        catch (Exception ex)
        {
            Log($"Error during lamp interaction debug copy: {ex}", LogLevel.Error);
        }
        void CollectUIControlsState(StringBuilder sb)
        {
            var fields = this.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            foreach (var field in fields)
            {
                var value = field.GetValue(this);
                if (value == null) continue;

                var type = value.GetType();
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
    }
}
