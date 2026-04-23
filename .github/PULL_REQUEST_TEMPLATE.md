<!-- Short, single-concern PRs merge fastest. If this is a bug fix + new feature, split. -->

## What

<!-- One or two sentences describing the change. -->

## Why

<!-- The motivation. Linked issue number if any: closes #123 -->

## How

<!-- Brief technical notes: which files / patterns / tradeoffs. -->

## Merge checklist

- [ ] `dotnet build DeepPurge.sln -c Release` — 0 errors, 0 warnings
- [ ] `dotnet test tests/DeepPurge.Tests/DeepPurge.Tests.csproj` — all tests pass
- [ ] If parser / sanitiser / detector: added a test that locks in the new behaviour
- [ ] CHANGELOG.md updated (under Unreleased or the next version heading)
- [ ] Version bumped if user-visible (see CONTRIBUTING.md "Version bump")
- [ ] No direct `%LocalAppData%\DeepPurge` references — all per-user paths go through `DataPaths`
- [ ] No new `bool` arguments on destructive pipelines — extend `DeleteOptions` instead
- [ ] SafetyGuard still blocks all previously-protected paths (no regressions in SafetyGuardTests)

<!-- Thanks! -->
