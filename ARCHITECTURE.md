# DeepPurge architecture

This document covers the boundaries that matter when changing DeepPurge. Scanner-specific details live beside the implementation.

## Solution layout

```text
DeepPurge.sln
  src/DeepPurge.Core/       Scanners, safety, recovery, execution, diagnostics
  src/DeepPurge.App/        WPF GUI with an administrator manifest
  src/DeepPurge.Cli/        Scriptable CLI with an asInvoker manifest
  tests/DeepPurge.Tests/    xUnit v3 with 459 cases
  tools/DeepPurge.Capture/  Private-desktop production screenshot tool
```

`DeepPurge.Core` has one intentional WPF dependency because `IconExtractor` returns `ImageSource`. Moving icon materialization into the app layer would remove that dependency.

## Safety boundary

### SafetyGuard

Every destructive file, registry, service, scheduled-task, driver, and system-cleanup route must cross the applicable safety policy before it mutates state. The guard blocks protected directories, boot files, critical registry roots, core services, and Microsoft task namespaces.

Tests keep the deny rules from changing silently.

### DeleteOptions

Cleanup pipelines share one options record:

```csharp
public readonly record struct DeleteOptions(
    bool DryRun = false,
    bool SecureDelete = false,
    bool UseRecycleBin = true,
    int MinAgeDays = 0);
```

Extend this record when a deletion policy changes. Don't add a second set of unrelated boolean parameters.

### Typed outcomes

Cleanup does not report planned work as completed work. Results distinguish previewed, recycled, permanently deleted, securely deleted, queued, skipped, failed, and cancelled items. Recovery manifests are written only after a confirmed mutation.

### Recovery evidence

Registry and administrative workflows bind recovery data to the original operation. Hashes, object identity, owner and access-control facts, and operation IDs prevent a nearby or changed file from being accepted as rollback data.

Some actions are not recoverable. The result model must say so.

## Persistent data

`DataPaths` resolves every setting, log, backup, snapshot, and activity path. A `DeepPurge.portable` marker beside the executable redirects the root from `%LocalAppData%\DeepPurge\` to `./Data/`.

Production code should not hard-code a user-data root.

`Log` writes an append-only, thread-safe rotating file. Operational exceptions that are handled for the user still belong in the log.

`PrivacyMaintenance` applies retention rules to logs, activity records, and deletion manifests. Path scrubbing affects reports and display data. It does not rewrite rollback records whose paths are required for recovery.

## External process execution

Windows-owned tools resolve from protected absolute system paths. Package managers resolve from known install roots. Arguments are passed separately, output is bounded, and timeout or cancellation has a typed result.

Package-manager operations run through the original desktop-user broker instead of inheriting the GUI's administrator token.

## GUI

### View model

`MainViewModel` is split across two files:

- `MainViewModel.cs` contains program inventory, cleanup, evidence, autorun, settings, and shared behavior.
- `MainViewModel.Extensions.cs` contains driver, startup-impact, shortcut, duplicate, cleaner, repair, schedule, health, and system-slimming behavior.

Observable collections bind directly to WPF grids. Long work runs asynchronously and marshals collection changes back to the application dispatcher.

### Navigation

Sidebar `RadioButton` controls use their `Tag` as the panel identifier. `MainWindow.NavButton_Checked` hides the current panel, selects the matching target, builds contextual actions, and starts any safe lazy load.

Adding a panel requires all of the following:

1. Add a tagged navigation control.
2. Add a named panel with collapsed default visibility.
3. Add the panel to `AllPanels`.
4. Add the navigation case and any loading behavior.
5. Add the capability contract entry and tests.

### Themes

`ThemeManager` swaps the active color `ResourceDictionary`. Controls use dynamic resources so theme changes apply at runtime. DeepPurge Slate is the default. Arctic provides the light option, and the remaining bundled themes retain their own contrast checks.

### Screenshot capture

`tools/DeepPurge.Capture` loads the production `MainWindow`, production resource dictionaries, and read-only scanner data. Its launcher creates a private Windows desktop and starts the WPF worker there. The worker refuses to run on the interactive `Default` desktop.

Capture mode disables the tray icon and limits startup to the program inventory. Requested panels then run their own read-only scans before `RenderTargetBitmap` saves the image. This keeps screenshot work away from the user's active display and avoids invented UI data.

## CLI

`ParsedArgs` handles flags, `--option value`, `--option=value`, and positional tokens. Options that consume the next token belong in `ValueOptions`.

Exit codes follow the command-line contract:

```text
0     success
1     general failure
2     invalid argument
13    access denied
1223  cancelled
```

Long-running commands catch cancellation before their general exception handler so cancellation remains distinct from failure.

## Install monitoring

`InstallSnapshotEngine.TraceInstallAsync` captures authoritative filesystem and registry state before and after an installer. Optional USN and Sysmon evidence adds context but does not authorize replay deletion.

Snapshot walks cover configured program and user-data roots plus bounded registry trees. Compressed snapshots are retained by per-program and global limits.

Replay uses the set of created files from an authoritative manifest. It checks recorded identity, size, timestamp, and hash immediately before deletion. Legacy or diagnostic-only manifests cannot be replayed.

## Concurrency rules

- COM shortcut inspection owns a dedicated STA thread.
- Independent initial scanners can run in parallel and return partial-source diagnostics.
- Large hashing and deletion loops respect cancellation and avoid unbounded worker creation.
- WPF collection changes return to the dispatcher.
- Fire-and-forget work must still log failure and expose useful state.

## Build and release

The exact .NET SDK version comes from `global.json`. Locked restore is enabled for the product projects.

```powershell
dotnet build DeepPurge.sln -c Release -r win-x64
dotnet test tests\DeepPurge.Tests\DeepPurge.Tests.csproj -c Release -r win-x64
./Build.ps1 -AuditDependenciesOnly
./Build.ps1 -Sign
./Build.ps1 -ValidateReleaseOnly -ReleaseChecksumsPath build\SHA256SUMS.txt
```

`Build.ps1 -Sign` publishes the self-contained GUI and CLI, signs them when a certificate is supplied, writes `SHA256SUMS.txt`, and can validate the Scoop manifest against the exact release assets. All 459 tests run before a normal Release publish.

The repository does not use GitHub build workflows. Release files are built, tested, signed, and uploaded locally.

## Test philosophy

Tests concentrate on safety decisions, parsers, command construction, identity checks, bounded scans, recovery provenance, WPF contracts, and release metadata. Integration checks use temporary files or read-only Windows APIs where practical. A test must never make an unreviewed system change.
