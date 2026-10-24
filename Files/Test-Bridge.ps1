[CmdletBinding()]
param([ValidateSet('all','admin','messaging')][string]$Suite = 'all')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $buildOutput = @(& (Join-Path $PSScriptRoot 'Build-Bridge.ps1') -Target Tests)
    $testExecutable = [string]$buildOutput[-1]
    if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) { throw '未找到隔离测试程序。' }
    if ($Suite -eq 'all') { & $testExecutable } else { & $testExecutable --suite $Suite }
    if ($LASTEXITCODE -ne 0) { throw '无硬件回归未通过。' }
    & (Join-Path $PSScriptRoot 'Test-BridgeLauncher.ps1')
    $webTests = Join-Path $PSScriptRoot 'AudioBridge.Tests\AdminWeb'
    if (Test-Path -LiteralPath $webTests -PathType Container) {
        $testFiles = @(Get-ChildItem -LiteralPath $webTests -File | Where-Object { $_.Name -match '\.test\.(js|cjs|mjs)$' } | Select-Object -ExpandProperty FullName)
        if ($testFiles.Count -eq 0) { throw '未找到管理页面回归，停止而非跳过检查。' }
        & node --test @testFiles
        if ($LASTEXITCODE -ne 0) { throw '管理页面回归未通过。' }
    }
} finally { Pop-Location }
