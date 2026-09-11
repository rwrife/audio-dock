<#
.SYNOPSIS
    Self-contained tests for scripts/finalize-evidence.ps1 (no Pester dependency).

.DESCRIPTION
    Runs on any PowerShell 7 host (Linux CI containers, windows-latest, developer
    machines). These tests build throwaway evidence bundles and run-metadata.json
    files under a temporary directory only. They never invoke dotnet, never touch
    audio state, and never write registry or production paths. Exit code 0 means
    all tests passed.

    Run: pwsh -NoProfile -File ./tests/powershell/test-finalize-evidence.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repositoryRoot 'scripts/finalize-evidence.ps1')

$script:passed = 0
$script:failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { $script:passed++; Write-Host "PASS: $Message" }
    else { $script:failed++; Write-Host "FAIL: $Message" -ForegroundColor Red }
}

function New-BundleFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [AllowEmptyCollection()][string[]]$Rows = @(),
        [AllowEmptyCollection()][string[]]$Honesty = @()
    )
    $lines = @(
        '# Audio Dock release evidence bundle (auto-generated)'
        ''
        '## Automated command results'
        ''
        '| Command | Status | Exit code | Output lines |'
        '|---|---|---|---|'
        '| build | passed | 0 | 10 |'
        ''
        '## Manual scenarios (operator must complete on physical hardware)'
        ''
        '| Scenario | Pass/Fail | Evidence reference |'
        '|---|---|---|'
        $Rows
        ''
        '## Honesty notes'
        ''
        $Honesty
    )
    Set-Content -LiteralPath $Path -Value ($lines | Where-Object { $null -ne $_ }) -Encoding utf8NoBOM
}

