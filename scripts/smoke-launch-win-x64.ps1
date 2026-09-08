[CmdletBinding()]
param(
    [string]$OutputRoot = "artifacts",

    [string]$Version = "0.1.0-preview",

    [int]$StartupWaitSeconds = 8,

    [int]$ShutdownWaitSeconds = 10
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repositoryRoot $OutputRoot
$packageName = "AudioDock-win-x64-$Version"
$packagePath = Join-Path $outputPath $packageName
$executablePath = Join-Path $packagePath "AudioDock.App.exe"

if (-not (Test-Path -LiteralPath $packagePath)) {
    throw "Expected package directory '$packagePath' was not created. Run package-win-x64.ps1 first."
}

if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Packaged executable '$executablePath' was not found."
}

$smokeLocalAppData = Join-Path ([System.IO.Path]::GetTempPath()) "AudioDock-Smoke-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $smokeLocalAppData | Out-Null
New-Item -ItemType Directory -Path (Join-Path $smokeLocalAppData "Roaming") | Out-Null

$originalLocalAppData = $env:LOCALAPPDATA
$originalAppData = $env:APPDATA
$process = $null
$usedGracefulShutdown = $false

try {
    $env:LOCALAPPDATA = $smokeLocalAppData
    $env:APPDATA = Join-Path $smokeLocalAppData "Roaming"

    $process = Start-Process -FilePath $executablePath -WorkingDirectory $packagePath -PassThru
    Start-Sleep -Seconds $StartupWaitSeconds
    $process.Refresh()

    if ($process.HasExited) {
        throw "Smoke launch failed: process exited during startup wait with code $($process.ExitCode)."
    }

    if ($process.MainWindowHandle -ne 0) {
        $usedGracefulShutdown = $process.CloseMainWindow()
        if ($usedGracefulShutdown) {
            [void]$process.WaitForExit($ShutdownWaitSeconds * 1000)
        }
    }

    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        [void]$process.WaitForExit($ShutdownWaitSeconds * 1000)
    }

    if (-not $process.HasExited) {
        throw "Smoke launch failed: process did not terminate within $ShutdownWaitSeconds seconds."
    }

    $shutdownMode = if ($usedGracefulShutdown) { "graceful" } else { "forced" }
    Write-Host "Smoke launch passed: packaged app started and terminated ($shutdownMode shutdown, exit code $($process.ExitCode))."
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    $env:LOCALAPPDATA = $originalLocalAppData
    $env:APPDATA = $originalAppData

    if (Test-Path -LiteralPath $smokeLocalAppData) {
        Remove-Item -LiteralPath $smokeLocalAppData -Recurse -Force -ErrorAction SilentlyContinue
    }
}
