#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PhoneAvd = 'deepseek-phone'
$script:PhoneSerial = 'emulator-5560'

function Get-PhonePaths {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $runtime = Join-Path $root 'Results/local/android-phone'
    [pscustomobject]@{
        Root = $root
        Sdk = Join-Path $root 'Cache/AndroidSdk'
        Archives = Join-Path $root 'Cache/AndroidPhonePackages'
        Runtime = $runtime
        Avd = Join-Path $runtime 'deepseek-phone.avd'
        Captures = Join-Path $runtime 'captures'
        ReadyFile = Join-Path $runtime 'control-ready.json'
        Adb = Join-Path $root 'Cache/AndroidSdk/platform-tools/adb.exe'
        Emulator = Join-Path $root 'Cache/AndroidSdk/emulator/emulator.exe'
    }
}

function Set-PhoneProcessEnvironment([Diagnostics.ProcessStartInfo]$Info) {
    $paths = Get-PhonePaths
    $Info.Environment['ANDROID_HOME'] = $paths.Sdk
    $Info.Environment['ANDROID_SDK_ROOT'] = $paths.Sdk
    $Info.Environment['ANDROID_AVD_HOME'] = $paths.Runtime
    $Info.Environment['ANDROID_USER_HOME'] = Join-Path $paths.Runtime 'android-user'
    $Info.Environment['ANDROID_EMULATOR_HOME'] = Join-Path $paths.Runtime 'android-user'
    # ADB permits auto-start only for the literal local host name. The numeric
    # loopback form is treated as a remote server and cannot bootstrap a daemon.
    $Info.Environment['ADB_SERVER_SOCKET'] = 'tcp:localhost:5038'
    $Info.Environment['ANDROID_ADB_SERVER_PORT'] = '5038'
    $Info.Environment['ADB_MDNS_AUTO_CONNECT'] = '0'
    # The emulator explicitly registers with our server; do not scan other
    # emulator transports while bootstrapping this dedicated ADB daemon.
    $Info.Environment['ADB_LOCAL_TRANSPORT_MAX_PORT'] = '0'
}

function Invoke-PhoneNative {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments,
        [int]$TimeoutMs = 12000,
        [int]$MaxBytes = 65536,
        [switch]$Binary,
        [switch]$AllowFailure
    )
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    Set-PhoneProcessEnvironment $info
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $started = $false
    $bytes = [IO.MemoryStream]::new()
    $errorBytes = [IO.MemoryStream]::new()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) { throw 'Cannot start the fixed Android SDK executable.' }
        $started = $true
        $buffer = [byte[]]::new(8192)
        $errorBuffer = [byte[]]::new(4096)
        $stdoutDone = $false
        $stderrDone = $false
        $stdoutTask = $process.StandardOutput.BaseStream.ReadAsync($buffer, 0, $buffer.Length)
        $stderrTask = $process.StandardError.BaseStream.ReadAsync($errorBuffer, 0, $errorBuffer.Length)
        while (-not ($stdoutDone -and $stderrDone)) {
            $remaining = $TimeoutMs - [int]$timer.ElapsedMilliseconds
            if ($remaining -le 0) { throw 'Android command timed out.' }
            $pending = [Collections.Generic.List[Threading.Tasks.Task]]::new()
            if (-not $stdoutDone) { $pending.Add($stdoutTask) }
            if (-not $stderrDone) { $pending.Add($stderrTask) }
            if ([Threading.Tasks.Task]::WaitAny($pending.ToArray(), $remaining) -eq -1) { throw 'Android command timed out.' }
            if (-not $stdoutDone -and $stdoutTask.IsCompleted) {
                $count = $stdoutTask.GetAwaiter().GetResult()
                if ($count -eq 0) { $stdoutDone = $true }
                else {
                    if ($bytes.Length + $count -gt $MaxBytes) { throw 'Android command output exceeds its size limit.' }
                    $bytes.Write($buffer, 0, $count)
                    $stdoutTask = $process.StandardOutput.BaseStream.ReadAsync($buffer, 0, $buffer.Length)
                }
            }
            if (-not $stderrDone -and $stderrTask.IsCompleted) {
                $errorCount = $stderrTask.GetAwaiter().GetResult()
                if ($errorCount -eq 0) { $stderrDone = $true }
                else {
                    if ($errorBytes.Length + $errorCount -gt 65536) { throw 'Android command error output exceeds its size limit.' }
                    $errorBytes.Write($errorBuffer, 0, $errorCount)
                    $stderrTask = $process.StandardError.BaseStream.ReadAsync($errorBuffer, 0, $errorBuffer.Length)
                }
            }
        }
        $remaining = [Math]::Max(1, $TimeoutMs - [int]$timer.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remaining)) { throw 'Android command timed out.' }
        $stderr = [Text.Encoding]::UTF8.GetString($errorBytes.ToArray())
        if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
            throw "Android command failed ($($process.ExitCode)): $($stderr.Substring(0, [Math]::Min(512, $stderr.Length)))"
        }
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = if ($Binary) { '' } else { [Text.Encoding]::UTF8.GetString($bytes.ToArray()) }
            Data = if ($Binary) { $bytes.ToArray() } else { $null }
            Error = $stderr
        }
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit(2000) | Out-Null }
        $bytes.Dispose()
        $errorBytes.Dispose()
        $process.Dispose()
    }
}

