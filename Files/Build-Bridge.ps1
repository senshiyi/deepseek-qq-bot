[CmdletBinding()]
param(
    [ValidateSet('Development','Tests','Release')][string]$Target = 'Development',
    [switch]$Activate
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'BridgeBuild.psm1') -Force
Initialize-BridgeBuild $projectRoot
if ($Activate -and $Target -ne 'Release') { throw '-Activate 只用于 Release。' }
$projectName = if ($Target -eq 'Tests') { 'AudioBridge.Tests' } else { 'AudioBridge' }
$projectPath = Join-Path $PSScriptRoot ($projectName + '\' + $projectName + '.csproj')
$buildId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts = Join-Path $projectRoot ('Cache\build\' + $Target.ToLowerInvariant() + '-' + $buildId)
Push-Location -LiteralPath $projectRoot
try {
    & dotnet restore $projectPath --locked-mode --artifacts-path $artifacts --nologo
    if ($LASTEXITCODE -ne 0) { throw '依赖恢复失败。' }
    if ($Target -eq 'Release') {
        $releaseDirectory = Join-Path $projectRoot ('Cache\releases\' + $buildId)
        $output = Join-Path $releaseDirectory 'app'
        & dotnet publish $projectPath --configuration Release --no-restore --artifacts-path $artifacts --output $output --self-contained false --nologo
        if ($LASTEXITCODE -ne 0) { throw '发布构建失败；未选择这个版本。' }
        $sourceCommit = (& git -C $projectRoot rev-parse HEAD)
        if ($LASTEXITCODE -ne 0) { throw '无法读取源码版本。' }
        $sourceDirty = @(& git -C $projectRoot status --porcelain -- .).Count -gt 0
        $manifest = [ordered]@{
            id = $buildId; createdAt = [DateTimeOffset]::UtcNow.ToString('o')
            sourceCommit = [string]$sourceCommit; sourceDirty = $sourceDirty
            assemblySha256 = (Get-FileHash -LiteralPath (Join-Path $output 'AudioBridge.dll') -Algorithm SHA256).Hash
            executableSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'AudioBridge.exe') -Algorithm SHA256).Hash
        }
        [IO.File]::WriteAllText((Join-Path $releaseDirectory 'release.json'), ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        if ($Activate) {
            $local = Join-Path $projectRoot 'Results\local'
            [void][IO.Directory]::CreateDirectory($local)
            $releasePointer = Join-Path $local 'bridge-release.json'
            $temporary = $releasePointer + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            $pointer = @{ directory = 'Cache/releases/' + $buildId }
            [IO.File]::WriteAllText($temporary, ($pointer | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
            Move-Item -LiteralPath $temporary -Destination $releasePointer -Force
            Write-Host '已选为下次启动版本；现有进程保持运行。'
        }
        Write-Output $releaseDirectory
    } else {
        & dotnet build $projectPath --configuration Release --no-restore --artifacts-path $artifacts --nologo
        if ($LASTEXITCODE -ne 0) { throw '隔离构建失败。' }
        Write-Output (Join-Path $artifacts ('bin\' + $projectName + '\release\' + $projectName + '.exe'))
    }
} finally { Pop-Location }
