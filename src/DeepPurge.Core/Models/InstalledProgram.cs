using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeepPurge.Core.Models;

public class InstalledProgram : INotifyPropertyChanged
{
    private bool _isSelected;
    private object? _icon;

    public string RegistryKeyName { get; set; } = string.Empty;
    public string RegistryPath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public string QuietUninstallString { get; set; } = string.Empty;
    public string DisplayIconPath { get; set; } = string.Empty;
    public long EstimatedSizeKB { get; set; }
    public long ActualSizeBytes { get; set; } = -1;
    public DateTime? LastUsedDate { get; set; }
    public bool IsSystemComponent { get; set; }
    public bool IsWindowsInstaller { get; set; }
    public string ParentKeyName { get; set; } = string.Empty;
    public RegistrySource Source { get; set; }

    /// <summary>
    /// Non-empty when a package manager (winget, scoop, chocolatey) also
    /// tracks this program. Populated by <c>PackageManagerScanner.EnrichAsync</c>.
    /// </summary>
    public string PackageManager { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string UpgradeAvailable { get; set; } = string.Empty;

    private RemovalCapability _removalCapability = RemovalCapability.Unsupported;
    private string _removalSourceIdentity = string.Empty;
    private UninstallerTrustFacts _uninstallerTrust = UninstallerTrustFacts.Empty;
    private string _uninstallerExecutablePath = string.Empty;
    private string _uninstallerArguments = string.Empty;
    private string _uninstallerOwner = string.Empty;
    private string _uninstallerPublisher = string.Empty;
    private string _uninstallerRisk = global::DeepPurge.Core.Models.UninstallerRisk.Unknown.ToString();

    public RemovalCapability RemovalCapability
    {
        get => _removalCapability;
        set { _removalCapability = value; OnPropertyChanged(); OnPropertyChanged(nameof(CapabilityDisplay)); OnPropertyChanged(nameof(RemovalFactsDisplay)); OnPropertyChanged(nameof(RemovalSupported)); OnPropertyChanged(nameof(CanUninstall)); OnPropertyChanged(nameof(RemovalStatus)); }
    }

    public string RemovalSourceIdentity
    {
        get => _removalSourceIdentity;
        set { _removalSourceIdentity = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(RemovalFactsDisplay)); }
    }

