using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DeepPurge.Core.Models;

public class InstalledProgram : INotifyPropertyChanged
{
    private bool _isSelected;
    private ImageSource? _icon;

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
    public bool IsSystemComponent { get; set; }
    public bool IsWindowsInstaller { get; set; }
    public string ParentKeyName { get; set; } = string.Empty;
    public RegistrySource Source { get; set; }

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string EstimatedSizeDisplay
    {
        get
        {
            if (EstimatedSizeKB <= 0) return "";
            if (EstimatedSizeKB < 1024) return $"{EstimatedSizeKB} KB";
            double mb = EstimatedSizeKB / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }
    }

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

    public bool HasUninstaller => !string.IsNullOrEmpty(UninstallString);
    public bool HasQuietUninstaller => !string.IsNullOrEmpty(QuietUninstallString);

    public string SourceDisplay => Source switch
    {
        RegistrySource.HKLM_Uninstall => "System",
        RegistrySource.HKLM_WOW64_Uninstall => "32-bit",
        RegistrySource.HKCU_Uninstall => "User",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum RegistrySource
{
    HKLM_Uninstall,
    HKLM_WOW64_Uninstall,
    HKCU_Uninstall
}
