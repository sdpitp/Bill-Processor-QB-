param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,
    [string]$CertificatePath,
    [string]$CertificatePasswordEnvVar = "RELEASE_CERT_PASSWORD",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $kitRoots = @(
        "C:\Program Files (x86)\Windows Kits\10\bin",
        "C:\Program Files\Windows Kits\10\bin"
    )

    foreach ($root in $kitRoots) {
        if (-not (Test-Path $root)) {
            continue
        }

        $candidate = Get-ChildItem -Path $root -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($candidate) {
            return $candidate.FullName
        }
    }

    throw "signtool.exe was not found. Install the Windows SDK signing tools."
}

if (-not (Test-Path $ArtifactDirectory)) {
    throw "Artifact directory '$ArtifactDirectory' does not exist."
}

if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = $env:RELEASE_CERT_PATH
}

if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    throw "CertificatePath is required (parameter or RELEASE_CERT_PATH)."
}

if (-not (Test-Path $CertificatePath)) {
    throw "Certificate file '$CertificatePath' was not found."
}

$certPassword = [Environment]::GetEnvironmentVariable($CertificatePasswordEnvVar)
if ([string]::IsNullOrWhiteSpace($certPassword)) {
    throw "Environment variable '$CertificatePasswordEnvVar' is required for certificate password."
}

$signToolPath = Find-SignTool
Write-Host "Using signtool: $signToolPath"

$filesToSign = Get-ChildItem -Path $ArtifactDirectory -File -Recurse |
    Where-Object { $_.Extension -in @(".exe", ".dll") } |
    Sort-Object FullName

if ($filesToSign.Count -eq 0) {
    Write-Host "No .exe/.dll files found to sign."
    return
}

foreach ($file in $filesToSign) {
    Write-Host "Signing $($file.FullName)"
    & $signToolPath sign `
        /fd SHA256 `
        /td SHA256 `
        /tr $TimestampUrl `
        /f $CertificatePath `
        /p $certPassword `
        $file.FullName

    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for '$($file.FullName)' with exit code $LASTEXITCODE."
    }
}

Write-Host "Signature verification..."
$verifyScript = Join-Path $PSScriptRoot "Verify-Signatures.ps1"
& $verifyScript -ArtifactDirectory $ArtifactDirectory -RequireSigned