    public UninstallerTrustFacts UninstallerTrust
    {
        get => _uninstallerTrust;
        set { _uninstallerTrust = value ?? UninstallerTrustFacts.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(ActionTrustDisplay)); OnPropertyChanged(nameof(RemovalFactsDisplay)); OnPropertyChanged(nameof(RemovalSupported)); OnPropertyChanged(nameof(CanUninstall)); OnPropertyChanged(nameof(RemovalStatus)); }
    }

    public string UninstallerExecutablePath
    {
        get => _uninstallerExecutablePath;
        set { _uninstallerExecutablePath = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(RemovalFactsDisplay)); }
    }

    public string UninstallerArguments
    {
        get => _uninstallerArguments;
        set { _uninstallerArguments = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(RemovalFactsDisplay)); }
    }

    public string UninstallerOwner
    {
        get => _uninstallerOwner;
        set { _uninstallerOwner = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(RemovalFactsDisplay)); }
    }

    public string UninstallerPublisher
    {
        get => _uninstallerPublisher;
        set { _uninstallerPublisher = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(RemovalFactsDisplay)); }
    }

    public string UninstallerRisk
    {
        get => _uninstallerRisk;
        set { _uninstallerRisk = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(RemovalFactsDisplay)); }
    }

    public string CapabilityDisplay => RemovalCapability.ToString();
    public string ActionTrustDisplay => UninstallerTrust.ReviewDisplay;
    public string RemovalFactsDisplay =>
        $"{CapabilityDisplay} | source={RemovalSourceIdentity} | " +
        $"action={UninstallerExecutablePath} {UninstallerArguments}".TrimEnd() +
        $" | signature={UninstallerTrust.SignatureDisplay} | " +
        $"publisher={DisplayOrUnknown(UninstallerPublisher)} | " +
        $"owner={DisplayOrUnknown(UninstallerOwner)} | " +
        $"risk={UninstallerRisk} | {UninstallerTrust.Reason}";
    public bool RemovalSupported => !IsProtected &&
        RemovalCapability != RemovalCapability.Unsupported &&
        UninstallerTrust.IsActionAvailable;
    public bool CanUninstall => RemovalSupported;
    public string RemovalStatus => RemovalSupported
        ? UninstallerTrust.Risk is global::DeepPurge.Core.Models.UninstallerRisk.Low
            ? "Ready"
            : $"Review: {UninstallerTrust.RiskDisplay}"
        : $"Unsupported: {UninstallerTrust.Reason}";

    internal void OnRemovalFactsChanged()
    {
        OnPropertyChanged(nameof(CapabilityDisplay));
        OnPropertyChanged(nameof(ActionTrustDisplay));
        OnPropertyChanged(nameof(SignatureDisplay));
        OnPropertyChanged(nameof(RemovalFactsDisplay));
        OnPropertyChanged(nameof(RemovalSupported));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(RemovalStatus));
    }

    public object? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    private bool _isProtected;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public bool IsProtected
    {
        get => _isProtected;
        set { _isProtected = value; OnPropertyChanged(); OnPropertyChanged(nameof(RemovalSupported)); OnPropertyChanged(nameof(CanUninstall)); OnPropertyChanged(nameof(RemovalStatus)); OnPropertyChanged(nameof(RemovalFactsDisplay)); }
    }

    public bool IsSuspectedBundleware { get; set; }
    public int OemBloatScore { get; set; }
    public string OemBloatReason { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string SignatureDisplay { get; set; } = string.Empty;

    public bool IsOemBloatCandidate => OemBloatScore >= 60;

    public string FlagsDisplay
    {
        get
        {
            var flags = new List<string>();
            if (IsSuspectedBundleware) flags.Add("Bundleware");
            if (IsOemBloatCandidate) flags.Add($"OEM {OemBloatScore}");
            return string.Join(", ", flags);
        }
    }

    public string EstimatedSizeDisplay
    {
        get
        {
            if (ActualSizeBytes > 0) return FormatBytes(ActualSizeBytes);
            if (EstimatedSizeKB <= 0) return "";
            if (EstimatedSizeKB < 1024) return $"{EstimatedSizeKB} KB";
            double mb = EstimatedSizeKB / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }
    }

    private static string FormatBytes(long bytes) => Diagnostics.SizeFormatter.Format(bytes);

    public string InstallDateDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(InstallDate) || InstallDate.Length != 8) return InstallDate;
            try
            {
                var dt = DateTime.ParseExact(InstallDate, "yyyyMMdd", null);
                return dt.ToString("yyyy-MM-dd");
            }
            catch { return InstallDate; }
        }
    }

    public bool IsRecentlyInstalled
    {
        get
        {
            if (string.IsNullOrEmpty(InstallDate) || InstallDate.Length != 8) return false;
            try
            {
                var dt = DateTime.ParseExact(InstallDate, "yyyyMMdd", null);
                return (DateTime.Now - dt).TotalDays <= 7;
            }
            catch { return false; }
        }
    }

    public string LastUsedDisplay => LastUsedDate.HasValue ? LastUsedDate.Value.ToString("yyyy-MM-dd") : "";

    public bool HasUninstaller => !string.IsNullOrEmpty(UninstallString);
    public bool HasQuietUninstaller => !string.IsNullOrEmpty(QuietUninstallString);

    public string SourceDisplay
    {
        get
        {
            if (!string.IsNullOrEmpty(PackageManager))
                return !string.IsNullOrEmpty(UpgradeAvailable)
                    ? $"{PackageManager} ↑"
                    : PackageManager;

            return Source switch
            {
                RegistrySource.HKLM_Uninstall => "System",
                RegistrySource.HKLM_WOW64_Uninstall => "32-bit",
                RegistrySource.HKCU_Uninstall => "User",
                RegistrySource.Portable => "Portable",
                _ => "",
            };
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string DisplayOrUnknown(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}

public enum RegistrySource
{
    HKLM_Uninstall,
    HKLM_WOW64_Uninstall,
    HKCU_Uninstall,
    Portable,
}
