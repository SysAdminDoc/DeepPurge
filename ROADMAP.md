# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Research-Driven Additions

### P1 — Trust and recovery

- [ ] P1 — Repair registry symbolic-link detection
  Why: Current detection does not read the registry key class or open links explicitly, so the code comment promises protection the implementation cannot prove.
  Evidence: `src/DeepPurge.Core/Safety/SafetyGuard.cs` `IsRegistrySymlink`; Microsoft `RegOpenKeyEx` `REG_OPTION_OPEN_LINK`, `RegQueryInfoKey`, and MS-RRP registry symbolic-link docs.
  Touches: `src/DeepPurge.Core/Safety/SafetyGuard.cs`, `src/DeepPurge.Core/Uninstall/UninstallEngine.cs`, `src/DeepPurge.Core/Cleaning/CleanerDefinition.cs`, `tests/DeepPurge.Tests/SafetyGuardTests.cs`
  Acceptance: Registry link fixtures are detected and skipped before delete/write; normal keys are not false positives; tests verify HKLM/HKCU/HKU delete callers use the corrected check.
  Complexity: M

- [ ] P1 — Remove remaining fixed-drive assumptions from repair and install tracing
  Why: Non-`C:` Windows installs were already a repeated hardening theme, but chkdsk and USN mode selection still assume `C:`.
  Evidence: `src/DeepPurge.Core/Repair/WindowsRepairEngine.cs` `ChkDsk => "C: /scan"`; `src/DeepPurge.Cli/Program.cs` and `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs` `UsnJournalReader.IsSupported(@"C:\")`.
  Touches: `src/DeepPurge.Core/Repair/WindowsRepairEngine.cs`, `src/DeepPurge.Cli/Program.cs`, `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs`, `tests/DeepPurge.Tests/WindowsRepairSanitiserTests.cs`
  Acceptance: chkdsk defaults to `Path.GetPathRoot(Environment.SystemDirectory)` and supports an explicit drive argument where exposed; USN support probes the installer/root volume; tests cover a non-`C:` system-root abstraction.
  Complexity: S

- [ ] P1 — Wire locked-file recovery into failed deletes
  Why: Restart Manager and delete-on-reboot support exist but are unused, so users still get silent skips or generic failures when leftovers are locked.
  Evidence: `src/DeepPurge.Core/FileSystem/LockedFileResolver.cs`; `src/DeepPurge.Core/Uninstall/UninstallEngine.cs`; BCU issue #129.
  Touches: `src/DeepPurge.Core/FileSystem/LockedFileResolver.cs`, shared delete primitive, `src/DeepPurge.App/ViewModels/`, `src/DeepPurge.Cli/Program.cs`, `tests/DeepPurge.Tests/`
  Acceptance: Failed file deletes report locking processes when available and offer/log queue-on-reboot behavior; CLI returns a distinct summary for queued deletes; tests exercise the fallback without requiring a real locked system file.
  Complexity: M

- [ ] P1 — Make the shell context-menu target path actionable
  Why: The registered command passes `--target "%1"` to the GUI, but the WPF app ignores startup arguments, so the right-click entry cannot open a forced-uninstall flow for that executable.
  Evidence: `src/DeepPurge.Core/Shell/ShellExtensionRegistrar.cs`; `src/DeepPurge.App/App.xaml.cs`; `src/DeepPurge.App/Views/MainWindow.xaml.cs`.
  Touches: `src/DeepPurge.App/App.xaml`, `src/DeepPurge.App/App.xaml.cs`, `src/DeepPurge.App/Views/MainWindow.xaml.cs`, `src/DeepPurge.Core/Shell/ShellExtensionRegistrar.cs`, `tests/DeepPurge.Tests/`
  Acceptance: Launching `DeepPurge.exe --target <exe>` opens the forced uninstall/remnant scan panel with the target path populated; invalid targets show a recoverable error; register/unregister smoke tests verify the command string.
  Complexity: M

### P2 — Parity, extensibility, and release truth

- [ ] P2 — Unify CLI app discovery with GUI package enrichment
  Why: `deeppurgecli list` and `uninstall` only use registry entries, while the GUI enriches with winget, Scoop, portable, and game-platform sources.
  Evidence: `src/DeepPurge.Cli/Program.cs` `CmdListAsync` and `CmdUninstallAsync`; `src/DeepPurge.Core/Packages/PackageManagerScanner.cs`; BCUninstaller multi-source discovery.
  Touches: `src/DeepPurge.Cli/Program.cs`, `src/DeepPurge.Core/Packages/PackageManagerScanner.cs`, `src/DeepPurge.Core/Models/InstalledProgram.cs`, `tests/DeepPurge.Tests/`
  Acceptance: CLI list can emit registry/package/portable/game entries with source and package id; uninstall can target supported package ids or return an explicit unsupported-source message; JSON/TSV output is covered by tests.
  Complexity: M

