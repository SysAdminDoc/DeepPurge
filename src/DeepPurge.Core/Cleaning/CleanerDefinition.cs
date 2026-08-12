using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Cleaning;

public class CleanerRule
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Detect { get; set; } = new();
    public List<string> DetectFile { get; set; } = new();
    public List<CleanerFileRule> Files { get; set; } = new();
    public List<string> Registry { get; set; } = new();
}

public class CleanerDefinitionDocument
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = CleanerDefinitionRunner.SchemaId;
    public int SchemaVersion { get; set; } = CleanerDefinitionRunner.CurrentSchemaVersion;
    public string Provenance { get; set; } = "";
    public List<CleanerRule> Rules { get; set; } = new();
}

public class CleanerFileRule
{
    public string Path { get; set; } = "";
    public string Pattern { get; set; } = "*";
    public bool Recurse { get; set; }
    public bool RemoveSelf { get; set; }
}

public enum CleanerValidationSeverity { Info, Warning, Error }

public enum CleanerRiskLevel { Low, Medium, High, Blocked }

public enum CleanerTrustState
{
    TrustedBundled,
    LocalReview,
    LegacyReview,
    Quarantined,
    Blocked,
}

/// <summary>
/// Machine-readable target diff used before a cleaner database is replaced.
/// Targets are expanded/normalized before comparison so a source update cannot
/// silently widen its deletion scope behind a changed variable or rule name.
/// </summary>
public sealed record CleanerTargetDiff(
    int PreviousRuleCount,
    int CandidateRuleCount,
    IReadOnlyList<string> AddedTargets,
    IReadOnlyList<string> RemovedTargets,
    IReadOnlyList<string> AddedRegistryTargets,
    IReadOnlyList<string> RemovedRegistryTargets)
{
    public bool HasChanges => AddedTargets.Count > 0 || RemovedTargets.Count > 0 ||
                              AddedRegistryTargets.Count > 0 || RemovedRegistryTargets.Count > 0;
    public int AddedTargetCount => AddedTargets.Count + AddedRegistryTargets.Count;
    public int RemovedTargetCount => RemovedTargets.Count + RemovedRegistryTargets.Count;
    public string Summary => HasChanges
        ? $"+{AddedTargetCount} target(s), -{RemovedTargetCount} target(s)"
        : "No target changes";

    public static CleanerTargetDiff Empty(int previousRules = 0, int candidateRules = 0)
        => new(previousRules, candidateRules, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>());
}

public record CleanerValidationIssue(
    CleanerValidationSeverity Severity,
    string RuleName,
    string Field,
    string Message);

public record CleanerValidationReport
{
    public string FilePath { get; init; } = "";
    public int SchemaVersion { get; init; } = CleanerDefinitionRunner.CurrentSchemaVersion;
    public string SchemaId { get; init; } = CleanerDefinitionRunner.SchemaId;
    public string Provenance { get; init; } = "";
    public string Origin { get; init; } = "";
    public string ContentSha256 { get; init; } = "";
    public CleanerTrustState TrustState { get; init; } = CleanerTrustState.LocalReview;
    public string? LastKnownGoodPath { get; init; }
    public string? QuarantinePath { get; init; }
    public List<CleanerRule> Rules { get; init; } = new();
    public List<CleanerValidationIssue> Issues { get; init; } = new();
    public CleanerRiskLevel RiskLevel { get; init; } = CleanerRiskLevel.Low;
    public long EstimatedBytes { get; init; }
    public int EstimatedItems { get; init; }
    public List<string> ExpandedTargets { get; init; } = new();
    public List<string> RegistryScopes { get; init; } = new();
    public bool IsValid => !Issues.Any(i => i.Severity == CleanerValidationSeverity.Error);
    public string FileName => string.IsNullOrWhiteSpace(FilePath) ? "(memory)" : Path.GetFileName(FilePath);
    public string Status => IsValid ? "Ready" : "Blocked";
    public string SchemaDisplay => SchemaVersion <= 0 ? "legacy array" : $"v{SchemaVersion}";
    public string ProvenanceDisplay => string.IsNullOrWhiteSpace(Provenance) ? "(none)" : Provenance;
    public string OriginDisplay => string.IsNullOrWhiteSpace(Origin) ? "(unknown)" : Origin;
    public string TrustDisplay => TrustState switch
    {
        CleanerTrustState.TrustedBundled => "Trusted bundled",
        CleanerTrustState.LocalReview => "Local review",
        CleanerTrustState.LegacyReview => "Legacy review",
        CleanerTrustState.Quarantined => "Quarantined",
        _ => "Blocked",
    };
    public string HashDisplay => string.IsNullOrWhiteSpace(ContentSha256)
        ? "(unavailable)"
        : ContentSha256.Length <= 12 ? ContentSha256 : ContentSha256[..12];
    public string RiskLabel => RiskLevel.ToString();
    public int ErrorCount => Issues.Count(i => i.Severity == CleanerValidationSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == CleanerValidationSeverity.Warning);
    public string RegistryScopesDisplay => RegistryScopes.Count == 0 ? "(none)" : string.Join(", ", RegistryScopes.Distinct(StringComparer.OrdinalIgnoreCase));
    public string ExpandedTargetsDisplay => ExpandedTargets.Count == 0 ? "(none)" : string.Join("; ", ExpandedTargets.Take(4));
    public string IssuesDisplay => Issues.Count == 0
        ? "No issues"
        : string.Join("; ", Issues.Take(4).Select(i => $"{i.Severity}: {i.RuleName}.{i.Field}: {i.Message}"));
}

