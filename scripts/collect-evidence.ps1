<#
.SYNOPSIS
    Automated release-evidence collector for Audio Dock (issue #7).

.DESCRIPTION
    Runs the documented baseline commands on the current host, captures redacted
    output plus run metadata, verifies the package checksum, and writes a
    structured evidence bundle under an ignored evidence directory.

    This tool automates evidence *capture*. It never changes audio state, never
    enables or disables sign-in startup, and proves nothing by itself: an
    automated run on any host (including GitHub Actions `windows-latest`) is CI
    evidence, not physical-device compatibility evidence. The manual scenario
    table in the emitted bundle must still be completed by a human operator on a
    supported Windows 10 22H2 or Windows 11 machine before any release claim.

    Output is redacted (user profile paths, user name, domain, computer name)
    before it is written, and the bundle is re-scanned for leaks afterwards.

    The script is dot-sourceable: `.`-sourcing loads the functions without
    running the collector, which is how the Pester tests in tests/powershell
    exercise it on CI.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = "evidence",

    [ValidateRange(1, 400)]
    [int]$TailLines = 40,

    [switch]$IncludeStartupProbe
)

function Get-RedactionRule {
    $rules = [ordered]@{}
    if ($env:USERPROFILE) { $rules[[regex]::Escape($env:USERPROFILE)] = '<USERDIR>' }
    $rules['(?i)C:\\Users\\[^\\/:*?"<>|]+'] = '<USERDIR>'
    $rules['/home/[A-Za-z0-9._-]+'] = '<USERDIR>'
    if ($env:USERNAME) { $rules['(?i)\b' + [regex]::Escape($env:USERNAME) + '\b'] = '<USER>' }
    if ($env:USERDOMAIN) { $rules['(?i)\b' + [regex]::Escape($env:USERDOMAIN) + '\b'] = '<DOMAIN>' }
    if ($env:COMPUTERNAME) { $rules['(?i)\b' + [regex]::Escape($env:COMPUTERNAME) + '\b'] = '<MACHINE>' }
    return $rules
}

function Hide-PersonalInformation {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$InputText
    )
    $rules = Get-RedactionRule
    foreach ($line in $InputText) {
        $text = $line
        foreach ($pattern in $rules.Keys) { $text = [regex]::Replace($text, $pattern, $rules[$pattern]) }
        $text
    }
}

function Test-ArchiveChecksum {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$ChecksumPath
    )
    $result = [ordered]@{
        Archive      = Split-Path -Leaf $ArchivePath
        State        = 'unknown'
        ExpectedHash = $null
        ActualHash   = $null
        Detail       = $null
    }
    if (-not (Test-Path -LiteralPath $ArchivePath) -or -not (Test-Path -LiteralPath $ChecksumPath)) {
        $result.State = 'missing'
        $result.Detail = 'Archive or checksum file is absent. Run the package step first.'
        return [pscustomobject]$result
    }
    $line = (Get-Content -LiteralPath $ChecksumPath -Raw).Trim()
    if ($line -notmatch '^([0-9a-f]{64})\s+(.+)$') {
        $result.State = 'malformed'
        $result.Detail = 'Checksum file does not contain a "<sha>  <file>" line.'
        return [pscustomobject]$result
    }
    $result.ExpectedHash = $Matches[1]
    $result.ActualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($result.ActualHash -eq $result.ExpectedHash) {
        $result.State = 'match'
        $result.Detail = 'Recomputed SHA-256 matches the recorded checksum.'
    }
    else {
        $result.State = 'mismatch'
        $result.Detail = 'Recomputed SHA-256 differs from the recorded checksum. Do not use this artifact.'
    }
    [pscustomobject]$result
}