function Assert-PhonePng([byte[]]$Data) {
    if (-not ('DeepSeekAndroidPhonePng' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
public static class DeepSeekAndroidPhonePng {
    private static readonly uint[] Table = MakeTable();
    private static uint[] MakeTable() {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320U ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    public static uint Crc(byte[] data, int offset, int length) {
        uint c = 0xffffffffU;
        for (int i = offset; i < offset + length; i++) c = Table[(c ^ data[i]) & 255] ^ (c >> 8);
        return c ^ 0xffffffffU;
    }
    private static uint Read(byte[] b, int p) { return ((uint)b[p] << 24) | ((uint)b[p+1] << 16) | ((uint)b[p+2] << 8) | b[p+3]; }
    public static void Validate(byte[] data) {
        byte[] signature = {137,80,78,71,13,10,26,10};
        if (data == null || data.Length < 57 || data.Length > 8388608) throw new FormatException("PNG length is invalid.");
        for (int i = 0; i < 8; i++) if (data[i] != signature[i]) throw new FormatException("PNG signature is invalid.");
        int p = 8, chunks = 0; bool idat = false, ended = false;
        byte bitDepth = 0, colorType = 0, interlace = 0;
        using var compressed = new System.IO.MemoryStream();
        while (p < data.Length) {
            if (++chunks > 4096 || data.Length - p < 12) throw new FormatException("PNG chunk sequence is invalid.");
            uint rawLength = Read(data, p);
            if (rawLength > data.Length - p - 12) throw new FormatException("PNG chunk is truncated.");
            int length = (int)rawLength;
            for (int i = p + 4; i < p + 8; i++) if (!((data[i] >= 65 && data[i] <= 90) || (data[i] >= 97 && data[i] <= 122))) throw new FormatException("PNG chunk type is invalid.");
            string type = System.Text.Encoding.ASCII.GetString(data, p + 4, 4);
            if (Crc(data, p + 4, length + 4) != Read(data, p + 8 + length)) throw new FormatException("PNG chunk CRC is invalid.");
            if (chunks == 1 && (type != "IHDR" || length != 13)) throw new FormatException("PNG must start with IHDR.");
            if (type == "IHDR") {
                if (chunks != 1 || length != 13 || Read(data,p+8) != 720 || Read(data,p+12) != 1280) throw new FormatException("PNG dimensions are not 720x1280.");
                byte depth = data[p+16], color = data[p+17];
                bool depthOk = color == 0 ? (depth==1||depth==2||depth==4||depth==8||depth==16) : color == 3 ? (depth==1||depth==2||depth==4||depth==8) : (color==2||color==4||color==6) && (depth==8||depth==16);
                if (!depthOk || data[p+18] != 0 || data[p+19] != 0 || data[p+20] > 1) throw new FormatException("PNG format is invalid.");
                bitDepth = depth; colorType = color; interlace = data[p+20];
            } else if (type == "IDAT") { if (length > 0) idat = true; compressed.Write(data,p+8,length); }
            else if (type == "IEND") {
                if (length != 0 || !idat || p + 12 != data.Length) throw new FormatException("PNG end or image data is invalid.");
                ended = true;
            } else if (type != "PLTE" && data[p+4] >= 65 && data[p+4] <= 90) throw new FormatException("PNG critical chunk is unsupported.");
            p += length + 12;
        }
        if (!ended) throw new FormatException("PNG has no complete IEND.");
        // Validate the zlib stream without trusting tolerant image decoders or inflating an unbounded payload.
        compressed.Position = 0;
        using var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress, true);
        int channels = colorType == 0 || colorType == 3 ? 1 : colorType == 2 ? 3 : colorType == 4 ? 2 : 4;
        int[] startX = interlace == 0 ? new[] {0} : new[] {0,4,0,2,0,1,0};
        int[] startY = interlace == 0 ? new[] {0} : new[] {0,0,4,0,2,0,1};
        int[] stepX = interlace == 0 ? new[] {1} : new[] {8,8,4,4,2,2,1};
        int[] stepY = interlace == 0 ? new[] {1} : new[] {8,8,8,4,4,2,2};
        var buffer = new byte[8192];
        for (int pass = 0; pass < startX.Length; pass++) {
            int width = (720 - startX[pass] + stepX[pass] - 1) / stepX[pass];
            int height = (1280 - startY[pass] + stepY[pass] - 1) / stepY[pass];
            int rowBytes = (width * channels * bitDepth + 7) / 8;
            for (int row = 0; row < height; row++) {
                int filter = zlib.ReadByte();
                if (filter < 0 || filter > 4) throw new FormatException("PNG scanline filter is invalid or truncated.");
                int remaining = rowBytes;
                while (remaining > 0) {
                    int count = zlib.Read(buffer,0,Math.Min(buffer.Length,remaining));
                    if (count == 0) throw new FormatException("PNG scanline data is truncated.");
                    remaining -= count;
                }
            }
        }
        if (zlib.ReadByte() != -1) throw new FormatException("PNG decompressed data exceeds its fixed dimensions.");
    }
}
'@ | Out-Null
    }
    [DeepSeekAndroidPhonePng]::Validate($Data)
    Add-Type -AssemblyName System.Drawing
    $stream = [IO.MemoryStream]::new($Data, $false)
    $image = $null
    $bitmap = $null
    try {
        $image = [Drawing.Image]::FromStream($stream, $false, $true)
        if ($image.Width -ne 720 -or $image.Height -ne 1280) { throw 'Decoded PNG dimensions are unexpected.' }
        $bitmap = [Drawing.Bitmap]::new($image)
        $bitmap.GetPixel(719, 1279) | Out-Null
    } finally {
        if ($bitmap) { $bitmap.Dispose() }
        if ($image) { $image.Dispose() }
        $stream.Dispose()
    }
}

function Invoke-PhoneAdb {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Binary, [switch]$AllowFailure, [int]$TimeoutMs = 12000)
    $paths = Get-PhonePaths
    if (-not (Test-Path -LiteralPath $paths.Adb -PathType Leaf)) { throw 'Android SDK is not installed in this project.' }
    Invoke-PhoneNative -Executable $paths.Adb -Arguments (@('-H', '127.0.0.1', '-P', '5038') + $Arguments) -Binary:$Binary -AllowFailure:$AllowFailure -TimeoutMs $TimeoutMs -MaxBytes $(if ($Binary) { 8388608 } else { 65536 })
}

