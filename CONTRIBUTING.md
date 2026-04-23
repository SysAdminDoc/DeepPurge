# Contributing to DeepPurge

Thanks for your interest. This guide covers local setup, PR conventions, and the bar for merge.

## Local setup

Requirements:

- Windows 10/11 x64
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- Git Bash or PowerShell 5.1+

```powershell
git clone https://github.com/SysAdminDoc/DeepPurge.git
cd DeepPurge
./BUILD.bat
```

This produces `build/DeepPurge.exe` (GUI) and `build/DeepPurgeCli.exe` (CLI).

## Running tests

```powershell
dotnet test tests/DeepPurge.Tests/DeepPurge.Tests.csproj
```

All PRs must keep the full suite green. If you touch a parser, detector, or sanitiser,
add a test that locks in the expected behaviour. We use xUnit 2.9.

## Project layout

```
src/
  DeepPurge.Core/       # Scanners, safety, diagnostics — no WPF refs (except IconExtractor)
  DeepPurge.App/        # WPF views, ViewModel, themes
  DeepPurge.Cli/        # Headless entry point (asInvoker manifest)
tests/
  DeepPurge.Tests/      # xUnit — no WPF runtime required to run
packaging/              # winget + scoop manifests
.github/workflows/      # CI (build.yml) + release (release.yml)
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for the cross-cutting flow and the v0.9 feature map.

## Coding standards

- **.NET 8 + nullable reference types enabled.** No `#nullable disable`.
- **Global usings are set in each csproj** — don't re-import `System`, `System.IO`, etc. in
  every file. Do import specific types per file (e.g. `using DeepPurge.Core.Safety;`).
- **SafetyGuard is the single choke-point for destructive ops.** Don't bypass it. If a new
  pipeline needs to delete, call `SafetyGuard.IsXxxSafeToDelete` before acting.
- **`DeleteOptions` is the only permitted way to thread DryRun/SecureDelete/Recycle through.**
  Don't add new bool arguments — extend the record.
- **No inner-loop allocations on large scans.** Use `ArrayPool<byte>` for buffers in any
  code that processes a significant number of files.
- **Log via `Core.Diagnostics.Log`.** Swallowed exceptions should still write a line to the
  rotating log so field issues can be debugged.

## Commit / PR conventions

- Prefer one concern per PR. A PR that fixes a bug AND adds a feature should be two PRs.
- First line ≤ 72 chars, imperative mood: `Fix DetectOS routing in Winapp2Parser`.
- Body explains the **why** — `git log -p` shows the what.
- If you fix a user-visible behaviour, add a CHANGELOG entry under the `Unreleased` or
  next-version heading.
- If your change is security-sensitive, prefer a silent fix + a GitHub Security Advisory
  once released. See [SECURITY.md](SECURITY.md).

## Merge bar

Before a PR lands:

- [ ] `dotnet build DeepPurge.sln -c Release` — 0 errors, 0 warnings
- [ ] `dotnet test` — all tests pass
- [ ] If a feature: `deeppurgecli doctor` still reports 0 FAIL on a clean install
- [ ] README / CHANGELOG updated if the PR changes observable behaviour
- [ ] Version bumped in the four places (see "Version bump" below)

## Version bump

All version strings must match. The canonical version number lives in:

1. `src/DeepPurge.App/DeepPurge.App.csproj` `<Version>`
2. `src/DeepPurge.Core/DeepPurge.Core.csproj` `<Version>`
3. `src/DeepPurge.Cli/DeepPurge.Cli.csproj` `<Version>`
4. `README.md` shields.io badge + heading
5. `CLAUDE.md` heading
6. `Build.ps1` title banner
7. `BUILD.bat` title
8. `packaging/winget/SysAdminDoc.DeepPurge.yaml` `PackageVersion`
9. `packaging/scoop/deeppurge.json` `version`
10. `CHANGELOG.md` new section heading

## Releasing

See [packaging/README.md](packaging/README.md) for the release-day checklist (tag →
GitHub Actions → SHA256 capture → winget PR → Scoop bucket commit).

## Code of conduct

Be decent. Technical criticism of PRs is welcome; personal attacks are not. Maintainers
reserve the right to lock threads that turn unproductive.
