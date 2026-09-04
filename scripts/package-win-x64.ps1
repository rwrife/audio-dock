[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Version = "0.1.0-preview",

    [string]$OutputRoot = "artifacts"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repositoryRoot $OutputRoot
if (Test-Path -LiteralPath $outputPath) {
    throw "Package output already exists at '$outputPath'. Use a fresh checkout or choose a new OutputRoot."
}

$publishPath = Join-Path $outputPath "publish"
$packageName = "AudioDock-win-x64-$Version"
$packagePath = Join-Path $outputPath $packageName
$archivePath = Join-Path $outputPath "$packageName.zip"

New-Item -ItemType Directory -Path $outputPath | Out-Null

& dotnet restore (Join-Path $repositoryRoot "src/AudioDock.App/AudioDock.App.csproj") `
    --runtime win-x64 `
    --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "Runtime-specific dotnet restore failed with exit code $LASTEXITCODE."
}

& dotnet publish (Join-Path $repositoryRoot "src/AudioDock.App/AudioDock.App.csproj") `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    "--output=$publishPath" `
    "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Move-Item -LiteralPath $publishPath -Destination $packagePath

$sbom = [ordered]@{
    bomFormat = "CycloneDX"
    specVersion = "1.5"
    serialNumber = "urn:uuid:$([guid]::NewGuid())"
    version = 1
    metadata = [ordered]@{
        timestamp = [DateTime]::UtcNow.ToString("o")
        component = [ordered]@{
            type = "application"
            name = "Audio Dock"
            version = $Version
            purl = "pkg:generic/audio-dock@$Version"
            licenses = @(@{ license = @{ id = "MIT" } })
        }
    }
    components = @(
        [ordered]@{
            type = "framework"
            name = ".NET"
            version = "8.0"
            purl = "pkg:generic/dotnet-runtime@8.0"
            licenses = @(@{ expression = "MIT" })
        }
    )
}
$sbom | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $packagePath "sbom.cdx.json") -Encoding utf8NoBOM

$hashLines = Get-ChildItem -LiteralPath $packagePath -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($packagePath.Length + 1).Replace("\", "/")
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relativePath"
    }
$hashLines | Set-Content -LiteralPath (Join-Path $packagePath "SHA256SUMS.txt") -Encoding ascii

Compress-Archive -LiteralPath $packagePath -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$archiveHash  $(Split-Path -Leaf $archivePath)" |
    Set-Content -LiteralPath "$archivePath.sha256" -Encoding ascii

Write-Host "Created self-contained package: $archivePath"
Write-Host "Package contents: $packagePath"
