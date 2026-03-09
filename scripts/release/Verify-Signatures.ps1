param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,
    [switch]$RequireSigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $ArtifactDirectory)) {
    throw "Artifact directory '$ArtifactDirectory' does not exist."
}

$files = Get-ChildItem -Path $ArtifactDirectory -File -Recurse |
    Where-Object { $_.Extension -in @(".exe", ".dll") } |
    Sort-Object FullName

if ($files.Count -eq 0) {
    Write-Host "No .exe/.dll files found for signature verification."
    return
}

$invalid = @()
foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature -FilePath $file.FullName
    Write-Host "$($signature.Status): $($file.FullName)"

    if ($RequireSigned.IsPresent -and $signature.Status -ne "Valid") {
        $invalid += $file.FullName
    }
}

if ($invalid.Count -gt 0) {
    throw "One or more files have invalid signatures: $($invalid -join ', ')"
}
