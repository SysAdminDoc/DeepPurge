using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DeepPurge.Core.Models;

public class WindowsApp
{
    public string Name { get; set; } = "";
    public string PackageFullName { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string Architecture { get; set; } = "";
    public bool IsFramework { get; set; }
    public bool IsResource { get; set; }
    public bool IsNonRemovable { get; set; }
    public long SizeBytes { get; set; }

    public string SizeDisplay
    {
        get
        {
            if (SizeBytes <= 0) return "";
            double mb = SizeBytes / 1024.0 / 1024.0;
            if (mb < 1) return $"{SizeBytes / 1024.0:F0} KB";
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }
    }

    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : PackageFamilyName;
}

public static class WindowsAppManager
{
    public static async Task<List<WindowsApp>> GetInstalledAppsAsync(bool includeFrameworks = false)
    {
        var apps = new List<WindowsApp>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-AppxPackage | Select-Object Name, PackageFullName, PackageFamilyName, Publisher, Version, InstallLocation, Architecture, IsFramework, IsResourcePackage, NonRemovable | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return apps;

            var json = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (string.IsNullOrWhiteSpace(json)) return apps;

            // Parse the JSON array
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var items = root.ValueKind == System.Text.Json.JsonValueKind.Array
                ? root.EnumerateArray()
                : new[] { root }.AsEnumerable();

            foreach (var item in items)
            {
                var isFramework = GetBool(item, "IsFramework");
                var isResource = GetBool(item, "IsResourcePackage");

                if (!includeFrameworks && (isFramework || isResource))
                    continue;

                var installLoc = GetStr(item, "InstallLocation");
                long size = 0;
                if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
                {
                    try
                    {
                        size = new DirectoryInfo(installLoc)
                            .EnumerateFiles("*", SearchOption.AllDirectories)
                            .Sum(f => { try { return f.Length; } catch { return 0L; } });
                    }
                    catch { }
                }

                apps.Add(new WindowsApp
                {
                    Name = CleanAppName(GetStr(item, "Name")),
                    PackageFullName = GetStr(item, "PackageFullName"),
                    PackageFamilyName = GetStr(item, "PackageFamilyName"),
                    Publisher = CleanPublisher(GetStr(item, "Publisher")),
                    Version = GetStr(item, "Version"),
                    InstallLocation = installLoc,
                    Architecture = GetStr(item, "Architecture"),
                    IsFramework = isFramework,
                    IsResource = isResource,
                    IsNonRemovable = GetBool(item, "NonRemovable"),
                    SizeBytes = size,
                });
            }
        }
        catch { }

        return apps.OrderBy(a => a.DisplayName).ToList();
    }

    public static async Task<bool> RemoveAppAsync(WindowsApp app)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Remove-AppxPackage -Package '{app.PackageFullName}'\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    public static async Task<bool> RemoveAppForAllUsersAsync(WindowsApp app)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Remove-AppxPackage -Package '{app.PackageFullName}' -AllUsers\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string CleanAppName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        // Convert "Microsoft.WindowsCalculator" to "Windows Calculator"
        name = Regex.Replace(name, @"^Microsoft\.", "");
        name = Regex.Replace(name, @"([a-z])([A-Z])", "$1 $2");
        return name;
    }

    private static string CleanPublisher(string publisher)
    {
        if (string.IsNullOrEmpty(publisher)) return publisher;
        // Extract CN from certificate string
        var match = Regex.Match(publisher, @"CN=([^,]+)");
        return match.Success ? match.Groups[1].Value : publisher;
    }

    private static string GetStr(System.Text.Json.JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() ?? "" : "";
    }

    private static bool GetBool(System.Text.Json.JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Number => v.GetInt32() != 0,
            _ => false
        };
    }
}
