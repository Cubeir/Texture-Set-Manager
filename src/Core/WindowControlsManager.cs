using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Texture_Set_Manager.Core;

/// <summary>
/// Universal control toggle utility for WinUI 3 applications
/// Manages enabling/disabling of controls while preserving original states
/// </summary>
public class WindowControlsManager
{
    private static readonly HashSet<string> _globalExclusions = new()
    {
        "DonateButton", "ChatButton", "CycleThemeButton", "SidebarLog",
    };

    // Reference-counted lock state, keyed by control INSTANCE (identity-based dictionary –
    // Control doesn't override Equals/GetHashCode, so this is safe). A control's IsEnabled is
    // restored only once its lock count returns to zero, so overlapping disable sessions can
    // no longer stomp on each other.
    private static readonly Dictionary<Control, int> _lockCounts = new();
    private static readonly Dictionary<Control, bool> _preLockState = new();

    /// <summary>
    /// Blanket mode: disable every supported control in the window EXCEPT the named ones
    /// (plus global exclusions). Use for "lock the whole window down" operations.
    /// </summary>
    public static void ToggleControls(Window window, bool enable, bool overrideGlobalExclusions = false, params string[] excludeNames)
    {
        if (window == null) return;
        var content = TryGetContent(window);
        if (content == null) return;

        var exclusions = overrideGlobalExclusions ? new HashSet<string>() : new HashSet<string>(_globalExclusions);
        if (excludeNames != null)
            foreach (var name in excludeNames)
                if (!string.IsNullOrEmpty(name)) exclusions.Add(name);

        Apply(enable, GetAllSupportedControls(content, exclusions));
    }

    /// <summary>
    /// Targeted mode: disable/enable ONLY the named controls (global exclusions still apply
    /// as a safety net).
    /// </summary>
    public static void ToggleSpecificControls(Window window, bool enable, params string[] controlNames)
    {
        if (window == null || controlNames == null || controlNames.Length == 0) return;
        var content = TryGetContent(window);
        if (content == null) return;

        var wanted = new HashSet<string>(controlNames);
        wanted.ExceptWith(_globalExclusions);

        var controls = GetAllSupportedControls(content, null).Where(c => wanted.Contains(c.Name));
        Apply(enable, controls);
    }

    // Window.Content throws COMException instead of returning null once the native window has
    // been torn down (e.g. a callback fires through a captured "this" after close). Swallow
    // that specific case – there's nothing left to toggle on a dead window – but let anything
    // else bubble up.
    private static UIElement? TryGetContent(Window window)
    {
        try
        {
            return window.Content;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Emergency reset – force-clears every lock on every control in the window regardless of
    /// count. Not part of normal flow; use only if a window can be torn down without its
    /// Closed handler running (crash, forced termination, etc).
    /// </summary>
    public static void ClearStates(Window window)
    {
        var content = window == null ? null : TryGetContent(window);
        if (content == null) return;

        foreach (var control in GetAllSupportedControls(content, null).ToList())
        {
            if (_lockCounts.Remove(control) && _preLockState.Remove(control, out var original))
                control.IsEnabled = original;
        }
    }

    private static void Apply(bool enable, IEnumerable<Control> controls)
    {
        var list = controls as IList<Control> ?? controls.ToList(); // materialize before mutating
        if (enable) Release(list); else Acquire(list);
    }

    private static void Acquire(IEnumerable<Control> controls)
    {
        foreach (var control in controls)
        {
            _lockCounts.TryGetValue(control, out var count);
            if (count == 0)
            {
                _preLockState[control] = control.IsEnabled;
                control.IsEnabled = false;
            }
            _lockCounts[control] = count + 1;
        }
    }

    private static void Release(IEnumerable<Control> controls)
    {
        foreach (var control in controls)
        {
            if (!_lockCounts.TryGetValue(control, out var count) || count <= 0)
                continue; // never locked / already released – ignore stray release, don't go negative

            count--;
            if (count == 0)
            {
                _lockCounts.Remove(control);
                if (_preLockState.Remove(control, out var original))
                    control.IsEnabled = original;
            }
            else
            {
                _lockCounts[control] = count;
            }
        }
    }

    public static void AddGlobalExclusion(string controlName) { if (!string.IsNullOrEmpty(controlName)) _globalExclusions.Add(controlName); }
    public static void RemoveGlobalExclusion(string controlName) { if (!string.IsNullOrEmpty(controlName)) _globalExclusions.Remove(controlName); }
    public static void ClearGlobalExclusions() => _globalExclusions.Clear();

    private static IEnumerable<Control> GetAllSupportedControls(DependencyObject parent, HashSet<string>? exclusions)
    {
        if (parent == null) yield break;

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);

            if (IsSupportedControl(child))
            {
                var control = (Control)child;
                if (exclusions == null || !exclusions.Contains(control.Name))
                    yield return control;
            }

            foreach (var grandChild in GetAllSupportedControls(child, exclusions))
                yield return grandChild;
        }
    }

    private static bool IsSupportedControl(DependencyObject control) =>
        control is Button or CheckBox or RadioButton or Slider or TextBox or PasswordBox or ComboBox or
        ListBox or ListView or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton or RatingControl or
        NumberBox or DatePicker or TimePicker or ToggleSwitch or MenuFlyoutItem or AppBarButton or
        AppBarToggleButton or AutoSuggestBox;

