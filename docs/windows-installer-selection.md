# Windows installer selection record

## Purpose

Issue #7 requires selecting **MSIX** or a **conventional installer** only after tested tray/startup/uninstall behavior on supported Windows versions. This record keeps that decision reproducible and evidence-based.

Current status: **pending physical evidence** (no installer selected yet).

## Candidate options

| Option | Why it is considered | Known risks to verify |
|---|---|---|
| MSIX | Native Windows packaging/update story; clean install/uninstall metadata | Startup/task-tray behavior may differ under packaged identity; per-user startup registration and data retention behavior must be observed on both supported OS versions |
| Conventional installer (e.g., MSI/Inno Setup/WiX bundle) | Familiar desktop deployment/uninstall flow for non-Store distribution | Must prove opt-in startup toggle ownership/removal, non-destructive uninstall defaults, and safe upgrade behavior without claiming universal driver/app compatibility |

## Required physical evidence before selection

All rows must be captured on **Windows 10 22H2** and **Windows 11** using real machines or VMs with interactive desktop sessions.

| Scenario | Required outcome |
|---|---|
| First launch and tray visibility | App starts, tray icon appears, main window and activity/status surfaces remain available |
| Startup registration (opt-in only) | Startup remains off by default; enabling is explicit/visible; disabling is explicit/visible and reversible |
| Upgrade over existing install | Existing scenes/settings remain intact; app binary updates; no surprise startup toggle changes |
| Uninstall default path | Installer artifacts removed; startup entry removed if created by installer; `%LOCALAPPDATA%\\AudioDock` retained by default |
| Optional uninstall data deletion | Data deletion happens only when explicitly selected by the user |
| Accessibility smoke | Keyboard reachability, visible focus, non-color-only status cues, and 200% scaling pass for install/startup/uninstall-related UI |

## Evidence bundle inputs

Use `docs/windows-release-evidence-template.md` and attach:

1. OS build and installer candidate metadata.
2. Command output and exit codes.
3. Redacted screenshots/log snippets proving startup toggle and uninstall behavior.
4. Explicit pass/fail per scenario above.

## Decision gate

Set `Selected option` only when both supported OS rows are complete with passing evidence.

- Selected option: **TBD**
- Decision date: **TBD**
- Evidence links: **TBD**
- Remaining blocker (if any): **Physical Windows validation not yet captured**
