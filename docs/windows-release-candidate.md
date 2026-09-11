# Windows release-candidate packaging

## Current package and evidence boundary

Audio Dock builds a self-contained `win-x64` ZIP before selecting an installer technology. The ZIP is intended for transparent testing from a clean Windows checkout; it contains the application, its .NET runtime, a CycloneDX 1.5 SBOM, and a SHA-256 manifest. A sibling `.zip.sha256` file hashes the archive itself. It does not install a driver, register a service, create a startup entry, or modify audio state during installation because it has no installation phase.

No MSIX or conventional installer has been selected yet. Selecting one requires recorded physical-Windows evidence that the tray lifecycle, opt-in startup flow, update flow, and uninstall behavior work on supported Windows 10 22H2 and Windows 11 systems. Use [the installer selection record](windows-installer-selection.md) and [release evidence template](windows-release-evidence-template.md) to capture and review that evidence. Until the evidence gate is complete, the ZIP is the only supported test-distribution format. It must not be described as a signed release or as evidence of universal device, driver, application, or accessibility compatibility.

## Producing and validating a package

Run the following commands from a clean checkout on Windows. CI first restores the solution in locked mode; the package script then re-evaluates only the `win-x64` runtime graph required for self-contained publishing. It refuses to overwrite an existing output directory so stale files cannot become part of a candidate.

```powershell
.\scripts\package-win-x64.ps1
.\scripts\test-package-win-x64.ps1
.\scripts\smoke-launch-win-x64.ps1
```

The output is `artifacts/AudioDock-win-x64-0.1.0-preview.zip`. Its corresponding directory contains:

- `SHA256SUMS.txt`: SHA-256 hashes for every packaged file.
- `sbom.cdx.json`: a CycloneDX 1.5 inventory of the application and bundled .NET framework runtime. Test-only packages are not shipped and are excluded.

The workflow artifact also contains `AudioDock-win-x64-0.1.0-preview.zip.sha256`, the SHA-256 hash of the ZIP itself.

Validate a downloaded candidate before extracting it:

```powershell
Get-FileHash .\AudioDock-win-x64-0.1.0-preview.zip -Algorithm SHA256
Expand-Archive .\AudioDock-win-x64-0.1.0-preview.zip -DestinationPath .\AudioDock-test
Get-Content .\AudioDock-test\AudioDock-win-x64-0.1.0-preview\SHA256SUMS.txt
```

GitHub Actions performs a clean locked restore, builds and tests Release, produces this ZIP, validates its layout/checksums/SBOM, smoke-launches the packaged tray executable, runs the evidence-collector self-tests, and uploads the package as a workflow artifact. CI smoke launch proves only that the packaged binary starts on `windows-latest` and can be terminated without a startup crash; it does not claim full tray interaction coverage, assistive-technology validation, or universal device/driver/application compatibility.

To prepare evidence for the installer-selection gate, run `.\scripts\collect-evidence.ps1` from a clean checkout. It executes the baseline commands above, records exit codes and redacted output tails plus run metadata into the git-ignored `evidence/` directory, verifies the ZIP checksum, and fails its own redaction self-check if any captured file still contains operator-identifying values. It never mutates audio state or startup settings, and its output is explicitly labeled host/CI-level evidence — the manual scenario table in `docs/windows-release-evidence-template.md` still has to be completed by a human on supported physical hardware.

Once that table is completed, `.\scripts\finalize-evidence.ps1` checks the bundle's bookkeeping (every required scenario row filled with an explicit pass/fail verdict and evidence reference, honesty notes recorded, ZIP checksum matching, no failed or errored automated commands) and lists per-row blockers with a non-zero exit until the bundle is complete and consistent. It validates documentation completeness only; it cannot observe physical behavior, and a complete bundle still requires human review before any release decision.

## Startup, upgrade, and removal

The ZIP itself makes no startup registration and has no installation phase. Inside the app, sign-in startup is an opt-in Settings toggle: enabling adds exactly one per-user entry (the current user's standard Run value, visible and manageable in Windows startup settings), disabling removes exactly that entry, and every change is followed by an observable read-back that the UI reports honestly (including when an entry was added or removed outside the app). Nothing writes startup state without an explicit user action.

When a later installer is selected, it must define startup-entry ownership: an entry created by the app toggle at the current install location must survive upgrades at that location, and uninstall must either keep the app's toggle working or remove/redirect the entry, without ever touching `%LOCALAPPDATA%\AudioDock` unless the user explicitly selects deletion. This uninstall-ownership behavior has no captured physical evidence yet.

Before replacing a ZIP build, use the Settings page to export scenes and create a same-machine backup. The default data folder is `%LOCALAPPDATA%\AudioDock`; it can contain scene names, endpoint identifiers and friendly names, optional executable matching data, settings, activity, undo state, and bounded diagnostics. It never contains audio samples. ZIP removal means closing Audio Dock, disabling sign-in startup in Settings if it was enabled (removal of the startup entry is not implicit when files are deleted), and deleting only the extracted application folder. To remove user data, first export or back it up if needed, then use the in-app clear controls or explicitly remove `%LOCALAPPDATA%\AudioDock`.

Diagnostics remain local and bounded. Exported diagnostics, package-test output, and any compatibility evidence must redact device names and IDs, executable paths, process names, and other personal information before sharing.

## Signing, maintenance, and release gate

The repository license is MIT. Package dependencies are centrally versioned in `Directory.Packages.props` and locked in each `packages.lock.json`; the CI restore uses `--locked-mode`. The Windows Core Audio boundary uses direct Windows SDK/Win32 interop, whose platform terms remain separate from this repository's license.

No candidate is code signed. A signed claim requires a certificate-backed signing command, timestamping, verification of the resulting signature, and captured output from that run. No release archive, changelog entry, or production release should be created until the ZIP workflow, the installer-selection evidence gate, physical tray/startup/uninstall evidence, required accessibility evidence, and any selected installer checks have real passing output.
