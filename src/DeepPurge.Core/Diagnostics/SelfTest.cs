using System.Security.Principal;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Diagnostics;

public enum SelfTestStatus { Ok, Warn, Fail, Skipped }

public record SelfTestResult(string Check, SelfTestStatus Status, string Detail, string? Hint = null);

/// <summary>
/// Diagnostic self-test that exercises each major scanner without touching
/// the filesystem or registry destructively. Used by <c>deeppurgecli doctor</c>
/// so users can report "it doesn't work" with concrete signal instead of
/// "nothing happens when I click it."
/// </summary>
public static class SelfTest
{
    public static IReadOnlyList<SelfTestResult> RunAll()
    {
        var results = new List<SelfTestResult>();

        // ── Environment ───────────────────────────────────────
        results.Add(CheckElevation());
        results.Add(CheckOsVersion());
        results.Add(CheckDataPaths());
        results.Add(CheckLogsWritable());

        // ── Scanners (read-only probes) ───────────────────────
        results.Add(CheckPnpUtil());
        results.Add(CheckWdiStartupInfo());
        results.Add(CheckRegistryAccess());
        results.Add(CheckShortcutsRoots());
        results.Add(CheckWinget());
        results.Add(CheckSchtasks());
        results.Add(CheckDriverStoreRepo());
        results.Add(CheckWinapp2Cached());
        results.Add(CheckSnapshotDir());

        return results;
    }

    // ═══════════════════════════════════════════════════════
    //  CHECKS
    // ═══════════════════════════════════════════════════════

