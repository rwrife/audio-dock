# Windows compatibility program

## Safety contract

The default harness is inventory-only: it captures metadata and diagnostics, never invokes `WriteAsync`, never opens an audio client or audio sample stream, and never transmits output. Tests that can change host state are separately named `WindowsMutationIntegrationTests` and are skipped unless both opt-in environment variables are present. They operate on exactly one operator-selected active endpoint, change only volume by at most 0.05, and restore the recorded value in `finally`; failure to verify restoration fails the test.

Do not run a mutation test during a call, recording, presentation, or other sensitive playback. Do not use an endpoint shared with an active safety or accessibility aid. A failed preflight (unknown ID, inactive endpoint, missing volume capability, or missing observed volume) makes no write.

## Compatibility matrix

| Area | Required scenario | Automated evidence | Physical evidence | Current conclusion |
|---|---|---|---|---|
| Endpoint lifecycle | Removal and reconnect | Simulated by `WindowsCompatibilityHarnessTests` | Not yet captured | Stable IDs preserve distinct endpoints; physical driver behavior remains unverified. |
| Friendly-name collision | Two endpoints named `USB Audio` | Simulated | Not yet captured | IDs, not names, distinguish targets. |
| App lifecycle | Restart and deferred rule | Core fake-adapter tests | Not yet captured | A non-running app is deferred, not applied. |
| Communications roles | Console/multimedia/communications defaults | Simulated planner tests | Not yet captured | Role writes remain capability-probed and require read-back. |
| Inaccessible/elevated apps | Access-denied/protected session | Simulated harness and adapter tests | Not yet captured | Failure is explicit; no escalation is attempted. |
| Exclusive/sandboxed/unavailable sessions | Missing, inaccessible, or unavailable session | Simulated tests | Not yet captured | No universal-control claim; affected rules are skipped or deferred. |
| Verification mismatch | Write result differs from fresh observation | Core fake-adapter test | Not yet captured | Transaction stops and attempts rollback. |
| Host mutation/restoration | One approved endpoint volume change | Opt-in Windows-only test | Not yet captured | Only a passing run on that endpoint/build is evidence. |
| Sign-in startup registration | Write/read-back/remove of the per-user startup entry | Coordinator logic: fake-backend tests (any OS). Real backend round trip: opt-in Windows-only probe test against a throwaway registry location | Not yet captured; startup UI visibility and uninstall ownership still need physical evidence | Registration is opt-in, verified by read-back, and reported honestly when unverifiable. |

Evidence labels are deliberately narrow: `simulated` means a fake backend; GitHub Actions on `windows-latest` is CI evidence, not physical-device evidence; `physical` means a manually recorded supported Windows run. Linux has no Windows evidence.

## Commands and evidence capture

Run these commands from a supported Windows 10 22H2 or Windows 11 checkout. Redact endpoint IDs, device names, user paths, and process names before attaching output to an issue or pull request.

```powershell
dotnet restore AudioDock.sln --locked-mode
dotnet test AudioDock.sln --configuration Release --no-restore --logger "console;verbosity=normal"
dotnet run --project src/AudioDock.Diagnostics --configuration Release > inventory-redacted.json

# Optional and state-changing: choose a single safe endpoint ID from the redacted inventory.
$env:AUDIO_DOCK_MUTATION_TEST = "1"
$env:AUDIO_DOCK_MUTATION_ENDPOINT_ID = "<operator-approved endpoint ID>"
dotnet test tests/AudioDock.Windows.Tests/AudioDock.Windows.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WindowsMutationIntegrationTests --logger "console;verbosity=normal"
Remove-Item Env:AUDIO_DOCK_MUTATION_TEST, Env:AUDIO_DOCK_MUTATION_ENDPOINT_ID

# Optional and state-changing (registry only, never audio): opt-in startup registration
# round trip. It writes only under a throwaway HKCU probe key and deletes that key in
# `finally`; it never touches the production Audio Dock startup entry or the Run key.
$env:AUDIO_DOCK_STARTUP_TEST = "1"
dotnet test tests/AudioDock.Windows.Tests/AudioDock.Windows.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WindowsStartupRegistrationIntegrationTests --logger "console;verbosity=normal"
Remove-Item Env:AUDIO_DOCK_STARTUP_TEST
```

Record Windows edition/build, endpoint transport/driver, session/application mode, command exit code, and the harness lines for both mutation and restoration. Do not publish raw inventory output. At the time this document was added, no physical-machine run has been captured; automated results are recorded by CI and pull-request checks only.

To reduce capture friction, `.\scripts\collect-evidence.ps1` automates the baseline command sequence (including the optional startup probe via `-IncludeStartupProbe`), redacts personal paths, verifies the ZIP checksum, and writes a structured bundle under the git-ignored `evidence/` directory. It changes no audio state and no startup settings. Its self-check fails the run if any captured file still contains the operator's user profile path, user name, domain, or computer name. Collector output remains automated host/CI-level evidence; the physical-evidence rows in the matrix above still require a manual run. The collector's own logic is covered by `tests/powershell/test-collect-evidence.ps1`, which CI executes on every pull request.
