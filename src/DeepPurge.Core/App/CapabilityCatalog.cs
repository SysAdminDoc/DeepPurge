namespace DeepPurge.Core.App;

/// <summary>
/// The public product surface in one machine-readable list. Release checks and
/// contract tests use this catalog to keep README claims, navigation, and CLI
/// routing from drifting apart.
/// </summary>
public sealed record CapabilitySurface(
    string Id,
    string ReadmeClaim,
    string? GuiTag = null,
    string? GuiElement = null,
    string? CliCommand = null,
    bool RequiresExpertMode = false,
    string? SourceMarker = null,
    string? UnsupportedReason = null)
{
    public bool HasReachableSurface =>
        (!string.IsNullOrWhiteSpace(GuiTag) && !string.IsNullOrWhiteSpace(GuiElement)) ||
        !string.IsNullOrWhiteSpace(CliCommand) ||
        !string.IsNullOrWhiteSpace(SourceMarker);
}

public sealed record SettingSurface(
    string Id,
    string ReadmeClaim,
    string ModelProperty,
    string? GuiBinding = null,
    string? CliCommand = null,
    string? UnsupportedReason = null)
{
    public bool HasReachableSurface =>
        !string.IsNullOrWhiteSpace(GuiBinding) ||
        !string.IsNullOrWhiteSpace(CliCommand);
}

public static class CapabilityCatalog
{
    public const string ContractVersion = "1";

    public static IReadOnlyList<CapabilitySurface> Capabilities { get; } =
    [
        new("InstalledPrograms", "Installed Programs", "Programs", "dgPrograms", "list"),
        new("BulkUninstall", "Bulk Uninstall", "Programs", "dgPrograms", "uninstall"),
        new("PackageManagerIntegration", "winget integration", "Programs", "dgPrograms", "list"),
        new("RemovalCapabilityTrust", "Explicit removal capability and trust", "Programs", "dgPrograms", "uninstall"),
        new("PortableRemoval", "Recoverable portable removal", "Programs", "dgPrograms", "uninstall"),
        new("ForcedUninstall", "Forced Uninstall", "Forced", "panelForced", "uninstall"),
        new("WindowsApps", "Windows Apps", "WindowsApps", "dgWindowsApps", "list"),
        new("LeftoverScanner", "Leftover Scanner", "Programs", "panelLeftovers", "uninstall"),
        new("ProgramExport", "Export", "Programs", "btnExport", "list"),

        new("JunkCleaner", "Junk Cleaner", "Junk", "dgJunk", "clean"),
        new("EvidenceRemover", "Evidence Remover", "Evidence", "dgEvidence", "clean"),
        new("EmptyFolders", "Empty Folders", "EmptyFolders", "dgEmptyFolders", "clean"),
        new("DiskAnalyzer", "Disk Analyzer", "Disk", "panelDisk", "clean"),
        new("OrphanMsiCleanup", "MSI/MSP orphan cleanup", "Junk", "dgJunk", "clean"),
        new("DryRunPreview", "Dry-run / Preview mode", "Junk", "dgJunk", "clean"),
        new("SecureDelete", "Secure Delete", "Settings", "chkSecure", "clean", RequiresExpertMode: true),
        new("SkippedItemDetails", "Skipped-item details", "Junk", "dgJunk", "clean"),

        new("AutorunManager", "Autorun Manager", "Autorun", "dgAutorun", "list"),
        new("StartupImpact", "Startup Impact ratings", "StartupImpact", "dgStartupImpact", "startup-impact"),
        new("SignatureBadges", "Digital signature badges", "Programs", "dgPrograms", "list"),
        new("BrowserExtensions", "Browser Extensions", "BrowserExt", "dgBrowserExt", "list"),
        new("DriverStore", "Driver Store cleanup", "Drivers", "dgDrivers", "drivers"),
        new("ContextMenuCleaner", "Context Menu Cleaner", "ContextMenu", "dgContextMenu", "clean"),
        new("ShortcutRepair", "Shortcut repair", "Shortcuts", "dgShortcuts", "shortcuts"),
        new("ServicesManager", "Services Manager", "Services", "dgServices", "orphans"),
        new("ScheduledTasks", "Scheduled Tasks", "Tasks", "dgTasks", "schedule"),
        new("RegistryHunter", "Registry Hunter", "Hunter", "panelHunter", null, RequiresExpertMode: true),
        new("OrphanDiscovery", "BAM remnant discovery", "Orphans", "panelOrphans", "orphans"),

        new("WindowsRepair", "SFC / DISM / chkdsk", "Repair", "panelRepair", "repair"),
        new("CacheRepair", "Font + Icon cache rebuild", "Repair", "panelRepair", "repair"),
        new("PerAppRepair", "Per-app repair", "Repair", "panelRepair", "repair"),
        new("InstallationMonitor", "Before/after snapshot", "InstallMonitor", "panelInstallMonitor", "snapshot"),
        new("ReplayUninstall", "Replay uninstall", "Forced", "panelForced", "uninstall"),
        new("DiagnosticJournal", "Diagnostic journal evidence", SourceMarker: "InstallSnapshotEngine"),
        new("CommunityCleaners", "winapp2.ini integration", "Winapp2", "panelWinapp2", "winapp2"),
        new("CustomCleaners", "Validated custom JSON cleaners", "Winapp2", "panelWinapp2", "cleaners"),
        new("DuplicateFinder", "Three-stage hash", "Duplicates", "dgDuplicates", "duplicates"),
        new("DuplicateIdentity", "Explicit keeper and identity revalidation", "Duplicates", "dgDuplicates", "duplicates"),

        new("HealthDashboard", "Health Dashboard", "Health", "panelHealth", "health"),
        new("SystemSlimming", "System Slimming", "Slimming", "panelSlimming", "slim", RequiresExpertMode: true),
        new("PortableDetection", "Portable app detection", "Programs", "dgPrograms", "list"),
        new("GameDetection", "Game platform detection", "Programs", "dgPrograms", "list"),
        new("GameRemovalSafety", "Game removal safety", "Programs", "dgPrograms", "uninstall"),
        new("BundlewareDetection", "Bundleware / sideload detection", "Programs", "dgPrograms", "list"),
        new("OemBloatScoring", "OEM bloat scoring", "Programs", "dgPrograms", "list"),

        new("ShellContextMenu", "Context menu", CliCommand: "register-shell"),
        new("ExpertMode", "Expert / Safe mode", "Settings", "panelSettings", "settings", RequiresExpertMode: true),
        new("SettingsTransfer", "Versioned settings import/export", "Settings", "panelSettings", "settings"),
        new("RestorePoints", "System Restore Points", "Restore", "dgRestore", "restore"),
        new("DeletionRecovery", "Deletion Recovery panel", "DeletionRecovery", "panelDeletionRecovery", "restore"),
        new("RegistryBackups", "Registry Backups panel", "Backups", "panelBackups", "restore"),
        new("ScheduledCleaning", "Scheduled cleaning", "Schedule", "panelSchedule", "schedule"),
        new("PortableMode", "Portable mode", "About", "panelAbout", "portable"),
        new("UpdateChecker", "Update checker", "About", "panelAbout", "check-update"),
        new("TrayIcon", "Tray icon", SourceMarker: "TrayIconService"),
    ];

