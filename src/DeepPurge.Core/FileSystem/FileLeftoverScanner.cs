using DeepPurge.Core.Models;

namespace DeepPurge.Core.FileSystem;

public class FileLeftoverScanner
{
    private readonly HashSet<string> _excludedFolders;

    // Primary terms: high-confidence, match these directly (full name, folder name, exe name)
    private readonly List<string> _primaryTerms = new();
    // Secondary terms: require additional validation (publisher, first word of multi-word name)
    private readonly List<string> _secondaryTerms = new();
    // The actual install path — anything under this is a guaranteed match
    private string _installPath = "";
    // Other installed programs' folders — used for cross-reference exclusion
    private HashSet<string> _otherProgramFolders = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SystemProtectedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows", @"C:\Windows\System32", @"C:\Windows\SysWOW64",
        @"C:\Program Files\Common Files", @"C:\Program Files (x86)\Common Files",
        @"C:\ProgramData\Microsoft", @"C:\Users\Default",
    };

    private static readonly HashSet<string> SharedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Shared", "Common Files", "WindowsApps",
        "Microsoft.NET", "assembly", "Package Cache",
    };

    // Words that are too generic to use as standalone search terms
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // OS/system terms
        "desktop", "windows", "system", "system32", "program", "programs",
        "application", "applications", "software", "portable", "update",
        "updater", "service", "services", "driver", "drivers", "runtime",
        "framework", "module", "components", "library", "common",
        // Generic app terms
        "app", "apps", "tool", "tools", "utility", "utilities",
        "free", "pro", "plus", "premium", "lite", "beta", "alpha",
        "setup", "install", "installer", "uninstall", "uninstaller",
        "launcher", "manager", "editor", "viewer", "player", "reader",
        "client", "server", "host", "helper", "agent", "daemon",
        "studio", "suite", "pack", "kit", "hub", "center", "centre",
        // Version/edition terms
        "version", "edition", "release", "build", "x86", "x64",
        "win32", "win64", "amd64", "arm64",
        // File terms
        "data", "files", "file", "temp", "cache", "logs", "log",
        "config", "settings", "preferences", "backup",
        // Common short words that substring-match everything
        "the", "for", "and", "net", "new", "web", "all",
    };

    public FileLeftoverScanner(HashSet<string>? customExclusions = null)
    {
        _excludedFolders = customExclusions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public List<LeftoverItem> ScanForLeftovers(InstalledProgram program, ScanMode mode)
    {
        var leftovers = new List<LeftoverItem>();
        BuildSearchTerms(program);
        BuildCrossReference(program);

        // 1. Check install location still exists
        if (!string.IsNullOrEmpty(program.InstallLocation))
            ScanInstallLocation(program.InstallLocation, leftovers);

        // 2. Scan ProgramData
        ScanDirectory(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), leftovers, "ProgramData");

        // 3. Scan user-specific locations
        ScanUserDataFolders(leftovers);

        // 4. Scan temp folders
        if (mode >= ScanMode.Moderate)
            ScanTempFolders(program, leftovers);

        // 5. Scan Start Menu shortcuts
        ScanStartMenu(leftovers);

        // 6. Scan Desktop shortcuts
        ScanDesktopShortcuts(program, leftovers);

        // 7. Scan Prefetch (Advanced only)
        if (mode >= ScanMode.Advanced)
            ScanPrefetch(program, leftovers);

        // 8. Scan all drives for matching folders (Advanced only)
        if (mode >= ScanMode.Advanced)
            ScanAllDrives(leftovers);

        // 9. Scan all user profiles (Advanced only)
        if (mode >= ScanMode.Advanced)
            ScanAllUserProfiles(leftovers);

        return leftovers;
    }

    // ═══════════════════════════════════════════════════════
    //  SMART TERM BUILDING
    // ═══════════════════════════════════════════════════════

    private void BuildSearchTerms(InstalledProgram program)
    {
        _primaryTerms.Clear();
        _secondaryTerms.Clear();
        _installPath = "";

        // PRIMARY: Full display name — always the strongest signal
        if (!string.IsNullOrEmpty(program.DisplayName))
            _primaryTerms.Add(program.DisplayName);

        // PRIMARY: Install folder name (e.g., "Telegram Desktop" from the path)
        if (!string.IsNullOrEmpty(program.InstallLocation))
        {
            _installPath = program.InstallLocation.TrimEnd('\\');
            var folderName = Path.GetFileName(_installPath);
            if (!string.IsNullOrEmpty(folderName) && folderName.Length > 2 && !IsStopWord(folderName))
                _primaryTerms.Add(folderName);
        }

        // PRIMARY: Executable name from uninstall string (e.g., "Telegram" from "Telegram.exe")
        var exeName = ExtractExeName(program.UninstallString);
        if (!string.IsNullOrEmpty(exeName) && exeName.Length > 3 && !IsStopWord(exeName))
        {
            var withoutExt = Path.GetFileNameWithoutExtension(exeName);
            if (!string.IsNullOrEmpty(withoutExt) && withoutExt.Length > 3 && !IsStopWord(withoutExt))
                _primaryTerms.Add(withoutExt);
        }

        // PRIMARY: Registry key name if it's a meaningful identifier (not a GUID)
        if (!string.IsNullOrEmpty(program.RegistryKeyName) &&
            program.RegistryKeyName.Length > 3 &&
            !program.RegistryKeyName.StartsWith('{') &&
            !IsStopWord(program.RegistryKeyName))
        {
            _primaryTerms.Add(program.RegistryKeyName);
        }

        // SECONDARY: First significant word from multi-word names
        // e.g., "Telegram" from "Telegram Desktop" — but ONLY as secondary
        if (!string.IsNullOrEmpty(program.DisplayName))
        {
            var words = program.DisplayName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1)
            {
                // Add the first non-stop word as secondary (lower confidence)
                foreach (var word in words)
                {
                    if (word.Length > 4 && !IsStopWord(word) && !_primaryTerms.Contains(word))
                    {
                        _secondaryTerms.Add(word);
                        break; // Only the FIRST significant word
                    }
                }
            }
        }

        // SECONDARY: Publisher name (only if not a mega-corp)
        if (!string.IsNullOrEmpty(program.Publisher) &&
            program.Publisher.Length > 3 &&
            !SharedPublishers.Contains(program.Publisher) &&
            !IsStopWord(program.Publisher))
        {
            _secondaryTerms.Add(program.Publisher);
        }

        // Deduplicate
        _primaryTerms.RemoveAll(t => string.IsNullOrEmpty(t) || t.Length <= 2);
        _secondaryTerms.RemoveAll(t => string.IsNullOrEmpty(t) || t.Length <= 2 ||
            _primaryTerms.Any(p => p.Equals(t, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Build a set of folder names that belong to OTHER installed programs.
    /// Used to prevent false positives: if "GitHub Desktop" is installed,
    /// its AppData folder should NOT be flagged when scanning "Telegram Desktop".
    /// </summary>
    private void BuildCrossReference(InstalledProgram targetProgram)
    {
        _otherProgramFolders.Clear();
        try
        {
            var allPrograms = Registry.InstalledProgramScanner.GetAllInstalledPrograms();
            foreach (var prog in allPrograms)
            {
                // Skip the target program itself
                if (prog.RegistryPath == targetProgram.RegistryPath) continue;
                if (prog.DisplayName == targetProgram.DisplayName) continue;

                // Add the display name as a known "other program" folder name
                if (!string.IsNullOrEmpty(prog.DisplayName))
                    _otherProgramFolders.Add(prog.DisplayName);

                // Add the install folder name
                if (!string.IsNullOrEmpty(prog.InstallLocation))
                {
                    var folder = Path.GetFileName(prog.InstallLocation.TrimEnd('\\'));
                    if (!string.IsNullOrEmpty(folder)) _otherProgramFolders.Add(folder);
                }
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════
    //  MATCHING ENGINE
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Determines if a folder/file name matches the target program.
    /// Returns a confidence level, or null if no match.
    /// 
    /// Matching rules:
    /// 1. Exact or substring match on PRIMARY terms → Safe
    /// 2. Match on SECONDARY terms only → Moderate (not auto-selected)
    /// 3. Folder belongs to another installed program → REJECT (no match)
    /// 4. Path is under the program's actual install location → Safe
    /// </summary>
    private LeftoverConfidence? MatchConfidence(string name, string fullPath)
    {
        if (string.IsNullOrEmpty(name)) return null;

        // RULE 3: Cross-reference — if this folder belongs to another installed program, reject
        if (_otherProgramFolders.Contains(name))
            return null;

        // RULE 4: Anything under the actual install path is a guaranteed match
        if (!string.IsNullOrEmpty(_installPath) &&
            fullPath.StartsWith(_installPath, StringComparison.OrdinalIgnoreCase))
            return LeftoverConfidence.Safe;

        // RULE 1: Primary term match (high confidence)
        if (_primaryTerms.Any(term => name.Equals(term, StringComparison.OrdinalIgnoreCase)))
            return LeftoverConfidence.Safe;

        // Primary substring match — folder name contains the full program name
        if (_primaryTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase) && term.Length >= 5))
            return LeftoverConfidence.Safe;

        // RULE 2: Secondary term match (lower confidence)
        if (_secondaryTerms.Any(term => name.Equals(term, StringComparison.OrdinalIgnoreCase)))
            return LeftoverConfidence.Moderate;

        return null;
    }

    /// <summary>Matches temp files more strictly — requires full name or exe name match.</summary>
    private LeftoverConfidence? MatchTempFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;

        // For temp files, ONLY match on primary terms (never secondary)
        // and require the full term to appear, not individual words
        if (_primaryTerms.Any(term =>
            fileName.Contains(term, StringComparison.OrdinalIgnoreCase) && term.Length >= 5))
            return LeftoverConfidence.Safe;

        return null;
    }

    // ═══════════════════════════════════════════════════════
    //  SCAN METHODS (mostly unchanged but using new matching)
    // ═══════════════════════════════════════════════════════

    private bool IsProtected(string path)
    {
        return SystemProtectedFolders.Any(p =>
            path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
            _excludedFolders.Contains(path);
    }

    private void ScanInstallLocation(string installPath, List<LeftoverItem> leftovers)
    {
        if (!Directory.Exists(installPath)) return;
        if (IsProtected(installPath)) return;

        var size = GetDirectorySize(installPath);
        leftovers.Add(new LeftoverItem
        {
            Path = installPath,
            DisplayPath = installPath,
            Type = LeftoverType.Folder,
            Confidence = LeftoverConfidence.Safe,
            SizeBytes = size,
            IsSelected = true,
            Details = "Program install directory (still exists after uninstall)"
        });
    }

    private void ScanDirectory(string basePath, List<LeftoverItem> leftovers, string label)
    {
        if (!Directory.Exists(basePath)) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var dirName = Path.GetFileName(dir);
                if (SharedFolderNames.Contains(dirName)) continue;
                if (IsProtected(dir)) continue;

                var confidence = MatchConfidence(dirName, dir);
                if (confidence == null) continue;

                var size = GetDirectorySize(dir);
                leftovers.Add(new LeftoverItem
                {
                    Path = dir,
                    DisplayPath = dir,
                    Type = LeftoverType.Folder,
                    Confidence = confidence.Value,
                    SizeBytes = size,
                    IsSelected = confidence == LeftoverConfidence.Safe,
                    Details = $"Application data in {label}"
                });
            }
        }
        catch { }
    }

    private void ScanUserDataFolders(List<LeftoverItem> leftovers)
    {
        var userFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),           // Roaming
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),       // Local
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };

        foreach (var folder in userFolders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            var label = folder.Contains("Roaming") ? "AppData\\Roaming" :
                        folder.Contains("Programs") ? "AppData\\Local\\Programs" : "AppData\\Local";
            ScanDirectory(folder, leftovers, label);
        }
    }

    private void ScanTempFolders(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        var tempPaths = new[]
        {
            Path.GetTempPath(),
            @"C:\Windows\Temp",
        };

        foreach (var tempPath in tempPaths)
        {
            if (!Directory.Exists(tempPath)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(tempPath))
                {
                    var dirName = Path.GetFileName(dir);
                    var confidence = MatchConfidence(dirName, dir);
                    if (confidence == null) continue;

                    var size = GetDirectorySize(dir);
                    leftovers.Add(new LeftoverItem
                    {
                        Path = dir, DisplayPath = dir, Type = LeftoverType.Folder,
                        Confidence = confidence.Value, SizeBytes = size,
                        IsSelected = confidence == LeftoverConfidence.Safe,
                        Details = "Temporary files"
                    });
                }

                // Temp FILES: use strict matching (full name only, no individual words)
                foreach (var file in Directory.GetFiles(tempPath))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var confidence = MatchTempFile(fileName);
                    if (confidence == null) continue;

                    try
                    {
                        var fi = new FileInfo(file);
                        leftovers.Add(new LeftoverItem
                        {
                            Path = file, DisplayPath = file, Type = LeftoverType.File,
                            Confidence = confidence.Value, SizeBytes = fi.Length,
                            IsSelected = confidence == LeftoverConfidence.Safe,
                            Details = "Temporary file"
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private void ScanStartMenu(List<LeftoverItem> leftovers)
    {
        var startMenuPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };

        foreach (var menuPath in startMenuPaths)
        {
            if (string.IsNullOrEmpty(menuPath) || !Directory.Exists(menuPath)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(menuPath))
                {
                    var dirName = Path.GetFileName(dir);
                    var confidence = MatchConfidence(dirName, dir);
                    if (confidence == null) continue;

                    leftovers.Add(new LeftoverItem
                    {
                        Path = dir, DisplayPath = dir, Type = LeftoverType.Folder,
                        Confidence = confidence.Value, SizeBytes = GetDirectorySize(dir),
                        IsSelected = confidence == LeftoverConfidence.Safe,
                        Details = "Start Menu shortcuts"
                    });
                }

                foreach (var file in Directory.GetFiles(menuPath, "*.lnk"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var confidence = MatchConfidence(fileName, file);
                    if (confidence == null) continue;

                    leftovers.Add(new LeftoverItem
                    {
                        Path = file, DisplayPath = file, Type = LeftoverType.File,
                        Confidence = confidence.Value, SizeBytes = new FileInfo(file).Length,
                        IsSelected = confidence == LeftoverConfidence.Safe,
                        Details = "Start Menu shortcut"
                    });
                }
            }
            catch { }
        }
    }

    private void ScanDesktopShortcuts(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        var desktopPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        foreach (var desktop in desktopPaths)
        {
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) continue;
            try
            {
                foreach (var file in Directory.GetFiles(desktop, "*.lnk"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var confidence = MatchConfidence(fileName, file);
                    if (confidence == null) continue;

                    leftovers.Add(new LeftoverItem
                    {
                        Path = file, DisplayPath = file, Type = LeftoverType.File,
                        Confidence = confidence.Value, SizeBytes = new FileInfo(file).Length,
                        IsSelected = confidence == LeftoverConfidence.Safe,
                        Details = "Desktop shortcut"
                    });
                }
            }
            catch { }
        }
    }

    private void ScanPrefetch(InstalledProgram program, List<LeftoverItem> leftovers)
    {
        var prefetchPath = @"C:\Windows\Prefetch";
        if (!Directory.Exists(prefetchPath)) return;

        try
        {
            foreach (var file in Directory.GetFiles(prefetchPath, "*.pf"))
            {
                // Prefetch files: match only on primary terms (strict)
                var fileName = Path.GetFileNameWithoutExtension(file);
                var confidence = MatchTempFile(fileName);
                if (confidence == null) continue;

                leftovers.Add(new LeftoverItem
                {
                    Path = file, DisplayPath = file, Type = LeftoverType.File,
                    Confidence = confidence.Value, SizeBytes = new FileInfo(file).Length,
                    IsSelected = confidence == LeftoverConfidence.Safe,
                    Details = "Windows Prefetch file"
                });
            }
        }
        catch { }
    }

    private void ScanAllDrives(List<LeftoverItem> leftovers)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var programDirs = new[]
            {
                Path.Combine(drive.RootDirectory.FullName, "Program Files"),
                Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)"),
            };

            foreach (var dir in programDirs)
            {
                if (!Directory.Exists(dir)) continue;
                ScanDirectory(dir, leftovers, $"Programs on {drive.Name}");
            }
        }
    }

    private void ScanAllUserProfiles(List<LeftoverItem> leftovers)
    {
        var usersDir = @"C:\Users";
        if (!Directory.Exists(usersDir)) return;

        try
        {
            foreach (var userDir in Directory.GetDirectories(usersDir))
            {
                var userName = Path.GetFileName(userDir);
                if (userName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                    userName.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                    userName.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                    userName.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                    continue;

                var appDataPaths = new[]
                {
                    Path.Combine(userDir, "AppData", "Roaming"),
                    Path.Combine(userDir, "AppData", "Local"),
                    Path.Combine(userDir, "AppData", "LocalLow"),
                };

                foreach (var path in appDataPaths)
                {
                    if (!Directory.Exists(path)) continue;
                    ScanDirectory(path, leftovers, $"{userName}\\{Path.GetFileName(path)}");
                }
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════

    private static bool IsStopWord(string word) => StopWords.Contains(word);

    private static readonly HashSet<string> SharedPublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Microsoft Corporation", "Intel", "Intel Corporation",
        "NVIDIA", "NVIDIA Corporation", "AMD", "Advanced Micro Devices",
        "Realtek", "Qualcomm", "Broadcom", "Google", "Google LLC",
        "Apple", "Apple Inc.", "Oracle", "Oracle Corporation",
    };

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

    public static long GetDirectorySize(string path)
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
