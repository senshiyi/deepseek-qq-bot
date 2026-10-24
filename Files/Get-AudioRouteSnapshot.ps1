#Requires -Version 7.0
<#
.SYNOPSIS
只读查询 Windows 默认音频端点、QQ 进程树和端点会话；不录音、不播放、不更改设置。
.EXAMPLE
pwsh -NoProfile -File .\Cache\Get-AudioRouteSnapshot.ps1 -Label after-qq-route-change
#>
[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Label = 'audio-route-snapshot'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw '此脚本仅支持 Windows。' }
if ([Environment]::Version.Major -lt 9) { throw 'NAudio 3.2.0 需要 .NET 9+；请使用对应的 PowerShell 7（7.5+）。' }
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$releaseDirectory = Join-Path $ProjectRoot 'Files\AudioBridge\bin\Release\net10.0-windows10.0.20348.0'
$loadedAudioAssemblies = @()
foreach ($assemblyName in @('NAudio.Core', 'NAudio.Wasapi')) {
    $assemblyPath = Join-Path $releaseDirectory ($assemblyName + '.dll')
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw "缺少现有 Release 依赖：$assemblyPath" }
    $previous = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $assemblyName })
    if (@($previous | Where-Object { $_.Location -ne $assemblyPath }).Count -gt 0) {
        throw '当前进程已加载其他路径的 NAudio；请用 pwsh -NoProfile -File 启动新进程运行本脚本。'
    }
    $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
    $loadedAudioAssemblies += [pscustomobject]@{ Name = $assemblyName; Version = $assembly.GetName().Version.ToString(); Path = $assemblyPath }
}
$started = [DateTimeOffset]::Now
$watch = [Diagnostics.Stopwatch]::StartNew()
$errors = [Collections.Generic.List[object]]::new()
$qqById = @{}
try {
    foreach ($processRow in @(Get-CimInstance Win32_Process -Filter "Name='QQ.exe'")) {
        $processId = [int]$processRow.ProcessId
        $arguments = [string]$processRow.CommandLine
        $kind = if ($arguments -match 'audio\.mojom\.AudioService|--service-sandbox-type=audio') { 'audio.mojom' }
            elseif ($arguments -match '--type=ppapi') { 'ppapi' } else { 'other-QQ' }
        $qqById[$processId] = [pscustomobject]@{
            ProcessId = $processId; ParentProcessId = [int]$processRow.ParentProcessId
            RootProcessId = $null; ArgumentKind = $kind
            CommandLineAvailable = -not [string]::IsNullOrWhiteSpace($arguments)
        }
    }
} catch { $errors.Add([pscustomobject]@{ Stage = 'QQProcessEnumeration'; Error = $_.Exception.Message }) }
foreach ($entry in $qqById.Values) {
    $currentId = [int]$entry.ProcessId
    $seen = [Collections.Generic.HashSet[int]]::new()
    while ($qqById.ContainsKey([int]$qqById[$currentId].ParentProcessId) -and $seen.Add($currentId)) {
        $currentId = [int]$qqById[$currentId].ParentProcessId
    }
    $entry.RootProcessId = $currentId
}
$defaults = [Collections.Generic.List[object]]::new()
$endpoints = [Collections.Generic.List[object]]::new()
$enumerator = [NAudio.CoreAudioApi.MMDeviceEnumerator]::new()
try {
    foreach ($flow in @([NAudio.CoreAudioApi.DataFlow]::Render, [NAudio.CoreAudioApi.DataFlow]::Capture)) {
        foreach ($role in @([NAudio.CoreAudioApi.Role]::Console, [NAudio.CoreAudioApi.Role]::Multimedia, [NAudio.CoreAudioApi.Role]::Communications)) {
            $defaultDevice = $null
            try {
                $defaultDevice = $enumerator.GetDefaultAudioEndpoint($flow, $role)
                $defaults.Add([pscustomobject]@{ Flow = $flow.ToString(); Role = $role.ToString()
                    Id = $defaultDevice.ID; Name = $defaultDevice.FriendlyName; Error = $null })
            } catch {
                $defaults.Add([pscustomobject]@{ Flow = $flow.ToString(); Role = $role.ToString()
                    Id = $null; Name = $null; Error = $_.Exception.Message })
                $errors.Add([pscustomobject]@{ Stage = "Default-$flow-$role"; Error = $_.Exception.Message })
            } finally { if ($null -ne $defaultDevice) { $defaultDevice.Dispose() } }
        }
    }
    $devices = $enumerator.EnumerateAudioEndPoints([NAudio.CoreAudioApi.DataFlow]::All, [NAudio.CoreAudioApi.DeviceState]::Active)
    try {
        foreach ($device in $devices) {
            $endpoint = [ordered]@{ Id = $null; Name = $null; Flow = $null; SessionCount = $null
                QqSessions = @(); UnmatchedSessionProcessIds = @(); SessionErrors = @(); Error = $null }
            $qqSessions = [Collections.Generic.List[object]]::new()
            $unmatched = [Collections.Generic.HashSet[int]]::new()
            $sessionErrors = [Collections.Generic.List[object]]::new()
            try {
                $endpoint.Id = $device.ID
                $endpoint.Name = $device.FriendlyName
                $endpoint.Flow = $device.DataFlow.ToString()
                $manager = $device.AudioSessionManager
                $manager.RefreshSessions()
                $sessions = $manager.Sessions
                $endpoint.SessionCount = $sessions.Count
                for ($index = 0; $index -lt $endpoint.SessionCount; $index++) {
                    $session = $null
                    try {
                        $session = $sessions[$index]
                        $ownerId = [int]$session.GetProcessID
                        if ($qqById.ContainsKey($ownerId)) {
                            $owner = $qqById[$ownerId]
                            $qqSessions.Add([pscustomobject]@{ ProcessId = $ownerId; RootProcessId = $owner.RootProcessId
                                ArgumentKind = $owner.ArgumentKind; State = $session.State.ToString()
                                SessionInstance = $session.GetSessionInstanceIdentifier })
                        } else { [void]$unmatched.Add($ownerId) }
                    } catch {
                        $sessionErrors.Add([pscustomobject]@{ Index = $index; Error = $_.Exception.Message })
                        $errors.Add([pscustomobject]@{ Stage = 'Session'; EndpointId = $endpoint.Id; Index = $index; Error = $_.Exception.Message })
                    } finally { if ($null -ne $session) { $session.Dispose() } }
                }
            } catch {
                $endpoint.Error = $_.Exception.Message
                $errors.Add([pscustomobject]@{ Stage = 'Endpoint'; EndpointId = $endpoint.Id; Error = $_.Exception.Message })
            } finally { $device.Dispose() }
            $endpoint.QqSessions = $qqSessions.ToArray()
            $endpoint.UnmatchedSessionProcessIds = @($unmatched | Sort-Object)
            $endpoint.SessionErrors = $sessionErrors.ToArray()
            $endpoints.Add([pscustomobject]$endpoint)
        }
    } finally { $devices.Dispose() }
} finally { $enumerator.Dispose() }
$qqProcesses = @($qqById.Values | Sort-Object ProcessId)
$audioMappings = @(
    foreach ($endpoint in $endpoints) {
        foreach ($session in $endpoint.QqSessions) {
            [pscustomobject]@{ ProcessId = $session.ProcessId; RootProcessId = $session.RootProcessId
                ArgumentKind = $session.ArgumentKind; Flow = $endpoint.Flow; EndpointId = $endpoint.Id
                EndpointName = $endpoint.Name; State = $session.State }
        }
    }
)
$qqRoots = @(
    foreach ($root in $qqProcesses | Where-Object { $_.RootProcessId -eq $_.ProcessId }) {
        [pscustomobject]@{ ProcessId = $root.ProcessId; ParentProcessId = $root.ParentProcessId
            ProcessIds = @($qqProcesses | Where-Object RootProcessId -eq $root.ProcessId | ForEach-Object ProcessId)
            SessionOwnerProcessIds = @($audioMappings | Where-Object RootProcessId -eq $root.ProcessId | ForEach-Object ProcessId | Sort-Object -Unique) }
    }
)
$finished = [DateTimeOffset]::Now
$snapshot = [ordered]@{
    StartedAt = $started.ToString('O'); CompletedAt = $finished.ToString('O'); ObservedAtUtc = $finished.UtcDateTime.ToString('O')
    DurationMilliseconds = $watch.Elapsed.TotalMilliseconds; Label = $Label
    ReadOnlyAudioInspection = $true; AudioCapturedOrPlayed = $false; SettingsChanged = $false
    PowerShellVersion = $PSVersionTable.PSVersion.ToString(); DotNetVersion = [Environment]::Version.ToString()
    Apartment = [Threading.Thread]::CurrentThread.GetApartmentState().ToString(); Assemblies = $loadedAudioAssemblies
    DefaultEndpoints = $defaults.ToArray(); ActiveEndpoints = $endpoints.ToArray()
    QqRoots = $qqRoots; QqProcesses = $qqProcesses; AudioProcessMappings = $audioMappings
    Partial = $errors.Count -gt 0; Errors = $errors.ToArray()
    Interpretation = @('Snapshot is not atomic; processes/streams can change while enumerating.',
        'Active means at least one running stream on this endpoint; it does not identify a QQ call.',
        'Session enumerators can be incomplete; missing entries do not prove no stream exists.',
        'Session PID is the reported owner; cross-process sessions can report the initial creator.',
        'AudioProcessMappings includes ppapi and other QQ session owners, not only audio.mojom.')
}
$resultDirectory = Join-Path $ProjectRoot 'Results\local'
[void][IO.Directory]::CreateDirectory($resultDirectory)
$reportPath = Join-Path $resultDirectory ("{0}-{1}-{2}.json" -f $Label, $finished.ToString('yyyyMMdd-HHmmss-fff'), $PID)
$snapshot | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
[pscustomobject]@{ Path = $reportPath; Partial = $snapshot.Partial; DefaultEndpoints = $snapshot.DefaultEndpoints
    QqRoots = $qqRoots; ActiveQqCaptureSessions = @($audioMappings | Where-Object { $_.Flow -eq 'Capture' -and $_.State -eq 'AudioSessionStateActive' }) } |
    ConvertTo-Json -Depth 8
