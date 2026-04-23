# Security Policy

## Supported versions

Only the latest minor release receives security fixes. Older releases are archived.

| Version | Supported |
|---------|-----------|
| 0.9.x   | Yes       |
| 0.8.x   | No        |
| < 0.8   | No        |

## Reporting a vulnerability

Please **do not** file a public GitHub issue for security reports.

Email a description of the issue to **snafumatthew@gmail.com** with the subject line
`DeepPurge security report`. Include:

1. Affected version (`DeepPurge version` from the About panel, or `deeppurgecli version`).
2. Impact description — what can an attacker do?
3. Reproduction steps, ideally with a minimal proof-of-concept.
4. Your disclosure timeline expectations (default: 90 days).

You can expect an acknowledgement within 48 hours and a triage update within 7 days.

## Scope

In scope:

- Local privilege escalation via DeepPurge running as Administrator
- Arbitrary code execution via crafted input (winapp2.ini, install snapshots, job names)
- SafetyGuard bypass — any input path or registry key that escapes the protected list
- Command injection in `ScheduleManager`, `WindowsRepairEngine`, or `UninstallEngine`
- Supply-chain issues in `DataPaths` (path traversal, symlink attacks)

Out of scope:

- Issues that require a separately-compromised user account on the same machine
- Social engineering requiring the user to paste malicious content into DeepPurge
- Denial of service against the local UI (crashing DeepPurge itself does not compromise the system)
- Vulnerabilities in dependencies that are already fixed upstream — upgrade and re-test first

## Safe-harbour

Good-faith security research is welcome. We will not pursue legal action for researchers who:

- Test only on systems they own or have permission to test
- Avoid destroying data, degrading service, or accessing third-party data
- Give a reasonable disclosure window before going public

## Hardening context

- Every destructive file and registry operation passes through
  [`Core/Safety/SafetyGuard.cs`](src/DeepPurge.Core/Safety/SafetyGuard.cs) — the blocklist
  is locked in by the `SafetyGuardTests` suite.
- The GUI runs `requireAdministrator`; the CLI runs `asInvoker` so scripting works without UAC.
  Destructive CLI commands invoked from a non-elevated shell fail at the underlying call
  (schtasks / pnputil / registry write), not in DeepPurge's logic.
- User-supplied schedule job names, winget IDs, and MSI product codes are regex-sanitised
  before being interpolated into a command line.
