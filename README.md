<!-- codex-branding:start -->
<p align="center"><img src="icon.png" width="128" alt="Deep Purge"></p>

<p align="center">
  <img alt="Version" src="https://img.shields.io/badge/version-0.8.0-58A6FF?style=for-the-badge">
  <img alt="License" src="https://img.shields.io/badge/license-MIT-4ade80?style=for-the-badge">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11-58A6FF?style=for-the-badge">
</p>
<!-- codex-branding:end -->

# DeepPurge v0.8.1

![Version](https://img.shields.io/badge/version-v0.8.1-blue) ![License](https://img.shields.io/badge/license-MIT-green) ![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)

A thorough, open-source Windows uninstaller that goes deep. Removes programs completely, hunts down every leftover, and cleans system cruft that other tools miss.

## Features

### Uninstall
- **Installed Programs** - Full registry scan (HKLM + HKCU, 32/64-bit) with extracted program icons
- **Bulk Uninstall** - Multi-select + one-click sequential uninstall with silent flags auto-applied *(inspired by BCUninstaller)*
- **winget integration** - Programs tracked by winget are tagged with their package ID; upgrade-available badge + right-click → "Upgrade via winget" *(inspired by BCU source-adapter pattern)*
- **Scoop integration** - Scoop apps that skip the Windows installer DB are auto-discovered and merged into the list
- **Silent-switch database** - Curated per-installer-family silent flags (`/S`, `/qn`, `/VERYSILENT`, `/quiet`, Squirrel `--uninstall --silent`) with vendor fingerprint overrides *(inspired by PatchMyPC)*
- **Forced Uninstall** - Scan for remnants of already-removed or partially uninstalled programs
- **Windows Apps** - Remove UWP/MSIX apps including system bloatware
- **Leftover Scanner** - Three scan modes (Safe / Moderate / Advanced) for registry keys, files, and folders
- **Export** - Export installed programs list to HTML, CSV, or JSON

### Cleanup
- **Junk Cleaner** - Browser caches, temp files, crash dumps, prefetch, installer cache, Windows Update leftovers
- **Evidence Remover** - Recent documents, jump lists, thumbnail cache, clipboard, DNS cache, Explorer history, Windows logs, crash reports, error reports, font cache, delivery optimization cache
- **Empty Folders** - Scan common locations for empty directory trees and remove them
- **Disk Analyzer** - Folder size breakdown and large file finder (50MB+) with delete capability. Uses WizTree's raw-MFT technique (`FSCTL_ENUM_USN_DATA` + `FSCTL_GET_NTFS_FILE_RECORD`) on NTFS volumes; parallel `FindFirstFileExW(FIND_FIRST_EX_LARGE_FETCH)` fallback on ReFS/FAT32. Typical full-drive scan in seconds.
- **Dry-run / Preview mode** - Every destructive pipeline can be previewed: enumerate and size items without touching them *(inspired by BleachBit)*
- **Secure Delete** - Privacy-grade wipe (single-pass cryptographic random + opaque rename + delete — multi-pass DoD wipes are obsolete on SSDs and deliberately omitted) *(inspired by BleachBit/PrivaZer)*
- **Live progress bars** - Every long-running delete reports item / total / bytes-freed / current path in the status bar

### System Management
- **Autorun Manager** - Registry Run/RunOnce, startup folders, and service autoruns with **reversible** disable (StartupApproved pattern) and delete
- **Digital signature badges** - Every autorun entry and service shows its WinVerifyTrust result (signer CN / Unsigned / Untrusted / Revoked) *(inspired by Sysinternals Autoruns)*
- **Browser Extensions** - Scan and remove extensions across Chrome, Edge, Brave, Firefox, Vivaldi, Opera
- **Context Menu Cleaner** - Find and remove orphaned shell context menu entries with broken executables or CLSIDs
- **Services Manager** - View all Windows services, identify orphaned services pointing to deleted executables, disable or delete
- **Scheduled Tasks** - Full task inventory with orphan detection, disable and delete capabilities
- **Registry Hunter** - Parallel substring or regex search across HKLM, HKLM\\WOW6432Node, HKCU, and HKCR with scope filters (keys / names / data), live hit counter, and depth / hit / time caps *(inspired by NirSoft RegScanner and Eric Zimmerman's Registry Explorer)*

### Safety
- **System Restore Points** - View, create, and manage restore points
- Automatic restore point creation before uninstall operations (one per batch in bulk mode — Windows throttles SRSetRestorePoint)
- **Registry Backups panel** - Browse, inspect, and restore the `.reg` exports created before every destructive registry op
- Recycle Bin for file deletions (with permanent-delete and secure-delete fallbacks)
- Confidence-based leftover classification (Safe / Moderate / Risky)
- Centralized `SafetyGuard` blocks every destructive call against Windows, Program Files, System32, and protected registry hives

### Themes
Eight built-in themes with runtime switching and persistence between sessions:
- **Catppuccin Mocha** (dark, default)
- **OLED Black** (pure black, blue accent)
- **Dracula** (classic purple)
- **Nord Polar** (frost tones)
- **GitHub Dark** (official palette)
- **Obsidian** (deep black, lavender accent)
- **Matrix** (neon green on black)
- **Arctic** (light mode)

## Build

Requires .NET 8 SDK. Run `BUILD.bat` from the project root.

```
BUILD.bat
```

Output: `build\DeepPurge.exe` (self-contained single-file portable executable, ~66 MB with compression).

## Requirements
- Windows 10/11
- Run as Administrator (enforced by the manifest)
- .NET 8 SDK (build only)
- Optional: winget (auto-detected; enrichment silently no-ops when unavailable)
- Optional: Scoop in `%USERPROFILE%\scoop\apps` (filesystem-scanned; no shelling required)

## License
MIT License
