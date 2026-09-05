# Windows release-candidate packaging

## Current package and evidence boundary

Audio Dock builds a self-contained `win-x64` ZIP before selecting an installer technology. The ZIP is intended for transparent testing from a clean Windows checkout; it contains the application, its .NET runtime, a CycloneDX 1.5 SBOM, and a SHA-256 manifest. A sibling `.zip.sha256` file hashes the archive itself. It does not install a driver, register a service, create a startup entry, or modify audio state during installation because it has no installation phase.

No MSIX or conventional installer has been selected yet. Selecting one requires recorded physical-Windows evidence that the tray lifecycle, opt-in startup flow, update flow, and uninstall behavior work on supported Windows 10 22H2 and Windows 11 systems. Until that evidence exists, the ZIP is the only supported test-distribution format. It must not be described as a signed release or as evidence of universal device, driver, application, or accessibility compatibility.

## Producing and validating a package

Run the following commands from a clean checkout on Windows. CI first restores the solution in locked mode; the package script then re-evaluates only the `win-x64` runtime graph required for self-contained publishing. It refuses to overwrite an existing output directory so stale files cannot become part of a candidate.

```powershell
.\scripts\package-win-x64.ps1
.\scripts\test-package-win-x64.ps1
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

GitHub Actions performs a clean locked restore, builds and tests Release, produces this ZIP, validates its layout/checksums/SBOM, and uploads the package as a workflow artifact. Package validation checks files and archive structure only; it does not claim a headless runner exercised the interactive tray UI.

## Startup, upgrade, and removal

The ZIP makes no startup registration. If a later installer adds opt-in startup, it must show the setting before enabling it, provide a visible reversible control, remove its own startup entry during uninstall, and leave `%LOCALAPPDATA%\AudioDock` untouched unless the user explicitly selects deletion.

Before replacing a ZIP build, use the Settings page to export scenes and create a same-machine backup. The default data folder is `%LOCALAPPDATA%\AudioDock`; it can contain scene names, endpoint identifiers and friendly names, optional executable matching data, settings, activity, undo state, and bounded diagnostics. It never contains audio samples. ZIP removal means closing Audio Dock and deleting only the extracted application folder. To remove user data, first export or back it up if needed, then use the in-app clear controls or explicitly remove `%LOCALAPPDATA%\AudioDock`.

Diagnostics remain local and bounded. Exported diagnostics, package-test output, and any compatibility evidence must redact device names and IDs, executable paths, process names, and other personal information before sharing.

## Signing, maintenance, and release gate

The repository license is MIT. Package dependencies are centrally versioned in `Directory.Packages.props` and locked in each `packages.lock.json`; the CI restore uses `--locked-mode`. The Windows Core Audio boundary uses direct Windows SDK/Win32 interop, whose platform terms remain separate from this repository's license.

No candidate is code signed. A signed claim requires a certificate-backed signing command, timestamping, verification of the resulting signature, and captured output from that run. No release archive, changelog entry, or production release should be created until the ZIP workflow, physical tray/startup/uninstall evidence, required accessibility evidence, and any selected installer checks have real passing output.
