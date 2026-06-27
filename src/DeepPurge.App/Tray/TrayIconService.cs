using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using DeepPurge.App.ViewModels;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Schedule;

namespace DeepPurge.App.Tray;

internal sealed class TrayIconService : IDisposable
{
    private const int TrayIconId = 1;
    private const int WmTrayIcon = 0x800 + 42;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifInfo = 0x00000010;
    private const int NiifInfo = 0x00000001;
    private const int NiifWarning = 0x00000002;
    private const int NiifError = 0x00000003;

    private readonly MainViewModel _viewModel;
    private readonly Action _openMainWindow;
    private readonly Action _exitApplication;
    private readonly Action<string, bool, bool> _showToast;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly HwndSource? _source;
    private readonly ContextMenu _menu;
    private readonly MenuItem _scheduleStatusItem;
    private readonly MenuItem _previewItem;
    private readonly DispatcherTimer _scheduleTimer;
    private bool _disposed;
    private bool _previewRunning;
    private int? _lastScheduleCount;

    public TrayIconService(
        Window owner,
        MainViewModel viewModel,
        Action openMainWindow,
        Action exitApplication,
        Action<string, bool, bool> showToast)
    {
        _viewModel = viewModel;
        _openMainWindow = openMainWindow;
        _exitApplication = exitApplication;
        _showToast = showToast;
        _hwnd = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _icon = ExtractSmallIcon(Environment.ProcessPath, out _ownsIcon);

        _scheduleStatusItem = new MenuItem { Header = "Scheduled jobs: checking...", IsEnabled = false };
        _previewItem = new MenuItem { Header = "Run clean preview" };
        _previewItem.Click += async (_, _) => await RunCleanPreviewAsync();

        _menu = BuildMenu();
        AddTrayIcon();

        _scheduleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5),
        };
        _scheduleTimer.Tick += (_, _) => RefreshScheduleStatus(showBalloonOnChange: true);
        _scheduleTimer.Start();

        RefreshScheduleStatus(showBalloonOnChange: false);
    }

    public void ShowBackgroundHint()
        => ShowBalloon("DeepPurge", "DeepPurge is running in the tray. Scheduled cleaning jobs continue through Task Scheduler.", BalloonIcon.Info);

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open DeepPurge" };
        open.Click += (_, _) => _openMainWindow();
        menu.Items.Add(open);

        menu.Items.Add(_scheduleStatusItem);

        var refresh = new MenuItem { Header = "Refresh schedule status" };
        refresh.Click += (_, _) => RefreshScheduleStatus(showBalloonOnChange: false, forceBalloon: true);
        menu.Items.Add(refresh);

        menu.Items.Add(_previewItem);
        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => _exitApplication();
        menu.Items.Add(exit);

        return menu;
    }

    private void AddTrayIcon()
    {
        var data = CreateNotifyIconData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = WmTrayIcon;
        data.hIcon = _icon;
        data.szTip = "DeepPurge";
        NotifyIcon(NimAdd, ref data, "add");
    }

    private NotifyIconData CreateNotifyIconData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmTrayIcon || wParam.ToInt32() != TrayIconId) return IntPtr.Zero;

        var mouseMessage = lParam.ToInt32();
        if (mouseMessage == WmLButtonDblClk)
        {
            _openMainWindow();
            handled = true;
        }
        else if (mouseMessage is WmRButtonUp or WmContextMenu)
        {
            ShowContextMenu();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        SetForegroundWindow(_hwnd);
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private void RefreshScheduleStatus(bool showBalloonOnChange, bool forceBalloon = false)
    {
        try
        {
            var jobs = new ScheduleManager().ListJobs();
            _viewModel.ScheduledJobs.Clear();
            foreach (var job in jobs) _viewModel.ScheduledJobs.Add(job);

            var count = jobs.Count;
            _scheduleStatusItem.Header = count == 1 ? "Scheduled jobs: 1 active" : $"Scheduled jobs: {count} active";

            if (forceBalloon || (showBalloonOnChange && _lastScheduleCount.HasValue && _lastScheduleCount.Value != count))
            {
                var message = count == 0
                    ? "No DeepPurge scheduled cleaning jobs are active."
                    : $"{count} DeepPurge scheduled cleaning job(s) are active.";
                ShowBalloon("Scheduled cleaning", message, BalloonIcon.Info);
            }

            _lastScheduleCount = count;
        }
        catch (Exception ex)
        {
            _scheduleStatusItem.Header = "Scheduled jobs: unavailable";
            Log.Warn($"Tray schedule status refresh failed: {ex.Message}");
            if (forceBalloon)
                ShowBalloon("Scheduled cleaning", $"Schedule status unavailable: {ex.Message}", BalloonIcon.Warning);
        }
    }

    private async Task RunCleanPreviewAsync()
    {
        if (_previewRunning) return;

        var cliPath = ResolveCliPath();
        if (cliPath == null)
        {
            ShowBalloon("Clean preview", "DeepPurgeCli.exe was not found next to the GUI executable.", BalloonIcon.Warning);
            return;
        }

        _previewRunning = true;
        _previewItem.IsEnabled = false;
        _previewItem.Header = "Clean preview running...";
        _showToast("Tray clean preview started", false, false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "clean junk evidence --dry-run --json",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                ShowBalloon("Clean preview", "Could not start DeepPurgeCli.exe.", BalloonIcon.Error);
                return;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(stderr) ? $"Exit code {process.ExitCode}" : stderr.Trim();
                ShowBalloon("Clean preview failed", message, BalloonIcon.Error);
                _showToast($"Tray clean preview failed: {message}", true, false);
                return;
            }

            var totalBytes = TryReadTotalBytes(stdout);
            var summary = totalBytes.HasValue
                ? $"Dry run found {SizeFormatter.Format(totalBytes.Value)} reclaimable."
                : "Dry-run clean preview completed.";
            ShowBalloon("Clean preview complete", summary, BalloonIcon.Info);
            _showToast(summary, false, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray clean preview failed: {ex.Message}");
            ShowBalloon("Clean preview failed", ex.Message, BalloonIcon.Error);
            _showToast($"Tray clean preview failed: {ex.Message}", true, false);
        }
        finally
        {
            _previewRunning = false;
            _previewItem.IsEnabled = true;
            _previewItem.Header = "Run clean preview";
        }
    }

    private static string? ResolveCliPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DeepPurgeCli.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "DeepPurge.Cli", "DeepPurgeCli.exe"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch { }
        }

        return null;
    }

    private static long? TryReadTotalBytes(string output)
    {
        var start = output.LastIndexOf('{');
        if (start < 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            return doc.RootElement.TryGetProperty("total", out var total) && total.TryGetInt64(out var bytes)
                ? bytes
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ShowBalloon(string title, string message, BalloonIcon icon)
    {
        if (_disposed) return;
        var data = CreateNotifyIconData();
        data.uFlags = NifInfo;
        data.szInfoTitle = title;
        data.szInfo = message.Length > 240 ? message[..237] + "..." : message;
        data.dwInfoFlags = (int)icon;
        NotifyIcon(NimModify, ref data, "balloon");
    }

    private static IntPtr ExtractSmallIcon(string? exePath, out bool ownsIcon)
    {
        ownsIcon = false;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var small = new IntPtr[1];
            try
            {
                if (ExtractIconEx(exePath, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
                {
                    ownsIcon = true;
                    return small[0];
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Tray icon extraction failed: {ex.Message}");
            }
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scheduleTimer.Stop();
        _menu.IsOpen = false;
        var data = CreateNotifyIconData();
        NotifyIcon(NimDelete, ref data, "delete");
        _source?.RemoveHook(WndProc);
        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    private static void NotifyIcon(int message, ref NotifyIconData data, string operation)
    {
        if (!Shell_NotifyIcon(message, ref data))
            Log.Warn($"Tray icon {operation} failed: Win32 {Marshal.GetLastWin32Error()}");
    }

    private enum BalloonIcon
    {
        Info = NiifInfo,
        Warning = NiifWarning,
        Error = NiifError,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
