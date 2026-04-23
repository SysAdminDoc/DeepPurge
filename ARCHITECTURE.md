# DeepPurge Architecture

A concise design overview for contributors. Focuses on cross-cutting patterns rather than per-scanner detail — read the individual source files for the specifics.

## Solution layout

```
DeepPurge.sln
├── src/DeepPurge.Core/       Pure logic: scanners, safety, diagnostics
├── src/DeepPurge.App/        WPF GUI (admin manifest, MVVM via CommunityToolkit.Mvvm)
├── src/DeepPurge.Cli/        Headless entry point (asInvoker manifest)
└── tests/DeepPurge.Tests/    xUnit, 64+ cases
```

`DeepPurge.Core` has exactly one hard WPF dependency — `IconExtractor` returns
`ImageSource` — which is why `UseWPF=true` is set on the Core csproj. If you ever
split icon handling into the App layer, that dependency disappears.

## Cross-cutting concerns

### SafetyGuard (`Core/Safety/SafetyGuard.cs`)

Every destructive operation (file delete, registry delete, service modify, scheduled-task
delete) passes through `SafetyGuard` before acting. The guard holds hard-coded blocklists:

- Protected directories (Windows, System32, WinSxS, etc.)
- Protected files (bootmgr, registry hives)
- Protected registry roots (HKLM\SYSTEM\CurrentControlSet\Control, SAM, etc.)
- Protected service names (50+ core Windows services)
- Protected scheduled-task paths (Microsoft\Windows\, Microsoft\Office\)

Tests in `SafetyGuardTests` lock in the deny rules so a refactor can't silently relax them.

### DeleteOptions (`Core/Safety/DeleteOptions.cs`)

The single argument record threaded through every destructive pipeline:

```csharp
public readonly record struct DeleteOptions(
    bool DryRun = false,
    bool SecureDelete = false,
    bool UseRecycleBin = true);
```

Convention: when you add a new destructive pipeline, take `DeleteOptions` — don't add
new bool args. When you add a new behavioural toggle, extend the record — don't add
a new positional arg.

### DataPaths (`Core/App/DataPaths.cs`)

Routes every persistent file through one resolver. A `DeepPurge.portable` marker next
to the exe flips the root from `%LocalAppData%\DeepPurge\` to `./Data/`. Every caller
that needs a log/backup/snapshot path MUST go through `DataPaths` — direct
`%LocalAppData%` references break portable mode.

### Log (`Core/Diagnostics/Log.cs`)

Append-only, thread-safe, 5 MB rotating. Used for swallowed exceptions so field issues
can be debugged without the user having to attach a debugger. Never throws — logging
failures must not crash callers.

### SelfTest (`Core/Diagnostics/SelfTest.cs`)

14-check environment probe driving `deeppurgecli doctor`. Read-only; never modifies
state. Returns structured `SelfTestResult` records the CLI formats.

## GUI architecture

### MVVM wiring

`MainViewModel` is a partial class split across two files:

- `MainViewModel.cs` — pre-v0.9 features (programs, junk, evidence, autoruns, ...)
- `MainViewModel.Extensions.cs` — v0.9 features (drivers, startup impact, shortcuts,
  duplicates, winapp2, repair, schedule, updates, install snapshot)

Observable collections are bound 1:1 to DataGrid ItemsSource. Async work runs under
`Task.Run(..., ct)`; results are marshaled back to the UI thread via
`_dispatcher.Invoke` / `BeginInvoke`.

### Panel switching

The sidebar is a `RadioButton` group with each button's `Tag` naming its target panel.
`MainWindow.xaml.cs.NavButton_Checked` hides all panels in `AllPanels`, then shows the
one for the selected tag. Adding a new panel is three steps:

1. Add a `RadioButton` in the sidebar XAML with a unique `Tag`
2. Add the panel element in the content area with `Visibility="Collapsed"` and
   `x:Name="panelXxx"` (or `dgXxx` for DataGrid)
3. Add the element to `AllPanels` and add a `case` in `NavButton_Checked`

### Themes

`ThemeManager` reapplies a merged `ResourceDictionary` on swap. Dark themes are first-class;
light is opt-in. Controls reference `{DynamicResource}` — never `{StaticResource}` — so
theme swap is instant. Theme choice persists to `DataPaths.ThemeFile`.

## CLI architecture

### Argument parsing

`ParsedArgs` in `Program.cs` handles `--flag`, `--option value`, `--option=value`, and
positional tokens. New options that take a value must be added to `ValueOptions` so the
parser consumes the next token. Boolean flags are free — anything `--xxx` not in the
value list is a flag.

### Exit codes (BCU convention)

```
0    success
1    general failure
2    bad argument
13   access denied
1223 user cancelled (CTRL_C or uninstaller returned 1223)
```

Callers (Task Scheduler, SCCM, Intune) key off these to decide retry behaviour.

### Threading

Commands with long-running work wrap `Task.Run` bodies in a try/catch that catches
`OperationCanceledException` first (to preserve 1223) then falls through to a generic
handler that logs + returns exit 1.

## Install-monitor flagship

`InstallSnapshotEngine.TraceInstallAsync` captures a before/after manifest of:

- Program Files, Program Files (x86), ProgramData, LocalAppData, AppData
- HKLM\SOFTWARE, HKLM\SOFTWARE\WOW6432Node, HKCU\SOFTWARE (depth-3 key tree)

Walks run in parallel via `Task.WhenAll`. Snapshots gzip to `DataPaths.Snapshots`,
pruned to 3 per program and 30 global. `Diff` computes adds AND removes (upgrade-
scenario fidelity). `ReplayRemoveAsync` feeds the added-files set through SafetyGuard
to enable "forced uninstall by exact manifest."

`MainViewModel.ForcedUninstallByManifestAsync` is the public surface from the GUI side.

## Threading rules

- **STA required:** `ShortcutRepairScanner` (COM IShellLinkW) — wraps its scan on a
  dedicated STA thread so callers on MTA `Task.Run` get correct apartment semantics.
- **Parallel IO:** `InstallSnapshotEngine` walks roots in parallel; `DuplicateFinder`
  hashes sequentially (ArrayPool pressure matters more than concurrency here).
- **WPF dispatch:** `MainViewModel._dispatcher.Invoke` for synchronous UI updates,
  `BeginInvoke` for fire-and-forget.

## Build + release flow

1. `dotnet build DeepPurge.sln -c Release` — compiles all 4 projects (+ tests)
2. `dotnet test` — locks in parser / sanitiser behaviour
3. `dotnet publish` per project → `build/DeepPurge.exe` + `build/DeepPurgeCli.exe`
4. (Optional) `./Build.ps1 -Sign -CertPath ...` → Authenticode + RFC 3161 timestamp
5. Git tag `vX.Y.Z` → GitHub Actions `release.yml` → release assets + SHA256SUMS.txt
6. `wingetcreate update ...` → winget PR
7. Commit `packaging/scoop/deeppurge.json` to a Scoop bucket

## Testing philosophy

Tests cover the parts that broke in the field: schema parsers, version comparisons,
sanitisers, threshold classifiers. We don't mock Windows — tests that need real
filesystem / registry / COM are deliberately not written.
