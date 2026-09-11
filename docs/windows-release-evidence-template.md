# Windows release evidence template (Issue #7)

Use this template to capture reproducible release-candidate evidence without over-claiming compatibility.

> Redact endpoint IDs, device names, executable paths, user paths, and process names before sharing logs.

## Run metadata

- Date/time (UTC):
- Operator:
- Commit SHA:
- Candidate artifact (ZIP + installer candidate):
- Windows edition/build:
- Device/driver notes (high-level only):

## Baseline commands

Use the automated collector to run the baseline and capture a redacted bundle:

```powershell
.\scripts\collect-evidence.ps1                 # optional: -IncludeStartupProbe for the registry-only round trip
```

It runs exactly the commands below, records per-command exit codes and redacted output tails under
`evidence/run-<timestamp>/`, verifies the ZIP checksum, and writes `evidence-bundle.md`. It changes no
audio state and touches no startup settings. Its results are host/CI-level evidence only; the manual
scenario table below still requires a human on physical hardware.

Equivalent manual commands (the collector wraps these):

```powershell
dotnet restore AudioDock.sln --locked-mode
dotnet format AudioDock.sln --verify-no-changes --no-restore
dotnet build AudioDock.sln --configuration Release --no-restore
dotnet test AudioDock.sln --configuration Release --no-build --logger "console;verbosity=normal"
.\scripts\package-win-x64.ps1
.\scripts\test-package-win-x64.ps1
.\scripts\smoke-launch-win-x64.ps1
```

Record each command's exit code and any relevant output snippets.

## Gate validation

After filling in the manual scenario table and honesty notes below, validate the
completed bundle with:

```powershell
.\scripts\finalize-evidence.ps1                    # validates the most recent evidence\run-* directory
.\scripts\finalize-evidence.ps1 -RunDirectory .\evidence\run-<timestamp>
```

It checks that every required manual scenario row is present with an explicit
pass/fail verdict and a non-empty evidence reference, that the honesty notes are
filled in, and that `run-metadata.json` shows a matching ZIP checksum with no
failed or errored automated commands. Exit code 0 means the bundle is complete
and internally consistent; 1 lists every blocker to fix; 2 means the bundle
could not be found or parsed.

The validator checks bookkeeping only. It does not and cannot independently
observe physical Windows behavior, and a complete bundle still requires human
review — installer selection and certificate-backed signing have separate gates.
Its own logic is covered by `tests/powershell/test-finalize-evidence.ps1`, which
CI executes on every pull request.

## Installer candidate under test

- Candidate type: MSIX / conventional installer (specify tool)
- Candidate version:
- Install command:
- Uninstall command:

## Required scenario evidence

| Scenario | Pass/Fail | Evidence reference |
|---|---|---|
| Tray appears after launch |  |  |
| Packaged tray app smoke-launches on `windows-latest` CI |  |  |
| Startup is off by default |  |  |
| Startup opt-in is explicit and reversible |  |  |
| Upgrade preserves scenes/settings |  |  |
| Uninstall removes startup entry it created |  |  |
| Uninstall retains `%LOCALAPPDATA%\\AudioDock` by default |  |  |
| Optional data deletion requires explicit user choice |  |  |
| Accessibility smoke (keyboard/focus/high contrast/200% scaling/non-color cues) |  |  |

## Mutation integration (optional, state-changing)

Run only on an operator-approved endpoint and capture restore behavior.

```powershell
$env:AUDIO_DOCK_MUTATION_TEST = "1"
$env:AUDIO_DOCK_MUTATION_ENDPOINT_ID = "<approved endpoint ID>"
dotnet test tests/AudioDock.Windows.Tests/AudioDock.Windows.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WindowsMutationIntegrationTests --logger "console;verbosity=normal"
Remove-Item Env:AUDIO_DOCK_MUTATION_TEST, Env:AUDIO_DOCK_MUTATION_ENDPOINT_ID
```

- Mutation test executed: yes/no
- Restore verification outcome:

## Startup registration round trip (optional, registry-only, never audio)

Writes only under a throwaway HKCU probe key; the key is deleted in `finally`. It never
touches the production Audio Dock startup entry or the Run key.

```powershell
$env:AUDIO_DOCK_STARTUP_TEST = "1"
dotnet test tests/AudioDock.Windows.Tests/AudioDock.Windows.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WindowsStartupRegistrationIntegrationTests --logger "console;verbosity=normal"
Remove-Item Env:AUDIO_DOCK_STARTUP_TEST
```

- Startup round-trip test executed: yes/no
- Probe-key cleanup (restoration) outcome:
- Notes:

## Packaging and supply-chain evidence

- ZIP SHA-256 (`*.zip.sha256`) verified: yes/no
- Package internal `SHA256SUMS.txt` verified: yes/no
- CycloneDX SBOM present and validated: yes/no
- `dotnet list ... --vulnerable --include-transitive` output attached: yes/no

## Compatibility and honesty notes

- What this run proves:
- What this run does **not** prove (device/driver/application classes not covered):
- Any partial failures or skipped checks:

## Release gate decision

- Installer decision ready (yes/no):
- Code-signing evidence captured (yes/no):
- Ready to publish release archive + changelog (yes/no):
- Blockers remaining:
