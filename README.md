# Audio Dock

**Audio Dock is a local-first Windows tray utility for people who switch between speakers, headsets, docks, and calls to save, preview, and safely apply named audio-device and per-app volume scenes.**

> **Status:** documentation and backlog scaffold only. There is no application, build, installer, or compatibility result yet.

## Motivation

Windows remembers some audio choices, but a dock, Bluetooth headset, game, meeting app, and USB microphone can still leave defaults and session volumes in the wrong state. Audio Dock makes those repeated changes an explicit, inspectable scene such as **Desk**, **Headset call**, or **Late night**, then applies the scene with a tray action or keyboard shortcut.

## Target users

- Hybrid workers moving between docked, undocked, and headset setups.
- Streamers and players who repeatedly rebalance a known set of applications.
- Keyboard and screen-reader users who want a predictable alternative to several Windows settings panels.
- Anyone who wants audio automation without an account, cloud service, or virtual audio driver.

## Concrete use cases

1. Save a **Headset call** scene: headset as playback and communications output, headset microphone as input, meeting app at 80%, music muted.
2. Save a **Desk speakers** scene and restore it after unplugging a dock.
3. Preview the exact endpoint and volume changes before applying a scene.
4. Apply a scene from the tray or a configurable global hotkey, then undo to the pre-apply snapshot.
5. Export scenes as readable JSON and restore them on the same or another Windows PC, resolving unmatched devices explicitly.

## Intended workflow

1. Audio Dock inventories active Windows render/capture endpoints and currently running audio sessions.
2. The user creates a named scene by capturing current state or selecting targets manually.
3. The editor reports missing/ambiguous endpoint matches and shows a before/after preview.
4. The user applies the scene. Audio Dock verifies observable state, reports partial failures, and keeps a bounded undo snapshot.
5. Optional hotkeys provide deliberate one-action switching. No background trigger is enabled by default.
6. The user can export, import, back up, or delete all local scene data.

## MVP features

- Endpoint inventory with stable IDs, friendly names, direction, availability, and default/communications roles.
- Named scenes for default playback, communications playback, default capture, communications capture, and master endpoint volume/mute.
- Per-application session volume/mute rules for currently running applications, with explicit deferred matching when an app is not running.
- Dry-run preview, ordered apply, post-apply verification, partial-failure report, and one-step rollback snapshot.
- Tray menu, scene editor, global hotkeys, and keyboard/screen-reader accessible status.
- Versioned local JSON, schema validation, import/export, and conflict-safe backup.
- Structured local diagnostic log that excludes audio content.

## Non-goals

- Recording, transcribing, analyzing, or transmitting audio.
- Mixing audio streams, replacing Windows audio drivers, creating virtual devices, or routing arbitrary process audio through a custom driver.
- Cloud sync, accounts, telemetry, remote control, voice activation, or automatic microphone surveillance.
- Promising universal control of protected, elevated, exclusive-mode, sandboxed, or not-yet-running sessions.
- macOS support in the MVP; native platform behavior should be proven on Windows before considering another adapter.

## Privacy, permissions, and storage

Audio Dock operates locally and never reads audio samples. It stores scene names, endpoint identifiers and friendly names, executable identity rules, requested volume/mute values, preferences, and bounded diagnostics under `%LOCALAPPDATA%\AudioDock`. It does not require microphone content access, network access, an account, or administrator rights for normal operation. A global-hotkey permission is not separately requested on Windows; any later startup registration is opt-in and reversible.

Executable paths can reveal user information, so the UI makes path-based matching optional and export can redact paths in favor of publisher/product metadata. Diagnostics are local, bounded, inspectable, and clearable. Imports are validated before they can change system state. Applying a scene is always a user-visible action in the MVP.

## Accessibility

All scene creation, preview, apply, undo, export, and settings actions must be keyboard reachable. Controls require programmatic names, logical focus order, visible focus, non-color-only state and error cues, Windows high-contrast support, and usable scaling at 200%. Tray notifications are supplementary; important results remain available in the main window and activity view.

## Technology direction

- **Platform:** Windows 10 version 22H2 and Windows 11.
- **Runtime/UI:** .NET 8 and WPF, using MVVM and a UI-independent core library.
- **Native boundary:** Windows Core Audio COM APIs behind narrow inventory, role-selection, endpoint-volume, and session-control interfaces. Any policy API with OS compatibility risk must be capability-probed and isolated.
- **Persistence:** versioned JSON written atomically; no database needed for the MVP.
- **Testing:** xUnit for matching/planning/persistence, fake native adapters for failure paths, and opt-in Windows integration tests on disposable scenes/devices.

## Development quickstart

The source tree is not implemented yet. The planned first milestone will create the solution and make these commands real:

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

Do not treat those commands as verified until the project skeleton issue lands and CI records real output.

## Milestones

1. Core scene schema, deterministic matching, planner, and CI.
2. Read-only Windows endpoint/session inventory.
3. Transactional scene application, verification, and rollback.
4. Accessible editor, tray workflow, and hotkeys.
5. Import/export, privacy controls, diagnostics, compatibility matrix, and signed release candidate.

See [PLAN.md](PLAN.md) and the issue tracker for acceptance criteria and dependency order.

## License

MIT — see [LICENSE](LICENSE).
