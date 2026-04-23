using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using DeepPurge.Core.App;

namespace DeepPurge.App;

public partial class App : Application
{
    // Single source of truth: the running assembly's version. Avoids the old
    // duplicated-string problem where csproj, manifest, and this hardcoded
    // const could silently disagree after a release bump.
    private static readonly string Version =
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0)).ToString(3);
    private static readonly string CrashLogDir = DataPaths.Logs;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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
        ThemeManager.ApplySavedOrDefault();
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
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nA crash log was written to:\n{CrashLogDir}",
            $"DeepPurge v{Version}",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
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
        try
        {
            Directory.CreateDirectory(CrashLogDir);
            var logFile = Path.Combine(CrashLogDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(logFile,
                $"[{DateTime.Now:O}] {source} exception in DeepPurge v{Version}{Environment.NewLine}{ex}");
        }
        catch { /* logging is best-effort */ }
    }
}
