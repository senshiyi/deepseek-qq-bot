#requires -Version 5.1
param([ValidateSet('Status','Enable','Restore')][string]$Action='Status')
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runtime=Join-Path $projectRoot 'Results/local/android-phone'
$beforePath=Join-Path $runtime 'whpx-before.json'
$featureName='HypervisorPlatform'
if($Action -eq 'Status'){
    Get-CimInstance Win32_OptionalFeature -Filter "Name='HypervisorPlatform' OR Name='VirtualMachinePlatform'"|
        Select-Object Name,InstallState|ConvertTo-Json -Compress
    exit 0
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($identity)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'This action requires an explicitly authorized administrator session. No system change was made.'
}
for($cursor=$runtime;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Runtime directory must not traverse filesystem links.'}
}
if((Test-Path -LiteralPath $beforePath) -and ((Get-Item -LiteralPath $beforePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Checkpoint must be a regular file.'}
$current=Get-WindowsOptionalFeature -Online -FeatureName $featureName
if($Action -eq 'Enable'){
    if([string]$current.State -notin @('Enabled','Disabled')){throw 'Feature is pending or unavailable. Resolve its current state before changing it.'}
    if([string]$current.State -eq 'Enabled'){Write-Output 'Windows Hypervisor Platform is already enabled; no change.';exit 0}
    [void][IO.Directory]::CreateDirectory($runtime)
    if(Test-Path -LiteralPath $beforePath){
        $before=Get-Content -LiteralPath $beforePath -Raw -Encoding UTF8|ConvertFrom-Json
        if($before.feature -ne $featureName -or $before.state -ne 'Disabled'){throw 'Existing checkpoint differs; preserved without overwrite.'}
    }else{
        [pscustomobject]@{feature=$featureName;state='Disabled';at=[DateTimeOffset]::Now.ToString('o')}|
            ConvertTo-Json|Set-Content -LiteralPath $beforePath -Encoding UTF8
    }
    $result=Enable-WindowsOptionalFeature -Online -FeatureName $featureName -NoRestart
}else{
    if(-not (Test-Path -LiteralPath $beforePath -PathType Leaf)){throw 'No original-state checkpoint; no restore was attempted.'}
    $before=Get-Content -LiteralPath $beforePath -Raw -Encoding UTF8|ConvertFrom-Json
    if($before.feature -ne $featureName -or $before.state -ne 'Disabled'){throw 'Original checkpoint does not authorize disabling this feature.'}
    if([string]$current.State -eq 'Disabled'){Write-Output 'Original disabled state is already restored; no change.';exit 0}
    if([string]$current.State -ne 'Enabled'){throw 'Feature is pending; resolve its current state first.'}
    $result=Disable-WindowsOptionalFeature -Online -FeatureName $featureName -NoRestart
}
$after=Get-WindowsOptionalFeature -Online -FeatureName $featureName
$report=[pscustomobject]@{at=[DateTimeOffset]::Now.ToString('o');action=$Action;feature=$featureName;state=[string]$after.State;restartNeeded=[bool]$result.RestartNeeded;automaticRestart=$false;otherFeaturesChangedByScript=$false}
$report|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $runtime ('whpx-'+$Action.ToLowerInvariant()+'-result.json')) -Encoding UTF8
$report|ConvertTo-Json
