#requires -Version 7.0
param()
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sdkRoot=Join-Path $projectRoot 'Cache/AndroidSdk'
$archives=Join-Path $projectRoot 'Cache/AndroidPhonePackages'
$extractRoot=Join-Path $projectRoot 'Cache/AndroidPhoneExtract'
foreach($target in @($sdkRoot,$archives,$extractRoot)){
    for($cursor=$target;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'SDK paths must not traverse filesystem links.'}
    }
}
& (Join-Path $PSScriptRoot 'Test-PhonePackages.ps1') | Out-Null
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packages.lock.json') -Raw -Encoding UTF8|ConvertFrom-Json
$map=@{
    'platform-tools'=@('platform-tools','platform-tools')
    'emulator'=@('emulator','emulator')
    'system-images;android-35;google_apis;x86_64'=@('x86_64','system-images/android-35/google_apis/x86_64')
}
$installed=@()
foreach($package in $manifest.packages){
    if(-not $map.ContainsKey($package.id)){throw 'Unexpected SDK package.'}
    $layout=$map[$package.id]
    $destination=Join-Path $sdkRoot $layout[1]
    if(Test-Path -LiteralPath $destination){throw ('Existing SDK component preserved; refusing overwrite: '+$layout[1])}
    for($cursor=$destination;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'SDK destination traverses a filesystem link.'}
    }
    $stage=Join-Path $extractRoot ([Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    $prefix=[IO.Path]::GetFullPath($stage)+[IO.Path]::DirectorySeparatorChar
    $zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $archives $package.archive))
    try{
        $expanded=0L
        foreach($entry in $zip.Entries){
            $path=[IO.Path]::GetFullPath((Join-Path $stage $entry.FullName))
            if(-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000){throw 'Unsafe archive entry.'}
            $expanded+=$entry.Length
            if($expanded -gt 17179869184L){throw 'Unexpected expanded SDK size.'}
        }
        [IO.Compression.ZipFileExtensions]::ExtractToDirectory($zip,$stage)
    }finally{$zip.Dispose()}
    $source=Join-Path $stage $layout[0]
    if(-not (Test-Path -LiteralPath $source -PathType Container)){throw 'Official archive layout differs from expected component.'}
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    Move-Item -LiteralPath $source -Destination $destination
    $installed+=[pscustomobject]@{id=$package.id;version=$package.version;path=$destination;expandedBytes=$expanded}
    Write-Output ('Extracted verified SDK component: '+$package.id)
}
[pscustomobject]@{at=[DateTimeOffset]::Now.ToString('o');components=$installed;systemFeaturesChanged=$false;deviceStarted=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $projectRoot 'Results/local/android-phone-sdk-20261023.json') -Encoding utf8
Write-Output 'SDK files ready inside project Cache. No system feature or Android device was started.'
