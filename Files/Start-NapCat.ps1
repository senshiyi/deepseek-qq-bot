[CmdletBinding()]
param(
    [ValidatePattern('^[1-9][0-9]{4,14}$')][string]$Qq,
    [switch]$PrepareOnly,
    [switch]$LoginFallback,
    [string]$ProjectRoot
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($ProjectRoot)){$ProjectRoot=Split-Path -Parent $PSScriptRoot}
$ProjectRoot=(Resolve-Path -LiteralPath $ProjectRoot).Path
$runtime=Join-Path $ProjectRoot 'Cache\NapCat\v4.18.38'
if(-not (Test-Path -LiteralPath (Join-Path $runtime 'napcat.bat'))){throw '请先运行Files/Prepare-NapCat.ps1准备固定版本。'}
# Only this project's executable is a conflict. PrepareOnly must remain a file-only operation.
if(-not $PrepareOnly){
    $expectedNode=[IO.Path]::GetFullPath((Join-Path $runtime 'node.exe'))
    try{$projectNodes=@(Get-CimInstance -ClassName Win32_Process -Filter "Name='node.exe'" -Property ExecutablePath -ErrorAction Stop)}
    catch{throw '无法只读确认本项目NapCat是否已运行，未启动；请先核对现有实例。'}
    foreach($nodeProcess in $projectNodes){
        if($nodeProcess.ExecutablePath -and [IO.Path]::GetFullPath([string]$nodeProcess.ExecutablePath).Equals($expectedNode,[StringComparison]::OrdinalIgnoreCase)){
            throw '本项目NapCat已在运行，拒绝重复启动；未停止或重启现有进程。'
        }
    }
}
if($LoginFallback){
    & (Join-Path $PSScriptRoot 'Prepare-NapCatLoginFallback.ps1') -ProjectRoot $ProjectRoot | Out-Null
}
$working=Join-Path $ProjectRoot 'Results\local\napcat'
$configDirectory=Join-Path $working 'config'
$chatPath=Join-Path $ProjectRoot 'group-chat.local.json'
$chat=if(Test-Path -LiteralPath $chatPath){Get-Content -LiteralPath $chatPath -Raw -Encoding UTF8|ConvertFrom-Json}else{$null}
if($Qq -and $chat -and $chat.selfId -and [string]$chat.selfId -ne $Qq){throw '群消息配置selfId与所选账号不同，未修改配置或启动。'}
$apiPort=3000
$eventsPort=17801
if($chat){
    $origin=[Uri]$chat.oneBotBaseUrl
    if($origin.Scheme -ne 'http' -or $origin.Host -notin @('127.0.0.1','localhost') -or $origin.AbsolutePath -ne '/' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment){throw '群消息API必须为本机HTTP根地址。'}
    $apiPort=$origin.Port
    $eventsPort=[int]$chat.eventPort
    if($apiPort -lt 1024 -or $apiPort -gt 65535 -or $eventsPort -lt 1024 -or $eventsPort -gt 65535){throw '配置端口必须为1024～65535。'}
}
[void][IO.Directory]::CreateDirectory($configDirectory)
function Save-LocalJson([string]$Path,$Value){
    if(Test-Path -LiteralPath $Path){
        $backups=Join-Path $working 'config-backups'
        [void][IO.Directory]::CreateDirectory($backups)
        Copy-Item -LiteralPath $Path -Destination (Join-Path $backups ((Split-Path -Leaf $Path)+'.'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff')+'.bak'))
    }
    $Value|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $Path -Encoding utf8
}
$panelPath=Join-Path $configDirectory 'webui.json'
$panel=if(Test-Path -LiteralPath $panelPath){Get-Content -LiteralPath $panelPath -Raw -Encoding UTF8|ConvertFrom-Json}else{[pscustomobject]@{}}
if(-not $panel.PSObject.Properties['token'] -or [string]::IsNullOrWhiteSpace([string]$panel.token)){
    $random=[byte[]]::new(32)
    $generator=[Security.Cryptography.RandomNumberGenerator]::Create()
    try{$generator.GetBytes($random)}finally{$generator.Dispose()}
    $panel|Add-Member -NotePropertyName token -NotePropertyValue ([BitConverter]::ToString($random).Replace('-','')) -Force
}
$panel|Add-Member -NotePropertyName host -NotePropertyValue '127.0.0.1' -Force
$panel|Add-Member -NotePropertyName port -NotePropertyValue 6099 -Force
$panel|Add-Member -NotePropertyName disableWebUI -NotePropertyValue $false -Force
$panel|Add-Member -NotePropertyName autoLoginAccount -NotePropertyValue '' -Force
Save-LocalJson $panelPath $panel
if([string]::IsNullOrWhiteSpace($Qq)){
    if($PrepareOnly){Write-Output '已准备仅本机WebUI配置；未选择账号，未启动NapCat或QQ。';exit 0}
    throw '请先选择机器人普通QQ账号，再用-Qq 参数明确指定；本脚本不会猜测桌面账号。'
}
$secret=[Environment]::GetEnvironmentVariable('ONEBOT_ACCESS_TOKEN')
if([string]::IsNullOrWhiteSpace($secret)){
    $envPath=Join-Path $ProjectRoot '.env'
    if(Test-Path -LiteralPath $envPath){
        foreach($line in [IO.File]::ReadLines($envPath)){
            if($line.Trim().StartsWith('ONEBOT_ACCESS_TOKEN=')){$secret=$line.Trim().Substring(20).Trim().Trim('"',"'");break}
        }
    }
}
if([string]::IsNullOrWhiteSpace($secret)){throw '请先准备本地.env中的ONEBOT_ACCESS_TOKEN，未启动。'}
$accountPath=Join-Path $configDirectory ('onebot11_'+$Qq+'.json')
$account=if(Test-Path -LiteralPath $accountPath){Get-Content -LiteralPath $accountPath -Raw -Encoding UTF8|ConvertFrom-Json}else{[pscustomobject]@{}}
if(-not $account.PSObject.Properties['network']){$account|Add-Member -NotePropertyName network -NotePropertyValue ([pscustomobject]@{})}
$network=$account.network
# Preserve unrelated adapters, but refuse to start them implicitly alongside this controlled local route.
foreach($name in @('httpServers','httpClients','httpSseServers','websocketServers','websocketClients','plugins')){
    if(-not $network.PSObject.Properties[$name]){$network|Add-Member -NotePropertyName $name -NotePropertyValue @()}
    $managedName=switch($name){'httpServers' {'deepseek-local-api'} 'httpClients' {'deepseek-local-events'} default {$null}}
    foreach($adapter in @($network.$name)){
        if($adapter.enable -and ($null -eq $managedName -or $adapter.name -ne $managedName)){
            throw '已有其他已启用OneBot适配器，保留原配置且不启动；请在本地核对后再运行。'
        }
    }
}
$network.httpServers=@($network.httpServers|Where-Object name -ne 'deepseek-local-api')+@([pscustomobject]@{
    name='deepseek-local-api';enable=$true;host='127.0.0.1';port=$apiPort;enableCors=$false;enableWebsocket=$false;messagePostFormat='array';token=$secret;debug=$false
})
$network.httpClients=@($network.httpClients|Where-Object name -ne 'deepseek-local-events')+@([pscustomobject]@{
    name='deepseek-local-events';enable=$true;url=('http://127.0.0.1:'+$eventsPort+'/onebot/');messagePostFormat='array';reportSelfMessage=$false;token=$secret;debug=$false
})
Save-LocalJson $accountPath $account
if(Test-Path -LiteralPath $chatPath){
    $chat|Add-Member -NotePropertyName selfId -NotePropertyValue ([long]$Qq) -Force
    Save-LocalJson $chatPath $chat
}
if($PrepareOnly){Write-Output '已准备所选账号的本机API/事件配置；未启动NapCat或登录。';exit 0}
Write-Output '即将启动用户选定账号。请核对并由你扫码；不要同时在桌面QQ登录同一账号。Ctrl+C停止本实例，不要运行KillQQ.bat。'
$previous=[Environment]::GetEnvironmentVariable('NAPCAT_WORKDIR','Process')
Push-Location -LiteralPath $ProjectRoot
try{
    $env:NAPCAT_WORKDIR=$working
    if($LoginFallback){
        Push-Location -LiteralPath $runtime
        try{& (Join-Path $runtime 'node.exe') (Join-Path $runtime 'index-login-fallback.cjs') '-q' $Qq}finally{Pop-Location}
    }else{
        & (Join-Path $runtime 'napcat.bat') '-q' $Qq
    }
}finally{
    [Environment]::SetEnvironmentVariable('NAPCAT_WORKDIR',$previous,'Process')
    Pop-Location
}