function New-MetadataFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$ChecksumState = 'match',
        [object[]]$Commands = @(),
        [bool]$Dirty = $false
    )
    [ordered]@{
        capturedAtUtc    = '20260911-000000'
        commit           = '0123456789abcdef0123456789abcdef01234567'
        workingTreeDirty = $Dirty
        archiveChecksum  = [pscustomobject]@{ Archive = 'x.zip'; State = $ChecksumState }
        automatedCommands = @($Commands | ForEach-Object { [ordered]@{ name = $_.Name; status = $_.Status; exitCode = 0; outputLines = 1 } })
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ad-finalize-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null

try {
    $allRows = @(Get-RequiredManualScenario | ForEach-Object { "| $_ |  |  |" })
    $filledRows = @(Get-RequiredManualScenario | ForEach-Object { "| $_ | pass | logs/$_ -see attached notes |" })
    $honestyFilled = @(
        '- What this run proves (host-level only): observed tray, startup, upgrade, uninstall, accessibility rows on this host.'
        '- What this run does not prove: other device/driver/application classes.'
        '- Blockers remaining: none for this host.'
    )
    $honestyEmpty = @(
        '- What this run proves (host-level only):'
        '- What this run does not prove:'
        '- Blockers remaining:'
    )

    # --- Empty (as-collected) bundle is rejected ---
    $dir1 = Join-Path $temporaryRoot 'run-empty'
    New-Item -ItemType Directory -Force -Path $dir1 | Out-Null
    New-BundleFile -Path (Join-Path $dir1 'evidence-bundle.md') -Rows $allRows -Honesty $honestyEmpty
    New-MetadataFile -Path (Join-Path $dir1 'run-metadata.json') -Commands @([pscustomobject]@{ Name = 'build'; Status = 'passed' })
    $empty = Test-EvidenceBundle -BundlePath (Join-Path $dir1 'evidence-bundle.md') -MetadataPath (Join-Path $dir1 'run-metadata.json')
    Assert-True (-not $empty.Ok) 'As-collected bundle with empty rows does not satisfy the gate'
    Assert-True ($empty.ScenarioCount -eq 8) 'Collector row format round-trips through the validator parser (8 rows matched)'
    Assert-True (@($empty.Unfilled | Where-Object { $_ -like '*verdict empty*' }).Count -eq 8) 'Every empty verdict is flagged'
    Assert-True (@($empty.HonestyGaps).Count -eq 3) 'Unfilled honesty notes are flagged'

    # --- Fully completed bundle passes ---
    $dir2 = Join-Path $temporaryRoot 'run-full'
    New-Item -ItemType Directory -Force -Path $dir2 | Out-Null
    New-BundleFile -Path (Join-Path $dir2 'evidence-bundle.md') -Rows $filledRows -Honesty $honestyFilled
    New-MetadataFile -Path (Join-Path $dir2 'run-metadata.json') -Commands @(
        [pscustomobject]@{ Name = 'build'; Status = 'passed' },
        [pscustomobject]@{ Name = 'startup-probe'; Status = 'skipped (tool unavailable)' }
    )
    $full = Test-EvidenceBundle -BundlePath (Join-Path $dir2 'evidence-bundle.md') -MetadataPath (Join-Path $dir2 'run-metadata.json')
    Assert-True ($full.Ok) 'Completed bundle with all-pass rows, references, notes, and clean metadata satisfies the gate'
    Assert-True ($full.Warnings.Count -eq 1 -and $full.Warnings[0] -like '*skipped*') 'Skipped automated command is a warning, not a blocker'
    Assert-True ((Show-GateReport -RunDirectory $dir2) -eq 0) 'Show-GateReport returns 0 for a satisfied gate'

    # --- Failed scenario row blocks ---
    $dir3 = Join-Path $temporaryRoot 'run-fail'
    New-Item -ItemType Directory -Force -Path $dir3 | Out-Null
    $failRows = @($filledRows | ForEach-Object { if ($_ -like '*Tray appears after launch*') { $_ -replace '\| pass \|', '| fail |' } else { $_ } })
    New-BundleFile -Path (Join-Path $dir3 'evidence-bundle.md') -Rows $failRows -Honesty $honestyFilled
    New-MetadataFile -Path (Join-Path $dir3 'run-metadata.json') -Commands @([pscustomobject]@{ Name = 'build'; Status = 'passed' })
    $failedCheck = Test-EvidenceBundle -BundlePath (Join-Path $dir3 'evidence-bundle.md') -MetadataPath (Join-Path $dir3 'run-metadata.json')
    Assert-True (-not $failedCheck.Ok -and $failedCheck.Failed -contains 'Tray appears after launch') 'A fail verdict blocks the gate instead of passing silently'
    Assert-True ((Show-GateReport -RunDirectory $dir3) -eq 1) 'Show-GateReport returns 1 when the gate is not satisfied'

    # --- Missing row blocks ---
    $missingRows = @($filledRows | Where-Object { $_ -notlike '*Accessibility smoke*' })
    $dirM = Join-Path $temporaryRoot 'run-missing'
    New-Item -ItemType Directory -Force -Path $dirM | Out-Null
    New-BundleFile -Path (Join-Path $dirM 'evidence-bundle.md') -Rows $missingRows -Honesty $honestyFilled
    New-MetadataFile -Path (Join-Path $dirM 'run-metadata.json')
    $missing = Test-EvidenceBundle -BundlePath (Join-Path $dirM 'evidence-bundle.md') -MetadataPath (Join-Path $dirM 'run-metadata.json')
    Assert-True (-not $missing.Ok -and @($missing.Missing | Where-Object { $_ -like 'Accessibility smoke*' }).Count -eq 1) 'A deleted required row is reported missing'

    # --- Empty reference and invalid verdict ---
    $noRefRows = @($filledRows | ForEach-Object { if ($_ -like '*Startup is off by default*') { $_ -replace '\| logs[^|]*\|', '|  |' } else { $_ } })
    $badVerdictRows = @($noRefRows | ForEach-Object { if ($_ -like '*Upgrade preserves*') { $_ -replace '\| pass \|', '| maybe |' } else { $_ } })
    $dirB = Join-Path $temporaryRoot 'run-bad'
    New-Item -ItemType Directory -Force -Path $dirB | Out-Null
    New-BundleFile -Path (Join-Path $dirB 'evidence-bundle.md') -Rows $badVerdictRows -Honesty $honestyFilled
    New-MetadataFile -Path (Join-Path $dirB 'run-metadata.json')
    $bad = Test-EvidenceBundle -BundlePath (Join-Path $dirB 'evidence-bundle.md') -MetadataPath (Join-Path $dirB 'run-metadata.json')
    Assert-True (@($bad.Unfilled | Where-Object { $_ -like '*evidence reference empty*' }).Count -eq 1) 'A pass row with no evidence reference is flagged'
    Assert-True (@($bad.Invalid | Where-Object { $_ -like '*maybe*' }).Count -eq 1) 'A non pass/fail verdict is flagged as invalid'

    # --- Metadata-driven blockers ---
    $dirN = Join-Path $temporaryRoot 'run-nometa'
    New-Item -ItemType Directory -Force -Path $dirN | Out-Null
    New-BundleFile -Path (Join-Path $dirN 'evidence-bundle.md') -Rows $filledRows -Honesty $honestyFilled
    $nometa = Test-EvidenceBundle -BundlePath (Join-Path $dirN 'evidence-bundle.md') -MetadataPath (Join-Path $dirN 'run-metadata.json')
    Assert-True (-not $nometa.Ok -and @($nometa.AutomatedBlockers | Where-Object { $_ -like '*run-metadata.json is missing*' }).Count -eq 1) 'Missing run metadata blocks the gate'

    New-MetadataFile -Path (Join-Path $dirN 'run-metadata.json') -ChecksumState 'mismatch' -Commands @([pscustomobject]@{ Name = 'package'; Status = 'failed (exit 1)' })
    $mismatch = Test-EvidenceBundle -BundlePath (Join-Path $dirN 'evidence-bundle.md') -MetadataPath (Join-Path $dirN 'run-metadata.json')
    Assert-True ($mismatch.ChecksumState -eq 'mismatch' -and @($mismatch.AutomatedBlockers | Where-Object { $_ -like "*checksum*" }).Count -eq 1) 'Checksum mismatch blocks the gate'
    Assert-True (@($mismatch.AutomatedBlockers | Where-Object { $_ -like "*'package'*" }).Count -eq 1) 'A failed automated command blocks the gate'

    New-MetadataFile -Path (Join-Path $dirN 'run-metadata.json') -Commands @([pscustomobject]@{ Name = 'test'; Status = 'error (terminating error)' }) -Dirty $true
    $dirty = Test-EvidenceBundle -BundlePath (Join-Path $dirN 'evidence-bundle.md') -MetadataPath (Join-Path $dirN 'run-metadata.json')
    Assert-True (@($dirty.AutomatedBlockers | Where-Object { $_ -like "*'test'*" }).Count -eq 1) 'A terminating-error automated command blocks the gate'
    Assert-True (@($dirty.Warnings | Where-Object { $_ -like '*dirty*' }).Count -eq 1) 'Dirty working tree is a warning, not a blocker'

    # --- Parse/lookup edge cases ---
    $noSection = Join-Path $temporaryRoot 'not-a-bundle.md'
    Set-Content -LiteralPath $noSection -Value @('# Random notes', '', 'Nothing to see here.') -Encoding utf8NoBOM
    $noSec = Test-EvidenceBundle -BundlePath $noSection -MetadataPath $null
    Assert-True ($null -ne $noSec.ParseError) 'A file without a Manual scenarios section is a parse error, not a pass'

    $dirX = Join-Path $temporaryRoot 'no-such-run'
    Assert-True ((Show-GateReport -RunDirectory $dirX) -eq 2) 'Show-GateReport returns 2 when the bundle file is absent'

    # --- Latest-run discovery ---
    $evidenceRoot = Join-Path $temporaryRoot 'evidence'
    New-Item -ItemType Directory -Force -Path (Join-Path $evidenceRoot 'run-20260101-000000') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $evidenceRoot 'run-20260102-000000') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $evidenceRoot 'unrelated') | Out-Null
    $latest = Get-LatestEvidenceRun -EvidenceRoot $evidenceRoot
    Assert-True ($latest -like '*run-20260102-000000') 'Latest evidence run is selected by timestamp name'
    Assert-True ($null -eq (Get-LatestEvidenceRun -EvidenceRoot (Join-Path $temporaryRoot 'absent'))) 'Missing evidence root returns null'

    Write-Host ''
    Write-Host "Passed: $script:passed  Failed: $script:failed"
    if ($script:failed -gt 0) { exit 1 }
    Write-Host 'finalize-evidence tests OK.'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