public static class CleanerDefinitionRunner
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://sysadmindoc.github.io/deeppurge/schemas/cleaner-definition.v1.json";

    private const string SchemaResourceName = "DeepPurge.CleanerDefinition.Schema.v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HashSet<string> RuleFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "Description", "Detect", "DetectFile", "Files", "Registry",
    };

    private static readonly HashSet<string> FileRuleFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Path", "Pattern", "Recurse", "RemoveSelf",
    };

    private static readonly HashSet<string> DocumentFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "$schema", "Schema", "SchemaVersion", "Rules", "Provenance",
    };

    public static CleanerTargetDiff CompareTargets(
        IEnumerable<CleanerRule> previous,
        IEnumerable<CleanerRule> candidate)
    {
        var oldRules = previous.ToList();
        var newRules = candidate.ToList();
        var oldTargets = oldRules.SelectMany(TargetsForRule)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newTargets = newRules.SelectMany(TargetsForRule)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldRegistry = oldRules.SelectMany(r => r.Registry.Select(NormalizeTarget))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newRegistry = newRules.SelectMany(r => r.Registry.Select(NormalizeTarget))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new CleanerTargetDiff(
            oldRules.Count,
            newRules.Count,
            newTargets.Except(oldTargets, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            oldTargets.Except(newTargets, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            newRegistry.Except(oldRegistry, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            oldRegistry.Except(newRegistry, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public static CleanerTargetDiff CompareWinapp2Targets(
        IEnumerable<Winapp2Entry> previous,
        IEnumerable<Winapp2Entry> candidate)
    {
        var oldEntries = previous.ToList();
        var newEntries = candidate.ToList();
        var oldTargets = oldEntries.SelectMany(TargetsForWinapp2Entry)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newTargets = newEntries.SelectMany(TargetsForWinapp2Entry)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldRegistry = oldEntries.SelectMany(RegistryTargetsForWinapp2Entry)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newRegistry = newEntries.SelectMany(RegistryTargetsForWinapp2Entry)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new CleanerTargetDiff(
            oldEntries.Count,
            newEntries.Count,
            newTargets.Except(oldTargets, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            oldTargets.Except(newTargets, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            newRegistry.Except(oldRegistry, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            oldRegistry.Except(newRegistry, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList());
    }

    internal static bool IsProtectedCleanerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('/', '\\');
        return (normalized.Contains("\\Microsoft.DesktopAppInstaller_", StringComparison.OrdinalIgnoreCase) &&
                normalized.Contains("\\LocalState", StringComparison.OrdinalIgnoreCase)) ||
               normalized.Contains("\\Microsoft\\WinGet\\", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> TargetsForRule(CleanerRule rule)
        => rule.Files.Select(file => NormalizeTarget(
                $"{Environment.ExpandEnvironmentVariables(file.Path)}|{file.Pattern}|recurse={file.Recurse}|removeSelf={file.RemoveSelf}"))
            .Concat(rule.DetectFile.Select(path => NormalizeTarget(Environment.ExpandEnvironmentVariables(path))));

    private static IEnumerable<string> TargetsForWinapp2Entry(Winapp2Entry entry)
        => entry.FileKeys.Select(NormalizeWinapp2FileTarget)
            .Concat(entry.DetectFile.Select(path => NormalizeTarget(Environment.ExpandEnvironmentVariables(path))));

    private static IEnumerable<string> RegistryTargetsForWinapp2Entry(Winapp2Entry entry)
        => entry.RegKeys.Select(NormalizeTarget)
            .Concat(entry.Detect.Select(NormalizeTarget));

    private static string NormalizeWinapp2FileTarget(string raw)
    {
        var parts = raw.Split('|');
        var path = parts.Length == 0 ? raw : Environment.ExpandEnvironmentVariables(parts[0]);
        return NormalizeTarget(string.Join('|', new[] { path }.Concat(parts.Skip(1))));
    }

    private static string NormalizeTarget(string value)
        => value.Trim().Replace('/', '\\');

    public static List<CleanerRule> LoadAll()
    {
        var rules = new List<CleanerRule>();
        try
        {
            foreach (var report in ValidateAll())
            {
                if (report.IsValid)
                {
                    rules.AddRange(report.Rules);
                    continue;
                }

                Log.Warn($"Cleaner validation blocked '{report.FilePath}': {report.IssuesDisplay}");
            }
        }
        catch (Exception ex) { Log.Warn($"Cleaner scan: {ex.Message}"); }
        return rules;
    }

    public static List<CleanerValidationReport> ValidateAll()
    {
        var reports = new List<CleanerValidationReport>();
        try
        {
            EnsureBundledCleaners();
            var dir = DataPaths.Cleaners;
            if (!Directory.Exists(dir)) return reports;

            foreach (var file in Directory.GetFiles(dir, "*.cleaner.json"))
            {
                var report = ValidateFile(file);
                if (report.IsValid)
                {
                    reports.Add(report with { LastKnownGoodPath = PreserveLastKnownGood(file) });
                    continue;
                }

                var quarantine = QuarantineInvalid(file, report.ContentSha256, out var quarantineReason);
                reports.Add(report with
                {
                    TrustState = quarantine is null ? CleanerTrustState.Blocked : CleanerTrustState.Quarantined,
                    QuarantinePath = quarantine,
                    LastKnownGoodPath = ExistingLastKnownGood(file),
                    Issues = quarantine is null
                        ? report.Issues.Concat(new[]
                        {
                            new CleanerValidationIssue(
                                CleanerValidationSeverity.Error,
                                "(file)",
                                "Quarantine",
                                quarantineReason),
                        }).ToList()
                        : report.Issues,
                });
            }
        }
        catch (Exception ex) { Log.Warn($"Cleaner validation scan: {ex.Message}"); }
        return reports;
    }

    public static CleanerValidationReport ValidateFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new CleanerValidationReport
            {
                FilePath = filePath ?? "",
                RiskLevel = CleanerRiskLevel.Blocked,
                Issues =
                [
                    new CleanerValidationIssue(
                        CleanerValidationSeverity.Error,
                        "(file)",
                        "FilePath",
                        "Cleaner definition file was not found.")
                ],
            };
        }

        var contentSha256 = "";
        try
        {
            var json = File.ReadAllText(filePath);
            contentSha256 = ComputeFileSha256(filePath);
            var document = ParseDocument(json);
            return ValidateRules(
                document.Rules,
                filePath,
                document.Issues,
                document.SchemaVersion,
                document.SchemaId,
                document.Provenance,
                OriginFor(filePath, document.Provenance),
                TrustFor(document.SchemaVersion, document.Provenance),
                contentSha256);
        }
        catch (JsonException ex)
        {
            return new CleanerValidationReport
            {
                FilePath = filePath,
                Origin = "Local file",
                ContentSha256 = contentSha256,
                TrustState = CleanerTrustState.Blocked,
                RiskLevel = CleanerRiskLevel.Blocked,
                Issues =
                [
                    new CleanerValidationIssue(
                        CleanerValidationSeverity.Error,
                        "(json)",
                        "Schema",
                        ex.Message)
                ],
            };
        }
        catch (Exception ex)
        {
            return new CleanerValidationReport
            {
                FilePath = filePath,
                Origin = "Local file",
                ContentSha256 = contentSha256,
                TrustState = CleanerTrustState.Blocked,
                RiskLevel = CleanerRiskLevel.Blocked,
                Issues =
                [
                    new CleanerValidationIssue(
                        CleanerValidationSeverity.Error,
                        "(file)",
                        "Read",
                        ex.Message)
                ],
            };
        }
    }

    public static CleanerValidationReport ValidateRule(CleanerRule rule, string source = "(memory)")
        => ValidateRules([rule], source);

    public static CleanerValidationReport ValidateRules(
        IEnumerable<CleanerRule> rules,
        string source = "(memory)",
        List<CleanerValidationIssue>? initialIssues = null,
        int schemaVersion = CurrentSchemaVersion,
        string? schemaId = null,
        string provenance = "",
        string? origin = null,
        CleanerTrustState? trustState = null,
        string contentSha256 = "")
    {
        var ruleList = rules.ToList();
        var issues = initialIssues ?? new List<CleanerValidationIssue>();
        var expandedTargets = new List<string>();
        var registryScopes = new List<string>();
        var risk = CleanerRiskLevel.Low;
        long estimatedBytes = 0;
        int estimatedItems = 0;

        if (ruleList.Count == 0)
        {
            issues.Add(new CleanerValidationIssue(
                CleanerValidationSeverity.Error,
                "(file)",
                "Rules",
                "Cleaner definition must contain at least one rule."));
        }

        foreach (var rule in ruleList)
        {
            var ruleName = string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name.Trim();
            if (string.IsNullOrWhiteSpace(rule.Name))
                AddIssue(issues, CleanerValidationSeverity.Error, ruleName, "Name", "Rule name is required.");

            foreach (var detect in rule.Detect)
                ValidateRegistryPath(detect, ruleName, "Detect", issues, registryScopes, forDelete: false, ref risk);

            foreach (var detectFile in rule.DetectFile)
                ValidatePath(detectFile, ruleName, "DetectFile", issues, expandedTargets, forDelete: false, ref risk);

            foreach (var file in rule.Files)
            {
                ValidateFileRule(file, ruleName, issues, expandedTargets, ref risk);
            }

            foreach (var regPath in rule.Registry)
                ValidateRegistryPath(regPath, ruleName, "Registry", issues, registryScopes, forDelete: true, ref risk);

            try
            {
                var (size, count) = Preview(rule);
                estimatedBytes += size;
                estimatedItems += count + rule.Registry.Count;
            }
            catch (Exception ex)
            {
                AddIssue(issues, CleanerValidationSeverity.Warning, ruleName, "Preview", $"Estimate failed: {ex.Message}");
            }
        }

        if (issues.Any(i => i.Severity == CleanerValidationSeverity.Error))
            risk = CleanerRiskLevel.Blocked;

        return new CleanerValidationReport
        {
            FilePath = source,
            SchemaVersion = schemaVersion,
            SchemaId = string.IsNullOrWhiteSpace(schemaId) ? SchemaId : schemaId,
            Provenance = provenance,
            Origin = origin ?? (source == "(memory)" ? "In-memory" : source),
            ContentSha256 = contentSha256,
            TrustState = trustState ?? TrustFor(schemaVersion, provenance),
            Rules = ruleList,
            Issues = issues,
            RiskLevel = risk,
            EstimatedBytes = estimatedBytes,
            EstimatedItems = estimatedItems,
            ExpandedTargets = expandedTargets.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RegistryScopes = registryScopes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    public static string GetSchemaJson()
    {
        try
        {
            using var stream = typeof(CleanerDefinitionRunner).Assembly.GetManifestResourceStream(SchemaResourceName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex) { Log.Warn($"Cleaner schema resource read failed: {ex.Message}"); }

        return "{}";
    }

    public static List<CleanerRule> FilterApplicable(List<CleanerRule> rules)
    {
        return rules.Where(IsApplicable).ToList();
    }

    public static bool IsApplicable(CleanerRule rule)
    {
        foreach (var regKey in rule.Detect)
        {
            if (!RegistryKeyExists(regKey)) return false;
        }
        foreach (var filePath in rule.DetectFile)
        {
            var expanded = Environment.ExpandEnvironmentVariables(filePath);
            if (expanded.Contains("..")) return false;
            if (!File.Exists(expanded) && !Directory.Exists(expanded)) return false;
        }
        return true;
    }

    public static (long Size, int ItemCount) Preview(CleanerRule rule)
    {
        long size = 0;
        int count = 0;
        foreach (var fr in rule.Files)
        {
            var expanded = Environment.ExpandEnvironmentVariables(fr.Path);
            if (!Directory.Exists(expanded)) continue;

            try
            {
                var enumFiles = fr.Recurse
                    ? SafetyGuard.SafeEnumerateFiles(expanded, fr.Pattern)
                    : Directory.EnumerateFiles(expanded, fr.Pattern, SearchOption.TopDirectoryOnly);
                foreach (var f in enumFiles)
                {
                    if (!SafetyGuard.IsPathSafeToDelete(f)) continue;
                    try { size += new FileInfo(f).Length; count++; }
                    catch { /* skip */ }
                }
            }
            catch { /* skip */ }
        }
        return (size, count);
    }

    public static DeleteSummary Execute(CleanerRule rule, DeleteOptions options,
        IProgress<DeleteProgress>? progress = null, CancellationToken ct = default)
    {
        var validation = ValidateRule(rule);
        if (!validation.IsValid)
        {
            Log.Warn($"Cleaner rule blocked '{rule.Name}': {validation.IssuesDisplay}");
            return DeleteSummary.FromResults(
                new[]
                {
                    DeletionExecutor.FailedExternal(
                        rule.Name,
                        "cleaner-rule",
                        validation.IssuesDisplay),
                },
                options.DryRun);
        }

        var files = new List<string>();

        foreach (var fr in rule.Files)
        {
            var expanded = Environment.ExpandEnvironmentVariables(fr.Path);
            if (!Directory.Exists(expanded)) continue;

            try
            {
                var enumFiles = fr.Recurse
                    ? SafetyGuard.SafeEnumerateFiles(expanded, fr.Pattern)
                    : Directory.EnumerateFiles(expanded, fr.Pattern, SearchOption.TopDirectoryOnly);
                files.AddRange(enumFiles);
            }
            catch { /* skip */ }
        }

        var executor = new DeletionExecutor();
        var results = new List<DeletionResult>(files.Count + rule.Files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            long size = 0;
            try { size = new FileInfo(f).Length; } catch { /* executor reports the exact failure */ }
            var result = executor.Execute(
                new DeletionRequest(f, ExpectedSizeBytes: size, Operation: "cleaner-file"),
                options,
                ct);
            results.Add(result);
            progress?.Report(new DeleteProgress(
                i + 1,
                files.Count,
                CurrentBytes(results, options.DryRun),
                f,
                !result.IsConfirmed && !result.IsPreview));
        }

        foreach (var fr in rule.Files.Where(f => f.RemoveSelf))
        {
            var expanded = Environment.ExpandEnvironmentVariables(fr.Path);
            results.Add(executor.Execute(
                new DeletionRequest(expanded, IsDirectory: true, Operation: "cleaner-remove-self"),
                options,
                ct));
        }

        foreach (var regPath in rule.Registry)
        {
            var result = RegistryDeletion.DeleteKeyTree(regPath, "cleaner-regkey", options.DryRun);
            results.Add(ToDeletionResult(result));
            if (result.Status is not RegistryDeletionStatus.Deleted and
                not RegistryDeletionStatus.DryRun)
                Log.Warn($"Cleaner registry delete '{regPath}' skipped: {result.Status} {result.ErrorMessage}");
        }

        var summary = DeleteSummary.FromResults(results, options.DryRun);
        if (!options.DryRun && summary.ItemsConfirmed > 0)
            ActivityLog.Record("cleaner", $"{rule.Name}: {summary.ItemsConfirmed} items", summary.BytesConfirmed, summary.ItemsConfirmed);

        return summary;
    }

    private static DeletionResult ToDeletionResult(RegistryDeletionResult result)
    {
        var outcome = result.Status switch
        {
            RegistryDeletionStatus.DryRun => DeletionOutcomeKind.Preview,
            RegistryDeletionStatus.Deleted => DeletionOutcomeKind.PermanentlyDeleted,
            RegistryDeletionStatus.SkippedMissing or
            RegistryDeletionStatus.SkippedMalformedPath or
            RegistryDeletionStatus.SkippedUnsafePath or
            RegistryDeletionStatus.SkippedSymlink or
            RegistryDeletionStatus.SkippedDrift => DeletionOutcomeKind.Skipped,
            _ => DeletionOutcomeKind.Failed,
        };
        return new DeletionResult(
            result.Path,
            outcome,
            0,
            "cleaner-regkey",
            result.Deleted || result.Status == RegistryDeletionStatus.DryRun
                ? null
                : result.ErrorMessage ?? result.Status.ToString(),
            Recoverable: result.Deleted);
    }

    private static long CurrentBytes(
        IReadOnlyList<DeletionResult> results,
        bool dryRun)
        => dryRun
            ? results.Where(r => r.IsPreview).Sum(r => r.SizeBytes)
            : results.Where(r => r.IsConfirmed).Sum(r => r.SizeBytes);

    private sealed record ParsedCleanerDocument(
        List<CleanerRule> Rules,
        int SchemaVersion,
        string SchemaId,
        string Provenance,
        List<CleanerValidationIssue> Issues);

    private static ParsedCleanerDocument ParseDocument(string json)
    {
        var issues = new List<CleanerValidationIssue>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            InspectRuleArray(doc.RootElement, issues);
            AddIssue(
                issues,
                CleanerValidationSeverity.Warning,
                "(file)",
                "SchemaVersion",
                "Legacy root-array cleaner format is supported, but new cleaner files should declare SchemaVersion 1 with a Rules array.");
            var legacyRules = JsonSerializer.Deserialize<List<CleanerRule>>(json, JsonOptions) ?? new();
            return new ParsedCleanerDocument(legacyRules, 0, "", "", issues);
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            AddIssue(issues, CleanerValidationSeverity.Error, "(file)", "Schema", "Root value must be a cleaner document object.");
            return new ParsedCleanerDocument(new List<CleanerRule>(), CurrentSchemaVersion, SchemaId, "", issues);
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!DocumentFields.Contains(prop.Name))
                AddIssue(issues, CleanerValidationSeverity.Error, "(file)", prop.Name, "Unknown cleaner document field.");
        }

        var schemaId = ReadOptionalString(doc.RootElement, "$schema") ??
                       ReadOptionalString(doc.RootElement, "Schema") ??
                       SchemaId;
        var provenance = ReadOptionalString(doc.RootElement, "Provenance") ?? "";
        var schemaVersion = CurrentSchemaVersion;

        if (!doc.RootElement.TryGetProperty("SchemaVersion", out var versionElement))
        {
            AddIssue(issues, CleanerValidationSeverity.Error, "(file)", "SchemaVersion", "SchemaVersion is required.");
        }
        else if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out schemaVersion))
        {
            AddIssue(issues, CleanerValidationSeverity.Error, "(file)", "SchemaVersion", "SchemaVersion must be an integer.");
        }
        else if (schemaVersion > CurrentSchemaVersion)
        {
            AddIssue(
                issues,
                CleanerValidationSeverity.Error,
                "(file)",
                "SchemaVersion",
                $"Unsupported future schema version {schemaVersion}; this DeepPurge build supports version {CurrentSchemaVersion}.");
        }
        else if (schemaVersion < CurrentSchemaVersion)
        {
            AddIssue(
                issues,
                CleanerValidationSeverity.Warning,
                "(file)",
                "SchemaVersion",
                $"Older schema version {schemaVersion}; validate and migrate to version {CurrentSchemaVersion}.");
        }

        if (!doc.RootElement.TryGetProperty("Rules", out var rulesElement))
        {
            AddIssue(issues, CleanerValidationSeverity.Error, "(file)", "Rules", "Rules array is required.");
            return new ParsedCleanerDocument(new List<CleanerRule>(), schemaVersion, schemaId, provenance, issues);
        }

        if (rulesElement.ValueKind != JsonValueKind.Array)
        {
            AddIssue(issues, CleanerValidationSeverity.Error, "(file)", "Rules", "Rules must be an array.");
            return new ParsedCleanerDocument(new List<CleanerRule>(), schemaVersion, schemaId, provenance, issues);
        }

        InspectRuleArray(rulesElement, issues);
        var rules = JsonSerializer.Deserialize<List<CleanerRule>>(rulesElement.GetRawText(), JsonOptions) ?? new();
        return new ParsedCleanerDocument(rules, schemaVersion, schemaId, provenance, issues);
    }

    private static void InspectRuleArray(JsonElement rulesElement, List<CleanerValidationIssue> issues)
    {
        int index = 0;
        foreach (var ruleElement in rulesElement.EnumerateArray())
        {
            var ruleName = JsonRuleName(ruleElement, index);
            if (ruleElement.ValueKind != JsonValueKind.Object)
            {
                AddIssue(issues, CleanerValidationSeverity.Error, ruleName, "Schema", "Each rule must be a JSON object.");
                index++;
                continue;
            }

            foreach (var prop in ruleElement.EnumerateObject())
            {
                if (!RuleFields.Contains(prop.Name))
                {
                    AddIssue(issues, CleanerValidationSeverity.Error, ruleName, prop.Name, "Unknown cleaner rule field.");
                    continue;
                }

                ValidateRulePropertyShape(prop, ruleName, issues);
            }

            index++;
        }
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString();
        }
        return null;
    }

    private static void ValidateRulePropertyShape(
        JsonProperty prop,
        string ruleName,
        List<CleanerValidationIssue> issues)
    {
        if (prop.NameEquals("Name") || prop.NameEquals("Description"))
        {
            if (prop.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                AddIssue(issues, CleanerValidationSeverity.Error, ruleName, prop.Name, "Expected a string.");
            return;
        }

        if (prop.NameEquals("Detect") || prop.NameEquals("DetectFile") || prop.NameEquals("Registry"))
        {
            ValidateStringArray(prop.Value, ruleName, prop.Name, issues);
            return;
        }

        if (prop.NameEquals("Files"))
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                AddIssue(issues, CleanerValidationSeverity.Error, ruleName, "Files", "Expected an array.");
                return;
            }

            int index = 0;
            foreach (var fileRule in prop.Value.EnumerateArray())
            {
                if (fileRule.ValueKind != JsonValueKind.Object)
                {
                    AddIssue(issues, CleanerValidationSeverity.Error, ruleName, $"Files[{index}]", "File rule must be an object.");
                    index++;
                    continue;
                }

                foreach (var fileProp in fileRule.EnumerateObject())
                {
                    if (!FileRuleFields.Contains(fileProp.Name))
                    {
                        AddIssue(issues, CleanerValidationSeverity.Error, ruleName, $"Files[{index}].{fileProp.Name}", "Unknown file rule field.");
                        continue;
                    }

                    var validType =
                        (fileProp.NameEquals("Path") || fileProp.NameEquals("Pattern")) &&
                        fileProp.Value.ValueKind is (JsonValueKind.String or JsonValueKind.Null);
                    validType |=
                        (fileProp.NameEquals("Recurse") || fileProp.NameEquals("RemoveSelf")) &&
                        fileProp.Value.ValueKind is JsonValueKind.True or JsonValueKind.False;

                    if (!validType)
                        AddIssue(issues, CleanerValidationSeverity.Error, ruleName, $"Files[{index}].{fileProp.Name}", "Unexpected value type.");
                }

                index++;
            }
        }
    }

    private static void ValidateStringArray(
        JsonElement value,
        string ruleName,
        string field,
        List<CleanerValidationIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, "Expected an array of strings.");
            return;
        }

        int index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                AddIssue(issues, CleanerValidationSeverity.Error, ruleName, $"{field}[{index}]", "Expected a string.");
            index++;
        }
    }

    private static void ValidateFileRule(
        CleanerFileRule file,
        string ruleName,
        List<CleanerValidationIssue> issues,
        List<string> expandedTargets,
        ref CleanerRiskLevel risk)
    {
        var expanded = ValidatePath(file.Path, ruleName, "Files.Path", issues, expandedTargets, forDelete: true, ref risk);

        if (string.IsNullOrWhiteSpace(file.Pattern))
            AddIssue(issues, CleanerValidationSeverity.Error, ruleName, "Files.Pattern", "Pattern is required.");

        if (file.Recurse)
            ElevateRisk(ref risk, CleanerRiskLevel.Medium);

        if (IsBroadPattern(file.Pattern) && file.Recurse)
        {
            AddIssue(issues, CleanerValidationSeverity.Warning, ruleName, "Files.Pattern", "Recursive broad wildcard cleanup is high risk; keep the target path narrow.");
            ElevateRisk(ref risk, CleanerRiskLevel.High);
        }

        if (file.RemoveSelf)
        {
            AddIssue(issues, CleanerValidationSeverity.Warning, ruleName, "Files.RemoveSelf", "RemoveSelf deletes the target directory after file cleanup.");
            ElevateRisk(ref risk, CleanerRiskLevel.High);
        }

        if (!string.IsNullOrWhiteSpace(expanded) && IsUserProfileRoot(expanded))
            AddIssue(issues, CleanerValidationSeverity.Error, ruleName, "Files.Path", "Cleaner target cannot be a user profile root.");
    }

    private static string ValidatePath(
        string raw,
        string ruleName,
        string field,
        List<CleanerValidationIssue> issues,
        List<string> expandedTargets,
        bool forDelete,
        ref CleanerRiskLevel risk)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, "Path is required.");
            return string.Empty;
        }

        if (raw.Contains("..", StringComparison.Ordinal))
            AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, "Path traversal is not allowed.");

        var expanded = Environment.ExpandEnvironmentVariables(raw);
        expandedTargets.Add(expanded);

        if (expanded.Contains('%', StringComparison.Ordinal))
            AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, "Environment variable did not resolve.");

        try
        {
            if (!Path.IsPathFullyQualified(expanded))
                AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, "Cleaner target must expand to an absolute path.");
        }
        catch (Exception ex)
        {
            AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, $"Path could not be parsed: {ex.Message}");
        }

        if (forDelete)
        {
            try
            {
                if (!SafetyGuard.IsPathSafeToDelete(expanded))
                    AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, "SafetyGuard blocks this file target.");
                else if (IsProtectedCleanerPath(expanded))
                    AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, "DeepPurge protects Windows package-manager state from cleaner rules.");
            }
            catch (Exception ex)
            {
                AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, $"SafetyGuard could not assess this target: {ex.Message}");
            }
        }

        if (forDelete)
            ElevateRisk(ref risk, CleanerRiskLevel.Medium);

        return expanded;
    }

    private static void ValidateRegistryPath(
        string raw,
        string ruleName,
        string field,
        List<CleanerValidationIssue> issues,
        List<string> registryScopes,
        bool forDelete,
        ref CleanerRiskLevel risk)
    {
        if (!RegistryDeletion.TryParseKeyPath(raw, out var target))
        {
            AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, "Registry path is malformed or uses an unsupported hive.");
            return;
        }

        registryScopes.Add(target.HiveName);

        if (forDelete && !SafetyGuard.IsRegistryPathSafeToDelete(target.CanonicalPath))
            AddIssue(issues, CleanerValidationSeverity.Error, ruleName, field, "SafetyGuard blocks this registry target.");

        if (forDelete)
        {
            ElevateRisk(ref risk, CleanerRiskLevel.Medium);
            if (target.HiveName is "HKLM" or "HKCR" or "HKU")
            {
                AddIssue(issues, CleanerValidationSeverity.Warning, ruleName, field, $"{target.HiveName} cleanup can affect all users or shell behavior.");
                ElevateRisk(ref risk, CleanerRiskLevel.High);
            }
        }
    }

    private static void AddIssue(
        List<CleanerValidationIssue> issues,
        CleanerValidationSeverity severity,
        string ruleName,
        string field,
        string message)
        => issues.Add(new CleanerValidationIssue(severity, ruleName, field, message));

    private static void ElevateRisk(ref CleanerRiskLevel current, CleanerRiskLevel candidate)
    {
        if (candidate > current) current = candidate;
    }

    private static bool IsBroadPattern(string pattern)
        => string.IsNullOrWhiteSpace(pattern) ||
           pattern.Equals("*", StringComparison.Ordinal) ||
           pattern.Equals("*.*", StringComparison.Ordinal);

    private static bool IsUserProfileRoot(string path)
    {
        try
        {
            var normalized = Path.GetFullPath(path).TrimEnd('\\');
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            return normalized.Equals(profile, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string JsonRuleName(JsonElement element, int index)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name.Equals("Name", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    var name = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }

        return $"rule[{index}]";
    }

    private static string OriginFor(string filePath, string provenance)
        => provenance.StartsWith("DeepPurge bundled", StringComparison.OrdinalIgnoreCase)
            ? "DeepPurge bundled"
            : string.IsNullOrWhiteSpace(provenance) ? "Local file" : provenance;

    private static CleanerTrustState TrustFor(int schemaVersion, string provenance)
        => provenance.StartsWith("DeepPurge bundled", StringComparison.OrdinalIgnoreCase)
            ? CleanerTrustState.TrustedBundled
            : schemaVersion <= 0
                ? CleanerTrustState.LegacyReview
                : CleanerTrustState.LocalReview;

    private static string? PreserveLastKnownGood(string filePath)
    {
        try
        {
            var directory = Path.Combine(DataPaths.Cleaners, "LastKnownGood");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, Path.GetFileName(filePath));
            File.Copy(filePath, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Log.Warn($"Cleaner last-known-good copy failed for '{filePath}': {ex.Message}");
            return null;
        }
    }

    private static string? ExistingLastKnownGood(string filePath)
    {
        var path = Path.Combine(DataPaths.Cleaners, "LastKnownGood", Path.GetFileName(filePath));
        return File.Exists(path) ? path : null;
    }

    private static string? QuarantineInvalid(
        string filePath,
        string contentSha256,
        out string reason)
    {
        try
        {
            if (SafetyGuard.IsReparsePoint(filePath))
            {
                reason = "The invalid cleaner file is a reparse point and cannot be quarantined safely.";
                return null;
            }

            var directory = Path.Combine(DataPaths.Cleaners, "Quarantine");
            Directory.CreateDirectory(directory);
            var hash = string.IsNullOrWhiteSpace(contentSha256) ? Guid.NewGuid().ToString("N") : contentSha256[..Math.Min(12, contentSha256.Length)];
            var target = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(filePath)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{hash}.cleaner.json");
            File.Move(filePath, target, overwrite: false);
            reason = "";
            return target;
        }
        catch (Exception ex)
        {
            reason = $"Could not quarantine invalid cleaner definition: {ex.Message}";
            return null;
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void EnsureBundledCleaners()
    {
        try
        {
            var dir = DataPaths.Cleaners;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "bundled-modern-apps.cleaner.json");
            if (File.Exists(target)) return;
            File.WriteAllText(target, BundledCleaners.Wrap(BundledCleaners.ModernApps, "DeepPurge bundled modern-app cleaners"), System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { Log.Warn($"Bundled cleaner extract: {ex.Message}"); }
    }

    private static bool RegistryKeyExists(string path)
    {
        try
        {
            var parts = path.Split('\\', 2);
            if (parts.Length < 2) return false;
            var hive = parts[0].ToUpperInvariant() switch
            {
                "HKCU" => Microsoft.Win32.Registry.CurrentUser,
                "HKLM" => Microsoft.Win32.Registry.LocalMachine,
                "HKCR" => Microsoft.Win32.Registry.ClassesRoot,
                _ => null
            };
            if (hive == null) return false;
            using var key = hive.OpenSubKey(parts[1]);
            return key != null;
        }
        catch { return false; }
    }
}

internal static class BundledCleaners
{
    internal static string Wrap(string rulesJson, string provenance)
        => $$"""
{
  "$schema": "{{CleanerDefinitionRunner.SchemaId}}",
  "SchemaVersion": {{CleanerDefinitionRunner.CurrentSchemaVersion}},
  "Provenance": "{{provenance.Replace("\"", "\\\"")}}",
  "Rules": {{rulesJson}}
}
""";

    internal const string ModernApps = """
[
  {
    "Name": "VS Code",
    "Description": "Visual Studio Code caches and logs",
    "DetectFile": ["%LOCALAPPDATA%\\Programs\\Microsoft VS Code"],
    "Files": [
      { "Path": "%APPDATA%\\Code\\Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Code\\CachedData", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Code\\CachedExtensionVSIXs", "Pattern": "*.vsix", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Code\\logs", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Code\\Service Worker\\CacheStorage", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Cursor",
    "Description": "Cursor AI editor caches and logs",
    "DetectFile": ["%LOCALAPPDATA%\\Programs\\cursor"],
    "Files": [
      { "Path": "%APPDATA%\\Cursor\\Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Cursor\\CachedData", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Cursor\\logs", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Cursor\\Service Worker\\CacheStorage", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Windsurf",
    "Description": "Windsurf editor caches and logs",
    "DetectFile": ["%LOCALAPPDATA%\\Programs\\windsurf"],
    "Files": [
      { "Path": "%APPDATA%\\Windsurf\\Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Windsurf\\CachedData", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Windsurf\\logs", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Discord",
    "Description": "Discord caches and crash reports",
    "DetectFile": ["%LOCALAPPDATA%\\Discord"],
    "Files": [
      { "Path": "%APPDATA%\\discord\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\discord\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\discord\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Slack",
    "Description": "Slack caches and logs",
    "DetectFile": ["%LOCALAPPDATA%\\slack"],
    "Files": [
      { "Path": "%APPDATA%\\Slack\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Slack\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Slack\\logs", "Pattern": "*.log", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Slack\\Service Worker\\CacheStorage", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Microsoft Teams",
    "Description": "Teams caches and logs (new Teams app)",
    "DetectFile": ["%LOCALAPPDATA%\\Packages\\MSTeams_8wekyb3d8bbwe"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\\EBWebView\\Default\\Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\\EBWebView\\Default\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Notion",
    "Description": "Notion desktop caches",
    "DetectFile": ["%LOCALAPPDATA%\\Programs\\Notion"],
    "Files": [
      { "Path": "%APPDATA%\\Notion\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Notion\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Notion\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Obsidian",
    "Description": "Obsidian caches and crash reports",
    "DetectFile": ["%LOCALAPPDATA%\\Obsidian"],
    "Files": [
      { "Path": "%APPDATA%\\obsidian\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\obsidian\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\obsidian\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Figma",
    "Description": "Figma desktop caches",
    "DetectFile": ["%LOCALAPPDATA%\\Figma"],
    "Files": [
      { "Path": "%APPDATA%\\Figma\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Figma\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Figma\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Docker Desktop",
    "Description": "Docker Desktop logs and caches",
    "DetectFile": ["%PROGRAMFILES%\\Docker\\Docker"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Docker\\log", "Pattern": "*.log", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Docker\\wsl\\data\\tmp", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Zen Browser",
    "Description": "Zen Browser caches (Firefox-based)",
    "DetectFile": ["%APPDATA%\\zen"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\zen\\Profiles", "Pattern": "cache2", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\zen\\Profiles", "Pattern": "startupCache", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Arc Browser",
    "Description": "Arc Browser caches (Chromium-based)",
    "DetectFile": ["%LOCALAPPDATA%\\Arc"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Arc\\User Data\\Default\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Arc\\User Data\\Default\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Arc\\User Data\\Default\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Claude Desktop",
    "Description": "Claude Desktop caches and logs",
    "DetectFile": ["%APPDATA%\\Claude"],
    "Files": [
      { "Path": "%APPDATA%\\Claude\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Claude\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Claude\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Claude\\logs", "Pattern": "*.log", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "WSL Caches",
    "Description": "Windows Subsystem for Linux temp and cache files",
    "DetectFile": ["%LOCALAPPDATA%\\Packages\\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Packages\\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\\LocalState\\rootfs\\tmp", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Postman",
    "Description": "Postman API client caches",
    "DetectFile": ["%LOCALAPPDATA%\\Postman"],
    "Files": [
      { "Path": "%APPDATA%\\Postman\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Postman\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Postman\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Spotify",
    "Description": "Spotify caches (streaming data, album art)",
    "DetectFile": ["%APPDATA%\\Spotify"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Spotify\\Storage", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Spotify\\Data", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  }
]
""";
}
