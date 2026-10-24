#requires -Version 7.0
param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AndroidPhone.Core.psm1') -Force
$module = Get-Module AndroidPhone.Core
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testParent = Join-Path $project 'Cache/AndroidPhoneTests'
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$script:passed = 0
function Assert-Case([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:passed++
    Write-Output "PASS $Name"
}
function Must-Reject([scriptblock]$Body, [string]$Pattern = '') {
    $rejected = $false
    try { & $Body | Out-Null } catch {
        if ($Pattern -and $_.Exception.Message -notmatch $Pattern) { throw "Wrong refusal: $($_.Exception.Message)" }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Expected refusal did not occur.' }
}
function Reset-Fake {
    & $module {
        $script:FakeDevices = "List of devices attached`nemulator-5560`tdevice`n"
        $script:FakeName = "deepseek-phone`nOK`n"
        $script:FakeBoot = '11111111-2222-3333-4444-555555555555'
        $script:FakeUid = '2000'
        $script:FakeUidFailure = $false
        $script:FakeUnrootFailure = $false
        $script:FakeSize = 'Physical size: 720x1280'
        $script:FakePng = [byte[]]$script:BaselinePng.Clone()
        $script:Calls.Clear()
        @{ avdName = 'deepseek-phone'; serial = 'emulator-5560'; status = 'control-ready'; bootId = $script:FakeBoot } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:FakeRoot 'control-ready.json') -Encoding utf8NoBOM
    }
}
function New-TestPngPayload([byte[]]$Header, [byte[]]$Raw) {
    $compressed = [IO.MemoryStream]::new()
    $zlib = [IO.Compression.ZLibStream]::new($compressed, [IO.Compression.CompressionLevel]::Optimal, $true)
    try { $zlib.Write($Raw, 0, $Raw.Length) } finally { $zlib.Dispose() }
    $payload = $compressed.ToArray()
    $compressed.Dispose()
    $chunk = [byte[]]::new($payload.Length + 12)
    $lengthBytes = [BitConverter]::GetBytes([int]$payload.Length)
    [Array]::Reverse($lengthBytes)
    [Array]::Copy($lengthBytes, 0, $chunk, 0, 4)
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('IDAT'), 0, $chunk, 4, 4)
    [Array]::Copy($payload, 0, $chunk, 8, $payload.Length)
    $crc = [BitConverter]::GetBytes([DeepSeekAndroidPhonePng]::Crc($chunk, 4, $payload.Length + 4))
    [Array]::Reverse($crc)
    [Array]::Copy($crc, 0, $chunk, $payload.Length + 8, 4)
    $iend = [byte[]]@(0,0,0,0,73,69,78,68,174,66,96,130)
    return ,([byte[]]($Header[0..32] + $chunk + $iend))
}
try {
    Assert-Case 'dedicated process environment allows local daemon bootstrap and disables auto discovery' {
        $environmentProbe = [Diagnostics.ProcessStartInfo]::new()
        Set-PhoneProcessEnvironment $environmentProbe
        if ($environmentProbe.Environment['ADB_SERVER_SOCKET'] -cne 'tcp:localhost:5038' -or
            $environmentProbe.Environment['ANDROID_ADB_SERVER_PORT'] -cne '5038' -or
            $environmentProbe.Environment['ADB_LOCAL_TRANSPORT_MAX_PORT'] -cne '0' -or
            $environmentProbe.Environment['ADB_MDNS_AUTO_CONNECT'] -cne '0') { throw 'Dedicated daemon environment was lost.' }
        if (-not $environmentProbe.Environment['ANDROID_USER_HOME'].StartsWith($project + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Android user files escaped this project.' }
    }
    $testPwsh = (Get-Process -Id $PID).Path
    Assert-Case 'native process argument transport and stdout work without a shell intermediary' {
        $result = Invoke-PhoneNative -Executable $testPwsh -Arguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', "[Console]::Out.Write('native-ok')") -TimeoutMs 10000
        if ($result.Output -cne 'native-ok') { throw 'Unexpected native output.' }
    }
    Assert-Case 'native command output cap enforced' {
        Must-Reject { Invoke-PhoneNative -Executable $testPwsh -Arguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', "[Console]::Out.Write(('x' * 1024))") -TimeoutMs 10000 -MaxBytes 64 } 'output exceeds its size limit'
    }
    Assert-Case 'native stderr flood is bounded during execution' {
        Must-Reject { Invoke-PhoneNative -Executable $testPwsh -Arguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', "[Console]::Error.Write(('e' * 131072)); Start-Sleep -Seconds 5") -TimeoutMs 10000 } 'error output exceeds its size limit'
    }
    Assert-Case 'native stdout and stderr drain concurrently' {
        $result = Invoke-PhoneNative -Executable $testPwsh -Arguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', "for (`$i=0; `$i -lt 64; `$i++) { [Console]::Out.Write(('o' * 512)); [Console]::Error.Write(('e' * 512)) }") -TimeoutMs 10000
        if ($result.Output.Length -ne 32768 -or $result.Error.Length -ne 32768) { throw 'Concurrent stream output was lost.' }
    }
    Assert-Case 'native command timeout enforced' {
        Must-Reject { Invoke-PhoneNative -Executable $testPwsh -Arguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 5') -TimeoutMs 200 } 'timed out'
    }
    Assert-Case 'missing native executable rejected without a cleanup exception masking it' {
        $message = ''
        try { Invoke-PhoneNative -Executable (Join-Path $testRoot 'not-installed.exe') -Arguments @() | Out-Null } catch { $message = $_.Exception.Message }
        if (-not $message -or $message -match 'associated with this object') { throw 'Native startup failure was masked.' }
    }
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new(720, 1280)
    $pngStream = [IO.MemoryStream]::new()
    try { $bitmap.Save($pngStream, [Drawing.Imaging.ImageFormat]::Png); $testPng = $pngStream.ToArray() }
    finally { $pngStream.Dispose(); $bitmap.Dispose() }
    & $module {
        param($Root, $Png)
        $script:FakeRoot = $Root
        $script:BaselinePng = [byte[]]$Png.Clone()
        $script:FakePng = [byte[]]$Png.Clone()
        $script:Calls = [Collections.Generic.List[object]]::new()
        function script:Get-PhonePaths {
            [pscustomobject]@{ Runtime = $script:FakeRoot; Captures = Join-Path $script:FakeRoot 'captures'; ReadyFile = Join-Path $script:FakeRoot 'control-ready.json' }
        }
        function script:Invoke-PhoneAdb {
            param([string[]]$Arguments, [switch]$Binary, [switch]$AllowFailure, [int]$TimeoutMs = 12000)
            $script:Calls.Add(@($Arguments))
            $joined = $Arguments -join ' '
            if ($joined -eq '-s emulator-5560 unroot' -and $script:FakeUnrootFailure) { throw 'Simulated unroot timeout after request.' }
            if ($joined -eq '-s emulator-5560 shell id -u' -and $script:FakeUidFailure) { throw 'Simulated UID read failure.' }
            $output = switch ($joined) {
                'devices' { $script:FakeDevices }
                '-s emulator-5560 emu avd name' { $script:FakeName }
                '-s emulator-5560 shell cat /proc/sys/kernel/random/boot_id' { $script:FakeBoot }
                '-s emulator-5560 shell id -u' { $script:FakeUid }
                '-s emulator-5560 shell wm size' { $script:FakeSize }
                default { '' }
            }
            [pscustomobject]@{ ExitCode = 0; Output = $output; Data = if ($Binary) { $script:FakePng } else { $null }; Error = '' }
        }
    } $testRoot $testPng

    Assert-Case 'target serial and AVD identity checked before a bounded tap' {
        Reset-Fake
        Invoke-AndroidPhoneAction -Action Tap -X 719 -Y 1279 | Out-Null
        $commands = & $module { @($script:Calls | ForEach-Object { $_ -join ' ' }) }
        if ($commands[0] -ne 'devices' -or $commands[1] -ne '-s emulator-5560 emu avd name' -or $commands[-1] -ne '-s emulator-5560 shell input tap 719 1279') { throw 'Unexpected command routing.' }
    }
    Assert-Case 'wrong serial rejected' {
        Reset-Fake
        & $module { $script:FakeDevices = "List of devices attached`nemulator-5554`tdevice`n" }
        Must-Reject { Invoke-AndroidPhoneAction -Action Home }
        if ((& $module { $script:Calls.Count }) -ne 1) { throw 'Commands were sent after wrong-device refusal.' }
    }
    Assert-Case 'right serial with wrong AVD name rejected' {
        Reset-Fake
        & $module { $script:FakeName = "personal-phone`nOK`n" }
        Must-Reject { Invoke-AndroidPhoneAction -Action Back }
        if ((& $module { $script:Calls.Count }) -ne 2) { throw 'Commands were sent after wrong-AVD refusal.' }
    }
    Assert-Case 'extra physical device on dedicated ADB server rejected' {
        Reset-Fake
        & $module { $script:FakeDevices += "PERSONAL_USB`tdevice`n" }
        Must-Reject { Invoke-AndroidPhoneAction -Action Home }
    }
    Assert-Case 'offline target rejected' {
        Reset-Fake
        & $module { $script:FakeDevices = "List of devices attached`nemulator-5560`toffline`n" }
        Must-Reject { Invoke-AndroidPhoneAction -Action Home }
    }
    Assert-Case 'unexpected device states do not get silently ignored' {
        Reset-Fake
        & $module { $script:FakeDevices += "PERSONAL_USB`tno permissions`n" }
        Must-Reject { Invoke-AndroidPhoneAction -Action Home }
    }
    Assert-Case 'tap bounds checked before device access' {
        Reset-Fake
        Must-Reject { Invoke-AndroidPhoneAction -Action Tap -X 720 -Y 1 }
        Must-Reject { Invoke-AndroidPhoneAction -Action Tap -X -1 -Y 1 }
        if ((& $module { $script:Calls.Count }) -ne 0) { throw 'Invalid touch accessed ADB.' }
    }
    Assert-Case 'swipe coordinate and duration limits enforced' {
        Reset-Fake
        Must-Reject { Invoke-AndroidPhoneAction -Action Swipe -EndY 1280 }
        Must-Reject { Invoke-AndroidPhoneAction -Action Swipe -DurationMs 49 }
        Must-Reject { Invoke-AndroidPhoneAction -Action Swipe -DurationMs 2001 }
        Invoke-AndroidPhoneAction -Action Swipe -X 0 -Y 0 -EndX 719 -EndY 1279 -DurationMs 2000 | Out-Null
    }
    Assert-Case 'text shell metacharacters and unsupported Chinese rejected before ADB' {
        Reset-Fake
        foreach ($unsafe in @('x;reboot', '$(id)', 'x&y', 'x|y', 'x>y', 'x<y', 'x`id`', 'x"y', "x'y", 'x%sy', '你好')) {
            Must-Reject { Invoke-AndroidPhoneAction -Action Text -Text $unsafe }
        }
        if ((& $module { $script:Calls.Count }) -ne 0) { throw 'Unsafe text accessed ADB.' }
    }
    Assert-Case 'space encoding stays in a single input-text argument' {
        Reset-Fake
        Invoke-AndroidPhoneAction -Action Text -Text 'hello world @bot' | Out-Null
        $last = & $module { ,$script:Calls[-1] }
        if ($last.Count -ne 6 -or $last[-1] -ne 'hello%sworld%s@bot') { throw 'Unexpected text argument encoding.' }
    }
    Assert-Case 'changed boot requires guest policy to be reapplied' {
        Reset-Fake
        & $module { $script:FakeBoot = 'another-boot' }
        Must-Reject { Invoke-AndroidPhoneAction -Action Home }
    }
    Assert-Case 'root UID refuses actions and revokes the old control receipt' {
        Reset-Fake
        & $module { $script:FakeUid = '0' }
        Must-Reject { Invoke-AndroidPhoneAction -Action Home } 'root or its UID is unknown'
        if (Test-Path -LiteralPath (Join-Path $testRoot 'control-ready.json')) { throw 'Root device kept its receipt.' }
        if ((& $module { @($script:Calls | Where-Object { ($_ -join ' ') -match 'shell input' }).Count }) -ne 0) { throw 'Input was sent while root.' }
    }
    Assert-Case 'UID query failure refuses actions and revokes the old receipt' {
        Reset-Fake
        & $module { $script:FakeUidFailure = $true }
        Must-Reject { Invoke-AndroidPhoneAction -Action Home } 'root or its UID is unknown'
        if (Test-Path -LiteralPath (Join-Path $testRoot 'control-ready.json')) { throw 'Unknown UID kept its receipt.' }
    }
    Assert-Case 'unroot request timeout can recover only after actual non-root UID readback' {
        Reset-Fake
        & $module { $script:FakeUnrootFailure = $true }
        if ((Restore-PhoneUnroot -TimeoutMs 100) -cne '2000') { throw 'Non-root readback was not returned.' }
        if (-not (Test-Path -LiteralPath (Join-Path $testRoot 'control-ready.json'))) { throw 'Verified non-root cleanup unexpectedly revoked receipt.' }
    }
    Assert-Case 'failed unroot with retained root revokes receipt and fails' {
        Reset-Fake
        & $module { $script:FakeUnrootFailure = $true; $script:FakeUid = '0' }
        Must-Reject { Restore-PhoneUnroot -TimeoutMs 50 } 'Cannot confirm'
        if (Test-Path -LiteralPath (Join-Path $testRoot 'control-ready.json')) { throw 'Failed unroot kept receipt.' }
    }
    Assert-Case 'unroot followed by an unreadable UID revokes receipt and fails' {
        Reset-Fake
        & $module { $script:FakeUidFailure = $true }
        Must-Reject { Restore-PhoneUnroot -TimeoutMs 50 } 'Cannot confirm'
        if (Test-Path -LiteralPath (Join-Path $testRoot 'control-ready.json')) { throw 'Unverified unroot kept receipt.' }
    }
    Assert-Case 'changed display refuses actions' {
        Reset-Fake
        & $module { $script:FakeSize = "Physical size: 720x1280`nOverride size: 1080x1920" }
        Must-Reject { Invoke-AndroidPhoneAction -Action Tap -X 1 -Y 1 }
    }
    Assert-Case 'PNG capture uses a generated path under the dedicated capture directory' {
        Reset-Fake
        $capture = Invoke-AndroidPhoneAction -Action Screenshot
        if (-not $capture.imagePath.StartsWith((Join-Path $testRoot 'captures') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Capture escaped its fixed output directory.' }
        if (-not (Test-Path -LiteralPath $capture.imagePath)) { throw 'Capture was not written.' }
    }
    Assert-Case 'wrong PNG dimensions refused' {
        Reset-Fake
        & $module { $script:FakePng[19] = 1 }
        Must-Reject { Invoke-AndroidPhoneAction -Action Screenshot }
    }
    Assert-Case '24-byte fake PNG header refused' {
        Reset-Fake
        & $module { $script:FakePng = [byte[]]$script:FakePng[0..23] }
        Must-Reject { Invoke-AndroidPhoneAction -Action Screenshot } 'PNG length is invalid'
    }
    Assert-Case 'truncated PNG with missing IEND refused' {
        Reset-Fake
        & $module { $script:FakePng = [byte[]]$script:FakePng[0..($script:FakePng.Length - 13)] }
        Must-Reject { Invoke-AndroidPhoneAction -Action Screenshot } 'IEND'
    }
    Assert-Case 'corrupt PNG CRC refused' {
        Reset-Fake
        & $module { $script:FakePng[29] = $script:FakePng[29] -bxor 1 }
        Must-Reject { Invoke-AndroidPhoneAction -Action Screenshot } 'CRC'
    }
    Assert-Case 'PNG trailing bytes refused' {
        Reset-Fake
        & $module { $script:FakePng = [byte[]]($script:FakePng + 0) }
        Must-Reject { Invoke-AndroidPhoneAction -Action Screenshot } 'PNG end'
    }
    Assert-Case 'CRC-correct but undecodable PNG image data refused' {
        Reset-Fake
        & $module {
            $offset = 8
            while ([Text.Encoding]::ASCII.GetString($script:FakePng, $offset + 4, 4) -cne 'IDAT') {
                $chunkLength = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($script:FakePng, $offset))
                $offset += $chunkLength + 12
            }
            $chunkLength = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($script:FakePng, $offset))
            $script:FakePng[$offset + 8] = 0
            $crc = [BitConverter]::GetBytes([DeepSeekAndroidPhonePng]::Crc($script:FakePng, $offset + 4, $chunkLength + 4))
            [Array]::Reverse($crc)
            [Array]::Copy($crc, 0, $script:FakePng, $offset + 8 + $chunkLength, 4)
        }
        Must-Reject { Invoke-AndroidPhoneAction -Action Screenshot }
    }
    Assert-Case 'valid zlib stream with truncated PNG scanlines refused' {
        Reset-Fake
        $header = & $module { ,$script:BaselinePng }
        $candidate = New-TestPngPayload $header ([byte[]]@(0))
        & $module { param($Png) $script:FakePng = $Png } $candidate
        Must-Reject { Invoke-AndroidPhoneAction -Action Screenshot } 'scanline data is truncated'
    }
    Assert-Case 'PNG decompression cannot exceed its fixed pixel dimensions' {
        Reset-Fake
        $header = & $module { ,$script:BaselinePng }
        if ($header[24] -ne 8 -or $header[25] -ne 6) { throw 'The synthetic PNG fixture must use RGBA8.' }
        $candidate = New-TestPngPayload $header ([byte[]]::new((720 * 4 + 1) * 1280 + 1))
        & $module { param($Png) $script:FakePng = $Png } $candidate
        Must-Reject { Invoke-AndroidPhoneAction -Action Screenshot } 'decompressed data exceeds'
    }
    Assert-Case 'missing control receipt does not touch devices' {
        Reset-Fake
        Remove-Item -LiteralPath (Join-Path $testRoot 'control-ready.json')
        Must-Reject { Invoke-AndroidPhoneAction -Action Home }
        if ((& $module { $script:Calls.Count }) -ne 0) { throw 'Missing receipt accessed ADB.' }
    }
    Write-Output "Offline contract tests passed: $script:passed. No SDK executable, real ADB device, emulator, network, or system setting was used."
}
finally {
    Remove-Module AndroidPhone.Core -Force -ErrorAction SilentlyContinue
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    $resolvedParent = [IO.Path]::GetFullPath($testParent) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTest.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedTest -Leaf) -match '^[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force -ErrorAction SilentlyContinue
    }
}
