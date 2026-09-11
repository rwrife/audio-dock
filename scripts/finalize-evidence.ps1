<#
.SYNOPSIS
    Gate validator for operator-completed release-evidence bundles (issue #7).

.DESCRIPTION
    Reads an evidence bundle produced by scripts/collect-evidence.ps1 after a
    human operator has filled in the manual scenario table and honesty notes,
    and checks the physical-evidence gate bookkeeping:

      - every required manual scenario row is present,
      - every row carries an explicit pass/fail verdict,
      - every row carries a non-empty evidence reference,
      - the three honesty-note bullets are filled in,
      - run-metadata.json shows a matching ZIP checksum and no failed or
        errored automated commands.

    Exit codes:
      0 = the gate is bookkeeping-complete (every required row passed,
          evidence references present, automated results clean).
      1 = the gate is NOT satisfied (missing rows, unfilled or failed rows,
          empty honesty notes, checksum problems, or automated failures).
      2 = the bundle could not be found or parsed.

    Honesty boundary: this validator checks only that the bundle is complete
    and internally consistent. It cannot and does not independently observe
    physical Windows behavior; the operator's claims are not re-measured here.
    A "satisfied" verdict means the evidence pack is complete and consistent,
    not that any device, driver, installer, or accessibility behavior was
    verified by this tool, and it does not by itself authorize publishing a
    release. Human review is still required before any release decision.

    The script is dot-sourceable: `.`-sourcing loads the functions without
    running the validator, which is how the tests in tests/powershell
    exercise it on CI. The required scenario list is imported from
    scripts/collect-evidence.ps1 so the two tools cannot drift.
#>
[CmdletBinding()]
param(
    [string]$RunDirectory
)

. (Join-Path $PSScriptRoot 'collect-evidence.ps1')

$script:HonestyLabels = @(
    'What this run proves'
    'What this run does not prove'
    'Blockers remaining'
)

function Get-JsonMember {
    param(
        [Parameter(Mandatory)][AllowNull()]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $InputObject) { return $null }
    if ($InputObject.PSObject.Properties.Name -contains $Name) { return $InputObject.$Name }
    return $null
}

function Get-LatestEvidenceRun {
    param([Parameter(Mandatory)][string]$EvidenceRoot)
    if (-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) { return $null }
    $latest = @(Get-ChildItem -LiteralPath $EvidenceRoot -Directory |
        Where-Object { $_.Name -like 'run-*' } |
        Sort-Object Name |
        Select-Object -Last 1)
    if ($latest.Count -eq 0) { return $null }
    return $latest[0].FullName
}

function Test-EvidenceBundle {
    param(
        [Parameter(Mandatory)][string]$BundlePath,
        [string]$MetadataPath
    )
    $result = [ordered]@{
        Ok               = $false
        ParseError       = $null
        Missing          = @()
        Unfilled         = @()
        Invalid          = @()
        Failed           = @()
        HonestyGaps      = @()
        AutomatedBlockers = @()
        Warnings         = @()
        ChecksumState    = 'not-read'
        ScenarioCount    = 0
    }

    if (-not (Test-Path -LiteralPath $BundlePath)) {
        $result.ParseError = "Bundle file not found: $BundlePath"
        return [pscustomobject]$result
    }

    $lines = @(Get-Content -LiteralPath $BundlePath)

    $scenarioStart = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^##\s+Manual scenarios') { $scenarioStart = $i; break }
    }
    if ($scenarioStart -lt 0) {
        $result.ParseError = 'No "## Manual scenarios" section found. Is this a collector evidence bundle?'
        return [pscustomobject]$result
    }

    $rows = New-Object System.Collections.Specialized.OrderedDictionary
    for ($i = $scenarioStart + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^##\s') { break }
        if ($line -match '^\|(.*?)\|(.*?)\|(.*?)\|\s*$') {
            $label = $Matches[1].Trim()
            $verdict = $Matches[2].Trim()
            $reference = $Matches[3].Trim()
            if ($label -eq 'Scenario' -or $label -match '^-+$') { continue }
            $rows[$label] = [pscustomobject]@{ Verdict = $verdict; Reference = $reference }
        }
    }

    foreach ($label in Get-RequiredManualScenario) {
        if (-not $rows.Contains($label)) {
            $result.Missing += $label
            continue
        }
        $result.ScenarioCount++
        $row = $rows[$label]
        $verdict = $row.Verdict.ToLowerInvariant()
        if ($verdict -eq '') {
            $result.Unfilled += "$label (verdict empty)"
        }
        elseif ($verdict -in @('pass', 'passed')) {
            # Verdict accepted; reference check still applies below.
        }
        elseif ($verdict -in @('fail', 'failed')) {
            $result.Failed += $label
        }
        else {
            $result.Invalid += "$label (verdict '$($row.Verdict)' is not pass/fail)"
        }
        if ($row.Reference -eq '') {
            $result.Unfilled += "$label (evidence reference empty)"
        }
    }

    $honestyStart = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^##\s+Honesty notes') { $honestyStart = $i; break }
    }
    if ($honestyStart -lt 0) {
        $result.HonestyGaps += 'Honesty notes section is missing entirely'
    }
    else {
        $section = New-Object System.Collections.Generic.List[string]
        for ($i = $honestyStart + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^##\s') { break }
            $section.Add($lines[$i])
        }
        foreach ($label in $script:HonestyLabels) {
            $found = $false
            $filled = $false
            foreach ($line in $section) {
                if ($line -match ('^- ' + [regex]::Escape($label))) {
                    $found = $true
                    $colonIndex = $line.IndexOf(':')
                    if ($colonIndex -ge 0 -and $line.Substring($colonIndex + 1).Trim() -ne '') {
                        $filled = $true
                    }
                    break
                }
            }
            if (-not $found) { $result.HonestyGaps += "$label (line missing)" }
            elseif (-not $filled) { $result.HonestyGaps += "$label (nothing recorded after the colon)" }
        }
    }

    if ($MetadataPath -and (Test-Path -LiteralPath $MetadataPath)) {
        $metadata = $null
        try {
            $metadata = Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json
        }
        catch {
            $result.AutomatedBlockers += "run-metadata.json unreadable: $($_.Exception.Message)"
        }
        if ($null -ne $metadata) {
            $checksum = Get-JsonMember $metadata 'archiveChecksum'
            $checksumState = Get-JsonMember $checksum 'state'
            if ($null -eq $checksumState) { $checksumState = 'unknown' }
            $result.ChecksumState = $checksumState
            if ($checksumState -ne 'match') {
                $result.AutomatedBlockers += "archive checksum state is '$checksumState', not 'match'"
            }

            foreach ($command in @(Get-JsonMember $metadata 'automatedCommands')) {
                if ($null -eq $command) { continue }
                $name = Get-JsonMember $command 'name'
                $status = [string](Get-JsonMember $command 'status')
                if ($null -eq $name) { $name = '<unnamed>' }
                if ($status -match '^(failed|error)') {
                    $result.AutomatedBlockers += "automated command '$name' reported: $status"
                }
                elseif ($status -like 'skipped*') {
                    $result.Warnings += "automated command '$name' was $status"
                }
            }

            if ((Get-JsonMember $metadata 'workingTreeDirty') -eq $true) {
                $result.Warnings += 'working tree was dirty when the automated results were captured'
            }
            $commit = Get-JsonMember $metadata 'commit'
            if ([string]::IsNullOrWhiteSpace([string]$commit)) {
                $result.Warnings += 'run metadata records no commit SHA for this run'
            }
        }
    }
    else {
        $result.AutomatedBlockers += 'run-metadata.json is missing: automated results and provenance cannot be checked'
    }

    $result.Ok = (
        $null -eq $result.ParseError -and
        $result.Missing.Count -eq 0 -and
        $result.Unfilled.Count -eq 0 -and
        $result.Invalid.Count -eq 0 -and
        $result.Failed.Count -eq 0 -and
        $result.HonestyGaps.Count -eq 0 -and
        $result.AutomatedBlockers.Count -eq 0
    )
    [pscustomobject]$result
}

