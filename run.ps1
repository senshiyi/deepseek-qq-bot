param([Parameter(ValueFromRemainingArguments=$true)][string[]]$BridgeArgs)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'Cache\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'Cache\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$projectPath = Join-Path $projectRoot 'Files\AudioBridge\AudioBridge.csproj'
Push-Location -LiteralPath $projectRoot
try {
    & dotnet run --project $projectPath --configuration Release -- @BridgeArgs
    exit $LASTEXITCODE
}
finally { Pop-Location }
