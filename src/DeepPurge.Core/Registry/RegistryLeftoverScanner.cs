using global::Microsoft.Win32;
using DeepPurge.Core.Models;

namespace DeepPurge.Core.Registry;

public class RegistryLeftoverScanner
{
    private readonly HashSet<string> _exclusions;
    private readonly List<string> _searchTerms = new();

    // System-critical keys that must NEVER be deleted
    private static readonly HashSet<string> SystemProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        @"HKLM\SOFTWARE\Microsoft\Windows",
        @"HKLM\SOFTWARE\Microsoft\Windows NT",
        @"HKLM\SOFTWARE\Classes",
        @"HKLM\SYSTEM\CurrentControlSet",
        @"HKLM\SOFTWARE\Policies",
        @"HKCU\SOFTWARE\Microsoft\Windows",
        @"HKCU\SOFTWARE\Policies",
    };

    // Common shared component publishers to be cautious with
    private static readonly HashSet<string> SharedPublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Microsoft Corporation", "Intel", "Intel Corporation",
        "NVIDIA", "NVIDIA Corporation", "AMD", "Advanced Micro Devices",
        "Realtek", "Qualcomm", "Broadcom", "Google", "Google LLC",
        "Apple", "Apple Inc.", "Oracle", "Oracle Corporation",
    };

    // Words too generic for standalone registry key matching
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop", "windows", "system", "program", "programs",
        "application", "software", "portable", "update", "updater",
        "service", "driver", "runtime", "framework", "module",
        "app", "tool", "tools", "utility", "free", "pro", "plus",
        "setup", "install", "installer", "uninstall", "launcher",
        "manager", "editor", "viewer", "player", "reader", "client",
        "server", "host", "helper", "agent", "studio", "suite",
        "version", "edition", "release", "build", "x86", "x64",
        "data", "files", "temp", "cache", "config", "settings",
        "the", "for", "and", "net", "new", "web", "all",
    };

    public RegistryLeftoverScanner(HashSet<string>? customExclusions = null)
    {
        _exclusions = customExclusions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public List<LeftoverItem> ScanForLeftovers(InstalledProgram program, ScanMode mode)
    {
        var leftovers = new List<LeftoverItem>();
        BuildSearchTerms(program);

        // 1. Remove the uninstall registry key itself
        ScanUninstallKey(program, leftovers);

        // 2. Scan application-specific software keys
        ScanSoftwareKeys(leftovers, mode);

        // 3. Scan COM/ActiveX registrations
        if (mode >= ScanMode.Moderate)
            ScanComRegistrations(program, leftovers);

        // 4. Scan App Paths
        ScanAppPaths(program, leftovers);

        // 5. Scan file associations
        if (mode >= ScanMode.Moderate)
            ScanFileAssociations(program, leftovers);

        // 6. Scan startup entries
        ScanStartupEntries(leftovers);

        // 7. Scan services
        if (mode >= ScanMode.Moderate)
            ScanServices(program, leftovers);

        // 8. Scan firewall rules
        if (mode >= ScanMode.Advanced)
            ScanFirewallRules(program, leftovers);

        // 9. Scan MUI cache
        if (mode >= ScanMode.Moderate)
            ScanMuiCache(program, leftovers);

        // 10. Scan shell extensions
        if (mode >= ScanMode.Advanced)
            ScanShellExtensions(program, leftovers);

        // 11. Scan AppCompat flags
        if (mode >= ScanMode.Advanced)
            ScanAppCompatFlags(program, leftovers);

        // 12. Scan MSI installer data
        if (program.IsWindowsInstaller)
            ScanMsiData(program, leftovers);

        return leftovers;
    }

    private void BuildSearchTerms(InstalledProgram program)
    {
        _searchTerms.Clear();

        if (!string.IsNullOrEmpty(program.DisplayName))
            _searchTerms.Add(program.DisplayName);

        if (!string.IsNullOrEmpty(program.Publisher) && !SharedPublishers.Contains(program.Publisher)
            && !StopWords.Contains(program.Publisher))
            _searchTerms.Add(program.Publisher);

        if (!string.IsNullOrEmpty(program.RegistryKeyName) && program.RegistryKeyName.Length > 3
            && !program.RegistryKeyName.StartsWith('{') && !StopWords.Contains(program.RegistryKeyName))
            _searchTerms.Add(program.RegistryKeyName);

        // Extract exe name from uninstall string
        var exeName = ExtractExeName(program.UninstallString);
        if (!string.IsNullOrEmpty(exeName) && exeName.Length > 3)
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(exeName);
            if (!string.IsNullOrEmpty(nameWithoutExt) && nameWithoutExt.Length > 3 && !StopWords.Contains(nameWithoutExt))
                _searchTerms.Add(nameWithoutExt);
        }

        // Extract folder name from install location
        if (!string.IsNullOrEmpty(program.InstallLocation))
        {
            var folderName = Path.GetFileName(program.InstallLocation.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(folderName) && folderName.Length > 2 && !StopWords.Contains(folderName))
                _searchTerms.Add(folderName);
        }

        // Remove remaining generic terms
        _searchTerms.RemoveAll(t => t.Length <= 2 || StopWords.Contains(t));
    }

    private bool MatchesProgram(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return _searchTerms.Any(term =>
            text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private LeftoverConfidence ClassifyConfidence(string keyPath)
    {
        if (_exclusions.Contains(keyPath)) return LeftoverConfidence.Risky;
        if (SystemProtectedKeys.Any(sp => keyPath.StartsWith(sp, StringComparison.OrdinalIgnoreCase)))
            return LeftoverConfidence.Risky;

        // High confidence if it's directly under the program's own key
        if (_searchTerms.Any(t => keyPath.EndsWith($@"\{t}", StringComparison.OrdinalIgnoreCase)))
            return LeftoverConfidence.Safe;

        return LeftoverConfidence.Moderate;
    }

    // --- Individual scan methods ---

    private void ScanUninstallKey(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        var fullPath = program.RegistryPath;
        if (!string.IsNullOrEmpty(fullPath))
        {
            leftovers.Add(new LeftoverItem
            {
                Path = fullPath,
                DisplayPath = fullPath,
                Type = LeftoverType.RegistryKey,
                Confidence = LeftoverConfidence.Safe,
                IsSelected = true,
                Details = "Uninstall registry entry"
            });
        }
    }

    private void ScanSoftwareKeys(List<LeftoverItem> leftovers, ScanMode mode)
    {
        // Search HKLM\SOFTWARE and HKCU\SOFTWARE for keys matching the program
        var roots = new (RegistryKey Hive, string Prefix, string[] Paths)[]
        {
            (global::Microsoft.Win32.Registry.LocalMachine, "HKLM", new[] { "SOFTWARE", @"SOFTWARE\WOW6432Node" }),
            (global::Microsoft.Win32.Registry.CurrentUser, "HKCU", new[] { "SOFTWARE" }),
        };

        foreach (var (hive, prefix, paths) in roots)
        {
            foreach (var basePath in paths)
            {
                try
                {
                    using var softwareKey = hive.OpenSubKey(basePath);
                    if (softwareKey == null) continue;

                    foreach (var subName in softwareKey.GetSubKeyNames())
                    {
                        if (MatchesProgram(subName))
                        {
                            // Skip the main Microsoft/Windows keys
                            if (subName.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                                subName.Equals("Classes", StringComparison.OrdinalIgnoreCase) ||
                                subName.Equals("Policies", StringComparison.OrdinalIgnoreCase) ||
                                subName.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var fullPath = $@"{prefix}\{basePath}\{subName}";
                            var confidence = ClassifyConfidence(fullPath);

                            leftovers.Add(new LeftoverItem
                            {
                                Path = fullPath,
                                DisplayPath = fullPath,
                                Type = LeftoverType.RegistryKey,
                                Confidence = confidence,
                                IsSelected = confidence == LeftoverConfidence.Safe,
                                Details = "Application settings key"
                            });
                        }

                        // Second level: check Publisher\AppName pattern
                        if (mode >= ScanMode.Moderate)
                        {
                            try
                            {
                                using var pubKey = softwareKey.OpenSubKey(subName);
                                if (pubKey == null) continue;

                                foreach (var appName in pubKey.GetSubKeyNames())
                                {
                                    if (MatchesProgram(appName))
                                    {
                                        var fullPath = $@"{prefix}\{basePath}\{subName}\{appName}";
                                        var confidence = ClassifyConfidence(fullPath);
                                        leftovers.Add(new LeftoverItem
                                        {
                                            Path = fullPath,
                                            DisplayPath = fullPath,
                                            Type = LeftoverType.RegistryKey,
                                            Confidence = confidence,
                                            IsSelected = confidence == LeftoverConfidence.Safe,
                                            Details = $"Application key under {subName}"
                                        });
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }
    }

    private void ScanComRegistrations(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        if (string.IsNullOrEmpty(program.InstallLocation)) return;
        var installPath = program.InstallLocation.ToLowerInvariant();

        try
        {
            using var clsidKey = global::Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("CLSID");
            if (clsidKey == null) return;

            foreach (var guid in clsidKey.GetSubKeyNames())
            {
                try
                {
                    using var entry = clsidKey.OpenSubKey($@"{guid}\InprocServer32");
                    var dllPath = entry?.GetValue(null) as string ?? "";
                    if (!string.IsNullOrEmpty(dllPath) && dllPath.ToLowerInvariant().Contains(installPath))
                    {
                        leftovers.Add(new LeftoverItem
                        {
                            Path = $@"HKCR\CLSID\{guid}",
                            DisplayPath = $@"HKCR\CLSID\{guid}",
                            Type = LeftoverType.RegistryKey,
                            Confidence = LeftoverConfidence.Moderate,
                            IsSelected = false,
                            Details = $"COM registration pointing to: {dllPath}"
                        });
                    }

                    using var localServer = clsidKey.OpenSubKey($@"{guid}\LocalServer32");
                    var exePath = localServer?.GetValue(null) as string ?? "";
                    if (!string.IsNullOrEmpty(exePath) && exePath.ToLowerInvariant().Contains(installPath))
                    {
                        leftovers.Add(new LeftoverItem
                        {
                            Path = $@"HKCR\CLSID\{guid}",
                            DisplayPath = $@"HKCR\CLSID\{guid}",
                            Type = LeftoverType.RegistryKey,
                            Confidence = LeftoverConfidence.Moderate,
                            IsSelected = false,
                            Details = $"COM server pointing to: {exePath}"
                        });
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void ScanAppPaths(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        var paths = new[] {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", global::Microsoft.Win32.Registry.LocalMachine, "HKLM"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", global::Microsoft.Win32.Registry.CurrentUser, "HKCU"),
        };

        foreach (var (regPath, hive, prefix) in paths)
        {
            try
            {
                using var appPathsKey = hive.OpenSubKey(regPath);
                if (appPathsKey == null) continue;

                foreach (var exeName in appPathsKey.GetSubKeyNames())
                {
                    if (MatchesProgram(Path.GetFileNameWithoutExtension(exeName)))
                    {
                        var fullPath = $@"{prefix}\{regPath}\{exeName}";
                        leftovers.Add(new LeftoverItem
                        {
                            Path = fullPath,
                            DisplayPath = fullPath,
                            Type = LeftoverType.RegistryKey,
                            Confidence = LeftoverConfidence.Safe,
                            IsSelected = true,
                            Details = "App Paths registration"
                        });
                    }
                }
            }
            catch { }
        }
    }

    private void ScanFileAssociations(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        if (string.IsNullOrEmpty(program.InstallLocation)) return;
        var installPath = program.InstallLocation.ToLowerInvariant();

        try
        {
            using var classesRoot = global::Microsoft.Win32.Registry.ClassesRoot;
            foreach (var name in classesRoot.GetSubKeyNames())
            {
                if (!name.StartsWith(".")) continue;
                try
                {
                    using var extKey = classesRoot.OpenSubKey(name);
                    var progId = extKey?.GetValue(null) as string;
                    if (string.IsNullOrEmpty(progId) || !MatchesProgram(progId)) continue;

                    // Check if the open command points to our install location
                    using var cmdKey = classesRoot.OpenSubKey($@"{progId}\shell\open\command");
                    var cmdValue = cmdKey?.GetValue(null) as string ?? "";
                    if (cmdValue.ToLowerInvariant().Contains(installPath))
                    {
                        leftovers.Add(new LeftoverItem
                        {
                            Path = $@"HKCR\{progId}",
                            DisplayPath = $@"HKCR\{progId} (handles {name})",
                            Type = LeftoverType.RegistryKey,
                            Confidence = LeftoverConfidence.Moderate,
                            IsSelected = false,
                            Details = $"File association: {name} -> {cmdValue}"
                        });
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void ScanStartupEntries(List<LeftoverItem> leftovers)
    {
        var runKeys = new[] {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", global::Microsoft.Win32.Registry.LocalMachine, "HKLM"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", global::Microsoft.Win32.Registry.LocalMachine, "HKLM"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", global::Microsoft.Win32.Registry.CurrentUser, "HKCU"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", global::Microsoft.Win32.Registry.CurrentUser, "HKCU"),
        };

        foreach (var (regPath, hive, prefix) in runKeys)
        {
            try
            {
                using var runKey = hive.OpenSubKey(regPath);
                if (runKey == null) continue;

                foreach (var valueName in runKey.GetValueNames())
                {
                    var valueData = runKey.GetValue(valueName) as string ?? "";
                    if (MatchesProgram(valueName) || MatchesProgram(valueData))
                    {
                        leftovers.Add(new LeftoverItem
                        {
                            Path = $@"{prefix}\{regPath}\{valueName}",
                            DisplayPath = $@"{prefix}\{regPath} -> {valueName}",
                            Type = LeftoverType.RegistryValue,
                            Confidence = LeftoverConfidence.Safe,
                            IsSelected = true,
                            Details = $"Startup entry: {valueData}"
                        });
                    }
                }
            }
            catch { }
        }
    }

    private void ScanServices(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        if (string.IsNullOrEmpty(program.InstallLocation)) return;
        var installPath = program.InstallLocation.ToLowerInvariant();

        try
        {
            using var servicesKey = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return;

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var svcKey = servicesKey.OpenSubKey(serviceName);
                    var imagePath = svcKey?.GetValue("ImagePath") as string ?? "";
                    if (imagePath.ToLowerInvariant().Contains(installPath) || MatchesProgram(serviceName))
                    {
                        leftovers.Add(new LeftoverItem
                        {
                            Path = $@"HKLM\SYSTEM\CurrentControlSet\Services\{serviceName}",
                            DisplayPath = $@"Service: {serviceName}",
                            Type = LeftoverType.Service,
                            Confidence = LeftoverConfidence.Moderate,
                            IsSelected = false,
                            Details = $"Service executable: {imagePath}"
                        });
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void ScanFirewallRules(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        if (string.IsNullOrEmpty(program.InstallLocation)) return;
        var installPath = program.InstallLocation.ToLowerInvariant();

        try
        {
            using var fwKey = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
            if (fwKey == null) return;

            foreach (var ruleName in fwKey.GetValueNames())
            {
                var ruleData = fwKey.GetValue(ruleName) as string ?? "";
                if (ruleData.ToLowerInvariant().Contains(installPath) || MatchesProgram(ruleName))
                {
                    leftovers.Add(new LeftoverItem
                    {
                        Path = $@"FirewallRule:{ruleName}",
                        DisplayPath = $@"Firewall Rule: {ruleName}",
                        Type = LeftoverType.RegistryValue,
                        Confidence = LeftoverConfidence.Safe,
                        IsSelected = true,
                        Details = "Windows Firewall rule"
                    });
                }
            }
        }
        catch { }
    }

    private void ScanMuiCache(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        if (string.IsNullOrEmpty(program.InstallLocation)) return;
        var installPath = program.InstallLocation.ToLowerInvariant();

        try
        {
            using var muiKey = global::Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
            if (muiKey == null) return;

            foreach (var valueName in muiKey.GetValueNames())
            {
                if (valueName.ToLowerInvariant().Contains(installPath))
                {
                    leftovers.Add(new LeftoverItem
                    {
                        Path = $@"HKCU\...\MuiCache\{Path.GetFileName(valueName)}",
                        DisplayPath = $@"MUI Cache: {valueName}",
                        Type = LeftoverType.RegistryValue,
                        Confidence = LeftoverConfidence.Safe,
                        IsSelected = true,
                        Details = "Application name cache entry"
                    });
                }
            }
        }
        catch { }
    }

    private void ScanShellExtensions(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        var shellPaths = new[] {
            @"*\shellex\ContextMenuHandlers",
            @"Directory\shellex\ContextMenuHandlers",
            @"Folder\shellex\ContextMenuHandlers",
            @"Directory\Background\shellex\ContextMenuHandlers",
        };

        foreach (var shellPath in shellPaths)
        {
            try
            {
                using var shellKey = global::Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(shellPath);
                if (shellKey == null) continue;

                foreach (var handlerName in shellKey.GetSubKeyNames())
                {
                    if (MatchesProgram(handlerName))
                    {
                        leftovers.Add(new LeftoverItem
                        {
                            Path = $@"HKCR\{shellPath}\{handlerName}",
                            DisplayPath = $@"Shell Extension: {handlerName}",
                            Type = LeftoverType.RegistryKey,
                            Confidence = LeftoverConfidence.Moderate,
                            IsSelected = false,
                            Details = "Context menu shell extension"
                        });
                    }
                }
            }
            catch { }
        }
    }

    private void ScanAppCompatFlags(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        if (string.IsNullOrEmpty(program.InstallLocation)) return;
        var installPath = program.InstallLocation.ToLowerInvariant();

        var compatPaths = new[] {
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Custom",
        };

        foreach (var compatPath in compatPaths)
        {
            try
            {
                using var key = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(compatPath);
                if (key == null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (valueName.ToLowerInvariant().Contains(installPath))
                    {
                        leftovers.Add(new LeftoverItem
                        {
                            Path = $@"HKLM\{compatPath}\{Path.GetFileName(valueName)}",
                            DisplayPath = $@"Compat Flag: {Path.GetFileName(valueName)}",
                            Type = LeftoverType.RegistryValue,
                            Confidence = LeftoverConfidence.Safe,
                            IsSelected = true,
                            Details = "Application compatibility flag"
                        });
                    }
                }
            }
            catch { }
        }
    }

    private void ScanMsiData(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        // Convert GUID to MSI compressed GUID format and search
        var guid = program.RegistryKeyName;
        if (!Guid.TryParse(guid, out _)) return;

        var msiPaths = new[] {
            $@"SOFTWARE\Classes\Installer\Products",
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData",
        };

        foreach (var msiPath in msiPaths)
        {
            try
            {
                SearchKeyRecursive(global::Microsoft.Win32.Registry.LocalMachine, msiPath,
                    guid.Replace("{", "").Replace("}", "").Replace("-", ""),
                    "HKLM", leftovers, 2);
            }
            catch { }
        }
    }

    private void SearchKeyRecursive(RegistryKey hive, string path, string searchTerm,
        string prefix, List<LeftoverItem> leftovers, int maxDepth, int currentDepth = 0)
    {
        if (currentDepth >= maxDepth) return;
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key == null) return;

            foreach (var subName in key.GetSubKeyNames())
            {
                if (subName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    var fullPath = $@"{prefix}\{path}\{subName}";
                    leftovers.Add(new LeftoverItem
                    {
                        Path = fullPath,
                        DisplayPath = fullPath,
                        Type = LeftoverType.RegistryKey,
                        Confidence = LeftoverConfidence.Moderate,
                        IsSelected = false,
                        Details = "MSI installer data"
                    });
                }
                else
                {
                    SearchKeyRecursive(hive, $@"{path}\{subName}", searchTerm,
                        prefix, leftovers, maxDepth, currentDepth + 1);
                }
            }
        }
        catch { }
    }

    private static string ExtractExeName(string uninstallString)
    {
        if (string.IsNullOrEmpty(uninstallString)) return "";
        var path = uninstallString.Trim();
        if (path.StartsWith('"'))
        {
            var end = path.IndexOf('"', 1);
            if (end > 0) path = path[1..end];
        }
        else
        {
            var space = path.IndexOf(' ');
            if (space > 0) path = path[..space];
        }
        try { return Path.GetFileName(path); }
        catch { return ""; }
    }
}