    /// <summary>
    /// Activates a freshly-launched window, then re-asserts activation ~500ms later
    /// (Windows' default double-click interval) as a guard: without this, the second
    /// click of the double-click that launched this window could land back on the
    /// caller before the new window's HWND fully took over that screen region,
    /// unintentionally bringing the caller back to the foreground.
    /// </summary>
    public static void Activate(Window window, int guardDelayMs = 500)
    {
        window.Activate();

        _ = window.DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(guardDelayMs);
            try { window.Activate(); } catch { /* window may already be closed */ }
        });
    }
}

/// <summary>
/// Extension methods for convenient usage
/// </summary>
public static class WindowControlsManagerExtensions
{
    /// <summary>
    /// Disables all controls in this window
    /// </summary>
    public static void DisableAllControls(this Window window, bool overrideGlobalExclusions = false, params string[] excludeNames)
    {
        WindowControlsManager.ToggleControls(window, false, overrideGlobalExclusions, excludeNames);
    }

    /// <summary>
    /// Enables all controls in this window
    /// </summary>
    public static void EnableAllControls(this Window window, bool overrideGlobalExclusions = false, params string[] excludeNames)
    {
        WindowControlsManager.ToggleControls(window, true, overrideGlobalExclusions, excludeNames);
    }

    /// <summary>
    /// Restores all controls in this window to their original states
    /// </summary>
    public static void RestoreAllControls(this Window window)
    {
        WindowControlsManager.ToggleControls(window, true);
    }

    /// <summary>
    /// Clears stored control states for this window
    /// </summary>
    public static void ClearControlStates(this Window window)
    {
        WindowControlsManager.ClearStates(window);
    }
}

/// <summary>
/// Utility class for driving a WinUI ProgressBar safely from background threads. All public
/// methods are safe to call from any thread; UI mutation is always marshalled onto the
/// ProgressBar's DispatcherQueue.
/// </summary>
public class ProgressBarManager
{
    private readonly ProgressBar _progressBar;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _lock = new();
    private int _activeOperations;

    public ProgressBarManager(ProgressBar progressBar)
    {
        _progressBar = progressBar ?? throw new ArgumentNullException(nameof(progressBar));
        _dispatcherQueue = progressBar.DispatcherQueue;
        ResetVisual();
    }

    /// <summary>
    /// Shows the progress bar in indeterminate mode. Multiple calls are safe – the bar stays
    /// visible until all operations complete.
    /// </summary>
    public void ShowProgress()
    {
        lock (_lock)
        {
            _activeOperations++;
            UpdateIndeterminateState();
        }
    }

    /// <summary>
    /// Hides the progress bar. Only hides once every ShowProgress() call has a matching
    /// HideProgress() call.
    /// </summary>
    public void HideProgress()
    {
        lock (_lock)
        {
            if (_activeOperations > 0)
            {
                _activeOperations--;
                UpdateIndeterminateState();
            }
        }
    }

    /// <summary>Forces the progress bar to hide regardless of active operation count.</summary>
    public void ForceHide()
    {
        lock (_lock)
        {
            _activeOperations = 0;
            UpdateIndeterminateState();
        }
    }

    public bool IsVisible
    {
        get { lock (_lock) { return _activeOperations > 0; } }
    }

    public int ActiveOperationsCount
    {
        get { lock (_lock) { return _activeOperations; } }
    }

    /// <summary>Shows the built-in WinUI "error" tint, e.g. after an exception.</summary>
    public void ReportError()
    {
        RunOnUi(() =>
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.ShowPaused = false;
            _progressBar.ShowError = true;
            _progressBar.Visibility = Visibility.Visible;
        });
    }

    /// <summary>Shows the built-in WinUI "paused" tint, used here for user-initiated cancellation.</summary>
    public void ReportCancelled()
    {
        RunOnUi(() =>
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.ShowError = false;
            _progressBar.ShowPaused = true;
            _progressBar.Visibility = Visibility.Visible;
        });
    }

    /// <summary>Marks the bar complete and hides it (success path).</summary>
    public void Complete() => RunOnUi(ResetVisual);

    private void UpdateIndeterminateState()
    {
        var shouldShow = _activeOperations > 0;
        RunOnUi(() =>
        {
            _progressBar.ShowError = false;
            _progressBar.ShowPaused = false;
            _progressBar.IsIndeterminate = shouldShow;
            _progressBar.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void ResetVisual()
    {
        _progressBar.IsIndeterminate = false;
        _progressBar.ShowError = false;
        _progressBar.ShowPaused = false;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Value = 0;
        _progressBar.Visibility = Visibility.Collapsed;
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
            action();
        else
            _dispatcherQueue.TryEnqueue(() => action());
    }
}

public static class ProgressBarExtensions
{
    private static readonly Dictionary<ProgressBar, ProgressBarManager> _managers = new();

    /// <summary>
    /// Gets or creates a ProgressBarManager for this ProgressBar instance.
    /// </summary>
    public static ProgressBarManager GetManager(this ProgressBar progressBar)
    {
        if (!_managers.TryGetValue(progressBar, out var manager))
        {
            manager = new ProgressBarManager(progressBar);
            _managers[progressBar] = manager;
        }
        return manager;
    }

    /// <summary>
    /// Shows progress on this ProgressBar. Thread-safe and handles multiple concurrent operations.
    /// </summary>
    public static void ShowProgress(this ProgressBar progressBar) => progressBar.GetManager().ShowProgress();

    /// <summary>
    /// Hides progress on this ProgressBar. Only hides when all operations complete.
    /// </summary>
    public static void HideProgress(this ProgressBar progressBar) => progressBar.GetManager().HideProgress();

    /// <summary>
    /// Forces the ProgressBar to hide regardless of active operations.
    /// </summary>
    public static void ForceHideProgress(this ProgressBar progressBar) => progressBar.GetManager().ForceHide();
}