function Assert-PhoneIdentity {
    param([int]$TimeoutMs = 12000)
    $listing = (Invoke-PhoneAdb -Arguments @('devices') -TimeoutMs $TimeoutMs).Output
    $devices = @($listing -split '\r?\n' | Where-Object { $_.Trim().Length -gt 0 -and $_.Trim() -cne 'List of devices attached' })
    if ($devices.Count -ne 1 -or $devices[0] -notmatch '^emulator-5560\s+device\s*$') {
        throw 'Refusing device access: dedicated ADB server must contain only emulator-5560 in device state.'
    }
    $identity = (Invoke-PhoneAdb -Arguments @('-s', $script:PhoneSerial, 'emu', 'avd', 'name') -TimeoutMs $TimeoutMs).Output
    $lines = @($identity -split '\r?\n' | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -ne 2 -or $lines[0].Trim() -cne $script:PhoneAvd -or $lines[1].Trim() -cne 'OK') {
        throw 'Refusing device access: emulator-5560 is not the dedicated deepseek-phone AVD.'
    }
}

function Restore-PhoneUnroot {
    param([ValidateRange(50, 30000)][int]$TimeoutMs = 15000)
    $paths = Get-PhonePaths
    $confirmed = $false
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        # A timeout is not proof that the unroot request failed; confirm the
        # actual identity/UID after adbd reconnects, then withhold control if unknown.
        try { Invoke-PhoneAdb -Arguments @('-s', $script:PhoneSerial, 'unroot') -TimeoutMs ([Math]::Min(5000, $TimeoutMs)) | Out-Null }
        catch { Write-Verbose $_.Exception.Message }
        do {
            try {
                $attemptTimeout = [Math]::Max(50, [Math]::Min(2000, $TimeoutMs - [int]$timer.ElapsedMilliseconds))
                Assert-PhoneIdentity -TimeoutMs $attemptTimeout
                $uid = (Invoke-PhoneAdb -Arguments @('-s', $script:PhoneSerial, 'shell', 'id', '-u') -TimeoutMs $attemptTimeout).Output.Trim()
                if ($uid -match '^\d+$' -and [long]$uid -gt 0) { $confirmed = $true; return $uid }
            } catch { Write-Verbose $_.Exception.Message }
            if ($timer.ElapsedMilliseconds -lt $TimeoutMs) { Start-Sleep -Milliseconds ([Math]::Max(1, [Math]::Min(250, $TimeoutMs - [int]$timer.ElapsedMilliseconds))) }
        } while ($timer.ElapsedMilliseconds -lt $TimeoutMs)
        throw 'Cannot confirm that dedicated ADB returned to a non-root UID; phone control has been disabled.'
    } finally {
        if (-not $confirmed -and (Test-Path -LiteralPath $paths.ReadyFile)) { Remove-Item -LiteralPath $paths.ReadyFile -Force }
    }
}

