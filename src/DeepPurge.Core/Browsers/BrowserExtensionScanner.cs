using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Browsers;

public enum ExtensionRiskLevel { Low, Medium, High, Critical }
public enum ExtensionPackageKind { Unknown, Directory, Xpi }

public class BrowserExtension : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Browser { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public string PackageRoot { get; set; } = "";
    public string Path { get; set; } = "";
    public ExtensionPackageKind PackageKind { get; set; }
    public bool IsSystemExtension { get; set; }
    public string InstallLocation { get; set; } = "";
    public bool IsRemovable { get; set; }
    public string RemovalReason { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public long SizeBytes { get; set; }

    public List<string> Permissions { get; set; } = new();
    public List<string> HostPermissions { get; set; } = new();
    public ExtensionRiskLevel RiskLevel { get; set; } = ExtensionRiskLevel.Low;
    public List<string> RiskLabels { get; set; } = new();

    public string RiskDisplay => RiskLevel.ToString();
    public string RemovalStatus => IsRemovable ? "Removable" : "Protected";
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

internal sealed record ExtensionRemovalResolution(
    bool IsRemovable,
    string PackagePath,
    string PackageRoot,
    ExtensionPackageKind PackageKind,
    string Reason);

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
                                    ProfilePath = profilePath,
                                    PackageRoot = extensionsDir,
                                    Path = versionDir,
                                    PackageKind = ExtensionPackageKind.Directory,
                                    IsRemovable = IsExactChromiumPackage(
                                        profilePath,
                                        extensionsDir,
                                        extId,
                                        versionDir),
                                    RemovalReason = "The package path is not an exact extension version directory.",
                                    IsEnabled = true,
                                    SizeBytes = GetDirectorySize(versionDir),
                                    Permissions = GetJsonStringArray(root, "permissions"),
                                    HostPermissions = GetJsonStringArray(root, "host_permissions"),
                                };

