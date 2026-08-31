# Core Audio compatibility and evidence

## Scope

The diagnostic command inventories Core Audio metadata only and never mutates audio state. The library also exposes a separate, narrow control adapter for transactional scene execution. Neither path opens an audio stream or reads audio samples.

The supported targets are Windows 10 22H2 and Windows 11. The implementation uses .NET 8 and direct Windows SDK COM/Win32 interop; it adds no third-party runtime dependency. Repository code is MIT licensed. Windows APIs remain governed by the Windows SDK and operating-system terms.

## Endpoint and role strategy

For both render and capture, `IMMDeviceEnumerator.EnumAudioEndpoints` is called with `DEVICE_STATEMASK_ALL`, covering active, disabled, not-present, and unplugged endpoints where the driver and Windows expose them. `IMMDevice.GetId` is the local stable ID; friendly names are presentation only and are never used to merge or select endpoints.

Each of the console, multimedia, and communications defaults is queried independently with `GetDefaultAudioEndpoint` for render and capture. The returned endpoint ID is compared ordinally with enumerated IDs. Missing defaults are left unassigned. Role mutation is isolated behind a runtime probe for the compatibility-sensitive, undocumented `IPolicyConfigVista` interface. Capability is not advertised when activation fails, and every attempted role change requires observable read-back before it can be reported as applied.

Endpoint volume/mute uses read methods on `IAudioEndpointVolume`. Active sessions are enumerated per active endpoint through `IAudioSessionManager2`; volume/mute is read through `ISimpleAudioVolume`. Session identity combines its endpoint ID with the Core Audio session identifier. Packaged identity is normalized to package-family name when Windows exposes one. Classic identity uses process name; inaccessible or exited processes retain a non-fabricated PID label and diagnostic. Executable paths are neither requested nor returned unless the caller explicitly opts in.

## Observation, failures, and fallback

Change observation is cancellation-aware snapshot polling (two seconds by default). A snapshot is emitted initially and whenever its deterministic endpoint/session/diagnostic fingerprint changes. Disposing the inventory cancels active observers and releases its backend; each property variant, process handle, and COM reference is released in `finally` paths during every capture.

An endpoint, volume, process, or session failure becomes a scoped diagnostic when the remaining snapshot is useful. Failure to create or enumerate the top-level Core Audio inventory surfaces as `AudioInventoryException`; no empty success is fabricated. Sessions can disappear between COM calls and are reported diagnostically on that poll. Unsupported/offline endpoint activation leaves volume/mute unknown rather than inventing values.

There is no silent mutation fallback. Endpoint and session volume/mute setters use `IAudioEndpointVolume` and `ISimpleAudioVolume`; inaccessible or vanished targets return explicit failures. Consumers may show the diagnostic or guide the user to Windows Settings, but must not claim Audio Dock changed a role or value unless read-back verification succeeds. See [transactional apply and Windows mutation](transactional-apply.md).

## Running the local diagnostic

On a Windows 10 22H2 or Windows 11 machine, from the repository root:

```powershell
dotnet run --project src/AudioDock.Diagnostics --configuration Release
dotnet run --project src/AudioDock.Diagnostics --configuration Release -- --watch
dotnet run --project src/AudioDock.Diagnostics --configuration Release -- --include-executable-paths
```

The command writes JSON to local standard output only. Paths are omitted by default; `--include-executable-paths` is explicit consent to include them. Redirecting or transmitting that output is the operator's choice, not behavior of Audio Dock. Press Ctrl+C to stop watch mode.

## Evidence limits

Managed fake/component tests cover unplugged endpoints, duplicate names with distinct IDs, simulated inaccessible-process and partial-failure diagnostics, disappearing sessions, transactional cancellation, verification, rollback success/failure, native-enum mapping, and teardown. Linux builds validate compilation and managed behavior only. They do not execute Windows COM, enumerate physical devices, validate driver behavior, or prove Windows 10/11 compatibility. The mutation integration test is opt-in, bounded, and restores the selected endpoint in `finally`; it was not run by Linux verification. Native evidence must be recorded from real supported Windows installations before stronger compatibility claims are made; protected, elevated, exclusive-mode, rapidly exiting, and driver-specific sessions may remain partially controllable or observable.