function Get-RequiredManualScenario {
    # Canonical list of manual scenario rows. scripts/finalize-evidence.ps1
    # re-uses this exact list to validate operator-completed bundles, so the
    # two tools can never drift on what the physical-evidence gate requires.
    @(
        'Tray appears after launch'
        'Startup is off by default'
        'Startup opt-in is explicit and reversible (visible in Windows startup settings)'
        'Upgrade preserves scenes/settings'
        'Uninstall (or ZIP-removal procedure) handles the startup entry per ownership rule'
        'Uninstall retains %LOCALAPPDATA%\AudioDock by default'
        'Optional data deletion requires explicit user choice'
        'Accessibility smoke (keyboard/focus/high contrast/200% scaling/non-color cues)'
    )
}

function Get-BaselineCommands {
    param([switch]$IncludeStartupProbe)
    $available = { $null -ne (Get-Command dotnet -ErrorAction SilentlyContinue) }
    $candidates = New-Object System.Collections.Generic.List[object]
    $candidates.Add([pscustomobject]@{ Name = 'restore'; Available = $available; Block = { dotnet restore AudioDock.sln --locked-mode } })
    $candidates.Add([pscustomobject]@{ Name = 'format'; Available = $available; Block = { dotnet format AudioDock.sln --verify-no-changes --no-restore } })
    $candidates.Add([pscustomobject]@{ Name = 'build'; Available = $available; Block = { dotnet build AudioDock.sln --configuration Release --no-restore } })
    $candidates.Add([pscustomobject]@{ Name = 'test'; Available = $available; Block = { dotnet test AudioDock.sln --configuration Release --no-build --logger "console;verbosity=normal" } })
    $candidates.Add([pscustomobject]@{ Name = 'vulnerability-report'; Available = $available; Block = { dotnet list AudioDock.sln package --vulnerable --include-transitive } })
    $candidates.Add([pscustomobject]@{ Name = 'package'; Available = $available; Block = { ./scripts/package-win-x64.ps1 } })
    $candidates.Add([pscustomobject]@{ Name = 'validate-package'; Available = $available; Block = { ./scripts/test-package-win-x64.ps1 } })
    $candidates.Add([pscustomobject]@{ Name = 'smoke-launch'; Available = $available; Block = { ./scripts/smoke-launch-win-x64.ps1 } })
    if ($IncludeStartupProbe) {
        $candidates.Add([pscustomobject]@{
            Name       = 'startup-probe'
            Available  = $available
            Block      = {
                try {
                    $env:AUDIO_DOCK_STARTUP_TEST = '1'
                    dotnet test tests/AudioDock.Windows.Tests/AudioDock.Windows.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WindowsStartupRegistrationIntegrationTests --logger "console;verbosity=normal"
                }
                finally {
                    Remove-Item Env:AUDIO_DOCK_STARTUP_TEST -ErrorAction SilentlyContinue
                }
            }
        })
    }
    return $candidates
}

function Run-AutomatedCommands {
    param(
        [Parameter(Mandatory)][object[]]$Commands,
        [string[]]$Skip = @(),
        [Parameter(Mandatory)][string]$EvidenceDirectory,
        [int]$TailLines = 40
    )
    $commandsDirectory = Join-Path $EvidenceDirectory 'commands'
    New-Item -ItemType Directory -Force -Path $commandsDirectory | Out-Null

    $results = New-Object System.Collections.Generic.List[object]
    foreach ($command in @($Commands | Where-Object { $Skip -notcontains $_.Name })) {
        $entry = [pscustomobject]@{ Name = $command.Name; Status = 'unknown'; ExitCode = $null; OutputLines = 0; Tail = @() }
        if (-not (& $command.Available)) {
            $entry = [pscustomobject]@{ Name = $command.Name; Status = 'skipped (tool unavailable)'; ExitCode = $null; OutputLines = 0; Tail = @() }
            $results.Add($entry)
            continue
        }
        $previousEap = $ErrorActionPreference
        $outputFile = [System.IO.Path]::GetTempFileName()
        try {
            try {
                $ErrorActionPreference = 'Stop'
                $PSNativeCommandUseErrorActionPreference = $false
                & $command.Block *> $outputFile
                $exitCode = $null
                if ($null -ne (Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue)) {
                    $exitCode = (Get-Variable -Name LASTEXITCODE).Value
                }
                $status = if ($null -eq $exitCode) { 'completed (no native exit code observed)' }
                          elseif ($exitCode -eq 0) { 'passed' }
                          else { "failed (exit $exitCode)" }
                $entry = [pscustomobject]@{ Name = $command.Name; Status = $status; ExitCode = $exitCode; OutputLines = 0; Tail = @() }
            }
            catch {
                Add-Content -LiteralPath $outputFile -Value ("collector-error: " + $_.Exception.Message)
                $entry = [pscustomobject]@{ Name = $command.Name; Status = 'error (terminating error)'; ExitCode = $null; OutputLines = 0; Tail = @() }
            }
        }
        finally {
            $ErrorActionPreference = $previousEap
        }
        $captured = @(Get-Content -LiteralPath $outputFile -ErrorAction SilentlyContinue)
        Remove-Item -LiteralPath $outputFile -Force -ErrorAction SilentlyContinue
        $tail = @(Hide-PersonalInformation -InputText @($captured | Select-Object -Last $TailLines))
        $commandPath = Join-Path $commandsDirectory ($command.Name + '.txt')
        Set-Content -LiteralPath $commandPath -Value $tail -Encoding utf8NoBOM
        $results.Add([pscustomobject]@{ Name = $entry.Name; Status = $entry.Status; ExitCode = $entry.ExitCode; OutputLines = $captured.Count; Tail = $tail })
    }
    return $results
}

