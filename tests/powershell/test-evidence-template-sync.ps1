<#
.SYNOPSIS
    Pins docs/windows-release-evidence-template.md to the canonical
    manual-scenario list and honesty labels used by the evidence tooling.

.DESCRIPTION
    scripts/collect-evidence.ps1 owns the canonical required manual-scenario
    rows (Get-RequiredManualScenario), and scripts/finalize-evidence.ps1
    validates operator-completed bundles against exactly that list plus three
    honesty-note labels. If the operator-facing template drifts from those
    labels, an operator can faithfully fill every template row and still have
    the gate validator report missing scenarios and honesty gaps.

    This test parses the template's raw text and asserts:
      - every canonical scenario label appears verbatim as a row label in the
        template's "Required scenario evidence" table,
      - every canonical honesty-note label appears as a "- <label>" bullet in
        the template's honesty notes section.

    It reads repository files only. It never invokes dotnet, never touches
    audio state, registry, or evidence directories. Exit code 0 means the
    template is in sync.

    Run: pwsh -NoProfile -File ./tests/powershell/test-evidence-template-sync.ps1
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

$templatePath = Join-Path $repositoryRoot 'docs/windows-release-evidence-template.md'
Assert-True (Test-Path -LiteralPath $templatePath) 'Release evidence template exists'
if (-not (Test-Path -LiteralPath $templatePath)) { exit 1 }

$lines = @(Get-Content -LiteralPath $templatePath)

# --- Locate the scenario table section ---
$scenarioStart = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s+Required scenario evidence') { $scenarioStart = $i; break }
}
Assert-True ($scenarioStart -ge 0) 'Template has a "Required scenario evidence" section'

$tableLabels = New-Object 'System.Collections.Generic.HashSet[string]'
if ($scenarioStart -ge 0) {
    for ($i = $scenarioStart + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^##\s') { break }
        # Same three-cell parse that scripts/finalize-evidence.ps1 applies to bundles.
        if ($line -match '^\|(.*?)\|(.*?)\|(.*?)\|\s*$') {
            $label = $Matches[1].Trim()
            if ($label -eq 'Scenario' -or $label -match '^-+$') { continue }
            [void]$tableLabels.Add($label)
        }
    }
}

$canonical = @(Get-RequiredManualScenario)
Assert-True ($canonical.Count -eq 8) "Canonical required-scenario list still has 8 rows (found $($canonical.Count))"
foreach ($label in $canonical) {
    Assert-True ($tableLabels.Contains($label)) "Template scenario row matches canonical label verbatim: $label"
}

# --- Locate the honesty notes section and check canonical labels as bullets ---
$honestyStart = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s+Compatibility and honesty notes') { $honestyStart = $i; break }
}
Assert-True ($honestyStart -ge 0) 'Template has a "Compatibility and honesty notes" section'

$honestyLines = New-Object System.Collections.Generic.List[string]
if ($honestyStart -ge 0) {
    for ($i = $honestyStart + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^##\s') { break }
        $honestyLines.Add($lines[$i])
    }
}
foreach ($label in $script:HonestyLabels) {
    $found = $false
    foreach ($line in $honestyLines) {
        # Same prefix rule the validator applies to completed bundles.
        if ($line -match ('^- ' + [regex]::Escape($label))) { $found = $true; break }
    }
    Assert-True $found "Template honesty note matches canonical label: $label"
}

Write-Host ''
Write-Host "Passed: $script:passed  Failed: $script:failed"
if ($script:failed -gt 0) { exit 1 }
Write-Host 'evidence-template-sync tests OK.'