                                if (ext.Description.StartsWith("__MSG_"))
                                    ext.Description = "";
                                if (ext.IsRemovable) ext.RemovalReason = "";

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
                extensions.AddRange(ScanFirefoxProfile(profileDir));
            }
        }
        catch (Exception ex) { Log.Warn($"Enumerating Firefox profile directories: {ex.Message}"); }
    }

    internal static IReadOnlyList<BrowserExtension> ScanFirefoxProfile(string profileDir)
    {
        var extensions = new List<BrowserExtension>();
        var addonsFile = Path.Combine(profileDir, "addons.json");
        if (!File.Exists(addonsFile)) return extensions;

        try
        {
            var json = File.ReadAllText(addonsFile);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("addons", out var addons) ||
                addons.ValueKind != JsonValueKind.Array)
                return extensions;

            foreach (var addon in addons.EnumerateArray())
            {
                var id = GetJsonString(addon, "id") ?? "";
                var name = GetJsonString(addon, "name") ?? id;
                var type = GetJsonString(addon, "type") ?? "";

                // Only include actual extensions, not themes or plugins.
                if (!type.Equals("extension", StringComparison.OrdinalIgnoreCase))
                    continue;

                var location = GetJsonString(addon, "location") ?? "";
                var isSystem =
                    GetJsonBool(addon, "isSystem") ||
                    GetJsonBool(addon, "isBuiltin") ||
                    GetJsonBool(addon, "temporarilyInstalled");
                var resolution = ResolveFirefoxPackage(
                    profileDir,
                    id,
                    isSystem,
                    location);

                var ffExt = new BrowserExtension
                {
                    Id = id,
                    Name = name,
                    Version = GetJsonString(addon, "version") ?? "",
                    Description = GetJsonString(addon, "description") ?? "",
                    Browser = "Mozilla Firefox",
                    ProfileName = Path.GetFileName(profileDir),
                    ProfilePath = profileDir,
                    PackageRoot = resolution.PackageRoot,
                    Path = resolution.PackagePath,
                    PackageKind = resolution.PackageKind,
                    IsSystemExtension = isSystem,
                    InstallLocation = location,
                    IsRemovable = resolution.IsRemovable,
                    RemovalReason = resolution.Reason,
                    IsEnabled = GetJsonBool(addon, "active"),
                    SizeBytes = GetPackageSize(
                        resolution.PackagePath,
                        resolution.PackageKind),
                    Permissions = GetJsonStringArray(
                        addon,
                        "userPermissions",
                        "permissions"),
                    HostPermissions = GetJsonStringArray(
                        addon,
                        "userPermissions",
                        "origins"),
                };
                ExtensionRiskClassifier.Classify(ffExt);
                extensions.Add(ffExt);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Parsing Firefox addons.json in {profileDir}: {ex.Message}");
        }

        return extensions;
    }

    public static bool RemoveExtension(BrowserExtension ext)
        => TryRemoveExtension(ext, out _);

    public static bool TryRemoveExtension(BrowserExtension ext, out string reason)
    {
        try
        {
            if (ext == null)
            {
                reason = "No extension was selected.";
                return false;
            }
            if (!ext.IsRemovable)
            {
                reason = string.IsNullOrWhiteSpace(ext.RemovalReason)
                    ? "This extension is not removable from its recorded source."
                    : ext.RemovalReason;
                return false;
            }

            string packagePath;
            ExtensionPackageKind packageKind;
            if (ext.Browser.Equals("Mozilla Firefox", StringComparison.OrdinalIgnoreCase))
            {
                var resolution = ResolveFirefoxPackage(
                    ext.ProfilePath,
                    ext.Id,
                    ext.IsSystemExtension,
                    ext.InstallLocation);
                if (!resolution.IsRemovable)
                {
                    reason = resolution.Reason;
                    return false;
                }
                if (!PathsEqual(ext.PackageRoot, resolution.PackageRoot) ||
                    !PathsEqual(ext.Path, resolution.PackagePath) ||
                    ext.PackageKind != resolution.PackageKind)
                {
                    reason = "The Firefox package changed since it was scanned.";
                    return false;
                }

                packagePath = resolution.PackagePath;
                packageKind = resolution.PackageKind;
            }
            else
            {
                if (!TryResolveChromiumPackage(ext, out packagePath, out reason))
                    return false;
                packageKind = ExtensionPackageKind.Directory;
            }

            var removed = packageKind switch
            {
                ExtensionPackageKind.Directory =>
                    SafetyGuard.SafeDeleteDirectory(packagePath),
                ExtensionPackageKind.Xpi =>
                    SafetyGuard.SafeDeleteFile(packagePath),
                _ => false,
            };
            reason = removed
                ? ""
                : "The exact extension package could not be deleted; rescan after closing the browser.";
            return removed;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            Log.Warn($"Removing extension '{ext?.Name}' at {ext?.Path}: {ex.Message}");
            return false;
        }
    }

    internal static ExtensionRemovalResolution ResolveFirefoxPackage(
        string profilePath,
        string extensionId,
        bool isSystemExtension,
        string installLocation)
    {
        if (!IsValidExtensionId(extensionId))
        {
            return new(
                false,
                "",
                "",
                ExtensionPackageKind.Unknown,
                "The Firefox add-on ID is rooted, traversing, or not a single filename.");
        }

        if (isSystemExtension || IsFirefoxManagedLocation(installLocation))
        {
            return new(
                false,
                "",
                "",
                ExtensionPackageKind.Unknown,
                "Firefox manages this built-in, system, or temporary extension.");
        }

        string profile;
        string packageRoot;
        string directoryPackage;
        string xpiPackage;
        try
        {
            profile = SafetyGuard.NormalizePath(profilePath);
            packageRoot = SafetyGuard.NormalizePath(
                Path.Combine(profile, "extensions"));
            directoryPackage = SafetyGuard.NormalizePath(
                Path.Combine(packageRoot, extensionId));
            xpiPackage = SafetyGuard.NormalizePath(directoryPackage + ".xpi");
        }
        catch
        {
            return new(
                false,
                "",
                "",
                ExtensionPackageKind.Unknown,
                "The Firefox profile path is invalid.");
        }

        if (!string.Equals(
                Path.GetDirectoryName(packageRoot),
                profile,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetDirectoryName(directoryPackage),
                packageRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetDirectoryName(xpiPackage),
                packageRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                false,
                "",
                packageRoot,
                ExtensionPackageKind.Unknown,
                "The Firefox package escaped the profile extension root.");
        }

        if (!Directory.Exists(packageRoot))
        {
            return new(
                false,
                "",
                packageRoot,
                ExtensionPackageKind.Unknown,
                "The Firefox profile extension root is missing.");
        }

        var hasDirectory = Directory.Exists(directoryPackage);
        var hasXpi = File.Exists(xpiPackage);
        if (hasDirectory == hasXpi)
        {
            return new(
                false,
                "",
                packageRoot,
                ExtensionPackageKind.Unknown,
                hasDirectory
                    ? "Both directory and XPI packages exist for this add-on ID."
                    : "The add-on package is stale, missing, or managed outside this profile.");
        }

        var packagePath = hasDirectory ? directoryPackage : xpiPackage;
        var packageKind = hasDirectory
            ? ExtensionPackageKind.Directory
            : ExtensionPackageKind.Xpi;
        if (!TryValidateCanonicalPackage(
                packageRoot,
                packagePath,
                hasDirectory,
                out var reason))
        {
            return new(
                false,
                "",
                packageRoot,
                ExtensionPackageKind.Unknown,
                reason);
        }

        return new(true, packagePath, packageRoot, packageKind, "");
    }

    private static bool TryResolveChromiumPackage(
        BrowserExtension extension,
        out string packagePath,
        out string reason)
    {
        packagePath = "";
        if (extension.PackageKind != ExtensionPackageKind.Directory ||
            !IsExactChromiumPackage(
                extension.ProfilePath,
                extension.PackageRoot,
                extension.Id,
                extension.Path))
        {
            reason = "The Chromium package is not an exact extension version directory.";
            return false;
        }

        packagePath = SafetyGuard.NormalizePath(extension.Path);
        reason = "";
        return true;
    }

    private static bool IsExactChromiumPackage(
        string profilePath,
        string packageRoot,
        string extensionId,
        string packagePath)
    {
        if (!IsValidExtensionId(extensionId)) return false;

        try
        {
            var profile = SafetyGuard.NormalizePath(profilePath);
            var root = SafetyGuard.NormalizePath(packageRoot);
            var expectedRoot = SafetyGuard.NormalizePath(
                Path.Combine(profile, "Extensions"));
            if (!PathsEqual(root, expectedRoot)) return false;

            var idRoot = SafetyGuard.NormalizePath(
                Path.Combine(root, extensionId));
            var package = SafetyGuard.NormalizePath(packagePath);
            if (!string.Equals(
                    Path.GetDirectoryName(idRoot),
                    root,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetDirectoryName(package),
                    idRoot,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            return TryValidateCanonicalPackage(
                root,
                package,
                expectedDirectory: true,
                out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryValidateCanonicalPackage(
        string packageRoot,
        string packagePath,
        bool expectedDirectory,
        out string reason)
    {
        try
        {
            var rootScope = FileOperationScope.Exact(packageRoot);
            if (!HandleBoundFileOperations.TryOpenValidated(
                    packageRoot,
                    expectedDirectory: true,
                    rootScope,
                    HandleBoundFileOperations.ReadAttributes,
                    FileShare.Read | FileShare.Write | FileShare.Delete,
                    out var root,
                    out reason,
                    out _))
                return false;
            root!.Dispose();

            var packageScope = FileOperationScope.Tree(packageRoot);
            if (!HandleBoundFileOperations.TryOpenValidated(
                    packagePath,
                    expectedDirectory,
                    packageScope,
                    HandleBoundFileOperations.ReadAttributes,
                    FileShare.Read | FileShare.Write | FileShare.Delete,
                    out var package,
                    out reason,
                    out _))
                return false;
            package!.Dispose();

            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool IsValidExtensionId(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId) ||
            extensionId is "." or ".." ||
            Path.IsPathRooted(extensionId) ||
            extensionId.IndexOfAny(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0 ||
            extensionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            extensionId.Any(char.IsControl) ||
            extensionId.TrimEnd(' ', '.') != extensionId)
            return false;

        return Path.GetFileName(extensionId)
            .Equals(extensionId, StringComparison.Ordinal);
    }

    private static bool IsFirefoxManagedLocation(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return false;
        return !installLocation.Equals(
            "app-profile",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return SafetyGuard.NormalizePath(left)
                .Equals(
                    SafetyGuard.NormalizePath(right),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static long GetPackageSize(
        string packagePath,
        ExtensionPackageKind packageKind)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return 0;
        try
        {
            return packageKind switch
            {
                ExtensionPackageKind.Directory => GetDirectorySize(packagePath),
                ExtensionPackageKind.Xpi => new FileInfo(packagePath).Length,
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    private static string? GetJsonString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() : null;
    }

    private static bool GetJsonBool(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) &&
           value.ValueKind is JsonValueKind.True;

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
