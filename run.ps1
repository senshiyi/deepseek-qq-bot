param([Parameter(ValueFromRemainingArguments=$true)][string[]]$BridgeArgs)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (-not $BridgeArgs -or $BridgeArgs[0] -in @('help','--help','-h')) {
    Write-Output @'
DeepSeek QQ：消息与管理面板
  start-chat.cmd                    启动消息宿主（已运行时打开面板）
  start-chat-background.cmd         后台启动消息宿主
  open-panel.cmd                    打开本机管理面板
  start.cmd <命令> [参数]           运行已选择的发布版本；不隐含构建
  start.cmd bridge-help             查看程序全部命令

维护入口（在项目目录打开 PowerShell）：
  .\Files\Build-Bridge.ps1                         隔离开发构建
  .\Files\Test-Bridge.ps1                          全部无硬件回归和页面检查
  .\Files\Build-Bridge.ps1 -Target Release          生成独立发布版本
  .\Files\Build-Bridge.ps1 -Target Release -Activate 生成并选作下次启动版本
发布与选择版本不会重启正在运行的服务。首次构建需要 .NET 10 SDK 和 NuGet。
'@
    exit 0
}
Import-Module (Join-Path $projectRoot 'Files\BridgeBuild.psm1') -Force
$bridgePath = Resolve-BridgeExecutable $projectRoot
if ($BridgeArgs[0] -eq 'bridge-help') { $BridgeArgs = @('help') }
Push-Location -LiteralPath $projectRoot
try {
    & $bridgePath @BridgeArgs
    exit $LASTEXITCODE
}
finally { Pop-Location }
