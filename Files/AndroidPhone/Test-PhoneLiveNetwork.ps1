#requires -Version 7.0
param([Parameter(Mandatory)][switch]$ConfirmDedicatedBlankPhone)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDedicatedBlankPhone) { throw 'This operator-only check requires the dedicated blank AVD.' }
Import-Module (Join-Path $PSScriptRoot 'AndroidPhone.Core.psm1') -Force
$paths = Get-PhonePaths
$prefix = @('-s', 'emulator-5560')
Assert-PhoneIdentity
$evidence = [ordered]@{
    timestamp = [DateTime]::UtcNow.ToString('o')
    device = 'deepseek-phone/emulator-5560'
    kind = 'live-device-network-acceptance'
    modelControlEnabled = $false
    hostFirewallChanged = $false
    clipboard = 'pending; host clipboard was neither read nor changed'
    httpsApplication = 'pending; TCP/443 alone does not verify a TLS session'
}
$listeners = [Collections.Generic.List[Net.Sockets.TcpListener]]::new()
$rootRequested = $false
$primaryFailure = $null
$cleanupFailure = $null
function Get-GuestOutput([string[]]$Arguments) {
    (Invoke-PhoneAdb -Arguments ($prefix + $Arguments)).Output
}
function Get-RejectCount([string]$Table, [string]$Destination) {
    foreach ($line in ($Table -split '\r?\n')) {
        if ($line -match ('^\s*(\d+)\s+\d+\s+REJECT\s+.*\s' + [regex]::Escape($Destination) + '\s')) {
            return [long]$Matches[1]
        }
    }
    throw "No counter found for $Destination."
}
function New-VerifiedLocalListener([Net.IPAddress]$Address) {
    $listener = [Net.Sockets.TcpListener]::new($Address, 0)
    $listener.Start()
    $listeners.Add($listener)
    $port = $listener.LocalEndpoint.Port
    $pending = $listener.AcceptTcpClientAsync()
    $baseline = [Net.Sockets.TcpClient]::new()
    try {
        $baseline.Connect($Address, $port)
        if (-not $pending.Wait(2000)) { throw 'Local test service did not accept its baseline connection.' }
        $accepted = $pending.GetAwaiter().GetResult()
        $accepted.Dispose()
    } finally { $baseline.Dispose() }
    [pscustomobject]@{ address = $Address.ToString(); port = $port; hostBaselineAccepted = $true }
}
function Test-BlockedEndpoint([string]$GuestTarget, [int]$Port, [string]$RuleDestination) {
    $before = Get-GuestOutput @('shell', 'iptables', '-w', '-L', 'BOT_PHONE_OUT', '-nvx')
    $probe = Invoke-PhoneAdb -Arguments ($prefix + @('shell', 'nc', '-z', '-w', '2', $GuestTarget, "$Port")) -AllowFailure
    $after = Get-GuestOutput @('shell', 'iptables', '-w', '-L', 'BOT_PHONE_OUT', '-nvx')
    $delta = (Get-RejectCount $after $RuleDestination) - (Get-RejectCount $before $RuleDestination)
    if ($probe.ExitCode -eq 0 -or $delta -le 0) { throw "Blocked endpoint was not confirmed by a rejecting rule: $GuestTarget." }
    [pscustomobject]@{ target = $GuestTarget; port = $Port; exitCode = $probe.ExitCode; rejectRule = $RuleDestination; rejectPacketDelta = $delta; result = 'blocked-by-guest-rule' }
}
try {
    $loopback = New-VerifiedLocalListener ([Net.IPAddress]::Loopback)
    $evidence.hostTestService = $loopback
    $lanAddress = @(Get-NetIPConfiguration | Where-Object IPv4DefaultGateway | ForEach-Object IPv4Address | Where-Object {
        $_.IPAddress -match '^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)'
    } | Select-Object -First 1)
    $lanService = if ($lanAddress.Count) { New-VerifiedLocalListener ([Net.IPAddress]::Parse($lanAddress[0].IPAddress)) } else { $null }
    $evidence.lanTestService = $lanService
    $rootRequested = $true
    Invoke-PhoneAdb -Arguments ($prefix + @('root')) | Out-Null
    Start-Sleep -Seconds 2
    Assert-PhoneIdentity
    if ((Get-GuestOutput @('shell', 'id', '-u')).Trim() -ne '0') { throw 'Guest root unavailable for operator-only policy counters.' }
    $evidence.outputRules = Get-GuestOutput @('shell', 'iptables', '-w', '-S', 'OUTPUT')
    if ((@($evidence.outputRules -split '\r?\n' | Where-Object { $_ -match '^-A ' }) | Select-Object -First 1) -ne '-A OUTPUT -j BOT_PHONE_OUT') { throw 'Guest output rule is no longer first.' }
    $evidence.hostAlias = Test-BlockedEndpoint '10.0.2.2' $loopback.port '10.0.0.0/8'
    if ($lanService) {
        $rule = if ($lanService.address.StartsWith('10.')) { '10.0.0.0/8' } elseif ($lanService.address.StartsWith('192.168.')) { '192.168.0.0/16' } else { '172.16.0.0/12' }
        $evidence.lan = Test-BlockedEndpoint $lanService.address $lanService.port $rule
    } else { $evidence.lan = 'pending; no default-route RFC1918 host interface available' }
    $evidence.linkLocal = Test-BlockedEndpoint '169.254.169.254' 443 '169.254.0.0/16'
    $public = Invoke-PhoneAdb -Arguments ($prefix + @('shell', 'nc', '-z', '-w', '5', 'example.com', '443')) -AllowFailure
    $evidence.dnsAndPublicTcp443 = [pscustomobject]@{ target = 'example.com'; port = 443; exitCode = $public.ExitCode; passed = ($public.ExitCode -eq 0) }
    if ($public.ExitCode -ne 0) { throw 'Public DNS/TCP 443 check did not pass.' }
    $evidence.ipv4Counters = Get-GuestOutput @('shell', 'iptables', '-w', '-L', 'BOT_PHONE_OUT', '-nvx')
    $evidence.ipv6Rules = Get-GuestOutput @('shell', 'ip6tables', '-w', '-S', 'BOT_PHONE6_OUTPUT')
    $evidence.ipv6Connectivity = 'pending; no known reachable IPv6 control service was used'
    $evidence.mounts = Get-GuestOutput @('shell', 'cat', '/proc/mounts')
    if ($evidence.mounts -match '\s(9p|virtiofs|cifs|smb3|fuse\.sshfs|vboxsf)\s') { throw 'Unexpected host-sharing filesystem type observed.' }
    $evidence.sharedFilesystem = 'no host-sharing filesystem type observed; launch has no shared-dir/SD/USB arguments'
    $evidence.guestAccountCounts = Get-GuestOutput @('shell', 'sh', '-c', "'dumpsys account | grep -E Accounts:'")
    $evidence.result = 'IPv4 host-alias, own-LAN and link-local rejection verified; DNS and public TCP443 passed; remaining checks are explicit'
} catch {
    $evidence.result = 'incomplete'
    $evidence.error = $_.Exception.Message
    $primaryFailure = $_
} finally {
    try {
        foreach ($listener in $listeners) { $listener.Stop() }
    } finally {
        try {
            if ($rootRequested) { $evidence.finalAdbUid = Restore-PhoneUnroot }
        } catch {
            $cleanupFailure = $_
            $evidence.result = 'failed; non-root cleanup could not be verified and control was disabled'
            $evidence.cleanupError = $_.Exception.Message
            if (Test-Path -LiteralPath $paths.ReadyFile) { Remove-Item -LiteralPath $paths.ReadyFile -Force }
        } finally {
            $evidenceFile = Join-Path $paths.Runtime ('live-network-{0}.json' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
            $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidenceFile -Encoding utf8NoBOM
            [pscustomobject]@{ evidencePath = $evidenceFile; result = $evidence.result; modelControlEnabled = $false } | ConvertTo-Json
        }
    }
}
if ($cleanupFailure) { throw $cleanupFailure }
if ($primaryFailure) { throw $primaryFailure }
