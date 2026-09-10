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
| Startup registration (opt-in only) | Startup remains off by default; enabling from Settings is explicit/visible and verified by read-back; disabling is explicit/visible and reversible; the app reconciles external changes honestly at next launch |
| Upgrade over existing install | Existing scenes/settings remain intact; app binary updates; no surprise startup toggle changes; the startup entry continues to launch the upgraded binary at the same location |
| Uninstall default path | Installer artifacts removed; startup entry handled per the ownership rule below; `%LOCALAPPDATA%\\AudioDock` retained by default |
| Optional uninstall data deletion | Data deletion happens only when explicitly selected by the user |
| Accessibility smoke | Keyboard reachability, visible focus, non-color-only status cues, and 200% scaling pass for install/startup/uninstall-related UI |

## Evidence bundle inputs

Use `docs/windows-release-evidence-template.md` and attach:

1. OS build and installer candidate metadata.
2. Command output and exit codes. The `.\scripts\collect-evidence.ps1` collector automates this part: it runs the baseline commands, verifies the ZIP checksum, redacts personal paths, and writes a structured bundle under `evidence/` (git-ignored). It only automates capture; automated output is host/CI-level evidence and cannot satisfy the physical rows above.
3. Redacted screenshots/log snippets proving startup toggle and uninstall behavior.
4. Explicit pass/fail per scenario above.

## Startup-entry ownership rule

The in-app Settings toggle owns one per-user startup entry pointing at the current
executable location. Whichever installer is selected must satisfy this rule:

- An upgrade that keeps the executable at the same path must leave the entry working (no toggle surprise, no duplicate entries).
- An upgrade that relocates the executable must redirect or re-create the entry so the startup toggle keeps controlling exactly one entry, or the app must honestly detect and report the stale entry.
- Uninstall must not silently leave a startup entry pointing at a deleted binary. The candidate must demonstrate either removing/redirecting the app-owned entry or the app detecting and repairing it on next run, and must show `%LOCALAPPDATA%\AudioDock` retained unless the user explicitly selects deletion.

## Decision gate

Set `Selected option` only when both supported OS rows are complete with passing evidence.

- Selected option: **TBD**
- Decision date: **TBD**
- Evidence links: **TBD**
- Remaining blocker (if any): **Physical Windows validation not yet captured**
