using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeepPurge.Core.Models;

public class LeftoverItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isExpanded;

    public string Path { get; set; } = string.Empty;
    public string DisplayPath { get; set; } = string.Empty;
    public LeftoverType Type { get; set; }
    public LeftoverConfidence Confidence { get; set; }
    public long SizeBytes { get; set; }
    public string Details { get; set; } = string.Empty;
    public List<LeftoverItem> Children { get; set; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public string SizeDisplay
    {
        get
        {
            if (SizeBytes <= 0) return "";
            if (SizeBytes < 1024) return $"{SizeBytes} B";
            double kb = SizeBytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }
    }

    public string TypeIcon => Type switch
    {
        LeftoverType.RegistryKey => "\uE8F1",
        LeftoverType.RegistryValue => "\uE8F1",
        LeftoverType.File => "\uE8A5",
        LeftoverType.Folder => "\uE8B7",
        LeftoverType.Service => "\uE912",
        LeftoverType.ScheduledTask => "\uE823",
        _ => "\uE8A5"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum LeftoverType
{
    RegistryKey,
    RegistryValue,
    File,
    Folder,
    Service,
    ScheduledTask
}

public enum LeftoverConfidence
{
    Safe,       // Bold - high confidence, safe to delete
    Moderate,   // Normal - probably safe but review recommended
    Risky,      // Red/excluded - could affect other programs or system
    Info        // Gray - shown for context only, not deletable
}

public class ScanResult
{
    public InstalledProgram Program { get; set; } = new();
    public List<LeftoverItem> RegistryLeftovers { get; set; } = new();
    public List<LeftoverItem> FileLeftovers { get; set; } = new();
    public int TotalLeftovers => RegistryLeftovers.Count + FileLeftovers.Count;
    public long TotalSizeBytes => FileLeftovers.Sum(f => f.SizeBytes);
    public DateTime ScanTime { get; set; } = DateTime.Now;
    public ScanMode Mode { get; set; }
    public TimeSpan ScanDuration { get; set; }
}

public enum ScanMode
{
    Safe,
    Moderate,
    Advanced
}

public class UninstallResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string ErrorOutput { get; set; } = string.Empty;
    public bool UninstallerSkipped { get; set; }
    public ScanResult? LeftoverScan { get; set; }
    public int RegistryItemsDeleted { get; set; }
    public int FileItemsDeleted { get; set; }
}
