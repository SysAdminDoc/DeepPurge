using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeepPurge.App.ViewModels;
using DeepPurge.App.Views;

namespace DeepPurge.Capture;

internal static class Program
{
    private const uint DesktopCreateWindow = 0x0002;
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopWriteObjects = 0x0080;
    private const uint WaitTimeout = 0x00000102;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int UoiName = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        SetBestDpiAwareness();

        var options = CaptureOptions.Parse(args);
        return options.Worker
            ? RunWorker(options)
            : RunOnPrivateDesktop(options);
    }

    private static int RunOnPrivateDesktop(CaptureOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        var desktopName = $"DeepPurgeCapture_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
        var desktop = CreateDesktop(
            desktopName,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            DesktopCreateWindow | DesktopReadObjects | DesktopWriteObjects,
            IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a private capture desktop.");

        try
        {
            var workerPath = Path.Combine(AppContext.BaseDirectory, "DeepPurge.Capture.exe");
            if (!File.Exists(workerPath))
                throw new FileNotFoundException("Build and run the DeepPurge.Capture apphost executable.", workerPath);

            Environment.SetEnvironmentVariable("DEEPPURGE_CAPTURE_MODE", "1", EnvironmentVariableTarget.Process);
            var commandLine = new StringBuilder(
                $"\"{workerPath}\" --worker --output-dir \"{options.OutputDirectory}\" --panels \"{string.Join(',', options.Panels)}\"");
            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = $"winsta0\\{desktopName}",
            };

            if (!CreateProcess(
                    workerPath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    AppContext.BaseDirectory,
                    ref startupInfo,
                    out var processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the capture worker.");
            }

            try
            {
                var wait = WaitForSingleObject(processInfo.Process, 360_000);
                if (wait == WaitTimeout)
                {
                    TerminateProcess(processInfo.Process, 124);
                    Console.Error.WriteLine("Capture timed out after 360 seconds.");
                    return 124;
                }

                if (!GetExitCodeProcess(processInfo.Process, out var exitCode))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read capture worker status.");

                return unchecked((int)exitCode);
            }
            finally
            {
                CloseHandle(processInfo.Thread);
                CloseHandle(processInfo.Process);
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    private static int RunWorker(CaptureOptions options)
    {
        var desktopName = GetCurrentDesktopName();
        if (string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Capture worker refused to run on the interactive desktop.");
            return 3;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/DeepPurge;component/Themes/Colors/DeepPurgeSlate.xaml"),
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/DeepPurge;component/Themes/BaseStyles.xaml"),
        });

        var exitCode = 0;
        var window = new MainWindow
        {
            Width = 1680,
            Height = 945,
            Left = 0,
            Top = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
        };

        window.ContentRendered += async (_, _) =>
        {
            try
            {
                await CapturePanelsAsync(window, options);
            }
            catch (Exception ex)
            {
                exitCode = 1;
                var errorPath = Path.Combine(options.OutputDirectory, "capture-error.txt");
                await File.WriteAllTextAsync(errorPath, ex.ToString());
            }
            finally
            {
                window.Close();
                app.Shutdown(exitCode);
            }
        };

        app.Run(window);
        return exitCode;
    }

    private static async Task CapturePanelsAsync(MainWindow window, CaptureOptions options)
    {
        if (window.DataContext is not MainViewModel viewModel)
            throw new InvalidOperationException("The production window did not expose its view model.");

        await WaitUntilAsync(() => !viewModel.IsInitialScanRunning, TimeSpan.FromSeconds(75));
        await Task.Delay(900);

        foreach (var panel in options.Panels)
        {
            if (!string.Equals(panel, "Programs", StringComparison.OrdinalIgnoreCase))
            {
                var navigation = FindDescendants<RadioButton>(window)
                    .FirstOrDefault(button => string.Equals(button.Tag as string, panel, StringComparison.OrdinalIgnoreCase));
                if (navigation is null)
                    throw new InvalidOperationException($"Navigation panel '{panel}' was not found.");
                navigation.IsChecked = true;
            }

            if (string.Equals(panel, "Junk", StringComparison.OrdinalIgnoreCase))
                await viewModel.ScanJunkAsync();

            if (string.Equals(panel, "Health", StringComparison.OrdinalIgnoreCase))
            {
                await WaitUntilAsync(
                    () => !viewModel.IsBusy && viewModel.HealthCategories.Count > 0,
                    TimeSpan.FromSeconds(210));
            }
            else
            {
                await WaitUntilAsync(() => !viewModel.IsBusy, TimeSpan.FromSeconds(20));
            }

            window.InvalidateMeasure();
            window.InvalidateArrange();
            window.InvalidateVisual();
            foreach (var element in FindDescendants<UIElement>(window))
                element.InvalidateVisual();
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(2_000);

            var outputPath = Path.Combine(
                options.OutputDirectory,
                $"deeppurge-{panel.ToLowerInvariant()}.png");
            SaveWindow(window, outputPath);
            Console.WriteLine($"Captured {panel} on private desktop '{GetCurrentDesktopName()}': {outputPath}");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"UI state did not settle within {timeout.TotalSeconds:0} seconds.");
            await Task.Delay(200);
        }
    }

    private static void SaveVisual(FrameworkElement visual, string outputPath)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var snapshot = new DrawingVisual();
        using (var drawing = snapshot.RenderOpen())
        {
            var brush = new VisualBrush(visual)
            {
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Stretch = Stretch.None,
            };
            drawing.DrawRectangle(brush, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        }
        bitmap.Render(snapshot);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);

        if (stream.Length < 50_000)
            throw new InvalidDataException($"Rendered screenshot is unexpectedly small: {stream.Length} bytes.");
    }

    private static void SaveWindow(Window window, string outputPath)
    {
        var visualPath = outputPath + ".visual.tmp.png";
        var nativePath = outputPath + ".native.tmp.png";
        try
        {
            SaveVisual(window, visualPath);
            var hasNative = TrySaveNativeWindow(window, nativePath);
            var visualScore = ScoreBrandRegion(visualPath);
            var nativeScore = hasNative ? ScoreBrandRegion(nativePath) : -1;
            var selectedPath = nativeScore >= visualScore ? nativePath : visualPath;
            File.Copy(selectedPath, outputPath, overwrite: true);
            Console.WriteLine($"Selected {(selectedPath == nativePath ? "native" : "visual-tree")} renderer " +
                              $"(brand scores: native={nativeScore}, visual={visualScore}).");
        }
        finally
        {
            if (File.Exists(visualPath)) File.Delete(visualPath);
            if (File.Exists(nativePath)) File.Delete(nativePath);
        }

        var size = new FileInfo(outputPath).Length;
        if (size < 50_000)
            throw new InvalidDataException($"Rendered screenshot is unexpectedly small: {size} bytes.");
    }

    private static bool TrySaveNativeWindow(Window window, string outputPath)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetClientRect(handle, out var client))
            return false;

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
            return false;

        using var bitmap = new System.Drawing.Bitmap(
            width,
            height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var deviceContext = graphics.GetHdc();
        try
        {
            if (!PrintWindow(handle, deviceContext, 1))
                return false;
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }

        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        return new FileInfo(outputPath).Length >= 50_000;
    }

    private static int ScoreBrandRegion(string path)
    {
        using var bitmap = new System.Drawing.Bitmap(path);
        var maxX = Math.Min(bitmap.Width, 285);
        var maxY = Math.Min(bitmap.Height, 110);
        var score = 0;
        for (var y = 0; y < maxY; y += 2)
        {
            for (var x = 0; x < maxX; x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.A > 0 && color.R + color.G + color.B >= 360)
                    score++;
            }
        }
        return score;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private static string GetCurrentDesktopName()
    {
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        var required = 0;
        GetUserObjectInformation(desktop, UoiName, IntPtr.Zero, 0, ref required);
        if (required <= 2)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size the desktop name buffer.");

        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (!GetUserObjectInformation(desktop, UoiName, buffer, required, ref required))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the desktop name.");
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetBestDpiAwareness()
    {
        if (!SetProcessDpiAwarenessContext(new IntPtr(-4)))
            SetProcessDpiAware();
    }

    private sealed record CaptureOptions(bool Worker, string OutputDirectory, IReadOnlyList<string> Panels)
    {
        public static CaptureOptions Parse(string[] args)
        {
            var worker = args.Contains("--worker", StringComparer.OrdinalIgnoreCase);
            var output = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", "assets", "screenshots"));
            IReadOnlyList<string> panels = ["Programs", "Junk", "DeletionRecovery"];

            for (var index = 0; index < args.Length; index++)
            {
                if (args[index].Equals("--output-dir", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                    output = Path.GetFullPath(args[++index]);
                else if (args[index].Equals("--panels", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                    panels = args[++index]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (panels.Count == 0)
                throw new ArgumentException("At least one panel must be requested.");
            return new CaptureOptions(worker, output, panels);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(
        string desktop,
        IntPtr device,
        IntPtr deviceMode,
        int flags,
        uint desiredAccess,
        IntPtr securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle,
        int index,
        IntPtr information,
        int length,
        ref int needed);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAware();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
}
