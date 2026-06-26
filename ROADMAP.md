# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Ideas / not committed

Things worth considering but not on a timeline:

- **Chocolatey integration** — `choco list --local-only` merging into the installed
  programs list, analogous to the existing winget + scoop path
- **OEM bloat scoring** — heuristics (publisher=Dell/HP/Lenovo, install source=OEM)
  to recommend batch-uninstall candidates on factory images
- **Tray icon** — background scheduled cleaning with tray notifications
- **Registry ETW tracing** — add `Microsoft.Diagnostics.Tracing.TraceEvent` for
  real-time registry change capture alongside USN journal filesystem tracking
- **MSI/MSP Installer cache orphan cleanup** — scan `%WINDIR%\Installer` for
  orphaned MSI/MSP patch files not referenced by any installed product. Can
  recover multi-GB. See InstallerClean (github.com/no-faff/InstallerClean) for
  the approach: query the Windows Installer database for active products, flag
  everything else as reclaimable.

## Research-Driven Additions

### P1 — Deployment

- [ ] P1 — Framework-dependent "slim" build target
  Why: Self-contained builds are ~66 MB each; framework-dependent builds are ~2-5 MB for users with .NET 10 runtime installed. Dramatically improves download and USB deployment experience.
  Evidence: InstallerClean triple distribution model (65MB self-contained / 2MB framework-dependent / CLI standalone); r/sysadmin preference for small portable tools
  Touches: `Build.ps1`, `BUILD.bat` — add parallel publish with `-p:SelfContained=false --no-self-contained`; output to `build/slim/`; update README.md packaging section
  Acceptance: `BUILD.bat` produces `build/slim/DeepPurge.exe` (~2-5 MB) and `build/slim/DeepPurgeCli.exe` (~2-5 MB) alongside the existing self-contained builds; both run correctly when .NET 10 runtime is installed
  Complexity: S

### P2 — Trust and scripting

- [ ] P2 — Granular deletion manifest
  Why: IT/sysadmin users need an audit trail of exactly what was deleted (path, type, size, timestamp, operation) for compliance and post-mortem review. ActivityLog records summaries but not per-item detail.
  Evidence: Winhance Change History logging; Win11Debloat revert tracking; enterprise compliance requests from r/sysadmin
  Touches: `SafetyGuard.cs` (add manifest write after each SafeDeleteFile/SafeDeleteDirectory call), new `Core/Diagnostics/DeletionManifest.cs` (JSONL writer), `DataPaths.cs` (add `DeletionManifests` path)
  Acceptance: After any cleanup operation, `%LocalAppData%\DeepPurge\Logs\deletions-YYYY-MM-DD.jsonl` contains one JSON line per deleted item with `{path, type, sizeBytes, timestampUtc, operation}`; CLI `clean` outputs manifest path on completion
  Complexity: M

- [ ] P2 — CLI `--json` stdout output mode
  Why: Sysadmin scripting workflows need machine-parseable output for piping to jq, PowerShell ConvertFrom-Json, or feeding into SCCM/Intune reports. All current CLI output is human-readable text.
  Evidence: BCU console JSON export; Czkawka CLI JSON output; winget `--output json`; r/sysadmin requests for scriptable cleanup tools
  Touches: `Program.cs` (add `--json` flag to ParsedArgs, add JSON serialization branch in each command handler: `list`, `drivers`, `startup-impact`, `shortcuts`, `orphans`, `doctor`, `duplicates`)
  Acceptance: `deeppurgecli list --json` outputs a JSON array of program objects to stdout; `deeppurgecli drivers --old --json | jq '.[] | .PublishedName'` works; exit codes unchanged
  Complexity: M

- [ ] P2 — Winapp2.ini staleness check + update command
  Why: Winapp2.ini auto-downloads on first run but never checks for updates. FluentCleaner and BleachBit both auto-update their cleaner databases. Stale rules miss new browser versions and app paths.
  Evidence: FluentCleaner auto-updates winapp2.ini; BleachBit auto-updates; winapp2.ini GitHub last commit May 2026 but no tagged release since Nov 2025
  Touches: `Winapp2Parser.cs` or new `Core/Cleaning/Winapp2Updater.cs` (check GitHub API for latest commit date vs local file timestamp), `Program.cs` (add `update-winapp2` command), `MainViewModel.Extensions.cs` (add button/indicator in GUI winapp2 panel)
  Acceptance: `deeppurgecli update-winapp2` downloads latest winapp2.ini from GitHub if local copy is older; GUI shows "Update available" indicator when stale; `--check-only` flag prints status without downloading
  Complexity: S

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** — obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** — user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** — overkill for a local desktop tool
- **Cloud sync of settings** — privacy surface without clear value
- **MSIX distribution** — sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app
