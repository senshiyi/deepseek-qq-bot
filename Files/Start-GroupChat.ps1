[CmdletBinding()]
param([string]$Group,[string]$ProjectRoot,[switch]$Background)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($ProjectRoot)){$ProjectRoot=Split-Path -Parent $PSScriptRoot}
$ProjectRoot=(Resolve-Path -LiteralPath $ProjectRoot).Path
$launchLock=$null
$locationPushed=$false
function Read-PositiveId($Value){
    $number=0L
    if(-not [long]::TryParse([string]$Value,[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$number) -or $number -le 0){throw '账号或群白名单包含无效编号。'}
    return $number.ToString([Globalization.CultureInfo]::InvariantCulture)
}
function Assert-EventPortFree([int]$Port){
    $listeners=[Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    if(@($listeners|Where-Object Port -eq $Port).Count -gt 0){throw "事件端口 $Port 已被占用，未结束任何进程；请先检查本项目管理面板或现有运行窗口，勿重复启动。"}
}
function Get-ExistingChatState {
    $accessPath=Join-Path $ProjectRoot 'Results\local\admin-access-token.txt'
    if(-not (Test-Path -LiteralPath $accessPath -PathType Leaf)){return $null}
    try{
        $access=[IO.File]::ReadAllText($accessPath).Trim()
        if($access -cnotmatch '^[a-fA-F0-9]{64}$'){return $null}
        $request=[Net.HttpWebRequest]::Create('http://127.0.0.1:17802/api/status')
        $request.Proxy=$null
        $request.Timeout=2000
        $request.ReadWriteTimeout=2000
        $request.Headers['Authorization']='Bearer '+$access
        $response=$null
        $reader=$null
        try{
            $response=$request.GetResponse()
            $reader=New-Object IO.StreamReader($response.GetResponseStream(),[Text.Encoding]::UTF8)
            $state=$reader.ReadToEnd()|ConvertFrom-Json
            if($null -ne $state.runtime){return $state}
        }finally{if($reader){$reader.Dispose()};if($response){$response.Dispose()}}
    }catch{return $null} # Never print an authentication value or response body.
    return $null
}
function Request-ExistingChatReload {
    try{
        $access=[IO.File]::ReadAllText((Join-Path $ProjectRoot 'Results\local\admin-access-token.txt')).Trim()
        if($access -cnotmatch '^[a-fA-F0-9]{64}$'){return $false}
        $request=[Net.HttpWebRequest]::Create('http://127.0.0.1:17802/api/control')
        $request.Proxy=$null
        $request.Timeout=5000
        $request.ReadWriteTimeout=5000
        $request.Method='POST'
        $request.ContentType='application/json'
        $request.Headers['Authorization']='Bearer '+$access
        $bytes=[Text.Encoding]::UTF8.GetBytes('{"action":"apply"}')
        $request.ContentLength=$bytes.Length
        $stream=$request.GetRequestStream()
        try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
        $response=$null
        $reader=$null
        try{
            $response=$request.GetResponse()
            $reader=New-Object IO.StreamReader($response.GetResponseStream(),[Text.Encoding]::UTF8)
            return ($reader.ReadToEnd()|ConvertFrom-Json).accepted -eq $true
        }finally{if($reader){$reader.Dispose()};if($response){$response.Dispose()}}
    }catch{return $false}
}
function Test-ConfiguredLogin([int]$TimeoutMilliseconds=6000){
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$bridgePath
    $start.Arguments='chat-check --chat-config "'+$chatPath+'"'
    $start.WorkingDirectory=$ProjectRoot
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $check=$null
    try{
        $check=[Diagnostics.Process]::Start($start)
        $stdout=$check.StandardOutput.ReadToEndAsync()
        $stderr=$check.StandardError.ReadToEndAsync()
        if(-not $check.WaitForExit([Math]::Max(1,$TimeoutMilliseconds))){$check.Kill();[void]$check.WaitForExit(1000);return $false}
        [void]$stderr.GetAwaiter().GetResult()
        if($check.ExitCode -ne 0){return $false}
        $report=$stdout.GetAwaiter().GetResult()|ConvertFrom-Json
        return ($report.command -eq 'chat-check' -and $report.online -eq $true -and $report.good -eq $true -and [string]$report.loggedInSelfId -ceq $selfId)
    }catch{return $false}
    finally{if($check){$check.Dispose()}}
}
try{
    try{$Host.UI.RawUI.WindowTitle='DeepSeek QQ - Group and Private Chat'}catch{}
    $chatPath=Join-Path $ProjectRoot 'group-chat.local.json'
    if(-not (Test-Path -LiteralPath $chatPath)){throw '缺少 group-chat.local.json；请先填写固定 selfId 与群白名单。'}
    $settings=Get-Content -LiteralPath $chatPath -Raw -Encoding UTF8|ConvertFrom-Json
    $selfId=Read-PositiveId $settings.selfId
    $engine=if($settings.engine){[string]$settings.engine}else{'deepseek'}
    if($engine -notin @('deepseek','school')){throw '群聊 engine 必须为 deepseek 或 school。'}
    $mode=if($settings.mode){[string]$settings.mode}else{'mention'}
    if($mode -notin @('mention','active','manual')){throw '群聊 mode 必须为 mention、active 或 manual。'}
    if($selfId -notmatch '^[1-9][0-9]{4,14}$'){throw '请在群配置中填写固定的有效 selfId，本入口不猜测账号。'}
    $allowedGroups=@($settings.allowedGroupIds|ForEach-Object{Read-PositiveId $_})
    if($allowedGroups.Count -lt 1 -or $allowedGroups.Count -gt 8 -or @($allowedGroups|Select-Object -Unique).Count -ne $allowedGroups.Count){throw '请配置 1～8 个不重复的授权群号。'}
    if([string]::IsNullOrWhiteSpace($Group)){
        if($settings.activeGroupId -and [long]$settings.activeGroupId -ne 0){$Group=Read-PositiveId $settings.activeGroupId}
        elseif($allowedGroups.Count -eq 1){$Group=$allowedGroups[0]}
        else{throw '白名单包含多个群，请在面板选择当前群或用 -Group 明确选择。'}
    }else{$Group=Read-PositiveId $Group}
    if($Group -notin $allowedGroups){throw '-Group 不在配置白名单中，未启动。'}
    $eventPort=17801
    if($null -ne $settings.eventPort){$eventPort=[int]$settings.eventPort}
    if($eventPort -lt 1024 -or $eventPort -gt 65535){throw '群事件端口必须为 1024～65535。'}
    $projectPath=Join-Path $ProjectRoot 'Files\AudioBridge\AudioBridge.csproj'
    $framework=([xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)).Project.PropertyGroup.TargetFramework
    $bridgePath=Join-Path $ProjectRoot ('Files\AudioBridge\bin\Release\'+$framework+'\AudioBridge.exe')
    $existing=Get-ExistingChatState
    if($null -ne $existing){
        if($existing.runtime.running){
            $pauseLabel=if($existing.runtime.paused){'（当前已暂停）'}else{''}
            Write-Host ('本项目消息监听宿主已在后台运行'+$pauseLabel+'，无需重复启动。当前群：'+$existing.runtime.groupId)
            if(Test-ConfiguredLogin){Write-Host 'QQ在线状态及NapCat账号检查通过；实际收发情况请查看面板最近事件和QQ回复。'}
            else{Write-Host 'NapCat账号接口当前不可用，消息宿主存活不代表能收发。请启动本项目NapCat并完成登录；现有宿主保留，不重复启动。' -ForegroundColor Yellow}
        }else{
            if((Test-ConfiguredLogin) -and (Request-ExistingChatReload)){Write-Host '管理宿主仍在，NapCat账号接口已恢复，已请求重新装载消息会话；请在面板核对完成状态。'}
            else{Write-Host '本项目管理宿主已运行，消息会话当前未就绪；请在面板核对状态和错误后应用设置。'}
        }
        Write-Host '正在打开管理面板。关闭网页不会停止机器人；如需退出后台服务，使用面板“停止机器人”。'
        & (Join-Path $PSScriptRoot 'Open-Panel.ps1') -ProjectRoot $ProjectRoot
        exit 0
    }
    Assert-EventPortFree $eventPort
    $local=Join-Path $ProjectRoot 'Results\local'
    [void][IO.Directory]::CreateDirectory($local)
    try{$launchLock=New-Object IO.FileStream((Join-Path $local 'group-chat-launcher.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
    catch{throw '本项目的群聊启动入口已打开，或启动锁无法取得；请先查看现有窗口。'}
    Push-Location -LiteralPath $ProjectRoot
    $locationPushed=$true
    Write-Host '正在编译并检查现有群消息入口……'
    & (Join-Path $ProjectRoot 'start.cmd') help | Out-Null
    if($LASTEXITCODE -ne 0){throw '现有 start.cmd 编译检查失败，未启动群聊。'}
    $projectPath=Join-Path $ProjectRoot 'Files\AudioBridge\AudioBridge.csproj'
    $framework=([xml](Get-Content -LiteralPath $projectPath -Raw)).Project.PropertyGroup.TargetFramework
    $bridgePath=Join-Path $ProjectRoot ('Files\AudioBridge\bin\Release\'+$framework+'\AudioBridge.exe')
    if(-not (Test-Path -LiteralPath $bridgePath)){throw '编译后未找到 AudioBridge.exe，未启动群聊。'}
    Write-Host 'NapCat 必须在线并登录配置账号。最长等待 180 秒；扫码或其他验证请在 NapCat 窗口手动完成。'
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(180)
    $loginStarted=Get-Date
    $qrOpened=$false
    $ready=Test-ConfiguredLogin
    if(-not $ready){
        $nodePath=[IO.Path]::GetFullPath((Join-Path $ProjectRoot 'Cache\NapCat\v4.18.38\node.exe'))
        $nodes=@(Get-CimInstance -ClassName Win32_Process -Filter "Name='node.exe'" -Property ExecutablePath -ErrorAction Stop)
        $running=@($nodes|Where-Object{$_.ExecutablePath -and [IO.Path]::GetFullPath([string]$_.ExecutablePath).Equals($nodePath,[StringComparison]::OrdinalIgnoreCase)})
        if($running.Count -eq 0){
            $napcatScript=Join-Path $ProjectRoot 'Files\Start-NapCat.ps1'
            if(-not (Test-Path -LiteralPath $napcatScript)){throw '缺少 Files/Start-NapCat.ps1，请先准备 NapCat。'}
            $command='$Host.UI.RawUI.WindowTitle = ''DeepSeek QQ - NapCat Login''; & '''+$napcatScript.Replace("'","''")+''' -ProjectRoot '''+$ProjectRoot.Replace("'","''")+''' -Qq '''+$selfId+''' -LoginFallback'
            $napcatWindow=if($Background){'Hidden'}else{'Normal'}
            Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -WorkingDirectory $ProjectRoot -WindowStyle $napcatWindow -ArgumentList @('-NoLogo','-NoProfile','-NoExit','-ExecutionPolicy','Bypass','-Command',$command)|Out-Null
            if($Background){Write-Host '已在后台启动NapCat；如果登录需扫码，会打开本次新生成的二维码图片。'}
            else{Write-Host '已打开专用 NapCat 登录窗口，请在该窗口查看登录状态。'}
        }else{Write-Host '本项目 NapCat 已在运行，保留现有实例，等待 API 与账号检查通过。'}
        while(-not $ready -and [DateTimeOffset]::UtcNow -lt $deadline){
            $remaining=[int]($deadline-[DateTimeOffset]::UtcNow).TotalMilliseconds
            $ready=Test-ConfiguredLogin ([Math]::Min(6000,[Math]::Max(1,$remaining)))
            if(-not $ready){
                $qrPath=Join-Path $ProjectRoot 'Results\local\napcat\cache\qrcode.png'
                if($Background -and -not $qrOpened -and (Test-Path -LiteralPath $qrPath -PathType Leaf) -and (Get-Item -LiteralPath $qrPath).LastWriteTime -ge $loginStarted){
                    Start-Process -FilePath $qrPath | Out-Null
                    $qrOpened=$true
                    Write-Host '请用选定机器人QQ扫描已打开的二维码并在手机确认；二维码不会输出到聊天或本脚本日志。'
                }
                $remaining=[int]($deadline-[DateTimeOffset]::UtcNow).TotalMilliseconds
                if($remaining -gt 0){Start-Sleep -Milliseconds ([Math]::Min(2000,$remaining))}
            }
        }
    }
    if(-not $ready){throw '180 秒内未取得配置账号的有效 OneBot 登录信息；请检查 NapCat 登录窗口后重新运行本入口。现有 NapCat 未被重启。'}
    Assert-EventPortFree $eventPort
    Write-Host "账号检查通过，开始持续消息服务。目标群：$Group；私聊仅处理配置白名单，没有自动时限。"
    Write-Host "P 暂停/恢复，X 暂停并取消当前回复，S 查看状态，Q 退出，统一控制群聊和私聊。当前引擎：$engine；累计上限以本地配置为准，不启动语音。"
    if($Background){
        $logId=[DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff')
        $outLog=Join-Path $local ('chat-background-'+$logId+'.out.log')
        $errLog=Join-Path $local ('chat-background-'+$logId+'.err.log')
        $arguments='chat --chat-config "'+$chatPath+'" --group '+$Group+' --mode '+$mode+' --engine '+$engine+' --live true --seconds 0'
        $backgroundProcess=Start-Process -FilePath $bridgePath -WorkingDirectory $ProjectRoot -WindowStyle Hidden -ArgumentList $arguments -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru
        $started=$false
        $startupDeadline=[DateTimeOffset]::UtcNow.AddSeconds(12)
        do{
            Start-Sleep -Milliseconds 250
            $state=Get-ExistingChatState
            if($null -ne $state -and $state.runtime.running){$started=$true;break}
            if($backgroundProcess.HasExited){break}
        }while([DateTimeOffset]::UtcNow -lt $startupDeadline)
        if(-not $started){throw '后台进程未在期限内报告监听就绪；未结束它。请查看管理面板及Results/local/chat-background日志，勿重复启动。'}
        Write-Host ('后台消息宿主已开始监听，进程ID：'+$backgroundProcess.Id+'。关闭本窗口和网页后仍运行；电脑需保持开机。')
        Write-Host '暂停、状态和停止请使用open-panel.cmd；没有设置开机自启或更改睡眠策略。'
        exit 0
    }
    & $bridgePath chat --chat-config $chatPath --group $Group --mode $mode --engine $engine --live true --seconds 0
    exit $LASTEXITCODE
}catch{
    Write-Host ('群聊未运行或已停止：'+$_.Exception.Message) -ForegroundColor Red
    exit 1
}finally{
    if($locationPushed){Pop-Location}
    if($launchLock){$launchLock.Dispose()}
}
