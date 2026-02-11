# DeepPurge v0.3.0

A thorough, open-source Windows uninstaller that goes deep. Removes programs completely, hunts down every leftover, and cleans system cruft that other tools miss.

## Features

### Uninstall
- **Installed Programs** - Full registry scan (HKLM + HKCU, 32/64-bit) with extracted program icons
- **Forced Uninstall** - Scan for remnants of already-removed or partially uninstalled programs
- **Windows Apps** - Remove UWP/MSIX apps including system bloatware
- **Leftover Scanner** - Three scan modes (Safe / Moderate / Advanced) for registry keys, files, and folders
- **Export** - Export installed programs list to HTML, CSV, or JSON

### Cleanup
- **Junk Cleaner** - Browser caches, temp files, crash dumps, prefetch, installer cache, Windows Update leftovers
- **Evidence Remover** - Recent documents, jump lists, thumbnail cache, clipboard, DNS cache, Explorer history, Windows logs, crash reports, error reports, font cache, delivery optimization cache
- **Empty Folders** - Scan common locations for empty directory trees and remove them
- **Disk Analyzer** - Folder size breakdown and large file finder (50MB+) with delete capability

### System Management
- **Autorun Manager** - Registry Run/RunOnce, startup folders, and service autoruns with disable/delete
- **Browser Extensions** - Scan and remove extensions across Chrome, Edge, Brave, Firefox, Vivaldi, Opera
- **Context Menu Cleaner** - Find and remove orphaned shell context menu entries with broken executables or CLSIDs
- **Services Manager** - View all Windows services, identify orphaned services pointing to deleted executables, disable or delete
- **Scheduled Tasks** - Full task inventory with orphan detection, disable and delete capabilities

### Safety
- **System Restore Points** - View, create, and manage restore points
- Automatic restore point creation before uninstall operations
- Registry backup before deletion
- Recycle Bin for file deletions
- Confidence-based leftover classification (Safe / Moderate / Risky)

### Themes
Five built-in dark themes with runtime switching:
- **Catppuccin Mocha** (default)
- **OLED Black**
- **Dracula**
- **Nord Polar**
- **GitHub Dark**

## Build

Requires .NET 8 SDK. Run `BUILD.bat` from the project root.

```
BUILD.bat
```

Output: `build\DeepPurge.exe` (self-contained single-file portable executable)

## Requirements
- Windows 10/11
- Run as Administrator
- .NET 8 SDK (build only)

## License
MIT License
