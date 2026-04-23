using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Shortcuts;

public enum ShortcutStatus { Valid, Broken, Unresolved, MsiAdvertised, Store }

public class ShortcutEntry
{
    public string Path       { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Arguments  { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public string Description{ get; set; } = "";
    public long   SizeBytes  { get; set; }
    public ShortcutStatus Status { get; set; }
}

/// <summary>
/// Enumerate *.lnk files under Desktop, per-user Start Menu, and common
/// Start Menu, parse each via the COM <c>IShellLinkW</c> / <c>IPersistFile</c>
/// interfaces, and classify whether the target still exists.
///
/// <para>
/// Threading note: <c>IShellLinkW</c> strictly requires an STA apartment.
/// Callers routinely run scanners on <see cref="Task.Run"/> which supplies
/// MTA worker threads — passing those to <c>CoCreateInstance(CLSID_ShellLink)</c>
/// is undefined behaviour in practice (usually works, occasionally throws
/// <c>E_NOINTERFACE</c> or corrupts state). We spin our own dedicated STA
/// thread for the entire scan so apartment semantics are correct without
/// forcing callers to care.
/// </para>
///
/// <para>
/// Deletion note: broken shortcuts are user-visible artifacts. We move them
/// to the Recycle Bin via <c>SHFileOperation</c> rather than <c>File.Delete</c>
/// so an accidental sweep is recoverable.
/// </para>
/// </summary>
public class ShortcutRepairScanner
{
    /// <summary>Synchronous scan that runs on a dedicated STA thread.</summary>
    public List<ShortcutEntry> ScanAll(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<List<ShortcutEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staThread = new Thread(() =>
        {
            try { tcs.SetResult(ScanAllCore(ct)); }
            catch (OperationCanceledException) { tcs.SetCanceled(ct); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "DeepPurge.ShortcutScanner",
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();
        return tcs.Task.GetAwaiter().GetResult();
    }

    public async Task<List<ShortcutEntry>> ScanAllAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => ScanAll(ct), ct);
    }

    public static IEnumerable<string> EnumerateRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
    }

    /// <summary>
    /// Send broken shortcuts to the Recycle Bin. Returns the number actually
    /// removed (excluding ones that were already gone by the time we got there).
    /// </summary>
    public int RecycleBroken(IEnumerable<ShortcutEntry> broken)
    {
        int n = 0;
        foreach (var s in broken.Where(x => x.Status == ShortcutStatus.Broken))
        {
            try
            {
                if (!File.Exists(s.Path)) continue;
                if (RecycleViaShell(s.Path)) n++;
            }
            catch (Exception ex) { Log.Warn($"Recycle '{s.Path}': {ex.Message}"); }
        }
        return n;
    }

    [Obsolete("Prefer RecycleBroken — permanent delete is surprising for user-visible shortcuts.")]
    public int DeleteBroken(IEnumerable<ShortcutEntry> broken) => RecycleBroken(broken);

    // ═══════════════════════════════════════════════════════
    //  CORE WALK (STA-bound)
    // ═══════════════════════════════════════════════════════

    private static List<ShortcutEntry> ScanAllCore(CancellationToken ct)
    {
        var results = new List<ShortcutEntry>();
        foreach (var root in EnumerateRoots())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch (Exception ex) { Log.Warn($"Shortcuts root '{root}': {ex.Message}"); continue; }

            foreach (var lnk in files)
            {
                ct.ThrowIfCancellationRequested();
                var entry = InspectOne(lnk);
                if (entry != null) results.Add(entry);
            }
        }
        return results;
    }

    private static ShortcutEntry? InspectOne(string lnkPath)
    {
        IShellLinkW? shellLink = null;
        IPersistFile? pf = null;
        try
        {
            var fi = new FileInfo(lnkPath);
            var entry = new ShortcutEntry { Path = lnkPath, SizeBytes = fi.Length };

            shellLink = (IShellLinkW)new ShellLink();
            pf = (IPersistFile)shellLink;
            pf.Load(lnkPath, 0);

            const uint SLR_NO_UI    = 0x0001;
            const uint SLR_NOUPDATE = 0x0008;
            const uint SLR_NOSEARCH = 0x0010;
            const uint SLR_NOTRACK  = 0x0020;
            try { shellLink.Resolve(IntPtr.Zero, SLR_NO_UI | SLR_NOUPDATE | SLR_NOSEARCH | SLR_NOTRACK); }
            catch { /* target missing: expected */ }

            var target = new System.Text.StringBuilder(512);
            shellLink.GetPath(target, target.Capacity, out _, 0);
            entry.TargetPath = target.ToString();

            var args = new System.Text.StringBuilder(1024);
            shellLink.GetArguments(args, args.Capacity);
            entry.Arguments = args.ToString();

            var wd = new System.Text.StringBuilder(512);
            shellLink.GetWorkingDirectory(wd, wd.Capacity);
            entry.WorkingDir = wd.ToString();

            var desc = new System.Text.StringBuilder(1024);
            shellLink.GetDescription(desc, desc.Capacity);
            entry.Description = desc.ToString();

            entry.Status = ClassifyTarget(entry.TargetPath);
            return entry;
        }
        catch (Exception ex)
        {
            Log.Warn($"Shortcut '{lnkPath}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (pf        != null) { try { Marshal.FinalReleaseComObject(pf);        } catch { } }
            if (shellLink != null) { try { Marshal.FinalReleaseComObject(shellLink); } catch { } }
        }
    }

    private static ShortcutStatus ClassifyTarget(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return ShortcutStatus.Unresolved;
        if (targetPath.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0 ||
            targetPath.EndsWith(".appref-ms",   StringComparison.OrdinalIgnoreCase) ||
            targetPath.EndsWith(".url",          StringComparison.OrdinalIgnoreCase))
            return ShortcutStatus.Store;
        if (File.Exists(targetPath) || Directory.Exists(targetPath)) return ShortcutStatus.Valid;
        return ShortcutStatus.Broken;
    }

    // ═══════════════════════════════════════════════════════
    //  SHFileOperation → Recycle Bin
    // ═══════════════════════════════════════════════════════

    private static bool RecycleViaShell(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        int rc = SHFileOperation(ref op);
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    private const uint FO_DELETE           = 0x0003;
    private const ushort FOF_SILENT        = 0x0004;
    private const ushort FOF_NOCONFIRMATION= 0x0010;
    private const ushort FOF_ALLOWUNDO     = 0x0040;
    private const ushort FOF_NOERRORUI     = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint   wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    // ═══════════════════════════════════════════════════════
    //  COM interop
    // ═══════════════════════════════════════════════════════

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
                     int cchMaxPath, out WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]  public string cAlternateFileName;
    }
}