function Show-GateReport {
    param([Parameter(Mandatory)][string]$RunDirectory)
    $bundlePath = Join-Path $RunDirectory 'evidence-bundle.md'
    $metadataPath = Join-Path $RunDirectory 'run-metadata.json'
    Write-Host "Validating evidence run: $RunDirectory"

    $check = Test-EvidenceBundle -BundlePath $bundlePath -MetadataPath $metadataPath
    if ($null -ne $check.ParseError) {
        Write-Host "PARSE ERROR: $($check.ParseError)"
        return 2
    }

    Write-Host "Required scenario rows matched: $($check.ScenarioCount)"
    Write-Host "Archive checksum state: $($check.ChecksumState)"
    foreach ($item in $check.Missing) { Write-Host "BLOCKER  missing scenario row: $item" }
    foreach ($item in $check.Unfilled) { Write-Host "BLOCKER  unfilled: $item" }
    foreach ($item in $check.Invalid) { Write-Host "BLOCKER  invalid verdict: $item" }
    foreach ($item in $check.Failed) { Write-Host "BLOCKER  scenario recorded as fail: $item" }
    foreach ($item in $check.HonestyGaps) { Write-Host "BLOCKER  honesty notes: $item" }
    foreach ($item in $check.AutomatedBlockers) { Write-Host "BLOCKER  automated results: $item" }
    foreach ($item in $check.Warnings) { Write-Host "warning: $item" }

    if ($check.Ok) {
        Write-Host 'GATE: complete and consistent - every required manual scenario is marked pass with an evidence reference, honesty notes are filled, and automated results are clean.'
    }
    else {
        Write-Host 'GATE: NOT satisfied - resolve every BLOCKER above before this run can support a release decision.'
    }
    Write-Host 'Reminder: this validator checks bundle bookkeeping only. It does not independently observe physical Windows behavior, and a complete bundle still requires human review; installer selection and certificate-backed signing have separate gates.'
    if ($check.Ok) { return 0 }
    return 1
}

if ($MyInvocation.InvocationName -ne '.') {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
        $RunDirectory = Get-LatestEvidenceRun -EvidenceRoot (Join-Path $repositoryRoot 'evidence')
        if ($null -eq $RunDirectory) {
            Write-Host 'No evidence/run-* directory found. Run scripts/collect-evidence.ps1 first, or pass -RunDirectory.'
            exit 2
        }
        Write-Host "Using most recent evidence run: $RunDirectory"
    }
    exit (Show-GateReport -RunDirectory $RunDirectory)
}
