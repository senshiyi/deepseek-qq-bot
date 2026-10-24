[CmdletBinding()]
param([switch]$VerifyOnly)

# Prepare files only. This script never starts Node, QQ, NapCat, or an installer.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectPath = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$version = 'v4.18.38'
$assetName = 'NapCat.Shell.Windows.Node.zip'
$expectedSize = 124079600L
$pinnedSha256 = 'b88007a373a30deefb387e7cf17911752ff900bce22c534b3da6b644f5c6d0ae'
$releaseApi = 'https://api.github.com/repos/NapNeko/NapCatQQ/releases/tags/' + $version
$downloadUrl = 'https://github.com/NapNeko/NapCatQQ/releases/download/' + $version + '/' + $assetName
$downloadPath = Join-Path $projectPath ('Cache/napcat-downloads/' + $version + '/' + $assetName)
$targetPath = Join-Path $projectPath ('Cache/NapCat/' + $version)
$recordDirectory = Join-Path $projectPath 'Results/local/napcat-preparation'

function Assert-ProjectPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $prefix = $projectPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($full -ne $projectPath -and -not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ('Path escapes project: ' + $full)
    }
    # Reject existing junctions/symlinks within this project before writing through them.
    $current = $full
    while ($current -and $current.StartsWith($projectPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('Refusing reparse point: ' + $current)
            }
        }
        if ($current -eq $projectPath) { break }
        $current = Split-Path -Parent $current
    }
    return $full
}

function Assert-Archive([string]$Path, [string]$OfficialDigest) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw ('Archive missing: ' + $Path) }
    if ((Get-Item -LiteralPath $Path).Length -ne $expectedSize) {
        throw ('Archive size mismatch; existing file preserved: ' + $Path)
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $pinnedSha256) { throw ('Pinned SHA256 mismatch; file preserved: ' + $Path) }
    if ($OfficialDigest -and $actual -ne $OfficialDigest) { throw ('GitHub asset digest mismatch: ' + $Path) }
    return $actual
}

foreach ($path in @($downloadPath, $targetPath, $recordDirectory)) { Assert-ProjectPath $path | Out-Null }
if ((Test-Path -LiteralPath $targetPath) -and -not $VerifyOnly) {
    throw ('Destination already exists; no files changed. Use -VerifyOnly to inspect: ' + $targetPath)
}
if ($VerifyOnly -and -not (Test-Path -LiteralPath $targetPath -PathType Container)) {
    throw ('Prepared directory missing: ' + $targetPath)
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$headers = @{ 'User-Agent' = 'deepseek-qq-bot-prepare-napcat'; 'Accept' = 'application/vnd.github+json' }
$release = Invoke-RestMethod -Uri $releaseApi -Headers $headers
if ($release.tag_name -ne $version -or $release.draft -or $release.prerelease) { throw 'Unexpected GitHub release metadata' }
$assets = @($release.assets | Where-Object { $_.name -eq $assetName })
if ($assets.Count -ne 1) { throw 'Expected exactly one official Windows.Node asset' }
$asset = $assets[0]
if ($asset.size -ne $expectedSize -or $asset.browser_download_url -ne $downloadUrl) { throw 'Asset metadata differs from pinned release' }
$officialDigest = $null
if ($asset.PSObject.Properties.Name -contains 'digest' -and $asset.digest) {
    if ($asset.digest -notmatch '^sha256:([a-fA-F0-9]{64})$') { throw 'Unsupported GitHub asset digest format' }
    $officialDigest = $Matches[1].ToLowerInvariant()
    if ($officialDigest -ne $pinnedSha256) { throw 'GitHub asset digest differs from pinned SHA256' }
}

if (-not (Test-Path -LiteralPath $downloadPath)) {
    if ($VerifyOnly) { throw ('Cached archive missing: ' + $downloadPath) }
    $downloadDirectory = Assert-ProjectPath (Split-Path -Parent $downloadPath)
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
    $partialPath = Assert-ProjectPath ($downloadPath + '.' + [guid]::NewGuid().ToString('N') + '.partial')
    Write-Host ('Downloading official ' + $version + ' asset (124079600 bytes)...')
    $savedProgress = $ProgressPreference
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -Headers @{ 'User-Agent' = $headers['User-Agent'] } -OutFile $partialPath
        Assert-Archive $partialPath $officialDigest | Out-Null
        [System.IO.File]::Move($partialPath, $downloadPath)
    } finally {
        $ProgressPreference = $savedProgress
        # Retain a failed partial for diagnosis. Never delete user files automatically.
    }
}
$archiveSha256 = Assert-Archive $downloadPath $officialDigest

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($downloadPath)
$stagingPath = Assert-ProjectPath ($targetPath + '.staging-' + [guid]::NewGuid().ToString('N'))
$stagingPrefix = $stagingPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$names = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$entryCount = 0
$expandedBytes = 0L
try {
    # Validate EVERY entry before creating the extraction directory.
    foreach ($entry in $archive.Entries) {
        $relative = $entry.FullName.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not $relative -or [System.IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw 'Unsafe ZIP entry name' }
        $destination = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($stagingPath, $relative))
        if (-not $destination.StartsWith($stagingPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw ('ZIP entry escapes extraction root: ' + $entry.FullName) }
        $unixFileType = (($entry.ExternalAttributes -shr 16) -band 61440)
        if ($unixFileType -eq 40960) { throw ('ZIP symlink rejected: ' + $entry.FullName) }
        if (-not $names.Add($destination)) { throw ('Duplicate ZIP target rejected: ' + $entry.FullName) }
        $expandedBytes += $entry.Length
        $entryCount++
    }
    if ($expandedBytes -gt 2147483648L -or $entryCount -gt 50000) { throw 'Archive expansion exceeds preparation bounds' }
    if (-not $VerifyOnly) {
        $targetParent = Assert-ProjectPath (Split-Path -Parent $targetPath)
        New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
        if (Test-Path -LiteralPath $stagingPath) { throw 'Staging directory already exists' }
        [System.IO.Compression.ZipFile]::ExtractToDirectory($downloadPath, $stagingPath)
        Assert-ProjectPath $stagingPath | Out-Null
        Assert-ProjectPath $targetPath | Out-Null
        if (Test-Path -LiteralPath $targetPath) { throw 'Destination appeared during extraction; staging preserved' }
        # Directory.Move does not replace an existing directory.
        [System.IO.Directory]::Move($stagingPath, $targetPath)
    }
} finally { $archive.Dispose() }

