Set-StrictMode -Version Latest

function Initialize-BridgeBuild([string]$ProjectRoot) {
    $env:DOTNET_CLI_HOME = Join-Path $ProjectRoot 'Cache\dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $ProjectRoot 'Cache\nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
}

function Resolve-BridgeExecutable([string]$ProjectRoot) {
    $releasePointer = Join-Path $ProjectRoot 'Results\local\bridge-release.json'
    if (Test-Path -LiteralPath $releasePointer -PathType Leaf) {
        $pointer = Get-Content -LiteralPath $releasePointer -Raw -Encoding UTF8 | ConvertFrom-Json
        $releaseBase = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'Cache\releases')) + [IO.Path]::DirectorySeparatorChar
        $releaseDirectory = [IO.Path]::GetFullPath((Join-Path $ProjectRoot ([string]$pointer.directory)))
        if (-not $releaseDirectory.StartsWith($releaseBase, [StringComparison]::OrdinalIgnoreCase)) { throw '发布路径超出本项目发布目录。' }
        $executable = Join-Path $releaseDirectory 'app\AudioBridge.exe'
        $assembly = Join-Path $releaseDirectory 'app\AudioBridge.dll'
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or -not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw '选定发布版本缺少文件；请重新发布，不会隐含构建。' }
        $manifest = Get-Content -LiteralPath (Join-Path $releaseDirectory 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash -cne [string]$manifest.assemblySha256 -or
            (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -cne [string]$manifest.executableSha256) { throw '发布产物与清单不一致，请重新发布。' }
        Write-Host ('运行发布版本：' + $manifest.id + '；源码：' + $manifest.sourceCommit)
        return $executable
    }
    # Migration only: preserve an already installed build until an explicit release is selected.
    $projectPath = Join-Path $ProjectRoot 'Files\AudioBridge\AudioBridge.csproj'
    $framework = ([xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)).Project.PropertyGroup.TargetFramework
    $legacy = Join-Path $ProjectRoot ('Files\AudioBridge\bin\Release\' + $framework + '\AudioBridge.exe')
    if (Test-Path -LiteralPath $legacy -PathType Leaf) {
        Write-Host '使用现有旧版产物；新发布入口：Files/Build-Bridge.ps1 -Target Release -Activate。'
        return $legacy
    }
    throw '尚无可运行的发布版本。请先运行 .\Files\Build-Bridge.ps1 -Target Release -Activate。'
}

Export-ModuleMember -Function Initialize-BridgeBuild, Resolve-BridgeExecutable
