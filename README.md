# DeepPurge v0.9.0

![Version](https://img.shields.io/badge/version-v0.9.0-blue) ![License](https://img.shields.io/badge/license-MIT-green) ![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey) ![CLI](https://img.shields.io/badge/CLI-headless-8A3FFC)

A thorough, open-source Windows uninstaller that goes deep. Removes programs completely, hunts down every leftover, and cleans system cruft that other tools miss. Ships a GUI and a headless CLI for scripting / Task Scheduler / Intune / SCCM.

## Features

### Uninstall
- **Installed Programs** - Full registry scan (HKLM + HKCU, 32/64-bit) with extracted program icons
- **Bulk Uninstall** - Multi-select + one-click sequential uninstall with silent flags auto-applied *(inspired by BCUninstaller)*
- **winget integration** - Programs tracked by winget are tagged with their package ID; upgrade-available badge + right-click → "Upgrade via winget"; uninstall uses `winget uninstall --id ... --exact` before registry fallbacks, and `doctor` reports source health when package-manager enrichment degrades *(inspired by BCU source-adapter pattern)*
- **Scoop integration** - Scoop apps that skip the Windows installer DB are auto-discovered, merged into the list, and removed through `scoop uninstall`
- **Chocolatey integration** - `choco list --local-only --limit-output` entries are merged into the installed programs list and removed through non-interactive `choco uninstall`
- **Privilege-contained package managers** - winget, Scoop, and Chocolatey resolve from known absolute install roots and run as the original non-elevated desktop user, never with the GUI's administrator token or a current-directory/PATH-selected shim
- **Silent-switch database** - Curated per-installer-family silent flags (`/S`, `/qn`, `/VERYSILENT`, `/quiet`, Squirrel `--uninstall --silent`) with vendor fingerprint overrides *(inspired by PatchMyPC)*
- **Forced Uninstall** - Scan for remnants of already-removed or partially uninstalled programs
- **Windows Apps** - Remove UWP/MSIX apps including system bloatware
- **Leftover Scanner** - Three scan modes (Safe / Moderate / Advanced) for registry keys, files, and folders
- **Export** - Export installed programs list to HTML, CSV, or JSON

### Cleanup
- **Junk Cleaner** - Browser caches, temp files, crash dumps, prefetch, installer cache, Windows Update leftovers
- **Evidence Remover** - Recent documents, jump lists, thumbnail cache, clipboard, DNS cache, Explorer history, Windows logs, crash reports, error reports, font cache, delivery optimization cache
- **Empty Folders** - Scan common locations for empty directory trees and remove them
- **Disk Analyzer** - Folder size breakdown and large file finder (50MB+) with delete capability. Uses WizTree's raw-MFT technique (`FSCTL_ENUM_USN_DATA` + `FSCTL_GET_NTFS_FILE_RECORD`) on NTFS volumes; parallel `FindFirstFileExW(FIND_FIRST_EX_LARGE_FETCH)` fallback on ReFS/exFAT/FAT32. Typical full-drive scan in seconds.
- **MSI/MSP orphan cleanup** - Scans `%WINDIR%\Installer` for old MSI/MSP files not referenced by active Windows Installer products
- **Dry-run / Preview mode** - Every destructive pipeline can be previewed: enumerate and size items without touching them *(inspired by BleachBit)*
- **Secure Delete** - Privacy-grade selected-file wipe (single-pass cryptographic random + opaque rename + delete; no volume free-space fill) *(inspired by BleachBit/PrivaZer)*
- **Live progress bars** - Every long-running delete reports item / total / bytes-freed / current path in the status bar
- **Skipped-item details** - Junk and Evidence cleanup summaries expose redacted reasons for missing, denied, unsafe, locked, command-failed, and too-recent items in GUI status, CLI text, CLI JSON, and activity history

### System Management
- **Autorun Manager** - Registry Run/RunOnce, startup folders, and service autoruns with **reversible** disable (StartupApproved pattern), guarded deletion where a rollback provider exists, and explicit unsupported states for protected sources
- **Startup Impact ratings** - High / Medium / Low per autorun process, parsed from the Windows Diagnostic Infrastructure boot traces in `System32\wdi\LogFiles\StartupInfo\*.xml`. Same metric Task Manager uses — no undocumented APIs.
- **Digital signature badges** - Every autorun entry and service shows its WinVerifyTrust result (signer CN / Unsigned / Untrusted / Revoked) *(inspired by Sysinternals Autoruns)*
- **Browser Extensions** - Scan extensions across Chrome, Edge, Brave, Firefox, Vivaldi, and Opera; remove only exact profile packages while protecting system and stale entries
- **Driver Store cleanup** - Enumerate third-party driver packages via `pnputil /enum-drivers`, group by `.inf` family, flag old versions for removal. Recovers 2-10 GB on OEM laptops. *(inspired by RAPR / DriverStoreExplorer)*
- **Context Menu Cleaner** - Find and remove orphaned shell context menu entries with broken executables or CLSIDs
- **Shortcut repair** - Enumerate `.lnk` files on Desktop / Start Menu via IShellLinkW COM; flag and delete broken-target shortcuts
- **Services Manager** - View all Windows services, identify orphaned services pointing to deleted executables, disable or delete
- **Scheduled Tasks** - Full task inventory with orphan detection, disable and delete capabilities
- **Registry Hunter** - Parallel substring or regex search across HKLM, HKLM\\WOW6432Node, HKCU, and HKCR with scope filters (keys / names / data), live hit counter, and depth / hit / time caps *(inspired by NirSoft RegScanner and Eric Zimmerman's Registry Explorer)*

### Windows Repair
- **SFC / DISM / chkdsk** - One-click `sfc /scannow`, `DISM /RestoreHealth`, `DISM /StartComponentCleanup` (WinSxS), `chkdsk` with live stdout streaming
- **Font + Icon cache rebuild** - Fixes broken cache corruption without a reboot
- **Per-app repair** - `winget repair <id>` and `msiexec /fa {ProductCode} /qn` for reinstall-without-data-loss

### Installation Monitor *(flagship)*
- **Before/after snapshot** - Captures filesystem + registry manifest before and after a traced installer; the diff becomes a precise per-app removal list
- **Replay uninstall** - "Forced Uninstall" references the exact manifest instead of heuristic name-matching and skips replay files whose created-object identity, size, timestamp, or SHA256 no longer matches the captured identity. Legacy manifests and diagnostic-only journal evidence cannot be replayed.
- **Diagnostic journal evidence** - The optional USN/Sysmon trace records parent-resolved filesystem changes and installer-process-tree registry activity for review, while authoritative pre/post snapshots decide what can be replayed.
- Manifests persisted in `%LocalAppData%\DeepPurge\Snapshots\<name>.manifest.json` (or `./Data/Snapshots/` in portable mode)

### Community Cleaner Definitions
- **winapp2.ini integration** - Parses the community-maintained [winapp2.ini](https://github.com/MoscaDotTo/Winapp2) database (2,500+ third-party cleaners). Auto-downloads on first run with commit/date/SHA256 provenance and a previous-file backup, honours `Detect=` / `DetectFile=` gating so only applicable rules fire, and gates every path through SafetyGuard.
- **Validated custom JSON cleaners** - Define versioned `*.cleaner.json` documents with schema linting, risk labels, provenance, expanded target previews, registry scope checks, and estimates. Use `deeppurgecli cleaners schema` and `deeppurgecli cleaners validate <file>` before `list|preview|run [--dry-run]`.

### Duplicate Finder
- **Three-stage hash** - Group by exact byte-size → XXH3 first-MB head-hash → XXH3 full-file for remaining collisions. Skips reparse points / junctions to avoid infinite loops. *(algorithm from Czkawka / fdupes)*

### Health Dashboard
- **System health score** - Assesses 4 categories (Junk Files, Privacy, Startup Impact, Disk Space) with 0-100 scores and A-F grade

### Program Discovery
- **Portable app detection** - Scans Desktop, Downloads, PortableApps folders, and removable drives for standalone executables not tracked by any installer. Shows with a "Portable" source badge. *(only Uninstalr previously offered this)*
- **Game platform detection** - Discovers Steam games (via `libraryfolders.vdf` + `appmanifest_*.acf`), Epic Games (via `.item` manifests), and GOG Galaxy titles (via registry). Games appear in the unified programs list with platform badges.
- **Bundleware / sideload detection** - Flags programs installed on the same day from a non-trusted publisher that appear as the sole representative of their publisher — likely bundled silently with other software.
- **OEM bloat scoring** - Flags likely OEM support/trial utilities while suppressing driver and firmware components
- **BAM remnant discovery** - Reads Windows Background Activity Moderator data to find previously-executed binaries that are no longer installed. Available via `deeppurgecli orphans --remnants`.

### System Slimming
- **Windows component cleanup** - Scans ~15 removable components (wallpapers, sample media, help files, MSI patch cache, delivery optimization, WER reports, font cache, log folders, Windows.old) with per-item sizes and delete through SafetyGuard.

### Shell Integration
- **Context menu** - `deeppurgecli register-shell` adds "Uninstall with DeepPurge" to the right-click menu for `.exe` files. `unregister-shell` removes it. The GUI accepts `--target <path>` to pre-populate the forced-uninstall panel.
- **Expert / Safe mode** - Toggle visibility of advanced operations (secure delete, advanced scan, registry hunter, service deletion). Persists between sessions via `settings.json`.
- **Versioned settings import/export** - Move excluded paths, cookie whitelist, program notes, privacy retention, and safety defaults between installed/portable deployments with schema metadata, redacted previews, validation errors, and rollback backups.

### Safety
- **System Restore Points** - View, create, and manage restore points
- Automatic restore point creation before uninstall operations (one per batch in bulk mode — Windows throttles SRSetRestorePoint)
- **Deletion Recovery panel** - list deletion manifests, preview recorded file/registry deletions, and run dry-run or live restores; registry rollback accepts only the exact SHA-256-bound artifact named by a versioned operation record
- **Registry Backups panel** - Open the ACL-protected `%ProgramData%\DeepPurge\RegistryBackups` store; every artifact binds its hive, subkey/value, object identity, owner/DACL, and operation ID, and legacy manifests cannot auto-discover a `.reg` file
- Recycle Bin for ordinary file cleanup through `IFileOperation` where supported, with explicit permanent-delete and secure-delete outcomes
- Typed cleanup results distinguish preview, recycled, permanent, secure, queued, skipped, failed, and cancelled items; only confirmed mutations enter recovery manifests
- Administrative mutation ledger for firewall, PATH, autorun, service, scheduled-task, shell, and DeepPurge schedule changes with before/after state, rollback data, verification, and exact typed outcomes
- Protected services, firewall rules, PATH entries, registry roots, and Microsoft scheduled-task paths are skipped before production mutation; unsupported rollback-sensitive actions are disabled in the WPF surface
- Confidence-based leftover classification (Safe / Moderate / Risky)
- Leftover ownership evidence and conflict review: another product's install root, protected Windows scope, and weak single-source matches are never auto-removable
- Centralized `SafetyGuard` blocks every destructive call against Windows, Program Files, System32, and protected registry hives
- Windows-owned helpers resolve only from protected absolute system paths; unknown relative executables and ambiguous registered-uninstaller command lines fail closed

### Automation
- **DeepPurgeCli.exe** - Full headless surface. Every workflow (uninstall, clean, repair, driver/shortcut/duplicate scans, install-trace, winapp2 run, update check) is scriptable. Exit codes follow BCU convention (0/1/2/13/1223).
- **Scheduled cleaning** - Registers Task Scheduler 2.0 exec actions in `\DeepPurge\` with separate executable/argument fields. Highest-privilege jobs run a content-addressed single-file CLI copy from an administrator-owned `%ProgramData%` store; GUI/CLI diagnostics expose the registered principal, action, and ACL trust. Legacy wrappers migrate to disabled dry-run definitions without being read or executed.
- **Tray icon** - DeepPurge can minimize to the Windows tray, show scheduled-cleaning status, refresh schedule notifications, and launch a background dry-run clean preview.
- **Portable mode** - Drop a file named `DeepPurge.portable` next to the exe; every setting / backup / log redirects to `./Data/` beside the binary. USB-stick / field deployment ready. *(BCU pattern)*
- **Update checker** - Hits GitHub Releases API to flag available upgrades; never blocks startup.

### Themes
Ten built-in themes with runtime switching and persistence between sessions:
- **DeepPurge Slate** (graphite/cyan, default)
- **Catppuccin Mocha** (dark)
- **OLED Black** (pure black, blue accent)
- **Dracula** (classic purple)
- **Nord Polar** (frost tones)
- **GitHub Dark** (official palette)
- **Obsidian** (deep black, lavender accent)
- **Matrix** (neon green on black)
- **Arctic** (light mode)
- **High Contrast** (WCAG AAA, bright saturated accents on pure black)

## Build

Requires .NET 10 SDK. Run `BUILD.bat` from the project root.

```
BUILD.bat
```

Output:
- `build\DeepPurge.exe` - GUI, ~66 MB, `requireAdministrator` manifest
- `build\DeepPurgeCli.exe` - CLI, ~66 MB, `asInvoker` manifest (scriptable, elevate externally if needed)
- `build\SHA256SUMS.txt` - SHA256 hashes for the published release assets

Both executables are self-contained single-file x64 portable builds. Add ARM64 package entries only after matching ARM64 assets are attached to the release and covered by `SHA256SUMS.txt`.

## CLI quickstart

```bash
DeepPurgeCli list                           # TSV-formatted installed programs
DeepPurgeCli uninstall "Some App" --silent  # Silent uninstall with auto-flag detection
DeepPurgeCli uninstall "Git.Git" --dry-run  # Preview source-native uninstall command
DeepPurgeCli clean junk evidence --dry-run  # Preview what would be freed
DeepPurgeCli repair sfc                     # sfc /scannow
DeepPurgeCli drivers --old                  # Old driver packages ready to remove
DeepPurgeCli startup-impact                 # High/Medium/Low per autorun process
DeepPurgeCli duplicates C:\Users\you        # Duplicate file groups
DeepPurgeCli snapshot trace "MyApp" setup.exe  # Record install delta
DeepPurgeCli update-winapp2 --check-only       # Show local/remote database provenance
DeepPurgeCli winapp2 .\winapp2.ini --dry-run   # Run community cleaner database
DeepPurgeCli schedule add --name Nightly --freq weekly --time 03:00 --day Mon --args "clean junk evidence"
DeepPurgeCli schedule list
DeepPurgeCli schedule migrate              # Replace legacy wrappers with disabled protected dry-runs
DeepPurgeCli schedule remove --name Nightly
DeepPurgeCli settings prune --dry-run       # Preview expired logs/activity/manifests to remove
DeepPurgeCli settings import settings.json --preview  # Validate an import without changing local settings
DeepPurgeCli check-update
DeepPurgeCli doctor                         # Environment self-test (16 checks, including package sources)
```

## Testing

```bash
dotnet test tests/DeepPurge.Tests/DeepPurge.Tests.csproj
```

The current suite has 400 tests covering parser routing, protected helper resolution, original-user process brokering, package-manager command builders and source diagnostics, cleaner schema validation, cleanup failure reporting, release validation, privacy retention, SafetyGuard block/allow lists, handle-bound deletion/rollback, protected Task Scheduler registration and migration, versioned settings import/export contracts, static WPF accessibility/resource-drift checks, and support-bundle redaction.

## Packaging

- **winget** — `packaging/winget/SysAdminDoc.DeepPurge.yaml` (submit via `wingetcreate`)
- **Scoop**  — `packaging/scoop/deeppurge.json` (drop into a personal bucket)
- **GitHub Releases** — build locally with `BUILD.bat` / `Build.ps1`, attach both executables plus `SHA256SUMS.txt`, then run `Build.ps1 -ValidateReleaseOnly -ReleaseChecksumsPath <path-to-SHA256SUMS.txt>` before publishing package manifests; the validator includes the project-level NuGet dependency audit
- **Dependency audit** — run `Build.ps1 -AuditDependenciesOnly` to check Core, App, CLI, and Tests for outdated or vulnerable NuGet packages without using the solution-level `dotnet list` path
- **Authenticode signing** — `./Build.ps1 -Sign -CertPath signing.pfx -CertPassword (Read-Host -AsSecureString)`


## Requirements
- Windows 10/11
- Run as Administrator (enforced by the manifest)
- .NET 10 SDK (build only)
- Optional: winget (auto-detected; `doctor` reports version/list parser health when unavailable or degraded)
- Optional: Scoop in `%USERPROFILE%\scoop\apps` (filesystem-scanned; `doctor` reports root availability and app count)
- Optional: Chocolatey (`choco.exe` on PATH; `doctor` reports version/list parser health when unavailable or degraded)

## License
MIT License
