<#
.SYNOPSIS
    Self-contained tests for scripts/collect-evidence.ps1 (no Pester dependency).

.DESCRIPTION
    Runs on any PowerShell 7 host (Linux CI containers, windows-latest, developer
    machines). These tests never invoke dotnet, never touch audio state, and never
    write outside a temporary directory. Exit code 0 means all tests passed.

    Run: pwsh -NoProfile -File ./tests/powershell/test-collect-evidence.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repositoryRoot 'scripts/collect-evidence.ps1')

$script:passed = 0
$script:failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { $script:passed++; Write-Host "PASS: $Message" }
    else { $script:failed++; Write-Host "FAIL: $Message" -ForegroundColor Red }
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ad-collect-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null

try {
    # --- Redaction ---
    $savedUserProfile = $env:USERPROFILE
    $savedUsername = $env:USERNAME
    $env:USERPROFILE = '/home/fakeuser'
    $env:USERNAME = 'fakeuser'
    try {
        $input = @(
            'C:\Users\realuser\AppData\Local\App',
            '/home/realuser/projects/app',
            'run by fakeuser completed',
            'plain line'
        )
        $redacted = @(Hide-PersonalInformation -InputText $input)
        Assert-True ($redacted[0] -notmatch 'realuser') 'Windows user profile path is redacted'
        Assert-True ($redacted[1] -notmatch 'realuser') 'POSIX home path is redacted'
        Assert-True ($redacted[2] -notmatch 'fakeuser') 'Username is redacted'
        Assert-True ($redacted[3] -eq 'plain line') 'Clean lines pass through unchanged'
    }
    finally {
        $env:USERPROFILE = $savedUserProfile
        $env:USERNAME = $savedUsername
    }

    # --- Checksum verification ---
    $artifact = Join-Path $temporaryRoot 'payload.zip'
    Set-Content -LiteralPath $artifact -Value 'fake archive bytes' -Encoding ascii
    $goodHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$artifact.sha256"
    "$goodHash  payload.zip" | Set-Content -LiteralPath $checksumPath -Encoding ascii
    $match = Test-ArchiveChecksum -ArchivePath $artifact -ChecksumPath $checksumPath
    Assert-True ($match.State -eq 'match') 'Valid checksum verifies as match'

    "0000000000000000000000000000000000000000000000000000000000000000  payload.zip" | Set-Content -LiteralPath $checksumPath -Encoding ascii
    $mismatch = Test-ArchiveChecksum -ArchivePath $artifact -ChecksumPath $checksumPath
    Assert-True ($mismatch.State -eq 'mismatch') 'Wrong hash reports mismatch, not success'

    $absent = Test-ArchiveChecksum -ArchivePath (Join-Path $temporaryRoot 'nope.zip') -ChecksumPath (Join-Path $temporaryRoot 'nope.zip.sha256')
    Assert-True ($absent.State -eq 'missing') 'Missing artifact reports missing, not success'

    'garbage' | Set-Content -LiteralPath $checksumPath -Encoding ascii
    $malformed = Test-ArchiveChecksum -ArchivePath $artifact -ChecksumPath $checksumPath
    Assert-True ($malformed.State -eq 'malformed') 'Malformed checksum line is rejected'

    # --- Command runner availability + redacted capture ---
    $evidenceDirectory = Join-Path $temporaryRoot 'evidence'
    New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null
    $savedUserProfile = $env:USERPROFILE
    $savedUsername = $env:USERNAME
    $env:USERPROFILE = '/home/fakeuser'
    $env:USERNAME = 'fakeuser'
    try {
        $unavailable = [pscustomobject]@{ Name = 'restore'; Available = { $false }; Block = { throw 'must not run' } }
        $results = @(Run-AutomatedCommands -Commands @($unavailable) -Skip @() -EvidenceDirectory $evidenceDirectory -TailLines 5)
        Assert-True ($results.Count -eq 1 -and $results[0].Status -like 'skipped*') 'Unavailable toolchain marks the command skipped (no fake pass)'

        $echoName = if ($IsWindows) { 'cmd.exe' } else { '/bin/echo' }
        $echoArg = if ($IsWindows) { '/c echo run by fakeuser at C:\Users\realuser done' } else { 'run by fakeuser at /home/realuser done' }
        $echo = [pscustomobject]@{ Name = 'echo'; Available = { $true }; Block = { & $echoName $echoArg } }
        $blocked = [pscustomobject]@{ Name = 'build'; Available = { $true }; Block = { throw 'boom' } }
        $results2 = @(Run-AutomatedCommands -Commands @($echo, $blocked) -Skip @('echo') -EvidenceDirectory $evidenceDirectory -TailLines 5)
        Assert-True (@($results2 | Where-Object { $_.Name -eq 'echo' }).Count -eq 0) 'Skip list removes the named command'

        $results3 = @(Run-AutomatedCommands -Commands @($echo) -Skip @() -EvidenceDirectory $evidenceDirectory -TailLines 5)
        Assert-True ($results3[0].ExitCode -eq 0) 'Native command exit code 0 is recorded as passed'
        $echoFile = Join-Path $evidenceDirectory 'commands/echo.txt'
        Assert-True (Test-Path -LiteralPath $echoFile) 'Captured command output is stored'
        $stored = Get-Content -LiteralPath $echoFile -Raw
        Assert-True ($stored -notmatch 'realuser' -and $stored -notmatch 'fakeuser') 'Stored command output is redacted'

        $results4 = @(Run-AutomatedCommands -Commands @($blocked) -Skip @() -EvidenceDirectory $evidenceDirectory -TailLines 5)
        Assert-True ($results4[0].Status -like 'error*') 'Terminating error is recorded as error, not passed'

        $baselines = @(Get-BaselineCommands)
        Assert-True ($baselines.Count -eq 8) 'Baseline command list has 8 entries without the startup probe'
        Assert-True ((@(Get-BaselineCommands -IncludeStartupProbe)).Count -eq 9) 'Startup probe adds one opt-in command'
    }
    finally {
        $env:USERPROFILE = $savedUserProfile
        $env:USERNAME = $savedUsername
    }

    Write-Host ''
    Write-Host "Passed: $script:passed  Failed: $script:failed"
    if ($script:failed -gt 0) { exit 1 }
    Write-Host 'collect-evidence tests OK.'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
