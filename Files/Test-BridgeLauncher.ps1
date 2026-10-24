[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$fixtureRoot = Join-Path $projectRoot ('Cache\launcher-checks\' + [Guid]::NewGuid().ToString('N'))
$fixtureFiles = Join-Path $fixtureRoot 'Files'
$release = Join-Path $fixtureRoot 'Cache\releases\fixture'
$app = Join-Path $release 'app'
$local = Join-Path $fixtureRoot 'Results\local'
foreach ($directory in @($fixtureFiles, $app, $local)) { [void][IO.Directory]::CreateDirectory($directory) }
Import-Module (Join-Path $PSScriptRoot 'BridgeBuild.psm1') -Force
$utf8 = New-Object Text.UTF8Encoding($true)
$assembly = Join-Path $app 'AudioBridge.dll'
$executable = Join-Path $app 'AudioBridge.exe'
[IO.File]::WriteAllText($assembly, 'test assembly only')
[IO.File]::WriteAllText($executable, 'test executable only')
$manifest = @{
    id = 'fixture'; sourceCommit = 'fixture'
    assemblySha256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText((Join-Path $release 'release.json'), ($manifest | ConvertTo-Json), $utf8)
$pointerPath = Join-Path $local 'bridge-release.json'
[IO.File]::WriteAllText($pointerPath, '{"directory":"Cache/releases/fixture"}', $utf8)
if ((Resolve-BridgeExecutable $fixtureRoot) -ne $executable) { throw 'Selected release was not resolved.' }
Write-Output 'PASS launcher: selected release matches its manifest'
[IO.File]::AppendAllText($assembly, 'changed')
$rejected = $false
try { Resolve-BridgeExecutable $fixtureRoot | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw 'Changed assembly was accepted.' }
Write-Output 'PASS launcher: modified release is rejected'
[IO.File]::WriteAllText($pointerPath, '{"directory":"../outside"}', $utf8)
$rejected = $false
try { Resolve-BridgeExecutable $fixtureRoot | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw 'Release outside project was accepted.' }
Write-Output 'PASS launcher: release path stays inside project'

# Exercise the actual launcher with only the external host lookup replaced by a fixture.
# No listening port, real token, QQ connection, or browser is used.
[IO.File]::WriteAllText($pointerPath, '{"directory":"Cache/releases/missing"}', $utf8)
$chatSettings = @{ selfId = 123456; allowedGroupIds = @(567890); engine = 'school'; mode = 'active' }
[IO.File]::WriteAllText((Join-Path $fixtureRoot 'group-chat.local.json'), ($chatSettings | ConvertTo-Json), $utf8)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BridgeBuild.psm1') -Destination $fixtureFiles
[IO.File]::WriteAllText((Join-Path $fixtureFiles 'Open-Panel.ps1'), 'param([string]$ProjectRoot) [IO.File]::WriteAllText((Join-Path $ProjectRoot ''panel-opened.txt''), ''opened'')', $utf8)
$launcherSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Start-GroupChat.ps1'))
$tokens = $null; $parseErrors = $null
$tree = [Management.Automation.Language.Parser]::ParseInput($launcherSource, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Launcher source does not parse.' }
$lookup = $tree.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ExistingChatState' }, $true)
if ($null -eq $lookup) { throw 'Cannot isolate external host lookup.' }
$mock = 'function Get-ExistingChatState { return [pscustomobject]@{runtime=[pscustomobject]@{running=$true;paused=$false;groupId=567890}} }'
$launcherSource = $launcherSource.Substring(0,$lookup.Extent.StartOffset) + $mock + $launcherSource.Substring($lookup.Extent.EndOffset)
$fixtureLauncher = Join-Path $fixtureFiles 'Start-GroupChat.ps1'
[IO.File]::WriteAllText($fixtureLauncher, $launcherSource, $utf8)
& (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $fixtureLauncher -ProjectRoot $fixtureRoot
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $fixtureRoot 'panel-opened.txt'))) { throw 'Broken next release prevented existing host panel access.' }
Write-Output 'PASS launcher: existing host panel remains accessible when the next release is missing'
