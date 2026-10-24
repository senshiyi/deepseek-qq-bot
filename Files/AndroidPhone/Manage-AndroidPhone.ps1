#requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Initialize', 'CheckHost', 'Start', 'Stop', 'Status', 'InspectGuestNetwork')][string]$Action)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AndroidPhone.Core.psm1') -Force
$paths = Get-PhonePaths
$serialPrefix = @('-s', 'emulator-5560')

function Wait-PhoneBoot {
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    do {
        try {
            Assert-PhoneIdentity
            if ((Invoke-PhoneAdb -Arguments ($serialPrefix + @('shell', 'getprop', 'sys.boot_completed'))).Output.Trim() -eq '1') { return }
        } catch { Write-Verbose $_.Exception.Message }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Dedicated phone did not complete boot within 180 seconds.'
}

function Set-PhoneGuestPolicy {
    Assert-PhoneIdentity
    Invoke-PhoneAdb -Arguments ($serialPrefix + @('root')) | Out-Null
    Start-Sleep -Seconds 2
    Assert-PhoneIdentity
    if ((Invoke-PhoneAdb -Arguments ($serialPrefix + @('shell', 'id', '-u'))).Output.Trim() -ne '0') { throw 'The chosen Google APIs image did not provide operator-only adb root.' }
    Invoke-PhoneAdb -Arguments ($serialPrefix + @('push', (Join-Path $PSScriptRoot 'guest-network-policy.sh'), '/data/local/tmp/bot-phone-network.sh')) | Out-Null
    $policy = Invoke-PhoneAdb -Arguments ($serialPrefix + @('shell', '/system/bin/sh', '/data/local/tmp/bot-phone-network.sh')) -TimeoutMs 30000
    if ($policy.Output -notmatch 'BOT_PHONE_POLICY_READBACK_OK') { throw 'Guest network policy readback did not complete.' }
    Invoke-PhoneAdb -Arguments ($serialPrefix + @('shell', 'settings', 'put', 'system', 'accelerometer_rotation', '0')) | Out-Null
    Invoke-PhoneAdb -Arguments ($serialPrefix + @('shell', 'settings', 'put', 'system', 'user_rotation', '0')) | Out-Null
    Restore-PhoneUnroot | Out-Null
    $bootId = (Invoke-PhoneAdb -Arguments ($serialPrefix + @('shell', 'cat', '/proc/sys/kernel/random/boot_id'))).Output.Trim()
    [pscustomobject]@{ status = 'control-ready'; avdName = 'deepseek-phone'; serial = 'emulator-5560'; bootId = $bootId; policy = 'applied-and-read-back'; modelControlEnabled = $false; realNetworkAcceptance = 'pending'; clipboardAcceptance = 'headless-design-pending-live-test'; timestamp = [DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $paths.ReadyFile -Encoding utf8NoBOM
}

switch ($Action) {
    'Initialize' {
        # This action is operator-only: writes AVD configuration and runtime directories.
        if (-not (Test-Path -LiteralPath (Join-Path $paths.Sdk 'system-images/android-35/google_apis/x86_64/system.img'))) { throw 'Verified SDK/system image has not been installed into the project SDK root.' }
        if (Test-Path -LiteralPath (Join-Path $paths.Runtime 'deepseek-phone.ini')) { throw 'An AVD already exists; refusing to overwrite its configuration or user data.' }
        New-Item -ItemType Directory -Path $paths.Avd, (Join-Path $paths.Runtime 'android-user'), $paths.Captures -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config.ini.template') -Destination (Join-Path $paths.Avd 'config.ini')
        "avd.ini.encoding=UTF-8`npath=$($paths.Avd)`ntarget=android-35`n" | Set-Content -LiteralPath (Join-Path $paths.Runtime 'deepseek-phone.ini') -Encoding utf8NoBOM
        [pscustomobject]@{ action = $Action; created = 'AVD configuration only'; diskCreated = $false; started = $false } | ConvertTo-Json
    }
    'CheckHost' {
        if (-not (Test-Path -LiteralPath $paths.Emulator)) { throw 'Verified emulator package is not installed yet.' }
        Invoke-PhoneNative -Executable $paths.Emulator -Arguments @('-accel-check') -TimeoutMs 30000 | Select-Object ExitCode, Output, Error | ConvertTo-Json
    }
    'Start' {
        if (-not (Test-Path -LiteralPath (Join-Path $paths.Avd 'config.ini'))) { throw 'Initialize the dedicated AVD first.' }
        if (-not (Test-Path -LiteralPath $paths.Emulator)) { throw 'Verified emulator package is not installed yet.' }
        # Do not reuse another process/device listening on our fixed ports.
        $occupied = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in @(5560, 5561) })
        if ($occupied.Count) { throw 'Port 5560/5561 is already in use; refusing to reuse or terminate its owner.' }
        # No USB device can have this reserved emulator serial. Other emulators
        # are not scanned (ADB_LOCAL_TRANSPORT_MAX_PORT=0 in our process env).
        Invoke-PhoneNative -Executable $paths.Adb -Arguments @('-L', 'tcp:localhost:5038', '--one-device', 'emulator-5560', 'start-server') | Out-Null
        $existing = (Invoke-PhoneAdb -Arguments @('devices')).Output
        if (@($existing -split '\r?\n' | Where-Object { $_ -match '^\S+\s+(device|offline|unauthorized)\b' }).Count) { throw 'Dedicated ADB server already contains a device; refusing to start.' }
        if (Test-Path -LiteralPath $paths.ReadyFile) { Remove-Item -LiteralPath $paths.ReadyFile }
        $info = [Diagnostics.ProcessStartInfo]::new()
        $info.FileName = $paths.Emulator
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        foreach ($argument in @('-avd', 'deepseek-phone', '-port', '5560', '-no-window', '-no-snapshot', '-no-audio', '-no-metrics', '-camera-back', 'none', '-camera-front', 'none', '-gpu', 'auto', '-accel', 'on', '-adb-path', $paths.Adb)) { $info.ArgumentList.Add($argument) }
        Set-PhoneProcessEnvironment $info
        $emulatorProcess = [Diagnostics.Process]::Start($info)
        try {
            Wait-PhoneBoot
            Set-PhoneGuestPolicy
            [pscustomobject]@{ action = $Action; processId = $emulatorProcess.Id; serial = 'emulator-5560'; guestPolicy = 'applied-and-read-back'; realIsolationAcceptance = 'pending'; display = 'headless; use bounded screenshots' } | ConvertTo-Json
        } catch {
            # Kill only the exact emulator process created in this action, never another emulator.
            if (-not $emulatorProcess.HasExited) { $emulatorProcess.Kill($true) }
            if (Test-Path -LiteralPath $paths.ReadyFile) { Remove-Item -LiteralPath $paths.ReadyFile }
            throw
        } finally { $emulatorProcess.Dispose() }
    }
    'Stop' {
        Assert-PhoneIdentity
        Invoke-PhoneAdb -Arguments ($serialPrefix + @('emu', 'kill')) | Out-Null
        if (Test-Path -LiteralPath $paths.ReadyFile) { Remove-Item -LiteralPath $paths.ReadyFile }
        [pscustomobject]@{ action = $Action; serial = 'emulator-5560'; stopRequested = $true } | ConvertTo-Json
    }
    'Status' {
        Assert-PhoneIdentity
        [pscustomobject]@{ avdName = 'deepseek-phone'; serial = 'emulator-5560'; controlReceiptExists = (Test-Path -LiteralPath $paths.ReadyFile); bootCompleted = (Invoke-PhoneAdb -Arguments ($serialPrefix + @('shell', 'getprop', 'sys.boot_completed'))).Output.Trim() } | ConvertTo-Json
    }
    'InspectGuestNetwork' {
        Assert-PhoneIdentity
        try {
            Invoke-PhoneAdb -Arguments ($serialPrefix + @('root')) | Out-Null
            Start-Sleep -Seconds 2
            Assert-PhoneIdentity
            $ipv4 = (Invoke-PhoneAdb -Arguments ($serialPrefix + @('shell', 'iptables', '-w', '-S', 'BOT_PHONE_OUT'))).Output
            $ipv6 = (Invoke-PhoneAdb -Arguments ($serialPrefix + @('shell', 'ip6tables', '-w', '-S', 'BOT_PHONE6_OUTPUT'))).Output
            [pscustomobject]@{ ipv4 = $ipv4; ipv6 = $ipv6; realConnectivityTests = 'not performed by this inspection' } | ConvertTo-Json
        } finally { Restore-PhoneUnroot | Out-Null }
    }
}
