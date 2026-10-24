#requires -Version 7.0
param()
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$archiveRoot=Join-Path $projectRoot 'Cache/AndroidPhonePackages'
for($cursor=$archiveRoot;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
        throw 'Package directory must not traverse a filesystem link.'
    }
}
[void][IO.Directory]::CreateDirectory($archiveRoot)
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packages.lock.json') -Raw -Encoding UTF8|ConvertFrom-Json
$results=@()
foreach($package in $manifest.packages){
    $uri=[Uri]$package.url
    if($uri.Scheme -ne 'https' -or $uri.DnsSafeHost -ne 'dl.google.com' -or $uri.Port -ne 443 -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        -not $uri.AbsolutePath.StartsWith('/android/repository/',[StringComparison]::Ordinal) -or
        $package.archive -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*[.]zip$' -or
        [IO.Path]::GetFileName($uri.AbsolutePath) -cne $package.archive -or
        $package.sha1 -cnotmatch '^[a-f0-9]{40}$' -or [long]$package.bytes -lt 1 -or [long]$package.bytes -gt 2000000000){
        throw 'Invalid official package metadata.'
    }
    $archive=Join-Path $archiveRoot $package.archive
    $partial=$archive+'.download'
    if(Test-Path -LiteralPath $archive){
        if((Get-Item -LiteralPath $archive).Length -ne [long]$package.bytes -or (Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash.ToLowerInvariant() -cne $package.sha1){
            throw "Existing archive differs from official metadata; preserved without overwriting: $($package.archive)"
        }
        Write-Output ('Already verified: '+$package.archive)
    }else{
        if((Test-Path -LiteralPath $partial) -and ((Get-Item -LiteralPath $partial -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
            throw 'Partial download must not be a filesystem link.'
        }
        Write-Output ('Downloading official package: '+$package.archive+' ('+$package.bytes+' bytes)')
        Invoke-WebRequest -Uri $uri -OutFile $partial -MaximumRedirection 0 -TimeoutSec 1800
        if((Get-Item -LiteralPath $partial).Length -ne [long]$package.bytes -or (Get-FileHash -LiteralPath $partial -Algorithm SHA1).Hash.ToLowerInvariant() -cne $package.sha1){
            throw "Downloaded bytes differ from official metadata; partial file preserved: $($package.archive)"
        }
        Move-Item -LiteralPath $partial -Destination $archive
    }
    $results+= [pscustomobject]@{archive=$package.archive;bytes=(Get-Item -LiteralPath $archive).Length;officialSha1Verified=$true;sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()}
    Write-Output ('Verified: '+$package.archive)
}
$record=[pscustomobject]@{at=[DateTimeOffset]::Now.ToString('o');downloadedOnly=$true;executedPackages=$false;packages=$results}
$local=Join-Path $projectRoot 'Results/local'
[void][IO.Directory]::CreateDirectory($local)
$record|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $local 'android-phone-packages-20261023.json') -Encoding utf8
Write-Output 'All packages verified. Nothing was extracted, installed or started.'
