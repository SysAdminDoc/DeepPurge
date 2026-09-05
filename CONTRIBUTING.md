# Contributing to DeepPurge

Thanks for helping improve DeepPurge. Changes are reviewed against one rule first: a maintenance tool must fail safely when Windows state is incomplete, unexpected, or changing underneath it.

## Local setup

You need Windows 10 or 11 x64 and the exact .NET SDK `10.0.302`. The repo's `global.json` disables SDK roll-forward.

```powershell
git clone https://github.com/SysAdminDoc/DeepPurge.git
cd DeepPurge
./BUILD.bat
```

The build produces `build\DeepPurge.exe`, `build\DeepPurgeCli.exe`, and `build\SHA256SUMS.txt`.

## Run the tests

```powershell
dotnet test tests\DeepPurge.Tests\DeepPurge.Tests.csproj -c Release -r win-x64
```

All pull requests must keep the full 459-test suite green. Add a focused test when you change a parser, scanner, safety decision, command builder, or recovery path. The suite uses xUnit v3.

## Project layout

```text
src/
  DeepPurge.Core/       Scanners, safety policy, recovery, and diagnostics
  DeepPurge.App/        WPF views, view model, themes, and tray integration
  DeepPurge.Cli/        Scriptable asInvoker command line
tests/
  DeepPurge.Tests/      xUnit v3 tests
tools/
  DeepPurge.Capture/    Private-desktop production screenshot tool
packaging/
  scoop/                Scoop manifest
.github/                Issue and pull request templates only
```

Read [ARCHITECTURE.md](ARCHITECTURE.md) before changing a cross-cutting safety or execution path.

## Coding standards

- Keep nullable reference types enabled.
- Route destructive work through the existing safety and recovery policies. Don't call raw delete APIs from a new production path.
- Pass `DeleteOptions` through cleanup workflows instead of adding separate boolean switches.
- Resolve external executables from trusted absolute paths and pass arguments without a command shell.
- Keep long scans asynchronous and report progress, cancellation, partial results, and errors.
- Write swallowed operational exceptions through `Core.Diagnostics.Log`.
- Avoid allocations inside large filesystem loops. Reuse buffers where the current implementation does.

## Pull requests

Keep each pull request focused. Use an imperative subject of 72 characters or fewer, and explain why the change is needed in the body. Update the changelog when users can observe the difference.

Security-sensitive reports belong in a private GitHub Security Advisory. See [SECURITY.md](SECURITY.md).

## Merge checklist

- [ ] `dotnet build DeepPurge.sln -c Release -r win-x64` completes with no errors and no new warnings.
- [ ] All 459 tests pass locally.
- [ ] `Build.ps1 -AuditDependenciesOnly` reports no vulnerable or unreadable dependency graph.
- [ ] `Build.ps1 -Sign` produces and verifies both release executables when a signing certificate is available.
- [ ] `DeepPurgeCli.exe doctor` has no unexpected failures on the test machine.
- [ ] User-facing behavior is exercised through the GUI or CLI.
- [ ] README, changelog, architecture notes, and version strings are current.
- [ ] UI changes include refreshed screenshots from `tools\DeepPurge.Capture`.

## Version locations

Keep the release version aligned in:

1. `src\DeepPurge.App\DeepPurge.App.csproj`
2. `src\DeepPurge.Core\DeepPurge.Core.csproj`
3. `src\DeepPurge.Cli\DeepPurge.Cli.csproj`
4. `tools\DeepPurge.Capture\DeepPurge.Capture.csproj`
5. `README.md`
6. `Build.ps1` and `BUILD.bat`
7. `packaging\scoop\deeppurge.json`
8. `CHANGELOG.md`

The release-day sequence lives in [packaging/README.md](packaging/README.md). Builds, tests, signing, and release validation run locally. GitHub hosts the repository and release files.

## Conduct

Be direct and respectful. Technical disagreement is welcome. Personal attacks aren't.
