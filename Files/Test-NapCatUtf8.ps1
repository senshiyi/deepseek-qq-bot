[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path $project ('Cache\napcat-utf8-checks\'+[Guid]::NewGuid().ToString('N'))
$configPath=Join-Path $testRoot 'group-chat.local.json'
$beforeReal=(Get-FileHash -LiteralPath (Join-Path $project 'group-chat.local.json') -Algorithm SHA256).Hash
try{
    [void][IO.Directory]::CreateDirectory((Join-Path $testRoot 'Cache\NapCat\v4.18.38'))
    [IO.File]::WriteAllText((Join-Path $testRoot 'Cache\NapCat\v4.18.38\napcat.bat'),'@exit /b 99')
    $fixture=[pscustomobject]@{oneBotBaseUrl='http://127.0.0.1:3000/';eventPort=17801;selfId=1667533356;allowedGroupIds=@(1095601728);botNames=@('小深','机器人','乖乖');mode='active';engine='school';memeDirectory='Files/Memes'}
    [IO.File]::WriteAllText($configPath,($fixture|ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $testRoot '.env'),'ONEBOT_ACCESS_TOKEN=isolated-utf8-test-token')
    $script=Join-Path $PSScriptRoot 'Start-NapCat.ps1'
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoLogo -NoProfile -ExecutionPolicy Bypass -File $script -PrepareOnly -ProjectRoot $testRoot -Qq 1667533356
    if($LASTEXITCODE -ne 0){throw 'Windows PowerShell 5.1 PrepareOnly failed.'}
    $actual=Get-Content -LiteralPath $configPath -Raw -Encoding UTF8|ConvertFrom-Json
    if(($actual.botNames -join '|') -cne '小深|机器人|乖乖'){throw 'Chinese bot names changed.'}
    $configDir=Join-Path $testRoot 'Results\local\napcat\config'
    $webPath=Join-Path $configDir 'webui.json'
    $web=Get-Content -LiteralPath $webPath -Raw -Encoding UTF8|ConvertFrom-Json
    $web|Add-Member -NotePropertyName utf8Test -NotePropertyValue '保留中文设置' -Force
    [IO.File]::WriteAllText($webPath,($web|ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoLogo -NoProfile -ExecutionPolicy Bypass -File $script -PrepareOnly -ProjectRoot $testRoot -Qq 1667533356
    if($LASTEXITCODE -ne 0){throw 'Second UTF8 prepare failed.'}
    $web=Get-Content -LiteralPath $webPath -Raw -Encoding UTF8|ConvertFrom-Json
    if($web.utf8Test -cne '保留中文设置'){throw 'Existing UTF8 WebUI property changed.'}
    $afterReal=(Get-FileHash -LiteralPath (Join-Path $project 'group-chat.local.json') -Algorithm SHA256).Hash
    if($beforeReal -ne $afterReal){throw 'Real configuration changed during isolated check.'}
    Write-Output 'PASS: Windows PowerShell 5.1 reads BOM-less UTF8 bot names and preserves UTF8 WebUI settings; PrepareOnly starts no client; real configuration unchanged.'
}finally{
    $resolved=[IO.Path]::GetFullPath($testRoot)
    $expected=[IO.Path]::GetFullPath((Join-Path $project 'Cache\napcat-utf8-checks'))+[IO.Path]::DirectorySeparatorChar
    if($resolved.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
