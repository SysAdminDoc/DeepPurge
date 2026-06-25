using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using DeepPurge.Core.App;

namespace DeepPurge.App;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    static App()
    {
        SetDllDirectory("");
    }

    // Single source of truth: the running assembly's version. Avoids the old
    // duplicated-string problem where csproj, manifest, and this hardcoded
    // const could silently disagree after a release bump.
    private static readonly string Version =
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0)).ToString(3);

    // Lazy — must NOT eagerly evaluate DataPaths during class load, because
    // a TypeInitializationException from DataPaths/UserIdentity P/Invoke
    // kills the process before any exception handler is wired up. That
    // manifests as "cursor spins for a second, then nothing — no window,
    // no error, no crash log."
    private static readonly Lazy<string> _crashLogDir = new(() =>
    {
        try { return DataPaths.Logs; }
        catch { return Path.Combine(Path.GetTempPath(), "DeepPurge", "Logs"); }
    });
    private static string CrashLogDir => _crashLogDir.Value;

    // Fallback log path that never touches DataPaths. Guaranteed to work
    // even when DataPaths/UserIdentity init itself is the thing that threw.
    private static string FallbackCrashLogDir
        => Path.Combine(Path.GetTempPath(), "DeepPurge", "Logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Wire global exception handlers FIRST — before calling *anything*
        // that could throw (including base.OnStartup, ThemeManager, DataPaths).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            base.OnStartup(e);

            if (!IsRunningAsAdministrator())
            {
                MessageBox.Show(
                    "DeepPurge requires administrator privileges.\nPlease run as administrator.",
                    $"DeepPurge v{Version}",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }

            // Apply the persisted theme (or dark default) before the main window shows.
            try
            {
                ThemeManager.ApplySavedOrDefault();
            }
            catch (Exception ex)
            {
                // Theme load failure must not prevent launch. Fall through with
                // whatever theme XAML embedded as the default.
                LogCrash(ex, "ThemeInit");
            }
        }
        catch (Exception ex)
        {
            // Last-resort safety net. If we get here, something fundamental blew up
            // during startup (DataPaths init, XAML resource load, etc.). Show the
            // error so users can report it instead of seeing nothing at all.
            LogCrash(ex, "Startup");
            try
            {
                MessageBox.Show(
                    $"DeepPurge failed to start:\n\n{ex.Message}\n\n" +
                    $"A crash log was written to:\n{FallbackCrashLogDir}\n\n" +
                    $"Full exception:\n{ex}",
                    $"DeepPurge v{Version} — Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* MessageBox itself failing means no display at all — nothing more we can do */ }
            Shutdown(1);
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception, "UI");
        try
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nA crash log was written to:\n{CrashLogDir}",
                $"DeepPurge v{Version}",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { /* display is best-effort */ }
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => LogCrash(e.ExceptionObject as Exception, "Domain");

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash(e.Exception, "Task");
        e.SetObserved();
    }

    private static void LogCrash(Exception? ex, string source)
    {
        if (ex == null) return;
        var content = $"[{DateTime.Now:O}] {source} exception in DeepPurge v{Version}{Environment.NewLine}{ex}";
        var fileName = $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log";

        // Try the normal DataPaths-routed log directory first.
        if (TryWriteCrashLog(CrashLogDir, fileName, content)) return;

        // Fallback to %TEMP%\DeepPurge\Logs — always writable, even when
        // DataPaths itself is the thing that threw.
        TryWriteCrashLog(FallbackCrashLogDir, fileName, content);
    }

    private static bool TryWriteCrashLog(string dir, string fileName, string content)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), content);
            return true;
        }
        catch { return false; }
    }
}
