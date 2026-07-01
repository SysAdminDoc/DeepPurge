using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Browsers;

public enum ExtensionRiskLevel { Low, Medium, High, Critical }

public class BrowserExtension : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Browser { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public long SizeBytes { get; set; }

    public List<string> Permissions { get; set; } = new();
    public List<string> HostPermissions { get; set; } = new();
    public ExtensionRiskLevel RiskLevel { get; set; } = ExtensionRiskLevel.Low;
    public List<string> RiskLabels { get; set; } = new();

    public string RiskDisplay => RiskLevel.ToString();
    public string RiskLabelsDisplay => RiskLabels.Count > 0 ? string.Join(", ", RiskLabels) : "";
    public string PermissionsDisplay => Permissions.Count + HostPermissions.Count > 0
        ? string.Join(", ", Permissions.Concat(HostPermissions).Take(5))
          + (Permissions.Count + HostPermissions.Count > 5 ? $" (+{Permissions.Count + HostPermissions.Count - 5})" : "")
        : "";

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string SizeDisplay => SizeBytes <= 0 ? "" : Diagnostics.SizeFormatter.Format(SizeBytes);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ExtensionRiskClassifier
{
    private static readonly HashSet<string> SensitiveApis = new(StringComparer.OrdinalIgnoreCase)
    {
        "nativeMessaging", "debugger", "proxy", "webRequestBlocking",
        "management", "privacy", "browsingData", "downloads",
        "history", "bookmarks", "topSites", "sessions", "cookies",
    };

    private static readonly HashSet<string> BackgroundApis = new(StringComparer.OrdinalIgnoreCase)
    {
        "background", "webRequest", "webNavigation", "declarativeNetRequest",
    };

    public static void Classify(BrowserExtension ext)
    {
        var labels = new List<string>();
        var risk = ExtensionRiskLevel.Low;

        var allPerms = ext.Permissions.Concat(ext.HostPermissions).ToList();

        if (HasBroadHostAccess(ext.HostPermissions) || HasBroadHostAccess(ext.Permissions))
        {
            labels.Add("Broad host access");
            risk = MaxRisk(risk, ExtensionRiskLevel.High);
        }

        var sensitiveHits = allPerms.Where(p => SensitiveApis.Contains(p)).ToList();
        if (sensitiveHits.Count > 0)
        {
            labels.Add($"Sensitive API ({string.Join(", ", sensitiveHits.Take(3))})");
            risk = MaxRisk(risk, ExtensionRiskLevel.High);
        }

        if (allPerms.Any(p => p.Equals("nativeMessaging", StringComparison.OrdinalIgnoreCase)))
        {
            labels.Add("Native messaging");
            risk = MaxRisk(risk, ExtensionRiskLevel.Critical);
        }

        if (allPerms.Any(p => BackgroundApis.Contains(p)))
        {
            labels.Add("Background activity");
            risk = MaxRisk(risk, ExtensionRiskLevel.Medium);
        }

        if (allPerms.Any(p => p.Equals("tabs", StringComparison.OrdinalIgnoreCase) ||
                              p.Equals("activeTab", StringComparison.OrdinalIgnoreCase)))
        {
            risk = MaxRisk(risk, ExtensionRiskLevel.Low);
        }

        ext.RiskLevel = risk;
        ext.RiskLabels = labels;
    }

    private static bool HasBroadHostAccess(IEnumerable<string> perms)
        => perms.Any(p => p is "<all_urls>" or "*://*/*" or "http://*/*" or "https://*/*");

    private static ExtensionRiskLevel MaxRisk(ExtensionRiskLevel a, ExtensionRiskLevel b)
        => a > b ? a : b;
}

public static class BrowserExtensionScanner
{
    public static List<BrowserExtension> GetAllExtensions()
    {
        var extensions = new List<BrowserExtension>();

        ScanChromiumExtensions(extensions, "Google Chrome",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data"));

        ScanChromiumExtensions(extensions, "Microsoft Edge",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "User Data"));

        ScanChromiumExtensions(extensions, "Brave",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BraveSoftware", "Brave-Browser", "User Data"));

        ScanChromiumExtensions(extensions, "Opera",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Opera Software", "Opera Stable"));

        ScanChromiumExtensions(extensions, "Vivaldi",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vivaldi", "User Data"));

        ScanFirefoxExtensions(extensions);

        return extensions;
    }

    private static void ScanChromiumExtensions(List<BrowserExtension> extensions, string browserName, string userDataPath)
    {
        if (!Directory.Exists(userDataPath)) return;

        // Find all profile directories (Default, Profile 1, Profile 2, etc.)
        var profiles = new List<string>();
        var defaultProfile = Path.Combine(userDataPath, "Default");
        if (Directory.Exists(defaultProfile)) profiles.Add(defaultProfile);

        try
        {
            foreach (var dir in Directory.GetDirectories(userDataPath, "Profile *"))
                profiles.Add(dir);
        }
        catch (Exception ex) { Log.Warn($"Enumerating browser profiles for {browserName}: {ex.Message}"); }

        foreach (var profilePath in profiles)
        {
            var profileName = Path.GetFileName(profilePath);
            var extensionsDir = Path.Combine(profilePath, "Extensions");
            if (!Directory.Exists(extensionsDir)) continue;

            try
            {
                foreach (var extDir in Directory.GetDirectories(extensionsDir))
                {
                    var extId = Path.GetFileName(extDir);
                    // Each extension has version subdirectories
                    try
                    {
                        foreach (var versionDir in Directory.GetDirectories(extDir))
                        {
                            var manifestPath = Path.Combine(versionDir, "manifest.json");
                            if (!File.Exists(manifestPath)) continue;

                            try
                            {
                                var json = File.ReadAllText(manifestPath);
                                using var doc = JsonDocument.Parse(json);
                                var root = doc.RootElement;

                                var name = GetJsonString(root, "name") ?? extId;
                                // Chrome built-in extensions use __MSG_ format
                                if (name.StartsWith("__MSG_")) name = name.Replace("__MSG_", "").Replace("__", "");

                                var ext = new BrowserExtension
                                {
                                    Id = extId,
                                    Name = name,
                                    Version = GetJsonString(root, "version") ?? "",
                                    Description = GetJsonString(root, "description") ?? "",
                                    Browser = browserName,
                                    ProfileName = profileName,
                                    Path = versionDir,
                                    IsEnabled = true,
                                    SizeBytes = GetDirectorySize(versionDir),
                                    Permissions = GetJsonStringArray(root, "permissions"),
                                    HostPermissions = GetJsonStringArray(root, "host_permissions"),
                                };

                                if (ext.Description.StartsWith("__MSG_"))
                                    ext.Description = "";

                                if (ext.HostPermissions.Count == 0 && root.TryGetProperty("permissions", out var permsEl))
                                {
                                    var urlPerms = ext.Permissions.Where(p => p.Contains("://") || p == "<all_urls>").ToList();
                                    foreach (var u in urlPerms) { ext.Permissions.Remove(u); ext.HostPermissions.Add(u); }
                                }

                                ExtensionRiskClassifier.Classify(ext);
                                extensions.Add(ext);
                            }
                            catch (Exception ex) { Log.Warn($"Parsing extension manifest in {versionDir}: {ex.Message}"); }
                        }
                    }
                    catch (Exception ex) { Log.Warn($"Enumerating extension versions for {extId}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log.Warn($"Scanning {browserName} extensions in {profilePath}: {ex.Message}"); }

            // Check preferences for disabled extensions
            var prefsPath = Path.Combine(profilePath, "Preferences");
            if (File.Exists(prefsPath))
            {
                try
                {
                    var prefsJson = File.ReadAllText(prefsPath);
                    using var prefsDoc = JsonDocument.Parse(prefsJson);
                    if (prefsDoc.RootElement.TryGetProperty("extensions", out var extNode) &&
                        extNode.TryGetProperty("settings", out var settings))
                    {
                        foreach (var prop in settings.EnumerateObject())
                        {
                            if (prop.Value.TryGetProperty("state", out var state))
                            {
                                var isDisabled = state.GetInt32() == 0;
                                var match = extensions.FirstOrDefault(e =>
                                    e.Id == prop.Name && e.Browser == browserName && e.ProfileName == profileName);
                                if (match != null) match.IsEnabled = !isDisabled;
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Warn($"Reading {browserName} preferences in {profilePath}: {ex.Message}"); }
            }
        }
    }

    private static void ScanFirefoxExtensions(List<BrowserExtension> extensions)
    {
        var firefoxPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mozilla", "Firefox", "Profiles");

        if (!Directory.Exists(firefoxPath)) return;

        try
        {
            foreach (var profileDir in Directory.GetDirectories(firefoxPath))
            {
                var profileName = Path.GetFileName(profileDir);
                var addonsFile = Path.Combine(profileDir, "addons.json");
                if (!File.Exists(addonsFile)) continue;

                try
                {
                    var json = File.ReadAllText(addonsFile);
                    using var doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.TryGetProperty("addons", out var addons)) continue;

                    foreach (var addon in addons.EnumerateArray())
                    {
                        var id = GetJsonString(addon, "id") ?? "";
                        var name = GetJsonString(addon, "name") ?? id;
                        var type = GetJsonString(addon, "type") ?? "";

                        // Only include actual extensions, not themes or plugins
                        if (type != "extension") continue;

                        var extPath = Path.Combine(profileDir, "extensions", id);
                        var xpiPath = extPath + ".xpi";

                        var ffExt = new BrowserExtension
                        {
                            Id = id,
                            Name = name,
                            Version = GetJsonString(addon, "version") ?? "",
                            Description = GetJsonString(addon, "description") ?? "",
                            Browser = "Mozilla Firefox",
                            ProfileName = profileName,
                            Path = Directory.Exists(extPath) ? extPath : (File.Exists(xpiPath) ? xpiPath : profileDir),
                            IsEnabled = addon.TryGetProperty("active", out var active) && active.GetBoolean(),
                            SizeBytes = Directory.Exists(extPath) ? GetDirectorySize(extPath) :
                                       File.Exists(xpiPath) ? new FileInfo(xpiPath).Length : 0,
                            Permissions = GetJsonStringArray(addon, "userPermissions", "permissions"),
                            HostPermissions = GetJsonStringArray(addon, "userPermissions", "origins"),
                        };
                        ExtensionRiskClassifier.Classify(ffExt);
                        extensions.Add(ffExt);
                    }
                }
                catch (Exception ex) { Log.Warn($"Parsing Firefox addons.json in {profileDir}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Enumerating Firefox profile directories: {ex.Message}"); }
    }

    public static bool RemoveExtension(BrowserExtension ext)
    {
        try
        {
            if (Directory.Exists(ext.Path))
                return Safety.SafetyGuard.SafeDeleteDirectory(ext.Path);
            if (File.Exists(ext.Path))
                return Safety.SafetyGuard.SafeDeleteFile(ext.Path);
        }
        catch (Exception ex) { Log.Warn($"Removing extension '{ext.Name}' at {ext.Path}: {ex.Message}"); }
        return false;
    }

    private static string? GetJsonString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() : null;
    }

    private static List<string> GetJsonStringArray(JsonElement element, string property)
    {
        var result = new List<string>();
        if (!element.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                result.Add(s);
        }
        return result;
    }

    private static List<string> GetJsonStringArray(JsonElement element, string parent, string child)
    {
        if (!element.TryGetProperty(parent, out var parentEl) || parentEl.ValueKind != JsonValueKind.Object)
            return new();
        return GetJsonStringArray(parentEl, child);
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(fi => { try { return fi.Length; } catch (Exception ex) { Log.Warn($"Reading file size for {fi.FullName}: {ex.Message}"); return 0; } });
        }
        catch (Exception ex) { Log.Warn($"Calculating directory size for {path}: {ex.Message}"); return 0; }
    }
}