$required = @('node.exe', 'index.js', 'napcat.bat', 'wrapper.node', 'package.json', 'config.json', 'napcat/napcat.mjs')
foreach ($relative in $required) {
    $path = Assert-ProjectPath (Join-Path $targetPath $relative)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Required packaged file missing: ' + $relative) }
}

if (-not $VerifyOnly) {
    $inventory = @(Get-ChildItem -LiteralPath $targetPath -File -Recurse | ForEach-Object {
        $path = Assert-ProjectPath $_.FullName
        [ordered]@{ path = $path.Substring($targetPath.Length + 1).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    New-Item -ItemType Directory -Path $recordDirectory -Force | Out-Null
    $recordPath = Assert-ProjectPath (Join-Path $recordDirectory ('napcat-' + $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json'))
    $record = [ordered]@{
        preparedAtUtc = [DateTime]::UtcNow.ToString('o'); releaseTag = $version; releaseUrl = $release.html_url
        metadataUrl = $releaseApi; publishedAtUtc = $release.published_at; assetId = $asset.id; assetName = $assetName
        downloadUrl = $downloadUrl; expectedBytes = $expectedSize; actualBytes = (Get-Item -LiteralPath $downloadPath).Length
        officialAssetDigest = $(if ($officialDigest) { 'sha256:' + $officialDigest } else { $null })
        sha256 = $archiveSha256; verification = $(if ($officialDigest) { 'Matched official GitHub asset SHA256 and pinned SHA256; not a publisher signature' } else { 'No digest supplied by current metadata; matched pinned SHA256 only; not a publisher signature' })
        archivePath = $downloadPath; extractedPath = $targetPath; zipEntryCount = $entryCount; expandedBytes = $expandedBytes
        requiredFiles = $required; started = $false; loggedIn = $false; sentMessages = $false; files = $inventory
    }
    $json = $record | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($recordPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ('Evidence: ' + $recordPath)
}
Write-Host ('Prepared files verified: ' + $targetPath)
Write-Host ('SHA256: ' + $archiveSha256)
Write-Host 'No program has been started. Account selection and manual QR login are separate steps.'
