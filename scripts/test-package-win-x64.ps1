[CmdletBinding()]
param(
    [string]$OutputRoot = "artifacts",

    [string]$Version = "0.1.0-preview"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repositoryRoot $OutputRoot
$packageName = "AudioDock-win-x64-$Version"
$packagePath = Join-Path $outputPath $packageName
$archivePath = Join-Path $outputPath "$packageName.zip"
$archiveChecksumPath = "$archivePath.sha256"

foreach ($path in @($packagePath, $archivePath, $archiveChecksumPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Expected package output '$path' was not created."
    }
}

foreach ($requiredFile in @("AudioDock.App.exe", "AudioDock.App.deps.json", "AudioDock.App.runtimeconfig.json", "sbom.cdx.json", "SHA256SUMS.txt")) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath $requiredFile))) {
        throw "Package is missing required file '$requiredFile'."
    }
}

$sbom = Get-Content -LiteralPath (Join-Path $packagePath "sbom.cdx.json") -Raw | ConvertFrom-Json
if ($sbom.bomFormat -ne "CycloneDX" -or $sbom.specVersion -ne "1.5" -or $sbom.metadata.component.name -ne "Audio Dock") {
    throw "SBOM does not identify the expected Audio Dock CycloneDX document."
}

Get-Content -LiteralPath (Join-Path $packagePath "SHA256SUMS.txt") | ForEach-Object {
    if ($_ -notmatch "^([0-9a-f]{64})  (.+)$") {
        throw "Malformed checksum entry '$_'."
    }

    $expectedHash = $matches[1]
    $relativePath = $matches[2]
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $packagePath $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Checksum mismatch for '$relativePath'."
    }
}

$archiveChecksum = Get-Content -LiteralPath $archiveChecksumPath -Raw
if ($archiveChecksum -notmatch "^([0-9a-f]{64})  .+\.zip\s*$") {
    throw "Archive checksum is malformed."
}

$actualArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualArchiveHash -ne $matches[1]) {
    throw "Archive checksum mismatch."
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $archiveEntries = @($archive.Entries.FullName)
    foreach ($requiredFile in @("AudioDock.App.exe", "sbom.cdx.json", "SHA256SUMS.txt")) {
        if (-not ($archiveEntries -match "/$([regex]::Escape($requiredFile))$")) {
            throw "Archive is missing '$requiredFile'."
        }
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Package layout, checksums, SBOM, and ZIP contents are valid."
