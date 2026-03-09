param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "",
    [switch]$SelfContained,
    [switch]$SingleFile = $true,
    [switch]$Sign,
    [string]$CertificatePath,
    [string]$CertificatePasswordEnvVar = "RELEASE_CERT_PASSWORD",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    $scriptRoot = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptRoot "..\..")).Path
}

function Get-GitShortSha {
    param([string]$RepoRoot)
    try {
        return (git -C $RepoRoot rev-parse --short HEAD).Trim()
    }
    catch {
        return "unknown"
    }
}

function Get-AssemblyVersion {
    param([string]$SemanticVersion)
    if ($SemanticVersion -match "^(\d+)\.(\d+)\.(\d+)") {
        return "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
    }

    throw "Version '$SemanticVersion' must start with Major.Minor.Patch (example: 1.0.0)."
}

$repoRoot = Get-RepoRoot
$appProjectPath = Join-Path $repoRoot "src\BillProcessor.App\BillProcessor.App.csproj"
if (-not (Test-Path $appProjectPath)) {
    throw "Unable to find app project at $appProjectPath."
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release"
}

$runtimeLabel = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) { "portable" } else { $RuntimeIdentifier }
$releaseName = "VendorBillProcessorQB-$Version-$runtimeLabel"
$stagingRoot = Join-Path $OutputRoot $releaseName
$publishDirectory = Join-Path $stagingRoot "publish"
$payloadDirectory = Join-Path $stagingRoot "payload"
$zipPath = Join-Path $OutputRoot "$releaseName.zip"

if (Test-Path $stagingRoot) {
    Remove-Item -Path $stagingRoot -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null

$assemblyVersion = Get-AssemblyVersion -SemanticVersion $Version
$selfContainedValue = if ($SelfContained.IsPresent) { "true" } else { "false" }
$singleFileValue = if ($SingleFile.IsPresent) { "true" } else { "false" }

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier) -and $SelfContained.IsPresent) {
    throw "SelfContained publishing requires a RuntimeIdentifier."
}

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier) -and $SingleFile.IsPresent) {
    Write-Warning "Single-file publish requires a RuntimeIdentifier. Falling back to multi-file portable publish."
    $singleFileValue = "false"
}

Write-Host "Publishing app..."
$publishArgs = @(
    "publish", $appProjectPath,
    "-c", $Configuration,
    "--self-contained:$selfContainedValue",
    "-p:PublishSingleFile=$singleFileValue",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:Version=$Version",
    "-p:FileVersion=$assemblyVersion",
    "-p:AssemblyVersion=$assemblyVersion",
    "-o", $publishDirectory
)

if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $publishArgs += @("-r", $RuntimeIdentifier)
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $payloadDirectory -Recurse -Force

$metadata = [ordered]@{
    version = $Version
    runtimeIdentifier = $RuntimeIdentifier
    configuration = $Configuration
    selfContained = $SelfContained.IsPresent
    singleFile = $SingleFile.IsPresent
    commit = Get-GitShortSha -RepoRoot $repoRoot
    builtAtUtc = [DateTime]::UtcNow.ToString("o")
}

$metadataPath = Join-Path $stagingRoot "release-metadata.json"
$metadata | ConvertTo-Json -Depth 6 | Out-File -FilePath $metadataPath -Encoding UTF8

$hashFilePath = Join-Path $stagingRoot "SHA256SUMS.txt"
if (Test-Path $hashFilePath) {
    Remove-Item -Path $hashFilePath -Force
}

Write-Host "Generating checksums..."
$filesForHash = Get-ChildItem -Path $payloadDirectory -File -Recurse | Sort-Object FullName
foreach ($file in $filesForHash) {
    $hash = Get-FileHash -Path $file.FullName -Algorithm SHA256
    $relative = $file.FullName.Substring($stagingRoot.Length + 1).Replace('\', '/')
    "$($hash.Hash.ToLowerInvariant()) *$relative" | Add-Content -Path $hashFilePath -Encoding UTF8
}

if ($Sign.IsPresent) {
    Write-Host "Signing release binaries..."
    $signScript = Join-Path $PSScriptRoot "Sign-ReleaseArtifacts.ps1"
    & $signScript `
        -ArtifactDirectory $payloadDirectory `
        -CertificatePath $CertificatePath `
        -CertificatePasswordEnvVar $CertificatePasswordEnvVar `
        -TimestampUrl $TimestampUrl
}

Write-Host "Creating zip package..."
if (-not (Test-Path $OutputRoot)) {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}

Compress-Archive `
    -Path (Join-Path $stagingRoot "*") `
    -DestinationPath $zipPath `
    -CompressionLevel Optimal `
    -Force

Write-Host ""
Write-Host "Release package created:"
Write-Host "  Staging: $stagingRoot"
Write-Host "  Zip:     $zipPath"
