[CmdletBinding()]
param([string]$ProjectRoot)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($ProjectRoot)){$ProjectRoot=Split-Path -Parent $PSScriptRoot}
$ProjectRoot=(Resolve-Path -LiteralPath $ProjectRoot).Path
function Read-JsonFile($Path){
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf)){return $null}
    try{return [IO.File]::ReadAllText($Path,[Text.Encoding]::UTF8)|ConvertFrom-Json}
    catch{throw ('无法读取 '+$Path+'：'+$_.Exception.Message)}
}
$voice=Read-JsonFile (Join-Path $ProjectRoot 'voice.local.json')
$enabled=$true
if($null -ne $voice -and $null -ne $voice.PSObject.Properties['enabled']){$enabled=[bool]$voice.enabled}
if(-not $enabled){Write-Host '语音未启用，跳过 Breeze 启动。';return}
$provider=''
if($null -ne $voice -and $null -ne $voice.PSObject.Properties['ttsProvider']){$provider=([string]$voice.ttsProvider).Trim().ToLowerInvariant()}
if($provider -ne 'breeze'){Write-Host '语音引擎不是 breeze，跳过 Breeze 启动。';return}
$baseText='http://127.0.0.1:8080'
if($null -ne $voice -and -not [string]::IsNullOrWhiteSpace([string]$voice.breezeBaseUrl)){$baseText=[string]$voice.breezeBaseUrl}
$base=$null
if(-not [Uri]::TryCreate($baseText,[UriKind]::Absolute,[ref]$base)){throw 'voice.local.json 的 breezeBaseUrl 不是有效地址，未启动 Breeze。'}
if($base.Scheme -ne 'http' -or $base.Host -notin @('127.0.0.1','localhost')){Write-Host 'Breeze 地址不是本机 http，跳过 Breeze 启动。';return}
$port=$base.Port
$health=$base.AbsoluteUri.TrimEnd('/')+'/health'
function Test-BreezeHealth{
    try{
        $request=[Net.HttpWebRequest]::Create($health)
        $request.Proxy=$null
        $request.Timeout=1500
        $request.ReadWriteTimeout=1500
        $response=$request.GetResponse()
        $body=$null
        try{$reader=New-Object IO.StreamReader($response.GetResponseStream(),[Text.Encoding]::UTF8);$body=$reader.ReadToEnd();$reader.Dispose()}
        finally{$response.Dispose()}
        return $body -match '"status"\s*:\s*"ok"'
    }catch{return $false}
}
if(Test-BreezeHealth){Write-Host ('Breeze 已在运行（端口 '+$port+'）。');return}
$config=Read-JsonFile (Join-Path $ProjectRoot 'Files\breeze.local.json')
if($null -eq $config){throw '缺少 Files/breeze.local.json（需要 installDirectory 与 modelFile）。'}
$install=([string]$config.installDirectory).Trim()
if([string]::IsNullOrWhiteSpace($install)){throw 'Files/breeze.local.json 缺少 installDirectory。'}
$install=[IO.Path]::GetFullPath($install)
if(-not (Test-Path -LiteralPath $install -PathType Container)){throw ('Breeze 安装目录不存在：'+$install)}
$serverName='breeze-server.exe'
if($null -ne $config.PSObject.Properties['serverExecutable'] -and -not [string]::IsNullOrWhiteSpace([string]$config.serverExecutable)){$serverName=[string]$config.serverExecutable}
$server=Join-Path $install $serverName
if(-not (Test-Path -LiteralPath $server -PathType Leaf)){throw ('找不到 Breeze 服务程序：'+$server)}
$modelName=([string]$config.modelFile).Trim()
if([string]::IsNullOrWhiteSpace($modelName)){throw 'Files/breeze.local.json 缺少 modelFile。'}
$model=Join-Path $install $modelName
if(-not (Test-Path -LiteralPath $model -PathType Leaf)){throw ('找不到 Breeze 模型：'+$model)}
$voicesText='Results\local\breeze-voices'
if($null -ne $config.PSObject.Properties['voicesDirectory'] -and -not [string]::IsNullOrWhiteSpace([string]$config.voicesDirectory)){$voicesText=[string]$config.voicesDirectory}
if(-not [IO.Path]::IsPathRooted($voicesText)){$voicesText=Join-Path $ProjectRoot $voicesText}
$voices=[IO.Path]::GetFullPath($voicesText)
[void][IO.Directory]::CreateDirectory($voices)
$local=Join-Path $ProjectRoot 'Results\local'
[void][IO.Directory]::CreateDirectory($local)
$outLog=Join-Path $local 'breeze-server.out.log'
$errLog=Join-Path $local 'breeze-server.err.log'
$arguments='"' + $model + '" --host 127.0.0.1 --port ' + $port + ' --voices-dir "' + $voices + '"'
Write-Host ('正在启动 Breeze：'+$server+'（端口 '+$port+'，音色目录 '+$voices+'）。')
$startedId=0
try{
    $process=Start-Process -FilePath $server -WorkingDirectory $install -WindowStyle Hidden -ArgumentList $arguments -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru
    $startedId=$process.Id
}catch{
    # 某些环境里环境变量大小写重复会让 Start-Process 直接报错；退回 .NET 启动，服务照常独立运行（只是不重定向日志）。
    $psi=New-Object Diagnostics.ProcessStartInfo
    $psi.FileName=$server
    $psi.WorkingDirectory=$install
    $psi.Arguments=$arguments
    $psi.UseShellExecute=$false
    $psi.CreateNoWindow=$true
    $process=[Diagnostics.Process]::Start($psi)
    $startedId=$process.Id
    Write-Host ('Start-Process 不可用（'+$_.Exception.Message+'），已改用 .NET 启动，本次不重定向日志。')
}
Write-Host ('Breeze 已启动，进程ID：'+$startedId+'；模型加载约半分钟，期间语音回退微软 TTS。日志：'+$outLog)