function Run-EvidenceCollector {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [string]$OutputRoot = 'evidence',
        [int]$TailLines = 40,
        [switch]$IncludeStartupProbe
    )
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest

    $timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $evidenceDirectory = Join-Path (Join-Path $RepositoryRoot $OutputRoot) "run-$timestamp"
    New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null

    $os = [ordered]@{
        IsWindows    = [bool]$IsWindows
        Caption      = $null
        Version      = $null
        BuildNumber  = $null
        Detail       = $null
    }
    if ($IsWindows) {
        try {
            $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
            $os.Caption = [string]$operatingSystem.Caption
            $os.Version = [string]$operatingSystem.Version
            $os.BuildNumber = [string]$operatingSystem.BuildNumber
        }
        catch {
            $os.Detail = "OS metadata unavailable: $($_.Exception.Message)"
        }
    }
    else {
        $os.Detail = 'Non-Windows host. Any results captured here are development-host evidence only and cannot support any Windows compatibility claim.'
    }

    $commit = $null
    $workingTreeDirty = $null
    if (Get-Command git -ErrorAction SilentlyContinue) {
        try {
            $commit = (& git -C $RepositoryRoot rev-parse HEAD 2>$null | Select-Object -First 1)
            $workingTreeDirty = [bool](& git -C $RepositoryRoot status --porcelain 2>$null | Select-Object -First 1)
        }
        catch {
            $commit = "git metadata failed: $($_.Exception.Message)"
        }
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue

    Push-Location $RepositoryRoot
    try {
        $commands = @(Get-BaselineCommands -IncludeStartupProbe:$IncludeStartupProbe)
        $results = @(Run-AutomatedCommands -Commands $commands -Skip @() -EvidenceDirectory $evidenceDirectory -TailLines $TailLines)
    }
    finally {
        Pop-Location
    }

    $packageName = 'AudioDock-win-x64-0.1.0-preview'
    $archivePath = Join-Path (Join-Path $RepositoryRoot 'artifacts') "$packageName.zip"
    $checksum = Test-ArchiveChecksum -ArchivePath $archivePath -ChecksumPath "$archivePath.sha256"

    $metadataPath = Join-Path $evidenceDirectory 'run-metadata.json'
    [ordered]@{
        capturedAtUtc        = $timestamp
        collector            = 'scripts/collect-evidence.ps1'
        repositoryRootName   = Split-Path -Leaf $RepositoryRoot
        commit               = $commit
        workingTreeDirty     = $workingTreeDirty
        dotnetAvailable      = ($null -ne $dotnet)
        os                   = [pscustomobject]$os
        archiveChecksum      = $checksum
        startupProbeIncluded = [bool]$IncludeStartupProbe
        automatedCommands    = @($results | ForEach-Object { [ordered]@{ name = $_.Name; status = $_.Status; exitCode = $_.ExitCode; outputLines = $_.OutputLines } })
        evidenceLevel        = 'automated-host'
        honestyNote          = 'Automated results are CI/host evidence, never physical-device, driver, installer, or accessibility evidence. A manual operator must complete the scenario table in evidence-bundle.md on supported Windows hardware.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadataPath -Encoding utf8NoBOM

    $rows = @($results | ForEach-Object { "| $($_.Name) | $($_.Status) | $($_.ExitCode) | $($_.OutputLines) |" })
    $manualRows = @(Get-RequiredManualScenario | ForEach-Object { "| $_ |  |  |" })
    $bundleLines = @(
        '# Audio Dock release evidence bundle (auto-generated)'
        ''
        "- Captured (UTC): $timestamp"
        "- Commit: $commit"
        "- Working tree dirty: $workingTreeDirty"
        "- Windows host: $([bool]$IsWindows) | OS: $($os.Caption) $($os.Version) (build $($os.BuildNumber))"
        if ($os.Detail) { "- OS note: $($os.Detail)" }
        "- Archive checksum: $($checksum.State) — $($checksum.Detail)"
        "- Startup probe included: $([bool]$IncludeStartupProbe) (registry-only round trip on a throwaway HKCU key; never the production startup entry)"
        ''
        '> This bundle was produced by automated tooling. It is CI/host-level evidence only.'
        '> It is not evidence of physical-device, driver, protected-session, installer, or accessibility compatibility,'
        '> and it never mutates audio state or startup settings on its own.'
        ''
        '## Automated command results'
        ''
        '| Command | Status | Exit code | Output lines |'
        '|---|---|---|---|'
        $rows
        ''
        'Redacted output tails are under `commands/`. Full outputs were intentionally not stored.'
        ''
        '## Manual scenarios (operator must complete on physical hardware)'
        ''
        'Complete these rows on the supported Windows 10 22H2 or Windows 11 machine that ran this collector, following'
        '`docs/windows-release-evidence-template.md`. This tool cannot and does not fill them in.'
        ''
        '| Scenario | Pass/Fail | Evidence reference |'
        '|---|---|---|'
        $manualRows
        ''
        '## Honesty notes'
        ''
        '- What this run proves (host-level only):'
        '- What this run does not prove:'
        '- Blockers remaining:'
    )
    $bundleLines = @(Hide-PersonalInformation -InputText @($bundleLines | Where-Object { $null -ne $_ }))
    $bundlePath = Join-Path $evidenceDirectory 'evidence-bundle.md'
    Set-Content -LiteralPath $bundlePath -Value $bundleLines -Encoding utf8NoBOM

    $leakValues = @($env:USERPROFILE, $env:USERNAME, $env:USERDOMAIN, $env:COMPUTERNAME) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_.Length -ge 3 }
    $leaks = New-Object System.Collections.Generic.List[string]
    Get-ChildItem -LiteralPath $evidenceDirectory -File -Recurse | ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw
        foreach ($value in $leakValues) {
            if ($content -like "*$value*") { $leaks.Add("$($_.Name) -> $value") }
        }
    }
    if ($leaks.Count -gt 0) {
        throw "Redaction leak detected in evidence bundle: $($leaks -join '; '). The bundle is unusable for sharing; investigate before attaching anywhere."
    }

    Write-Host "Evidence bundle written to $evidenceDirectory (redaction self-check passed)."
    Write-Host 'Reminder: automated results are not physical-Windows compatibility evidence; complete the manual scenario table by hand.'
    [pscustomobject]@{ EvidenceDirectory = $evidenceDirectory; MetadataPath = $metadataPath; BundlePath = $bundlePath; Results = $results }
}

if ($MyInvocation.InvocationName -ne '.') {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $null = Run-EvidenceCollector -RepositoryRoot $repositoryRoot -OutputRoot $OutputRoot -TailLines $TailLines -IncludeStartupProbe:$IncludeStartupProbe
}
