# Audio Dock implementation plan

## Scope

The MVP is a Windows 10/11 tray application that turns a desired audio configuration into a named, reviewable scene. It inventories devices and active sessions, resolves portable match rules, computes a change plan, previews it, applies supported changes, verifies observable results, and retains the immediately preceding state for rollback.

The design must remain useful when only some controls are available. Unsupported endpoint-role changes or inaccessible sessions are reported as explicit partial results, never silently ignored.

## Architecture

```text
AudioDock.App (WPF/MVVM)
  scene editor · preview · tray · hotkeys · accessibility
                   |
AudioDock.Core
  scene schema · matching · planning · result model · import/export
                   |
AudioDock.Windows
  endpoint inventory · role adapter · endpoint volume · session controls
                   |
             Windows Core Audio
```

### Core contracts

- `AudioScene`: version, name, role targets, endpoint state rules, application session rules, metadata.
- `EndpointDescriptor` and `SessionDescriptor`: normalized observations with no COM objects crossing the adapter boundary.
- `IMatchResolver`: deterministic scoring and explicit ambiguity results; never picks an equal-score match silently.
- `IScenePlanner`: pure desired/current-state comparison producing ordered `PlannedChange` items.
- `IAudioControlAdapter`: capability report, inventory snapshot, apply-one operation, and read-back verification.
- `ISceneExecutor`: captures rollback state, executes a plan, verifies each operation, and returns applied/skipped/failed/rolled-back facts.
- `ISceneStore`: atomic versioned JSON CRUD and export/import validation.

## Technology choices

- **.NET 8:** maintained runtime, strong test tooling, and direct COM interop options.
- **WPF + MVVM:** mature Windows accessibility and tray integration; Windows is intentionally the only MVP platform.
- **Windows Core Audio COM:** native endpoint/session observation and control without capturing samples or installing a virtual driver.
- **JSON:** scenes should be inspectable, diffable, portable, and small. Schema migration is simpler than adding a database.
- **xUnit + fake adapters:** native audio state varies by machine, so most behavior must be deterministic and testable without changing developer audio settings.

Third-party packages are not selected until license, maintenance, and OS-support review. In particular, endpoint-role selection APIs require a focused compatibility spike before becoming a promise.

## Data and matching

A scene stores a hierarchy of matching hints rather than assuming endpoint IDs are portable: exact local ID, interface/container identity when available, direction, friendly name, manufacturer/product hints, and an optional user-approved alias. Application rules prefer package family or signed product metadata where available and use executable paths only with explicit consent.

Imports are data only. They cannot execute commands or load plugins. Unknown schema versions and ambiguous matches block apply until reviewed. Writes use temporary-file + atomic replace, and export supports path redaction.

## Milestones and dependency order

### M1 — Skeleton, schema, and CI

Create solution boundaries, scene/result models, JSON schema migration, deterministic matching/planning, fake adapters, tests, formatting, and Windows CI. This unlocks all later work.

### M2 — Read-only native inventory

Implement endpoint and active-session enumeration, normalized capability facts, device-change observation, and a diagnostic inventory screen. No system audio mutation yet.

### M3 — Transactional apply and rollback

Implement endpoint roles where capability-proven, endpoint volume/mute, running-session controls, dry-run plans, pre-apply snapshots, ordered operations, read-back verification, cancellation boundaries, partial-failure reporting, and undo.

### M4 — Accessible product workflow

Build the scene list/editor, capture-current action, ambiguity resolution, before/after preview, tray menu, global hotkeys, persistent activity results, and first-run privacy explanation.

### M5 — Portability and release readiness

Add validated import/export, redacted exports, diagnostics controls, compatibility tests across supported Windows versions, packaging, signing documentation, release notes, and installer/uninstall behavior.

## Testing strategy

- **Unit:** schema migrations, invalid imports, matching scores/ties, planner ordering, idempotency, rollback-plan construction, redaction, and store atomicity.
- **Property/fuzz:** malformed and future-version JSON never mutates audio state; matching stays deterministic.
- **Component:** fake adapters simulate unplugging, access denial, stale sessions, partial writes, verification mismatch, and rollback failure.
- **Windows integration:** read-only inventory by default; mutation tests are opt-in, record pre-state, use bounded volume values, restore in `finally`, and disclose device requirements.
- **UI/accessibility:** automation peers, keyboard-only paths, focus order, high contrast, scaling, and non-color-only status.
- **CI:** restore/build/test on `windows-latest`; package smoke checks only after packaging exists.

No test may claim all hardware, Bluetooth stacks, audio drivers, protected sessions, or exclusive-mode apps are compatible. A maintained compatibility matrix must separate simulated, CI, and physical-machine evidence.

## Packaging and distribution

Start with a self-contained `win-x64` ZIP for transparent testing, then add MSIX or a conventional installer after tray/startup/uninstall behavior is validated. Release assets need checksums and an SBOM. Code signing is documented but not claimed until a real signed artifact exists. The app must uninstall without deleting user scenes unless the user opts in.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Endpoint-role selection differs across Windows builds | Isolate the policy adapter, capability-probe, test 22H2/11, and degrade to guided settings rather than lying about success. |
| IDs change after driver updates or ports change | Layered user-reviewable matching, aliases, ambiguity blocking, and preview. |
| Sessions appear/disappear during apply | Snapshot plus per-operation re-resolution, explicit skipped results, and deferred rules. |
| Partial application leaves surprising state | Ordered plan, read-back verification, rollback snapshot, persistent result view, and bounded safe values. |
| Elevated/protected/exclusive sessions reject control | No privilege escalation; disclose unsupported sessions and preserve remaining scene value. |
| Scene files leak executable paths | Optional path matching, redacted export, local-only defaults, and clearable diagnostics. |
| Hotkeys conflict with other apps | Conflict detection, configurable chords, opt-in registration, and easy disable. |

## Explicit non-goals

- Audio recording, transcription, content inspection, or telemetry.
- Virtual audio cables, kernel drivers, DSP, equalization, or stream mixing.
- Rule engines that automatically react to microphone use, calendar state, network, or location in the MVP.
- Remote/cloud control, team sync, accounts, subscriptions, or mobile companion apps.
- macOS/Linux parity before the Windows contracts and compatibility limits are proven.
- Universal control claims for every device, driver, application, or Windows build.
