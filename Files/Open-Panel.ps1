[CmdletBinding()]
param([string]$ProjectRoot)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($ProjectRoot)){$ProjectRoot=Split-Path -Parent $PSScriptRoot}
try{
    $ProjectRoot=(Resolve-Path -LiteralPath $ProjectRoot).Path
    $tokenPath=Join-Path $ProjectRoot 'Results\local\admin-access-token.txt'
    if(-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)){throw '面板尚未启动，请先运行 start-chat.cmd，随后重试。'}
    $token=[IO.File]::ReadAllText($tokenPath).Trim()
    if($token -cnotmatch '^[a-fA-F0-9]{64}$'){throw '本地管理口令文件无效，请检查原文件；没有自动覆盖。'}
    $url='http://127.0.0.1:17802/#token='+$token
    Start-Process -FilePath $url | Out-Null
    Write-Host '已在浏览器打开本机管理面板。关闭网页不会停止机器人。'
}catch{
    Write-Host ('未打开面板：'+$_.Exception.Message) -ForegroundColor Red
    exit 1
}