    public static IReadOnlyList<SettingSurface> Settings { get; } =
    [
        new("ExpertMode", "Expert / Safe mode", nameof(AppSettings.ExpertMode), "ExpertMode", "settings"),
        new("ExcludedPaths", "Versioned settings import/export", nameof(AppSettings.ExcludedPaths), "SettingsExcludedPathsText", "settings"),
        new("CookieWhitelist", "Versioned settings import/export", nameof(AppSettings.CookieWhitelist), "SettingsCookieWhitelistText", "settings"),
        new("MinAgeDaysJunk", "Dry-run / Preview mode", nameof(AppSettings.MinAgeDaysJunk), "SettingsMinAgeJunkText", "clean"),
        new("MinAgeDaysEvidence", "Dry-run / Preview mode", nameof(AppSettings.MinAgeDaysEvidence), "SettingsMinAgeEvidenceText", "clean"),
        new("RetentionDaysLogs", "Versioned settings import/export", nameof(AppSettings.RetentionDaysLogs), "SettingsRetentionLogsText", "settings"),
        new("RetentionDaysActivity", "Versioned settings import/export", nameof(AppSettings.RetentionDaysActivity), "SettingsRetentionActivityText", "settings"),
        new("RetentionDaysDeletionManifests", "Versioned settings import/export", nameof(AppSettings.RetentionDaysDeletionManifests), "SettingsRetentionDeletionManifestsText", "settings"),
        new("ScrubSensitivePathsInReports", "Versioned settings import/export", nameof(AppSettings.ScrubSensitivePathsInReports), "SettingsScrubSensitivePaths", "settings"),
        new("ProgramNotes", "Versioned settings import/export", nameof(AppSettings.ProgramNotes), CliCommand: "note"),
    ];

    public static string RenderMatrix()
        => string.Join(
            Environment.NewLine,
            Capabilities.Select(c =>
                $"capability {c.Id}: README='{c.ReadmeClaim}' GUI={c.GuiTag ?? "(none)"}/{c.GuiElement ?? "(none)"} CLI={c.CliCommand ?? "(none)"} " +
                $"source={c.SourceMarker ?? "(none)"} unsupported={c.UnsupportedReason ?? "(none)"}")
            .Concat(Settings.Select(s =>
                $"setting {s.Id}: README='{s.ReadmeClaim}' model={s.ModelProperty} GUI={s.GuiBinding ?? "(none)"} CLI={s.CliCommand ?? "(none)"} unsupported={s.UnsupportedReason ?? "(none)"}")));
}
