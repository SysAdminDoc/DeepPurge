using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.ServiceProcess;
using DeepPurge.Core.Security;
using Microsoft.Win32;

namespace DeepPurge.Core.Services;

public class ServiceEntry : INotifyPropertyChanged
{
    private bool _isSelected;
    private SignatureStatus _signatureStatus = SignatureStatus.Unknown;
    private string _signatureDisplay = "";

    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string StartType { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsOrphaned { get; set; }
    public string StatusDisplay => IsOrphaned ? "Orphaned" : Status;

    public SignatureStatus SignatureStatus
    {
        get => _signatureStatus;
        set { _signatureStatus = value; OnPropertyChanged(); }
    }

    public string SignatureDisplay
    {
        get => _signatureDisplay;
        set { _signatureDisplay = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ServiceScanner
{
    // Only include Win32 services: own-process (16), share-process (32),
    // interactive own (272), interactive share (288). Drivers and adapters excluded.
    private static readonly HashSet<int> Win32ServiceTypes = new() { 16, 32, 272, 288 };

    private static readonly string SystemRoot =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static List<ServiceEntry> GetAllServices(bool orphanedOnly = false)
    {
        var entries = new List<ServiceEntry>();

        Dictionary<string, ServiceController> running;
        try
        {
            running = ServiceController.GetServices()
                .ToDictionary(s => s.ServiceName, s => s, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            running = new Dictionary<string, ServiceController>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var servicesKey = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return entries;

            foreach (var name in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var svc = servicesKey.OpenSubKey(name);
                    if (svc == null) continue;

                    var typeVal = svc.GetValue("Type");
                    if (typeVal == null) continue;
                    var type = Convert.ToInt32(typeVal);
                    if (!Win32ServiceTypes.Contains(type)) continue;

                    var imagePath = svc.GetValue("ImagePath")?.ToString() ?? "";
                    var displayName = svc.GetValue("DisplayName")?.ToString() ?? name;
                    var description = svc.GetValue("Description")?.ToString() ?? "";
                    var startTypeRaw = svc.GetValue("Start")?.ToString() ?? "";

                    var startTypeDisplay = startTypeRaw switch
                    {
                        "0" => "Boot",
                        "1" => "System",
                        "2" => "Automatic",
                        "3" => "Manual",
                        "4" => "Disabled",
                        _ => startTypeRaw,
                    };

                    var status = running.TryGetValue(name, out var sc) ? sc.Status.ToString() : "Stopped";
                    var isOrphaned = IsOrphanedService(imagePath);

                    if (orphanedOnly && !isOrphaned) continue;

                    entries.Add(new ServiceEntry
                    {
                        Name = name,
                        DisplayName = displayName,
                        Description = description,
                        ImagePath = imagePath,
                        StartType = startTypeDisplay,
                        Status = status,
                        IsOrphaned = isOrphaned,
                        IsSelected = false,
                    });
                }
                catch { /* skip unreadable service */ }
            }
        }
        catch { /* registry unavailable */ }
        finally
        {
            foreach (var sc in running.Values) sc.Dispose();
        }

        PopulateSignatures(entries);
        return entries.OrderByDescending(e => e.IsOrphaned).ThenBy(e => e.DisplayName).ToList();
    }

    /// <summary>
    /// Enrich each entry with its executable's WinVerifyTrust signature.
    /// Runs across 8 parallel workers because signature checks hit CryptoAPI
    /// which serializes internally on cert-chain validation — single-threaded
    /// makes this noticeably slow on systems with 500+ services.
    /// </summary>
    private static void PopulateSignatures(List<ServiceEntry> entries)
    {
        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = 8 }, entry =>
        {
            try
            {
                var path = StripServiceArguments(entry.ImagePath);
                if (string.IsNullOrEmpty(path)) return;
                if (!File.Exists(path)) return;

                var info = DigitalSignatureInspector.Inspect(path);
                entry.SignatureStatus = info.Status;
                entry.SignatureDisplay = info.Display;
            }
            catch { /* non-fatal */ }
        });
    }

    /// <summary>
    /// Extract just the executable path from an ImagePath — which frequently
    /// includes arguments (e.g. <c>"C:\Windows\System32\svchost.exe -k netsvcs"</c>).
    /// </summary>
    private static string StripServiceArguments(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return "";

        var trimmed = imagePath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 0) trimmed = trimmed[1..end];
        }
        else
        {
            var exeIdx = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0) trimmed = trimmed[..(exeIdx + 4)];
        }

