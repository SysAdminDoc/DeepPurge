using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepPurge.Core.Models;

public class WindowsApp : INotifyPropertyChanged
{
    private bool _isSelected;

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

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string SizeDisplay
    {
        get
        {
            if (SizeBytes <= 0) return "";
            double kb = SizeBytes / 1024.0;
            if (kb < 1) return $"{SizeBytes} B";
            if (kb < 1024) return $"{kb:F0} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }
    }

    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : PackageFamilyName;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class WindowsAppManager
{
    /// <summary>
    /// PackageFullName uses a tightly restricted char set (letters, digits,
    /// dots, underscores, hyphens, tildes). Anything else is rejected so we
    /// never hand an attacker-controllable string to PowerShell.
    /// </summary>
    private static readonly Regex ValidPackageFullName =
        new(@"^[A-Za-z0-9][A-Za-z0-9._\-~+]*$", RegexOptions.Compiled);

    public static async Task<List<WindowsApp>> GetInstalledAppsAsync(bool includeFrameworks = false)
    {
        var apps = new List<WindowsApp>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-AppxPackage | " +
                    "Select-Object Name, PackageFullName, PackageFamilyName, Publisher, Version, " +
                    "InstallLocation, Architecture, IsFramework, IsResourcePackage, NonRemovable | " +
                    "ConvertTo-Json -Compress\"",
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

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : (IEnumerable<JsonElement>)new[] { root };

            foreach (var item in items)
            {
                var isFramework = GetBool(item, "IsFramework");
                var isResource = GetBool(item, "IsResourcePackage");
                if (!includeFrameworks && (isFramework || isResource)) continue;

                var installLoc = GetStr(item, "InstallLocation");
                var size = ComputeInstallSize(installLoc);

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
        catch { /* best-effort */ }

        return apps.OrderBy(a => a.DisplayName).ToList();
    }

    public static Task<bool> RemoveAppAsync(WindowsApp app) =>
        RunRemoveAsync(app, allUsers: false);

    public static Task<bool> RemoveAppForAllUsersAsync(WindowsApp app) =>
        RunRemoveAsync(app, allUsers: true);

    // ═══════════════════════════════════════════════════════
    //  Internals
    // ═══════════════════════════════════════════════════════

    private static async Task<bool> RunRemoveAsync(WindowsApp app, bool allUsers)
    {
        if (string.IsNullOrWhiteSpace(app.PackageFullName)) return false;
        if (!ValidPackageFullName.IsMatch(app.PackageFullName)) return false;
        if (app.IsNonRemovable) return false;

        var allUsersFlag = allUsers ? " -AllUsers" : "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"" +
                    $"Remove-AppxPackage -Package '{app.PackageFullName}'{allUsersFlag} -ErrorAction Stop\"",
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

    private static long ComputeInstallSize(string installLoc)
    {
        if (string.IsNullOrEmpty(installLoc) || !Directory.Exists(installLoc)) return 0;
        try
        {
            return new DirectoryInfo(installLoc)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    private static string CleanAppName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        // "Microsoft.WindowsCalculator" → "Windows Calculator"
        name = Regex.Replace(name, @"^Microsoft\.", "");
        name = Regex.Replace(name, @"([a-z])([A-Z])", "$1 $2");
        return name;
    }

    private static string CleanPublisher(string publisher)
    {
        if (string.IsNullOrEmpty(publisher)) return publisher;
        var match = Regex.Match(publisher, @"CN=([^,]+)");
        return match.Success ? match.Groups[1].Value : publisher;
    }

    private static string GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static bool GetBool(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
            _ => false,
        };
    }
}
