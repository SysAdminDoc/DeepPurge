using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace DeepPurge.Core.Services;

public class ServiceEntry
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string StartType { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsOrphaned { get; set; }
    public bool IsSelected { get; set; }
    public string StatusDisplay => IsOrphaned ? "Orphaned" : Status;
}

public static class ServiceScanner
{
    public static List<ServiceEntry> GetAllServices(bool orphanedOnly = false)
    {
        var entries = new List<ServiceEntry>();
        try
        {
            using var servicesKey = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return entries;

            var runningServices = ServiceController.GetServices()
                .ToDictionary(s => s.ServiceName, s => s, StringComparer.OrdinalIgnoreCase);

            foreach (var name in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var svc = servicesKey.OpenSubKey(name);
                    if (svc == null) continue;

                    var typeVal = svc.GetValue("Type");
                    if (typeVal == null) continue;
                    var type = Convert.ToInt32(typeVal);
                    // Filter to Win32 services (type 16=own process, 32=share process, 272/288=interactive)
                    if (type != 16 && type != 32 && type != 272 && type != 288) continue;

                    var imagePath = svc.GetValue("ImagePath")?.ToString() ?? "";
                    var displayName = svc.GetValue("DisplayName")?.ToString() ?? name;
                    var description = svc.GetValue("Description")?.ToString() ?? "";
                    var startType = svc.GetValue("Start")?.ToString() ?? "";

                    var startTypeDisplay = startType switch
                    {
                        "0" => "Boot",
                        "1" => "System",
                        "2" => "Automatic",
                        "3" => "Manual",
                        "4" => "Disabled",
                        _ => startType
                    };

                    var status = "Stopped";
                    if (runningServices.TryGetValue(name, out var sc))
                    {
                        status = sc.Status.ToString();
                    }

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
                        IsSelected = isOrphaned,
                    });
                }
                catch { }
            }
        }
        catch { }

        return entries.OrderByDescending(e => e.IsOrphaned).ThenBy(e => e.DisplayName).ToList();
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

    public static bool DisableService(ServiceEntry entry)
    {
        return RunSc($"config \"{entry.Name}\" start=disabled");
    }

    public static bool DeleteService(ServiceEntry entry)
    {
        StopService(entry);
        return RunSc($"delete \"{entry.Name}\"");
    }

    private static bool RunSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe", Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool IsOrphanedService(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return false;

        var path = imagePath.Trim();
        // Remove quotes
        if (path.StartsWith('"'))
        {
            var end = path.IndexOf('"', 1);
            if (end > 0) path = path[1..end];
        }
        else
        {
            // Handle paths with arguments
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".exe ", StringComparison.OrdinalIgnoreCase))
            {
                var exeIdx = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIdx > 0) path = path[..(exeIdx + 4)];
            }
        }

        path = Environment.ExpandEnvironmentVariables(path);

        // System paths are never orphaned
        if (path.StartsWith(@"C:\Windows\system32", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"C:\Windows\SysWOW64", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        // Check if the executable exists
        return !string.IsNullOrEmpty(path) && path.Contains('\\') && !File.Exists(path);
    }
}
