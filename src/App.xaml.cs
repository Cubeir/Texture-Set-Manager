using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinUIEx;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Texture_Set_Manager;
/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private static Mutex? _mutex = null;
    private static EventWaitHandle? _wakeEvent = null;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();

        TraceManager.Initialize();

        // 1. Catches unhandled exceptions on the UI thread from any window
        this.UnhandledException += (s, e) =>
        {
            WriteCrashLog("UI Thread", $"[{e.Exception.GetType().FullName} / 0x{e.Exception.HResult:X8}] {e.Message}", e.Exception.ToString());
            // intentionally not setting e.Handled = true
            // let it crash naturally so WER still gets the dump
        };

        // 2. Catches exceptions escaping async void after an await,
        // and anything thrown on the UI thread that XAML doesn't intercept
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            WriteCrashLog("Unobserved Task", e.Exception.Message, e.Exception.ToString());
            e.SetObserved(); // prevents process termination for tasks, since we've logged it ourselves
        };

        // 3. Catches exceptions on background threads, Thread.Start, etc.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            WriteCrashLog("Background Thread", ex?.Message ?? "Unknown", ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "No details");
            // can't prevent termination here, but the log is written
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            _mutex = new Mutex(true, GetUniqueName(), out var isNewInstance);

            if (!isNewInstance)
            {
                // Signal the already-running instance to bring itself to front. This replaced
                // hunting for the other process by name and poking its MainWindowHandle: a
                // packaged WinUI process doesn't reliably report one, so that approach could
                // silently do nothing and leave the user staring at a window that never came up.
                if (EventWaitHandle.TryOpenExisting($"{GetUniqueName()}_wake", out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
                Exit();
                return;
            }

            // Create the wake event for this instance to listen on
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"{GetUniqueName()}_wake");
            _ = Task.Run(() =>
            {
                while (_wakeEvent.WaitOne())
                {
                    MainWindow.Instance?.DispatcherQueue.TryEnqueue(() => BringToFront(MainWindow.Instance));
                }
            });

            // Constructing MainWindow is what kicks off InitializeComponent and the rest of the
            // startup work. The delay before Activate() gives XAML time to finish building the
            // tree, so the window doesn't flash a black background or show a splash whose image
            // hasn't loaded yet.
            _window = new MainWindow();
            await Task.Delay(175);
            _window.Activate();
        }
        catch (Exception ex)
        {
            // Anything that escapes here means the app never became usable. The UnhandledException
            // hook can't see it (this is async void, past the first await), so record it directly
            // – otherwise a startup crash is the one crash that leaves no report behind.
            WriteCrashLog("OnLaunched", ex.Message, ex.ToString());
            throw;
        }
    }

    private static void BringToFront(Window? window)
    {
        if (window == null) return;

        try
        {
            // Restore() un-maximizes as well as un-minimizes, so only call it when the window
            // actually is minimized - otherwise re-launching the app would quietly shrink a
            // maximized window back down.
            if (window.AppWindow?.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                window.Restore();

            window.SetForegroundWindow();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[App] Couldn't bring the existing window to front: {ex.Message}");
        }
    }

    /// <summary>
    /// Dropped next to the app's local data and surfaced by MainWindow on the next launch,
    /// so a crash the user hit yesterday is still reportable today.
    /// </summary>
    public static void WriteCrashLog(string source, string message, string detail)
    {
        try
        {
            var logPath = Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                "last_session_crash_log.txt");

            File.AppendAllText(logPath,
                $"=== Crash Report ===\n" +
                $"Version:   {EnvironmentVariables.appVersion}\n" +
                $"Source:    {source}\n" +
                $"Time:      {DateTime.Now}\n" +
                $"Message:   {message}\n" +
                $"Detail:\n{detail}\n\n" +
                $"{TraceManager.GetAllTraceLogs()}\n\n");
        }
        catch { /* if even this fails there's nothing left to do */ }
    }

    public static string GetUniqueName()
    {
        try
        {
            var family = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            var idx = family.LastIndexOf('_');
            var suffix = (idx >= 0 && idx < family.Length - 1)
                ? family[(idx + 1)..]
                : family;
            return $"tsmanager_{suffix}";
        }
        catch
        {
            return "texture_set_manager";
        }
    }

    public static Windows.ApplicationModel.PackageVersion GetPackageVersion()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.Version;
        }
        catch
        {
            Trace.WriteLine("[GetPackageVersion] Failed.");
            return new Windows.ApplicationModel.PackageVersion { Major = 0, Minor = 0, Build = 0, Revision = 0 };
        }
    }

    // Clean up mutex when app exits
    ~App()
    {
        CleanupMutex();
    }

    public static void CleanupMutex()
    {
        try { _mutex?.ReleaseMutex(); } catch { /* not owned / already released */ }
        _mutex?.Dispose();
        _mutex = null;

        _wakeEvent?.Dispose();
        _wakeEvent = null;
    }
}


/// <summary>
/// Custom TraceListener that captures all Trace.WriteLine calls
/// </summary>
public class InMemoryTraceListener : TraceListener
{
    private readonly ConcurrentQueue<TraceEntry> _entries = new();
    private readonly int _maxEntries;
    private int _count;

    public InMemoryTraceListener(int maxEntries = 1000)
    {
        _maxEntries = maxEntries;
    }

    public override void Write(string? message)
    {
        // Usually not used, but implement for completeness
        WriteLine(message);
    }

    public override void WriteLine(string? message)
    {
        var entry = new TraceEntry
        {
            Timestamp = DateTime.Now,
            Message = message,
            ThreadId = Environment.CurrentManagedThreadId
        };

        _entries.Enqueue(entry);

        // Interlocked counter rather than ConcurrentQueue.Count: that property walks the whole
        // queue, so trimming a 25,000-entry buffer on every single trace line was quietly O(n)
        // per write on whatever thread happened to be logging.
        if (Interlocked.Increment(ref _count) > _maxEntries)
        {
            if (_entries.TryDequeue(out _))
                Interlocked.Decrement(ref _count);
        }
    }

    public string GetAllEntries()
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== Trace Logs");

        foreach (var entry in _entries)
        {
            sb.AppendLine($"[{entry.Timestamp:HH:mm:ss.fff}] [T{entry.ThreadId}] {entry.Message}");
        }

        return sb.ToString();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _count, 0);
    }

    private class TraceEntry
    {
        public DateTime Timestamp { get; set; }
        public string? Message { get; set; }
        public int ThreadId { get; set; }
    }
}

public static class TraceManager
{
    private static InMemoryTraceListener? _listener;

    public static void Initialize()
    {
        // Enable if we don't want debugger output...
        // Trace.Listeners.Clear();

        _listener = new InMemoryTraceListener(maxEntries: 25000);
        Trace.Listeners.Add(_listener);

        Trace.WriteLine("TraceManager initialized");
    }

    public static string GetAllTraceLogs()
    {
        return _listener?.GetAllEntries() ?? "Trace logging not initialized";
    }

    public static void ClearTraceLogs()
    {
        _listener?.Clear();
    }
}
