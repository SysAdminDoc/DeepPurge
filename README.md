# DeepPurge v0.9.0

![Version](https://img.shields.io/badge/version-v0.9.0-blue) ![License](https://img.shields.io/badge/license-MIT-green) ![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey) ![CLI](https://img.shields.io/badge/CLI-headless-8A3FFC)

A thorough, open-source Windows uninstaller that goes deep. Removes programs completely, hunts down every leftover, and cleans system cruft that other tools miss. Ships a GUI and a headless CLI for scripting / Task Scheduler / Intune / SCCM.

## Features

### Uninstall
- **Installed Programs** - Full registry scan (HKLM + HKCU, 32/64-bit) with extracted program icons
- **Bulk Uninstall** - Multi-select + one-click sequential uninstall with silent flags auto-applied *(inspired by BCUninstaller)*
- **winget integration** - Programs tracked by winget are tagged with their package ID; upgrade-available badge + right-click → "Upgrade via winget" *(inspired by BCU source-adapter pattern)*
- **Scoop integration** - Scoop apps that skip the Windows installer DB are auto-discovered and merged into the list
- **Chocolatey integration** - `choco list --local-only --limit-output` entries are merged into the installed programs list and CLI output
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

### System Management
- **Autorun Manager** - Registry Run/RunOnce, startup folders, and service autoruns with **reversible** disable (StartupApproved pattern) and delete
- **Startup Impact ratings** - High / Medium / Low per autorun process, parsed from the Windows Diagnostic Infrastructure boot traces in `System32\wdi\LogFiles\StartupInfo\*.xml`. Same metric Task Manager uses — no undocumented APIs.
- **Digital signature badges** - Every autorun entry and service shows its WinVerifyTrust result (signer CN / Unsigned / Untrusted / Revoked) *(inspired by Sysinternals Autoruns)*
- **Browser Extensions** - Scan and remove extensions across Chrome, Edge, Brave, Firefox, Vivaldi, Opera
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
- **Replay uninstall** - "Forced Uninstall" now references the exact manifest instead of heuristic name-matching — closed-source Revo's headline feature, open-source
- Manifests persisted in `%LocalAppData%\DeepPurge\Snapshots\<name>.manifest.json` (or `./Data/Snapshots/` in portable mode)

### Community Cleaner Definitions
- **winapp2.ini integration** - Parses the community-maintained [winapp2.ini](https://github.com/MoscaDotTo/Winapp2) database (2,500+ third-party cleaners). Auto-downloads on first run with commit/date/SHA256 provenance and a previous-file backup, honours `Detect=` / `DetectFile=` gating so only applicable rules fire, and gates every path through SafetyGuard.
- **Validated custom JSON cleaners** - Define `*.cleaner.json` rules with schema linting, risk labels, expanded target previews, registry scope checks, and estimates. Use `deeppurgecli cleaners validate <file>` before `list|preview|run [--dry-run]`.

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

### Safety
- **System Restore Points** - View, create, and manage restore points
- Automatic restore point creation before uninstall operations (one per batch in bulk mode — Windows throttles SRSetRestorePoint)
- **Deletion Recovery panel** - list deletion manifests, preview recorded file/registry deletions, run dry-run restores, and open manifest or backup folders
- **Registry Backups panel** - Browse, inspect, and restore the `.reg` exports created before every destructive registry op
- Recycle Bin for file deletions (with permanent-delete and secure-delete fallbacks)
- Confidence-based leftover classification (Safe / Moderate / Risky)
- Centralized `SafetyGuard` blocks every destructive call against Windows, Program Files, System32, and protected registry hives

### Automation
- **DeepPurgeCli.exe** - Full headless surface. Every workflow (uninstall, clean, repair, driver/shortcut/duplicate scans, install-trace, winapp2 run, update check) is scriptable. Exit codes follow BCU convention (0/1/2/13/1223).
- **Scheduled cleaning** - Registers tasks in `\DeepPurge\` via `schtasks.exe` running as SYSTEM. "Clean every Monday 03:00" is two clicks.
- **Tray icon** - DeepPurge can minimize to the Windows tray, show scheduled-cleaning status, refresh schedule notifications, and launch a background dry-run clean preview.
- **Portable mode** - Drop a file named `DeepPurge.portable` next to the exe; every setting / backup / log redirects to `./Data/` beside the binary. USB-stick / field deployment ready. *(BCU pattern)*
- **Update checker** - Hits GitHub Releases API to flag available upgrades; never blocks startup.

### Themes
Nine built-in themes with runtime switching and persistence between sessions:
- **Catppuccin Mocha** (dark, default)
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

Both are self-contained single-file portable executables. ARM64 builds can be produced locally with `Build.ps1 -Runtime win-arm64`.

## CLI quickstart

```bash
DeepPurgeCli list                           # TSV-formatted installed programs
DeepPurgeCli uninstall "Some App" --silent  # Silent uninstall with auto-flag detection
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
DeepPurgeCli schedule remove --name Nightly
DeepPurgeCli check-update
DeepPurgeCli doctor                         # Environment self-test (14 checks)
```

## Testing

```bash
dotnet test tests/DeepPurge.Tests/DeepPurge.Tests.csproj
```

Covers UpdateChecker version-compare, Winapp2Parser detect/bucket routing, StartupImpact thresholds, SafetyGuard block/allow lists, ScheduleManager name sanitisation, and DataPaths path resolution.

## Packaging

- **winget** — `packaging/winget/SysAdminDoc.DeepPurge.yaml` (submit via `wingetcreate`)
- **Scoop**  — `packaging/scoop/deeppurge.json` (drop into a personal bucket)
- **GitHub Releases** — build locally with `BUILD.bat` / `Build.ps1`, generate SHA256s, and attach the artifacts with `gh release create` / `gh release upload`
- **Authenticode signing** — `./Build.ps1 -Sign -CertPath signing.pfx -CertPassword (Read-Host -AsSecureString)`


## Requirements
- Windows 10/11
- Run as Administrator (enforced by the manifest)
- .NET 10 SDK (build only)
- Optional: winget (auto-detected; enrichment silently no-ops when unavailable)
- Optional: Scoop in `%USERPROFILE%\scoop\apps` (filesystem-scanned; no shelling required)
- Optional: Chocolatey (`choco.exe` on PATH; enrichment silently no-ops when unavailable)

## License
MIT License
