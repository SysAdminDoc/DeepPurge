using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeepPurge.Core.Safety;

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
    public List<LeftoverEvidence> Evidence { get; set; } = new();
    public List<OwnershipConflict> OwnershipConflicts { get; set; } = new();
    public bool IsAutoRemovalEligible { get; set; }

    public bool IsReviewOnly => !IsAutoRemovalEligible;

    public string EvidenceDisplay => Evidence.Count == 0
        ? ""
        : string.Join(", ", Evidence.Select(e => $"{e.Source} ({e.Strength})"));

    public string OwnershipDisplay => IsAutoRemovalEligible
        ? "Auto-removable"
        : OwnershipConflicts.Count > 0
            ? $"Review-only: {OwnershipConflicts.Count} conflict(s)"
            : "Review-only";

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

public enum EvidenceStrength
{
    Weak,
    Supporting,
    Strong,
}

public sealed record LeftoverEvidence(
    string Source,
    string Value,
    EvidenceStrength Strength);

public sealed record OwnershipConflict(
    string OwnerDisplayName,
    string OwnerIdentity,
    string CandidatePath,
    string Reason);

public sealed record LeftoverOwnershipDecision(
    IReadOnlyList<LeftoverEvidence> Evidence,
    IReadOnlyList<OwnershipConflict> Conflicts,
    bool ProtectedBySystem,
    bool AutoRemovalEligible,
    string ReviewReason);

/// <summary>
/// Final ownership gate for leftover candidates. Discovery scanners can offer
/// hints, but only this policy decides whether a candidate may be selected for
/// automatic removal.
/// </summary>
public static class LeftoverOwnershipGate
{
    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string SystemDirectory = Environment.SystemDirectory;
    private static readonly string ProgramDataWindows = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Microsoft",
        "Windows");

    private static readonly string[] ProtectedRegistryRoots =
    {
        @"HKLM\SOFTWARE\Microsoft\Windows",
        @"HKLM\SOFTWARE\Microsoft\Windows NT",
        @"HKLM\SOFTWARE\Classes",
        @"HKLM\SYSTEM\CurrentControlSet",
        @"HKLM\SOFTWARE\Policies",
        @"HKCU\SOFTWARE\Microsoft\Windows",
        @"HKCU\SOFTWARE\Policies",
    };

    public static LeftoverOwnershipDecision Evaluate(
        InstalledProgram targetProgram,
        string candidatePath,
        IEnumerable<InstalledProgram> installedPrograms,
        IEnumerable<LeftoverEvidence>? evidence = null)
    {
        var signals = evidence?.ToList() ?? new List<LeftoverEvidence>();
        var conflicts = new List<OwnershipConflict>();
        var protectedBySystem = false;
        var isRegistryPath = LooksLikeRegistryPath(candidatePath);

        if (isRegistryPath)
        {
            protectedBySystem = ProtectedRegistryRoots.Any(root =>
                candidatePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) ||
                !SafetyGuard.IsRegistryPathSafeToDelete(candidatePath);
        }
        else if (TryNormalizePath(candidatePath, out var normalizedCandidate))
        {
            protectedBySystem = IsSystemPath(normalizedCandidate) ||
                                !SafetyGuard.IsPathSafeToDelete(normalizedCandidate);

            if (!string.IsNullOrWhiteSpace(targetProgram.InstallLocation) &&
                IsUnder(normalizedCandidate, targetProgram.InstallLocation))
            {
                signals.Add(new LeftoverEvidence(
                    "InstallLocation",
                    targetProgram.InstallLocation,
                    EvidenceStrength.Strong));
            }

            foreach (var other in installedPrograms)
            {
                if (ReferenceEquals(other, targetProgram) ||
                    SameIdentity(other, targetProgram))
                    continue;
                if (string.IsNullOrWhiteSpace(other.InstallLocation) ||
                    !TryNormalizePath(other.InstallLocation, out var otherInstall))
                    continue;
                if (!IsUnder(normalizedCandidate, otherInstall)) continue;

                conflicts.Add(new OwnershipConflict(
                    other.DisplayName,
                    other.RegistryPath,
                    candidatePath,
                    $"Candidate is inside another installed product's install root: {otherInstall}"));
            }
        }
        else
        {
            protectedBySystem = true;
        }

        if (isRegistryPath)
        {
            foreach (var other in installedPrograms)
            {
                if (ReferenceEquals(other, targetProgram) ||
                    SameIdentity(other, targetProgram))
                    continue;
                if (!string.IsNullOrWhiteSpace(other.RegistryPath) &&
                    IsRegistryDescendant(candidatePath, other.RegistryPath))
                {
                    conflicts.Add(new OwnershipConflict(
                        other.DisplayName,
                        other.RegistryPath,
                        candidatePath,
                        "Candidate is owned by another installed product's registry branch."));
                }
            }
        }

        var strongSignals = signals.Count(s => s.Strength == EvidenceStrength.Strong);
        var supportingSignals = signals.Count(s => s.Strength == EvidenceStrength.Supporting);
        var hasWeakEvidence = strongSignals == 0 && supportingSignals < 2;
        var autoRemovalEligible = !protectedBySystem &&
                                  conflicts.Count == 0 &&
                                  !hasWeakEvidence;

        var reviewReason = protectedBySystem
            ? "Protected Windows/system path or policy scope."
            : conflicts.Count > 0
                ? "Ownership conflicts with another installed product."
                : hasWeakEvidence
                    ? "Only weak or single-source ownership evidence is available."
                    : string.Empty;

        return new LeftoverOwnershipDecision(
            signals,
            conflicts,
            protectedBySystem,
            autoRemovalEligible,
            reviewReason);
    }

    private static bool SameIdentity(InstalledProgram left, InstalledProgram right)
    {
        if (!string.IsNullOrWhiteSpace(left.RegistryPath) &&
            !string.IsNullOrWhiteSpace(right.RegistryPath))
            return left.RegistryPath.Equals(right.RegistryPath, StringComparison.OrdinalIgnoreCase);
        return left.DisplayName.Equals(right.DisplayName, StringComparison.OrdinalIgnoreCase) &&
               left.Publisher.Equals(right.Publisher, StringComparison.OrdinalIgnoreCase) &&
               left.InstallLocation.Equals(right.InstallLocation, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeRegistryPath(string path)
        => ProtectedRegistryRoots.Any(root =>
               path.StartsWith(root[..Math.Min(root.Length, 4)], StringComparison.OrdinalIgnoreCase)) ||
           path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("HKU\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsRegistryDescendant(string candidate, string owner)
        => candidate.Equals(owner, StringComparison.OrdinalIgnoreCase) ||
           candidate.StartsWith(owner.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizePath(string path, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch { return false; }
    }

    private static bool IsUnder(string path, string root)
    {
        if (!TryNormalizePath(root, out var normalizedRoot)) return false;
        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemPath(string path)
        => IsUnder(path, WindowsDirectory) ||
           IsUnder(path, SystemDirectory) ||
           IsUnder(path, ProgramDataWindows) ||
           path.Equals(Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
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
