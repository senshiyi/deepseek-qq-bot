[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path -LiteralPath $ProjectRoot).Path
$modelName = 'sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23'
$revision = '204ad334e2e683fd295359930cc16fc0432a23ac'
$repository = 'https://huggingface.co/csukuangfj/' + $modelName
$modelPath = Join-Path $projectPath ('Models/' + $modelName)
$cachePath = Join-Path $projectPath ('Cache/ModelDownloads/' + $modelName + '/' + $revision)
$manifestPath = Join-Path $modelPath 'model.manifest.json'
$files = @(
    @{ name = 'encoder-epoch-99-avg-1.int8.onnx'; size = 21621684; sha256 = '1c556ea57cec304e55ec4b72e52c1cc098bb01476ed7d90f3de939fe126487b1' },
    @{ name = 'decoder-epoch-99-avg-1.onnx'; size = 7509745; sha256 = '5ee0f03a2768ff1d5c83ef3a493243c7935d316cd41280037b14783a3467cc78' },
    @{ name = 'joiner-epoch-99-avg-1.int8.onnx'; size = 1795562; sha256 = 'a7cf9d82757bdcf786059454495a9ca95e4bd7347f72473fc08d794475c36169' },
    @{ name = 'tokens.txt'; size = 48697; sha256 = '8b294db9045d6e5f94647f4c1eec1af4da143a75053c399611444b378ff966ac' }
)

function Assert-ModelFile([string]$Path, $Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw ('文件不存在：' + $Path) }
    if ((Get-Item -LiteralPath $Path).Length -ne $Expected.size) { throw ('文件大小不符，保留原文件，不覆盖：' + $Path) }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.sha256) { throw ('SHA256 不符，保留原文件，不覆盖：' + $Path) }
}

if (Test-Path -LiteralPath $manifestPath) {
    try { $existingManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw ('已有模型清单无法解析，保留原文件，不覆盖：' + $manifestPath) }
    if ($existingManifest.model -ne $modelName -or $existingManifest.revision -ne $revision) {
        throw ('已有模型清单与固定版本不同，保留原文件，不覆盖：' + $manifestPath)
    }
}

# Check every existing target before downloading or adding anything to it.
foreach ($file in $files) {
    $target = Join-Path $modelPath $file.name
    if (Test-Path -LiteralPath $target) { Assert-ModelFile $target $file }
    elseif ($VerifyOnly) { throw ('模型文件缺失：' + $target) }
}

if (-not $VerifyOnly) {
    New-Item -ItemType Directory -Path $modelPath -Force | Out-Null
    New-Item -ItemType Directory -Path $cachePath -Force | Out-Null
    foreach ($file in $files) {
        $target = Join-Path $modelPath $file.name
        if (Test-Path -LiteralPath $target) { continue }
        $cached = Join-Path $cachePath $file.name
        if (Test-Path -LiteralPath $cached) {
            Assert-ModelFile $cached $file
        } else {
            $partial = Join-Path $cachePath ($file.name + '.' + [guid]::NewGuid().ToString('N') + '.partial')
            try {
                Write-Host ('下载 ' + $file.name + '，固定模型版本 ' + $revision)
                Invoke-WebRequest -UseBasicParsing -Uri ($repository + '/resolve/' + $revision + '/' + $file.name) -OutFile $partial
                Assert-ModelFile $partial $file
                Move-Item -LiteralPath $partial -Destination $cached
            } finally {
                if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
            }
        }
        # Copy without -Force: never replace a file created while this script runs.
        [System.IO.File]::Copy($cached, $target, $false)
        Assert-ModelFile $target $file
    }

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        $manifest = [ordered]@{
            model = $modelName
            revision = $revision
            repository = $repository
            license = 'Apache-2.0'
            licenseSource = $repository + '/blob/' + $revision + '/README.md'
            licenseText = 'https://www.apache.org/licenses/LICENSE-2.0'
            runtime = 'org.k2fsa.sherpa.onnx 1.13.8; CPU; Windows x64'
            hashSource = 'ONNX: maintainer Hugging Face LFS metadata; tokens: SHA256 of pinned revision bytes, independently rechecked on download'
            installedAtUtc = [DateTime]::UtcNow.ToString('o')
            files = @($files | ForEach-Object { [ordered]@{ name = $_.name; size = $_.size; sha256 = $_.sha256; url = $repository + '/resolve/' + $revision + '/' + $_.name } })
        }
        $json = ($manifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine
        $stream = [System.IO.File]::Open($manifestPath, [System.IO.FileMode]::CreateNew)
        try {
            $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
            $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
    }
}

foreach ($file in $files) { Assert-ModelFile (Join-Path $modelPath $file.name) $file }
Write-Host ('本地模型四个文件已通过 SHA256 校验：' + $modelPath)
Write-Host '运行时离线使用，不需要腾讯云账号、语音 API 或 CUDA。模型仅支持中文。'