function Assert-PhoneControlReady {
    $paths = Get-PhonePaths
    if (-not (Test-Path -LiteralPath $paths.ReadyFile)) { throw 'Phone has not completed the managed boot and guest policy steps.' }
    $receipt = Get-Content -LiteralPath $paths.ReadyFile -Raw | ConvertFrom-Json
    if ($receipt.avdName -cne $script:PhoneAvd -or $receipt.serial -cne $script:PhoneSerial -or $receipt.status -cne 'control-ready') {
        throw 'Phone control receipt is invalid.'
    }
    Assert-PhoneIdentity
    try {
        $uid = (Invoke-PhoneAdb -Arguments @('-s', $script:PhoneSerial, 'shell', 'id', '-u')).Output.Trim()
        if ($uid -notmatch '^\d+$' -or [long]$uid -eq 0) { throw 'ADB UID is root or unknown.' }
    } catch {
        Remove-Item -LiteralPath $paths.ReadyFile -Force
        throw 'Dedicated ADB is root or its UID is unknown; phone control has been disabled.'
    }
    $bootId = (Invoke-PhoneAdb -Arguments @('-s', $script:PhoneSerial, 'shell', 'cat', '/proc/sys/kernel/random/boot_id')).Output.Trim()
    if ($bootId -cne $receipt.bootId) { throw 'Phone rebooted; guest network policy must be applied again before control.' }
}