    private static SelfTestResult CheckElevation()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            bool admin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            return admin
                ? new("Elevation", SelfTestStatus.Ok,   "Running as Administrator")
                : new("Elevation", SelfTestStatus.Warn, "Not elevated — autorun / services / WDI reads may be empty",
                      Hint: "Relaunch from an elevated shell (Run as administrator).");
        }
        catch (Exception ex) { return new("Elevation", SelfTestStatus.Fail, ex.Message); }
    }

    private static SelfTestResult CheckOsVersion()
    {
        var v = Environment.OSVersion.Version;
        var supported = v.Major >= 10;
        return supported
            ? new("OS version", SelfTestStatus.Ok,   $"Windows {v}")
            : new("OS version", SelfTestStatus.Fail, $"Needs Windows 10+; running {v}");
    }

    private static SelfTestResult CheckDataPaths()
    {
        try
        {
            _ = DataPaths.Root;
            var mode = DataPaths.IsPortable ? "portable" : "installed";
            return new("Data paths", SelfTestStatus.Ok, $"{mode} → {DataPaths.Root}");
        }
        catch (Exception ex) { return new("Data paths", SelfTestStatus.Fail, ex.Message); }
    }

    private static SelfTestResult CheckLogsWritable()
    {
        try
        {
            var probe = Path.Combine(DataPaths.Logs, ".probe");
            File.WriteAllText(probe, "ok"); File.Delete(probe);
            return new("Logs writable", SelfTestStatus.Ok, DataPaths.Logs);
        }
        catch (Exception ex) { return new("Logs writable", SelfTestStatus.Fail, ex.Message); }
    }

    private static SelfTestResult CheckPnpUtil()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "pnputil.exe");
        return File.Exists(path)
            ? new("pnputil.exe",  SelfTestStatus.Ok, path)
            : new("pnputil.exe",  SelfTestStatus.Fail, "Not found — Driver Store tool disabled");
    }

    private static SelfTestResult CheckWdiStartupInfo()
    {
        // Directory.Exists returns false on ACL-denied directories in .NET, which
        // would misdiagnose elevation issues as "directory missing." Instead, try
        // to enumerate and classify by the exception we actually get.
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "wdi", "LogFiles", "StartupInfo");
        try
        {
            var n = Directory.GetFiles(dir, "Startup*.xml").Length;
            return n > 0
                ? new("WDI StartupInfo", SelfTestStatus.Ok,   $"{n} trace file(s) in {dir}")
                : new("WDI StartupInfo", SelfTestStatus.Warn, "Directory exists but no Startup*.xml",
                      Hint: "Reboot Windows once — the WDI trace is generated on next boot.");
        }
        catch (DirectoryNotFoundException)
        {
            return new("WDI StartupInfo", SelfTestStatus.Warn,
                "Directory missing (WDI not provisioned)",
                Hint: "Reboot Windows once — the WDI provider creates the trace directory on next boot.");
        }
        catch (UnauthorizedAccessException)
        {
            return new("WDI StartupInfo", SelfTestStatus.Warn, "Access denied",
                Hint: "Relaunch elevated — WDI logs are admin-only.");
        }
        catch (Exception ex) { return new("WDI StartupInfo", SelfTestStatus.Fail, ex.Message); }
    }

    private static SelfTestResult CheckRegistryAccess()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            return k != null
                ? new("Registry access", SelfTestStatus.Ok,   "HKLM\\...\\Uninstall readable")
                : new("Registry access", SelfTestStatus.Warn, "HKLM\\...\\Uninstall returned null");
        }
        catch (Exception ex) { return new("Registry access", SelfTestStatus.Fail, ex.Message); }
    }

    private static SelfTestResult CheckShortcutsRoots()
    {
        try
        {
            int found = 0;
            foreach (var d in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            })
            {
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) found++;
            }
            return new("Shortcut roots", SelfTestStatus.Ok, $"{found}/3 roots accessible");
        }
        catch (Exception ex) { return new("Shortcut roots", SelfTestStatus.Fail, ex.Message); }
    }

    private static SelfTestResult CheckWinget()
    {
        return ProbeExe("winget.exe", "--version")
            ? new("winget", SelfTestStatus.Ok,   "Installed")
            : new("winget", SelfTestStatus.Warn, "Not on PATH (optional — required for per-app 'winget repair')",
                  Hint: "winget install Microsoft.AppInstaller  (or install from the Microsoft Store)");
    }

    private static SelfTestResult CheckSchtasks()
    {
        return ProbeExe("schtasks.exe", "/?")
            ? new("schtasks", SelfTestStatus.Ok,   "Available")
            : new("schtasks", SelfTestStatus.Fail, "schtasks.exe missing — schedule feature broken");
    }

    private static SelfTestResult CheckDriverStoreRepo()
    {
        var repo = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        return Directory.Exists(repo)
            ? new("DriverStore", SelfTestStatus.Ok, repo)
            : new("DriverStore", SelfTestStatus.Warn, "FileRepository missing — sizes will be zero",
                  Hint: "This is a stock Windows path; its absence usually means an extremely locked-down image.");
    }

    private static SelfTestResult CheckWinapp2Cached()
    {
        var path = Path.Combine(DataPaths.Cleaners, "winapp2.ini");
        return File.Exists(path)
            ? new("winapp2.ini", SelfTestStatus.Ok,   $"{new FileInfo(path).Length / 1024} KB cached")
            : new("winapp2.ini", SelfTestStatus.Warn, "Not downloaded yet",
                  Hint: "Open the Community Cleaners panel, or run 'deeppurgecli winapp2' once to populate the cache.");
    }

    private static SelfTestResult CheckSnapshotDir()
    {
        try
        {
            var count = Directory.Exists(DataPaths.Snapshots)
                ? Directory.GetFiles(DataPaths.Snapshots, "*.snapshot.json.gz").Length
                : 0;
            return new("Snapshots dir", SelfTestStatus.Ok, $"{count} snapshots in {DataPaths.Snapshots}");
        }
        catch (Exception ex) { return new("Snapshots dir", SelfTestStatus.Fail, ex.Message); }
    }

    private static bool ProbeExe(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(3000);
            return true;
        }
        catch { return false; }
    }
}
