using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DeepPurge.Core.Browsers;

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
            if (kb < 1024) return $"{kb:F0} KB";
            return $"{kb / 1024.0:F1} MB";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
        catch { }

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
                                };

                                if (ext.Description.StartsWith("__MSG_"))
                                    ext.Description = "";

                                extensions.Add(ext);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }

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
                catch { }
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

                        extensions.Add(new BrowserExtension
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
                        });
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public static bool RemoveExtension(BrowserExtension ext)
    {
        try
        {
            if (Directory.Exists(ext.Path))
            {
                Directory.Delete(ext.Path, true);
                return true;
            }
            if (File.Exists(ext.Path))
            {
                File.Delete(ext.Path);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string? GetJsonString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() : null;
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(fi => { try { return fi.Length; } catch { return 0; } });
        }
        catch { return 0; }
    }
}
