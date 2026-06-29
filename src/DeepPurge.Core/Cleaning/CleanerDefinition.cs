using System.Text.Json;
using System.Text.Json.Serialization;
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

public record CleanerValidationIssue(
    CleanerValidationSeverity Severity,
    string RuleName,
    string Field,
    string Message);

public class CleanerValidationReport
{
    public string FilePath { get; init; } = "";
    public int SchemaVersion { get; init; } = CleanerDefinitionRunner.CurrentSchemaVersion;
    public string SchemaId { get; init; } = CleanerDefinitionRunner.SchemaId;
    public string Provenance { get; init; } = "";
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
                reports.Add(ValidateFile(file));
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

        try
        {
            var json = File.ReadAllText(filePath);
            var document = ParseDocument(json);
            return ValidateRules(
                document.Rules,
                filePath,
                document.Issues,
                document.SchemaVersion,
                document.SchemaId,
                document.Provenance);
        }
        catch (JsonException ex)
        {
            return new CleanerValidationReport
            {
                FilePath = filePath,
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
        string provenance = "")
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
            return new DeleteSummary(0, 1, 0, options.DryRun);
        }

        long freed = 0;
        int cleaned = 0, skipped = 0;
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

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];

            if (!SafetyGuard.IsPathSafeToDelete(f)) { skipped++; continue; }

            try
            {
                var fi = new FileInfo(f);
                var sz = fi.Length;

                if (!options.DryRun)
                {
                    if (options.SecureDelete) SecureDelete.Wipe(f);
                    else SafetyGuard.SafeDeleteFile(f);
                }

                freed += sz;
                cleaned++;
            }
            catch { skipped++; }

            progress?.Report(new DeleteProgress(i + 1, files.Count, freed, f, false));
        }

        foreach (var fr in rule.Files.Where(f => f.RemoveSelf))
        {
            var expanded = Environment.ExpandEnvironmentVariables(fr.Path);
            if (!options.DryRun && Directory.Exists(expanded) && SafetyGuard.IsPathSafeToDelete(expanded))
            {
                try { SafetyGuard.SafeDeleteDirectory(expanded); }
                catch (Exception ex) { Log.Warn($"RemoveSelf '{expanded}': {ex.Message}"); }
            }
        }

        foreach (var regPath in rule.Registry)
        {
            var result = RegistryDeletion.DeleteKeyTree(regPath, "cleaner-regkey", options.DryRun);
            if (result.Status is RegistryDeletionStatus.Deleted or RegistryDeletionStatus.DryRun or RegistryDeletionStatus.SkippedMissing)
                continue;

            Log.Warn($"Cleaner registry delete '{regPath}' skipped: {result.Status} {result.ErrorMessage}");
        }

        if (!options.DryRun && cleaned > 0)
            ActivityLog.Record("cleaner", $"{rule.Name}: {cleaned} items", freed, cleaned);

        return new DeleteSummary(cleaned, skipped, freed, options.DryRun);
    }

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
