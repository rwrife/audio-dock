# Transactional scene application and Windows mutation boundary

## Execution model

`AudioDock.Core.Execution.SceneExecutor` is UI-independent. `PreviewAsync` captures inventory and returns the same ordered plan used by apply: default roles, endpoint volume/mute, then running-session volume/mute. Each plan item carries its typed desired value and original match rule so the executor can resolve it again immediately before a write.

`ApplyAsync` captures the complete relevant pre-state before its first mutation. At each safe operation boundary it:

1. observes cancellation;
2. captures fresh inventory and deterministically re-resolves the target;
3. rejects stale, missing, ambiguous, or newly unsupported targets;
4. issues one typed command;
5. captures fresh inventory and verifies the observable result.

Volumes are constrained by `VolumeLevel` to finite values from 0.0 through 1.0. Read-back allows a 0.005 scalar tolerance for device quantization. A non-running application rule is reported as `Deferred`, never `Applied`. Unsupported and ambiguous controls remain explicit `Skipped` facts.

A failed write or verification mismatch stops the transaction and triggers best-effort rollback in reverse order. The result preserves `Failed`, `VerificationMismatch`, `RolledBack`, and `RollbackFailed` facts. Cancellation is accepted only between operations and also triggers rollback of completed writes. After a successful apply, one in-memory undo snapshot is retained; a later successful apply supersedes it, and undo consumes it exactly once. No snapshot is persisted yet.

## Windows adapter

`WindowsAudioInventory` implements the narrow `IAudioControlAdapter` contract. The production backend supports:

- active endpoint master volume and mute through `IAudioEndpointVolume`;
- active session volume and mute through `ISimpleAudioVolume`;
- default console, multimedia, and communications roles through the compatibility-sensitive `IPolicyConfigVista` interface.

The role interface is probed before role capability is advertised. It is not a documented public Windows SDK contract, so availability may differ by Windows build. A missing interface, access denial, protected/elevated/exclusive session, disconnected endpoint, exited session, or COM error is returned as a failure; the caller must not claim the change succeeded. Every successful scene operation still requires executor read-back verification.

The adapter enumerates metadata and invokes control setters only. It does not create an audio client, open a capture/render stream, inspect samples, install a driver, elevate privileges, or communicate over a network.

## Automated evidence

Managed fake-adapter tests cover:

- ordered role, endpoint, and running-session writes;
- pre-state capture and read-back after each write;
- access denial after an earlier successful write;
- disappearing sessions before use;
- observable verification mismatch;
- cancellation at a safe boundary;
- rollback success and rollback failure;
- explicit deferred rules and one-use undo.

These tests establish core transaction behavior and managed adapter wiring. Builds and tests run on Linux with Windows targeting enabled, but Linux cannot execute Core Audio COM or establish Windows/device/driver/application compatibility.

## Opt-in Windows integration check

The integration test is skipped unless all safety gates are present. First use the read-only diagnostic to copy the exact ID of an endpoint you explicitly approve for a brief volume change. Then run on Windows PowerShell:

```powershell
$env:AUDIO_DOCK_MUTATION_TEST = "1"
$env:AUDIO_DOCK_MUTATION_ENDPOINT_ID = "<exact active endpoint ID>"
dotnet test tests/AudioDock.Windows.Tests/AudioDock.Windows.Tests.csproj `
  --configuration Release `
  --filter FullyQualifiedName~WindowsMutationIntegrationTests `
  --logger "console;verbosity=normal"
```

The test records the original volume, changes it by at most 0.05 within bounds, reads it back, and restores the original value in `finally`. It reports mutation and restoration separately and fails if restoration cannot be observed. Do not run it during a call or other sensitive playback. This check proves only the selected endpoint on that specific host; it does not validate role changes, every device, protected sessions, drivers, Bluetooth stacks, or universal Windows compatibility.
