# Blocked Roadmap Items

Items moved here from ROADMAP.md because they require external resources,
credentials, or human judgment that cannot be resolved during autonomous
development passes.

## v0.9.x — stabilization

- [ ] **Submit winget manifest** to `microsoft/winget-pkgs`
      Blocked: needs a published GitHub Release with a stable download URL
      and ideally an Authenticode-signed binary.

- [ ] **Create `SysAdminDoc/scoop-bucket`** repo and commit the Scoop manifest
      Blocked: needs GitHub repo creation + a published release to populate
      the manifest's `url` and `hash` fields.

- [ ] **Obtain code-signing certificate** and wire `DEEPPURGE_CERT_PATH` /
      `DEEPPURGE_CERT_PASSWORD` secrets into the release workflow
      Blocked: requires purchase decision (EV vs OV), Azure Code Signing
      enrollment, and secret provisioning in GitHub Actions.

- [ ] **Real-world field test** of each v0.9 panel on a freshly-imaged
      Windows 11 VM
      Blocked: requires a clean VM image and manual human testing.

## v0.12 — accessibility + polish

- [ ] **Crowdin submission** for localization strings
      Blocked: requires Crowdin project creation and external contributor setup.
      The `.resx` infrastructure is already in place (`Properties/Resources.resx`).

## v1.0 — large features (need dedicated session + testing)

- [ ] **Velopack auto-updater**
      Blocked: requires Velopack NuGet integration, release workflow changes,
      and end-to-end testing of the download/apply/restart flow.
      `Core/Updates/UpdateChecker.cs` has the detection; Velopack adds the apply.

- [ ] **ETW registry monitoring for install tracking**
      Blocked: requires `Microsoft.Diagnostics.Tracing.TraceEvent` NuGet,
      kernel-level ETW session setup, and testing under real installer workloads.
      The USN journal + snapshot approach works for now.

- [ ] **CIM migration from System.Management (WMI)**
      Blocked: low-value for Windows-only app. System.Management works fine on
      Windows; CIM migration adds complexity without clear benefit. Revisit if
      the project ever targets cross-platform.

- [ ] **Wire .resx localization into XAML and code-behind**
      Blocked: requires editing ~100 hardcoded strings in MainWindow.xaml
      (1616 lines) with `{x:Static}` bindings, expanding Resources.resx from
      20 to ~150 strings. Needs visual verification that no binding breaks
      the UI. The infrastructure is ready (Resources.resx, Resources.Designer.cs).

- [ ] **ViewModel decomposition — extract per-panel ViewModels**
      Blocked: MainViewModel (1666 lines across 2 partials) needs splitting
      into ~10 per-panel VMs. Requires updating all XAML DataContext bindings
      and MainWindow.xaml.cs panel switching. Risk of breaking bindings
      without visual testing. AppSettings infrastructure is ready.

- [ ] **CsWin32 type-safe PInvoke**
      Blocked: touches the most sensitive P/Invoke code (FastDiskAnalyzer MFT
      structs, UsnJournalReader, ShortcutRepairScanner COM). Struct alignment
      bugs could cause data corruption or crashes. Needs a dedicated session
      with thorough per-API testing. CsWin32 0.3.298 is ready.

- [ ] **winget COM API migration**
      Blocked: `Microsoft.Management.Deployment` COM requires specific registration
      that doesn't work reliably from non-MSIX/unpackaged apps. Current CLI parsing
      approach works. Revisit when winget ships official NuGet interop.

- [ ] **Hunter Mode (drag-to-identify)**
      Blocked: requires new WPF overlay window with Win32 `WindowFromPoint` +
      `GetWindowThreadProcessId` interop and visual/interactive testing.

- [ ] **WCAG 2.2 accessibility pass**
      Blocked: requires visual testing in all four Windows contrast themes
      (Aquatic, Desert, Dusk, Night Sky) + Narrator testing + keyboard-only
      verification. HighContrast ResourceDictionary must map brushes to
      SystemColor*Color and meet 2px/3:1 focus indicator requirements.

- [ ] **Run elevated rendered QA across all themes**
      Blocked: DeepPurge.exe requires administrator elevation, and this
      autonomous session is non-elevated. Launch `build\DeepPurge.exe`
      elevated and inspect Programs, Deletion Recovery, Settings / Privacy,
      Scheduled Cleaning, About / Updates, and legacy safety panels across
      every built-in theme; fix any clipping, contrast, focus, disabled-state,
      or empty-state regressions found.

- [ ] **Publish independent accuracy benchmark**
      Blocked: requires a clean VM image, 8 specific test apps (Adobe CC,
      Discord, Steam, Bongo Cat, Notepad++ Portable, Brave Portable,
      CCleaner leftovers, Corel PDF Fusion), screen recordings, and
      reproducible methodology documentation. May drive accuracy
      improvements in leftover scanners.
