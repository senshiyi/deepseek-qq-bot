#requires -Version 7.0
param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AndroidPhone.Core.psm1') -Force
$paths = Get-PhonePaths
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packages.lock.json') -Raw | ConvertFrom-Json
$results = foreach ($package in $manifest.packages) {
    $archive = Join-Path $paths.Archives $package.archive
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Missing archive: $($package.archive). This script does not download anything." }
    $item = Get-Item -LiteralPath $archive
    if ($item.Length -ne $package.bytes) { throw "Archive byte count mismatch: $($package.archive)" }
    $sha1 = (Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash.ToLowerInvariant()
    if ($sha1 -cne $package.sha1) { throw "Official metadata SHA1 mismatch: $($package.archive)" }
    [pscustomobject]@{ archive = $package.archive; bytes = $item.Length; officialSha1Verified = $true; localSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$results | ConvertTo-Json -Depth 4
