[CmdletBinding()]
param([string]$ProjectRoot)
$ErrorActionPreference = 'Stop'
if([string]::IsNullOrWhiteSpace($ProjectRoot)){$ProjectRoot=Split-Path -Parent $PSScriptRoot}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$runtime = Join-Path $ProjectRoot 'Cache\NapCat\v4.18.38'
$originalPath = Join-Path $runtime 'napcat\napcat.mjs'
$indexPath = Join-Path $runtime 'index.js'
$sidecarPath = Join-Path $runtime 'napcat\napcat.login-fallback.mjs'
$entryPath = Join-Path $runtime 'index-login-fallback.cjs'
$expectedSourceHash = 'F80E4BE3E8640369D23846CF3E8BCFD2A5B6DC5920F50D7AD733D7CCA18179E6'
$expectedIndexHash = '5DE31BEF6CC1B48CEF0229A8B22B32B3211E41253C3E75A9547E996BECF9B388'
function Get-LocalSha256([string]$Path){
    $stream=[IO.File]::OpenRead($Path)
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')}
    finally{$algorithm.Dispose();$stream.Dispose()}
}
if(-not (Test-Path -LiteralPath $originalPath) -or -not (Test-Path -LiteralPath $indexPath)){
    throw '缺少固定版本的官方NapCat源码或index.js；未生成fallback，未启动。'
}
if((Get-LocalSha256 $originalPath) -cne $expectedSourceHash -or
   (Get-LocalSha256 $indexPath) -cne $expectedIndexHash){
    throw '官方NapCat源码或入口SHA-256不匹配固定版本；拒绝应用fallback。'
}
$source = [IO.File]::ReadAllText($originalPath)
$index = [IO.File]::ReadAllText($indexPath)
$newline = if($source.Contains("`r`n")){"`r`n"}else{"`n"}
function Replace-ExactlyOnce([string]$Body,[string]$Before,[string]$After){
    $Before = $Before.Replace("`r`n","`n").Replace("`n",$newline)
    $After = $After.Replace("`r`n","`n").Replace("`n",$newline)
    if(([regex]::Matches($Body,[regex]::Escape($Before))).Count -ne 1){
        throw '固定源码片段不是单次匹配；未应用fallback。'
    }
    return $Body.Replace($Before,$After)
}
$begin = 'async function KAe(t, e, n, r, i) {'
$end = 'async function VAe(t, e, n, r, i) {'
if(([regex]::Matches($source,[regex]::Escape($begin))).Count -ne 1 -or
   ([regex]::Matches($source,[regex]::Escape($end))).Count -ne 1){throw '固定登录函数边界不匹配。'}
$startAt = $source.IndexOf($begin,[StringComparison]::Ordinal)
$endAt = $source.IndexOf($end,$startAt,[StringComparison]::Ordinal)
if($endAt -le $startAt){throw '固定登录函数顺序不匹配。'}
$originalLogin = $source.Substring($startAt,$endAt-$startAt)
$login = Replace-ExactlyOnce $originalLogin '  const o = { isLogined: !1, loginFlowInitialized: !1 };' @'
  const o = { isLogined: !1, loginFlowInitialized: !1 };
  let __loginFallbackClaimed = false, __loginFallbackCycle = 0, __loginFallbackTimer;
  const __clearLoginFallbackTimer = () => {
    if (__loginFallbackTimer !== undefined) {
      clearTimeout(__loginFallbackTimer), __loginFallbackTimer = undefined;
    }
  };
'@
$login = Replace-ExactlyOnce $login '  }, c.onQRCodeLoginSucceed = async (u) => {' @'
  }, c.onQRCodeLoginSucceed = async (u) => {
    __clearLoginFallbackTimer();
'@
$login = Replace-ExactlyOnce $login '  }, c.onLoginDisConnected = () => {' @'
  }, c.onLoginDisConnected = () => {
    __clearLoginFallbackTimer(), __loginFallbackCycle++, __loginFallbackClaimed = false;
'@
$login = Replace-ExactlyOnce $login '  }, c.onLogoutSucceed = () => {' @'
  }, c.onLogoutSucceed = () => {
    __clearLoginFallbackTimer(), __loginFallbackCycle++, __loginFallbackClaimed = false;
'@
$login = Replace-ExactlyOnce $login @'
  }, c.onLoginConnected = () => {
    JAe(t, e).then(async () => {
'@ @'
  }, c.onLoginConnected = () => {
    if (__loginFallbackClaimed) return;
    __loginFallbackClaimed = true;
    const __claimedLoginCycle = __loginFallbackCycle;
    __clearLoginFallbackTimer();
    JAe(t, e).then(async () => {
      if (__claimedLoginCycle !== __loginFallbackCycle) return;
'@
# Keep this an operand in the original return-comma-expression. In particular,
# do not terminate the return before its final await a (the actual login result).
$login = Replace-ExactlyOnce $login '  }, t.addKernelLoginListener(cu(c, e)), t.connect(), await a;' @'
  }, t.addKernelLoginListener(cu(c, e)), (() => {
    const __connectLoginCycle = __loginFallbackCycle, __connectResult = t.connect();
    if (__connectResult === true && __connectLoginCycle === __loginFallbackCycle && !__loginFallbackClaimed && !o.isLogined) {
      __loginFallbackTimer = setTimeout(() => {
        __loginFallbackTimer = undefined;
        try {
          if (__connectResult === true && __connectLoginCycle === __loginFallbackCycle &&
              !__loginFallbackClaimed && !o.loginFlowInitialized && !o.isLogined &&
              !pe.getQQLoginQrcodeURL() && !pe.getQQLoginError() && t.getMsfStatus() === 1) {
            c.onLoginConnected();
          }
        } catch {
          e.logWarn("[Core] [Login] Optional login fallback check was skipped; use the original login controls.");
        }
      }, 5000);
    }
  })(), await a;
'@
$sidecar = Replace-ExactlyOnce $source $originalLogin $login
$sidecar = Replace-ExactlyOnce $sidecar 'xe.join(iE, "napcat.mjs")' 'xe.join(iE, "napcat.login-fallback.mjs")'
# Copy the official bootstrap verbatim, changing only its selected module.
$bootstrap = Replace-ExactlyOnce $index "'napcat.mjs'" "'napcat.login-fallback.mjs'"
foreach($candidate in @(@{Path=$sidecarPath;Content=$sidecar},@{Path=$entryPath;Content=$bootstrap})){
    if((Test-Path -LiteralPath $candidate.Path) -and [IO.File]::ReadAllText($candidate.Path) -cne $candidate.Content){
        throw '目标sidecar已有不同内容，保留原文件；请先人工核对后再生成。'
    }
}
foreach($candidate in @(@{Path=$sidecarPath;Content=$sidecar},@{Path=$entryPath;Content=$bootstrap})){
    if(-not (Test-Path -LiteralPath $candidate.Path)){
        [IO.File]::WriteAllText($candidate.Path,$candidate.Content,(New-Object Text.UTF8Encoding($false)))
    }
}
if((Get-LocalSha256 $originalPath) -cne $expectedSourceHash -or
   (Get-LocalSha256 $indexPath) -cne $expectedIndexHash){throw '原官方文件发生变化，请停止使用生成入口并核对。'}
[pscustomobject]@{
    Prepared=$true;OriginalUnchanged=$true;Version='4.18.38';Entry=$entryPath
    OriginalSourceSha256=$expectedSourceHash
    SidecarSha256=Get-LocalSha256 $sidecarPath
    BootstrapSha256=Get-LocalSha256 $entryPath
    QqStarted=$false;NativeLoaded=$false
}
