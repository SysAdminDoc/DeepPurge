using System.Text.Json.Serialization;

namespace DeepPurge.Core.InstallMonitor;

public enum InstallTraceMode
{
    Unknown = 0,
    PrePostSnapshot = 1,
    PrePostSnapshotWithDiagnostics = 2,
}

public enum InstallObjectChangeKind
{
    Unknown = 0,
    Created = 1,
    Modified = 2,
    Renamed = 3,
    Deleted = 4,
}

public readonly record struct UsnFileId(ulong LowPart, ulong HighPart = 0)
{
    [JsonIgnore]
    public bool IsEmpty => LowPart == 0 && HighPart == 0;

    public override string ToString()
        => HighPart == 0
            ? LowPart.ToString("x16")
            : $"{HighPart:x16}{LowPart:x16}";
}

public sealed record UsnChange(
    string Path,
    InstallObjectChangeKind ChangeKind,
    DateTime TimestampUtc,
    UsnFileId FileReferenceNumber,
    UsnFileId ParentFileReferenceNumber,
    bool PathResolved,
    uint RawReason,
    ushort RecordMajorVersion);

public sealed record SysmonRegistryChange(
    string EventType,
    string TargetObject,
    string Details,
    DateTime TimeCreated,
    string ProcessGuid = "",
    int ProcessId = 0,
    string Image = "");

public sealed record SnapshotEntry(
    string Path,
    long SizeBytes,
    DateTime LastWriteUtc,
    string? Sha256 = null,
    uint? VolumeSerialNumber = null,
    ulong? FileIndex = null,
    InstallObjectChangeKind ChangeKind = InstallObjectChangeKind.Unknown)
{
    [JsonIgnore]
    public bool HasStableIdentity =>
        VolumeSerialNumber.HasValue &&
        FileIndex.HasValue &&
        ChangeKind == InstallObjectChangeKind.Created;
}

public sealed record RegistryKeyEntry(string Path);

public sealed record InstallerIdentity(
    string Path,
    long SizeBytes,
    DateTime LastWriteUtc,
    string Sha256,
    uint VolumeSerialNumber,
    ulong FileIndex);

public sealed class InstallSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProgramName { get; set; } = "";
    public string InstallerPath { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public List<SnapshotEntry> Files { get; set; } = new();
    public List<RegistryKeyEntry> RegistryKeys { get; set; } = new();
}

public sealed class InstallDelta
{
    /// <summary>
    /// Objects proven absent from the pre-launch snapshot and present after
    /// the installer window. Only entries in this collection can be replayed.
    /// </summary>
    public List<SnapshotEntry> AddedFiles { get; set; } = new();

    /// <summary>
    /// Objects that existed before launch and changed during the trace.
    /// These are evidence only and are never replayed.
    /// </summary>
    public List<SnapshotEntry> ModifiedFiles { get; set; } = new();

    public List<string> AddedRegistryKeys { get; set; } = new();
    public List<string> RemovedFiles { get; set; } = new();
    public List<string> RemovedRegistryKeys { get; set; } = new();
    public long TotalAddedBytes => AddedFiles.Sum(f => f.SizeBytes);
    public bool IsUpgrade => ModifiedFiles.Count > 0 ||
                             RemovedFiles.Count > 0 ||
                             RemovedRegistryKeys.Count > 0;
}

public sealed class InstallTraceDiagnostics
{
    public DateTime WindowStartedUtc { get; set; }
    public DateTime WindowEndedUtc { get; set; }
    public int InstallerProcessId { get; set; }
    public bool UsnAvailable { get; set; }
    public bool SysmonAvailable { get; set; }
    public bool SysmonProcessTreeCorrelated { get; set; }
    public string UsnAttribution { get; set; } =
        "Volume-window diagnostic only; never replay eligible.";
    public List<UsnChange> FileChanges { get; set; } = new();
    public List<SysmonRegistryChange> RegistryChanges { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class InstallManifest
{
    public int SchemaVersion { get; set; }
    public string ProgramName { get; set; } = "";
    public InstallTraceMode TraceMode { get; set; }
    public DateTime TraceStartedUtc { get; set; }
    public DateTime TraceEndedUtc { get; set; }
    public InstallerIdentity? Installer { get; set; }
    public bool ReplayEligible { get; set; }
    public string ReplayEligibilityReason { get; set; } = "";
    public InstallDelta Delta { get; set; } = new();
    public InstallTraceDiagnostics Diagnostics { get; set; } = new();

    [JsonIgnore]
    public bool LoadedFromTrustedStore { get; internal set; }
}

public sealed record InstallReplayResult(
    int Removed,
    int Skipped,
    long Freed,
    IReadOnlyList<string> SkippedReasons,
    string? BlockedReason = null)
{
    [JsonIgnore]
    public bool IsBlocked => !string.IsNullOrWhiteSpace(BlockedReason);

    public void Deconstruct(out int removed, out int skipped, out long freed)
    {
        removed = Removed;
        skipped = Skipped;
        freed = Freed;
    }
}