        // Resolve the NT paths the SCM writes ("\SystemRoot\…", "system32\…")
        if (trimmed.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            trimmed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                trimmed[@"\SystemRoot\".Length..]);
        else if (!Path.IsPathRooted(trimmed) &&
                 (trimmed.StartsWith("system32", StringComparison.OrdinalIgnoreCase) ||
                  trimmed.StartsWith("syswow64", StringComparison.OrdinalIgnoreCase)))
            trimmed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                trimmed);

        return Environment.ExpandEnvironmentVariables(trimmed);
    }

    public static bool StopService(ServiceEntry entry)
    {
        try
        {
            using var sc = new ServiceController(entry.Name);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            return true;
        }
        catch { return false; }
    }

    public static bool DisableService(ServiceEntry entry) => RunSc($"config \"{entry.Name}\" start=disabled");

    public static bool DeleteService(ServiceEntry entry)
    {
        // Best-effort stop; deletion succeeds even if the service was already gone.
        StopService(entry);
        return RunSc($"delete \"{entry.Name}\"");
    }

    // ═══════════════════════════════════════════════════════
    //  Internals
    // ═══════════════════════════════════════════════════════

    private static bool RunSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns true only when we are *confident* the ImagePath points to a
    /// non-existent executable. False positives here are dangerous: flagging a
    /// legitimate system service as orphaned invites the user to delete it.
    /// We therefore resolve \SystemRoot\ and relative `system32\...` paths
    /// before the existence check, and err on the side of "not orphaned" when
    /// the path is ambiguous.
    /// </summary>
    private static bool IsOrphanedService(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;

        var path = StripArguments(imagePath).Trim();
        if (string.IsNullOrEmpty(path)) return false;

        path = ResolveNtPath(path);
        path = Environment.ExpandEnvironmentVariables(path);

        // Shared hosts and anything under %SystemRoot% are never considered orphaned.
        var lower = path.ToLowerInvariant();
        if (lower.Contains("svchost.exe") ||
            lower.StartsWith(SystemRoot.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith(@"c:\windows\", StringComparison.OrdinalIgnoreCase))
            return false;

        // Only flag fully-qualified paths we can actually check.
        if (!Path.IsPathRooted(path)) return false;

        return !File.Exists(path);
    }

    private static string StripArguments(string imagePath)
    {
        var trimmed = imagePath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 0 ? trimmed[1..end] : trimmed;
        }

        // Unquoted: truncate at first ".exe" occurrence so `cmd args` don't
        // confuse File.Exists. This is the common Windows pattern.
        var exeIdx = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0) return trimmed[..(exeIdx + 4)];

        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    /// <summary>
    /// Resolve NT-style paths the Service Control Manager writes, e.g.
    /// "\SystemRoot\System32\foo.exe" or "system32\foo.exe".
    /// </summary>
    private static string ResolveNtPath(string path)
    {
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(SystemRoot, path[@"\SystemRoot\".Length..]);

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            return path[4..];

        // SCM also accepts drivers as plain "system32\foo.sys" (relative to SystemRoot).
        if (!Path.IsPathRooted(path) &&
            (path.StartsWith("system32", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("syswow64", StringComparison.OrdinalIgnoreCase)))
        {
            return Path.Combine(SystemRoot, path);
        }

        return path;
    }
}