function Invoke-AndroidPhoneAction {
    param(
        [Parameter(Mandatory)][ValidateSet('Screenshot', 'Tap', 'Swipe', 'Text', 'Back', 'Home')][string]$Action,
        [int]$X = 0, [int]$Y = 0, [int]$EndX = 0, [int]$EndY = 0,
        [int]$DurationMs = 300, [string]$Text = ''
    )
    if ($Action -in @('Tap', 'Swipe')) {
        if ($X -lt 0 -or $X -ge 720 -or $Y -lt 0 -or $Y -ge 1280) { throw 'Touch coordinates must be within 720x1280.' }
    }
    if ($Action -eq 'Swipe') {
        if ($EndX -lt 0 -or $EndX -ge 720 -or $EndY -lt 0 -or $EndY -ge 1280 -or $DurationMs -lt 50 -or $DurationMs -gt 2000) {
            throw 'Swipe must stay within 720x1280 and last between 50 and 2000 ms.'
        }
    }
    if ($Action -eq 'Text' -and ($Text.Length -lt 1 -or $Text.Length -gt 160 -or $Text -cnotmatch '^[A-Za-z0-9 .,@_:/+\-]+$')) {
        throw 'Text currently supports 1-160 ASCII letters/digits and space . , @ _ : / + - only; Chinese/other symbols require a future guest-only input method.'
    }
    Assert-PhoneControlReady
    $screen = (Invoke-PhoneAdb -Arguments @('-s', $script:PhoneSerial, 'shell', 'wm', 'size')).Output.Trim()
    if ($screen -notmatch '(?:Physical|Override) size: 720x1280' -or $screen -match 'Override size: (?!720x1280)') {
        throw 'Unexpected phone display size; refusing coordinates until the operator reviews the device.'
    }
    $prefix = @('-s', $script:PhoneSerial)
    switch ($Action) {
        'Screenshot' {
            $result = Invoke-PhoneAdb -Arguments ($prefix + @('exec-out', 'screencap', '-p')) -Binary
            $png = $result.Data
            Assert-PhonePng $png
            $paths = Get-PhonePaths
            New-Item -ItemType Directory -Path $paths.Captures -Force | Out-Null
            $fileName = '{0}-{1}.png' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [Guid]::NewGuid().ToString('N')
            $destination = Join-Path $paths.Captures $fileName
            [IO.File]::WriteAllBytes($destination, $png)
            # Keep at most 30 captures generated by this tool; never enumerate/delete other files.
            $owned = @(Get-ChildItem -LiteralPath $paths.Captures -File | Where-Object { $_.Name -match '^\d{8}T\d{9}Z-[a-f0-9]{32}\.png$' } | Sort-Object Name -Descending)
            $owned | Select-Object -Skip 30 | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
            return [pscustomobject]@{ action = $Action; serial = $script:PhoneSerial; imagePath = $destination; width = 720; height = 1280 }
        }
        'Tap' { $arguments = @('shell', 'input', 'tap', "$X", "$Y") }
        'Swipe' { $arguments = @('shell', 'input', 'swipe', "$X", "$Y", "$EndX", "$EndY", "$DurationMs") }
        'Text' { $arguments = @('shell', 'input', 'text', $Text.Replace(' ', '%s')) }
        'Back' { $arguments = @('shell', 'input', 'keyevent', '4') }
        'Home' { $arguments = @('shell', 'input', 'keyevent', '3') }
    }
    Invoke-PhoneAdb -Arguments ($prefix + $arguments) | Out-Null
    [pscustomobject]@{ action = $Action; serial = $script:PhoneSerial; sent = $true }
}

Export-ModuleMember -Function Get-PhonePaths, Set-PhoneProcessEnvironment, Invoke-PhoneNative, Invoke-PhoneAdb, Assert-PhoneIdentity, Restore-PhoneUnroot, Invoke-AndroidPhoneAction
