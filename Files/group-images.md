# 群图片事件入口接线说明

核验日期：2026-10-19。当前已实现独立 `GroupImageInbox` 收件模块，纯解析测试和本机回环 HTTP 烟测通过；尚未安装 NapCat、收到真实 QQ 群图片，或验证桌面 QQ 通话与图片入口共存。语音识别和语音合成继续留在本机；后续只有经用户问题明确关联的图片与文本才送至 DeepSeek。

## 本机入口与授权范围

入口固定为 `http://127.0.0.1:17800/onebot/`，末尾保留 `/`；端口可在构造入口时修改，不能改成公网或局域网监听。主程序必须实际启动入口并显示 `IsRunning=true` 后，事件上报才会成功。本文件不假设已经存在可用的图片启动命令。

构造 `GroupImageInbox` 时必须提供授权群号，建议同时提供机器人 QQ 号 `authorizedSelfId`。设置后，事件 `group_id`、`self_id` 必须分别匹配；`self_id` 是机器人账号，`user_id` 是发图者。不要从混合通话声音推断具体发图者身份。

密钥只放本地环境变量 `ONEBOT_ACCESS_TOKEN`；不要写入代码、提交记录、截图或普通日志。可以在启动终端安全输入，后续启动的机器人进程继承它：

```powershell
$imageSecret = Read-Host '输入本地图片入口随机密钥' -AsSecureString
$env:ONEBOT_ACCESS_TOKEN = [System.Net.NetworkCredential]::new('', $imageSecret).Password
Remove-Variable imageSecret
```

入口支持原生 NapCat 的 `X-Signature: sha1=<正文 HMAC-SHA1>`，也保留 `Authorization: Bearer <该密钥>` 给自定义本机调用。两种方式至少有一种验证通过才接收事件，空密钥时不启动。它不根据群消息执行电脑命令，不读取事件里指定的文件路径，也不主动发送群消息。

## NapCat 的接线位置与鉴权

选用 NapCat 的 **HTTP 客户端／事件上报**，上报地址填写本机入口，`messagePostFormat` 选择 `array`，保持 `reportSelfMessage=false`、`debug=false`。保留既有配置，只增添该连接项，不用下面片段覆盖整份配置。示例初始保持关闭；主程序入口就绪并确认桌面 QQ 共存方案后，再手动启用这一项：

```json
{
  "network": {
    "httpClients": [
      {
        "name": "qq-local-group-images",
        "enable": false,
        "url": "http://127.0.0.1:17800/onebot/",
        "messagePostFormat": "array",
        "reportSelfMessage": false,
        "token": "填写与 ONEBOT_ACCESS_TOKEN 相同的本地随机密钥",
        "debug": false
      }
    ]
  }
}
```

NapCat 官方 HTTP 客户端把 `token` 用于原始事件正文的 HMAC-SHA1，并发送 `X-Signature: sha1=...`；它没有发送 Bearer 头。本入口已按该原生格式实现兼容：使用同一 `ONEBOT_ACCESS_TOKEN`，在读取受限原始字节后计算 HMAC，再用固定时序比较验证签名，验证前不重新格式化 JSON。正常接线无需另装 Bearer 适配器；不能关闭鉴权绕过 401。兼容格式已经通过本机模拟事件验证，但尚未运行 NapCat 或接收真实群事件。

依据：[NapCat WebUI 配置指南](https://doc.napneko.icu/config/basic)、[官方 HTTP 客户端实现](https://github.com/NapNeko/NapCatQQ/blob/main/packages/napcat-onebot/network/http-client.ts)。配置角色以实际 `network.httpClients` 上报实现为准。

NapCat 部署与桌面 QQ 群通话共存仍需单独验证，不预设同一账号在两套 QQ 客户端中稳定在线。任何安装、QQ 客户端改动或账号登录按项目既有授权与人工步骤执行。

## 收件与图片关联边界

- 只接受 OneBot 11 群消息的数组 `message`，从 `image` 段记录 URL；字符串 CQ 码、私聊、非图片事件不进入图片候选。
- 正文最大 256 KiB，声明长度和分块读取都受限；每个 HTTP 请求读取限时 3 秒。超过 5 分钟的事件、未来超过 30 秒的事件被拒绝。
- 内存最多 20 张图片元数据，只保存群、发图者、消息 ID、时间和 URL；按旧消息整组淘汰，不把一条多图消息截成不完整的一组。没有磁盘图片缓存，退出即清空内存。
- `Resolve(groupId, now, maxAge)` 返回 `None`、`Unique` 或 `Ambiguous`。同一条消息的多图可返回一组；多个消息或冲突来源需要简短确认，不能直接挑最新图片回答。
- 入站只保存 HTTP(S) 地址，不下载文件，拒绝 `file:`、本地路径、`data:`、带账号密码和回环地址。后续实际下载模块仍须限制允许的图片来源、重定向、体积和格式；收件成功不证明 URL 已可用或图片已理解。

## 状态、停止与故障

查看入口 `IsRunning`、`LastError` 和候选关联结果。入口启动或请求错误只记录在图片模块，不应终止语音会话。`401` 表示签名／Bearer 不匹配、鉴权缺失或正文被改动，先检查两侧 Token 和是否存在重写正文的代理；`413` 表示正文过大；`400` 表示 JSON 无法解析。合法但不属于授权范围的事件返回 `200` 和 `{"accepted":false}`。

停止时取消入口的生命周期令牌并等待 `StartAsync` 退出；测试已确认停止后相同监听前缀可以重新绑定。未设置 Token、端口占用或 URLACL 权限不足时保留错误供查看，不自动提权，也不修改系统 URLACL；先诊断具体错误，再按必要授权处理。

恢复时停止图片入口、关闭 NapCat 中新增的 `qq-local-group-images` HTTP 客户端项，并在启动终端执行 `Remove-Item Env:ONEBOT_ACCESS_TOKEN`。本阶段没有修改 QQ 文件、系统音频默认设备或 URLACL，不需要恢复这些设置。

## 实际验证与剩余步骤

已完成 10 项纯解析／取消启动检查，以及 18 项真实本机 HTTP 烟测：绑定 `127.0.0.1`、有效 Bearer 接收、错误 Token 401、串群和错误 selfId 不入库、声明正文及 chunked 正文超限 413、坏 JSON 400、后续正常请求恢复、有效原生 HMAC 接收、篡改正文／错误 HMAC 密钥／缺失鉴权／错误签名格式／正文空格变化被拒绝、取消停止和相同前缀重新绑定。烟测使用随机临时端口、假的进程环境 Token 和假的图片 URL，没有连接 QQ 或下载图片，没有修改 URLACL。

可重复烟测：

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'Cache/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'Cache/nuget'
dotnet run --project Cache/ImageInboxSelfTest/ImageInboxSelfTest.csproj --configuration Release -- --http
```

该工程只用于本地测试，会临时替换自身进程的 Token，并在结束后恢复；结果保存在 `Cache/ImageInboxSelfTest/http-smoke-results.json`。它不启动正式群图片服务。

仍待完成：提供主程序启停与状态入口，部署并验证图片入口与桌面 QQ 通话共存，接收指定测试群的真实图片，再验证图片关联、下载和 DeepSeek 图片问答。完成这些真实验证前，不宣称“群图片自动接入已完成”。