- [ ] P2 — Expose JSON cleaner definitions with schema validation
  Why: `CleanerDefinitionRunner` exists but has no GUI/CLI entry point, no schema, and no tests, making the changelogged custom cleaner surface effectively unreachable.
  Evidence: `src/DeepPurge.Core/Cleaning/CleanerDefinition.cs`; BleachBit CleanerML; winapp2 declarative cleaner ecosystem; FluentCleaner custom database support.
  Touches: `src/DeepPurge.Core/Cleaning/CleanerDefinition.cs`, `src/DeepPurge.Cli/Program.cs`, `src/DeepPurge.App/ViewModels/`, `src/DeepPurge.App/Views/MainWindow.xaml`, `tests/DeepPurge.Tests/`
  Acceptance: CLI supports `cleaners list|preview|run --dry-run`; GUI shows applicable custom cleaners with preview sizes and exclusion-aware details; invalid JSON/schema failures are surfaced; tests cover detect, files, registry, dry-run, and child-reparse behavior.
  Complexity: M

- [ ] P2 — Make Amcache/BAM remnant discovery truthful and wired
  Why: The parser checks for `Amcache.hve` but reads BAM registry data, and no production flow calls it, so the remnant-discovery claim is not observable.
  Evidence: `src/DeepPurge.Core/Registry/AmcacheParser.cs`; `src/DeepPurge.Cli/Program.cs` `CmdOrphans`; EricZimmerman AmcacheParser; Windows BAM forensic references.
  Touches: `src/DeepPurge.Core/Registry/AmcacheParser.cs`, `src/DeepPurge.Cli/Program.cs`, `src/DeepPurge.App/ViewModels/`, `tests/DeepPurge.Tests/`
  Acceptance: Either parse real `Amcache.hve` with fixture-backed behavior or rename/scope the feature to BAM execution evidence; `orphans` and GUI orphan scans can include the results; tests prove installed-program cross-reference and stale executable filtering.
  Complexity: M

- [ ] P2 — Wire existing `.resx` localization into UI text
  Why: Localization resources and generated accessors exist, but user-visible XAML/code-behind strings still bypass them, so the claimed i18n path is not observable.
  Evidence: `src/DeepPurge.App/Properties/Resources.resx`; `src/DeepPurge.App/Properties/Resources.Designer.cs`; `src/DeepPurge.App/Views/MainWindow.xaml`; BleachBit translation/localization practice.
  Touches: `src/DeepPurge.App/Properties/Resources.resx`, `src/DeepPurge.App/Views/MainWindow.xaml`, `src/DeepPurge.App/Views/MainWindow.xaml.cs`, `src/DeepPurge.App/ViewModels/`, `tests/DeepPurge.Tests/`
  Acceptance: Primary navigation, action buttons, dialog titles, and status copy bind to resources instead of literals; a culture-switch smoke test or generated-resource test proves the strings are consumed; missing-resource fallback remains English.
  Complexity: M

- [ ] P2 — Align release truth with .NET 10 and ARM64
  Why: Project files target .NET 10 and CI release matrices include ARM64, but local build scripts, CodeQL, docs, manifests, and visible XAML version text still contain .NET 8, x64-only, or `v0.8.1` truth.
  Evidence: `src/**/*.csproj`, `Build.ps1`, `BUILD.bat`, `.github/workflows/codeql.yml`, `README.md`, `CONTRIBUTING.md`, `ARCHITECTURE.md`, `packaging/`, `src/DeepPurge.App/Views/MainWindow.xaml`; Microsoft .NET lifecycle and RID catalog.
  Touches: `Build.ps1`, `BUILD.bat`, `.github/workflows/codeql.yml`, `README.md`, `CONTRIBUTING.md`, `ARCHITECTURE.md`, `packaging/scoop/deeppurge.json`, `packaging/winget/SysAdminDoc.DeepPurge.yaml`, `src/DeepPurge.App/Views/MainWindow.xaml`
  Acceptance: Scripts install/use .NET 10, CodeQL builds net10, visible versions bind to assembly version, README/docs no longer claim .NET 8 or x64-only, and package manifests include correct x64/arm64 release asset placeholders.
  Complexity: S

## Ideas / not committed

Things worth considering but not on a timeline:

- **Chocolatey integration** — `choco list --local-only` merging into the installed
  programs list, analogous to the existing winget + scoop path
- **OEM bloat scoring** — heuristics (publisher=Dell/HP/Lenovo, install source=OEM)
  to recommend batch-uninstall candidates on factory images
- **Tray icon** — background scheduled cleaning with tray notifications
- **Registry ETW tracing** — add `Microsoft.Diagnostics.Tracing.TraceEvent` for
  real-time registry change capture alongside USN journal filesystem tracking

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** — obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** — user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** — overkill for a local desktop tool
- **Cloud sync of settings** — privacy surface without clear value
- **MSIX distribution** — sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app
