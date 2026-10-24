# NapCat 普通 QQ 账号：文字与表情包接入

核查日期：2026-10-20。当前语音工作暂停；本说明只接入群文字、QQ 表情与图片/GIF 表情包。

## 已准备的内容与边界

已从 [NapCat 官方 v4.18.38 发行页](https://github.com/NapNeko/NapCatQQ/releases/tag/v4.18.38) 下载 `NapCat.Shell.Windows.Node.zip`，保存在项目 `Cache/napcat-downloads/v4.18.38/`，展开到 `Cache/NapCat/v4.18.38/`。准备阶段仅下载和核验；随后按用户授权启动，由用户扫码登录1667533356，并在指定群验证了文字和表情图。未修改 `E:\QQ`、注册表、系统音频设备或管理员设置。本轮结果见 [群消息验证](group-messaging-results.md)。

压缩包为 124,079,600 字节，SHA-256 为：

```text
b88007a373a30deefb387e7cf17911752ff900bce22c534b3da6b644f5c6d0ae
```

它与 GitHub 该资产返回的 `digest` 及准备脚本固定值均一致。这是来源资产的完整性核对，不等于发布者数字签名。833 个 ZIP 条目在解压前通过目标目录边界、重复目标与符号链接检查；展开后为 705 个文件、361,420,291 字节。来源、资产 ID、版本、下载地址及逐文件 SHA-256 清单保存在本机忽略目录 `Results/local/napcat-preparation/`。

重建准备命令：

```powershell
Set-Location 'E:\Desktop\WorkSpace\deepseek-qq-bot'
.\Files\Prepare-NapCat.ps1
```

目标目录已存在时脚本直接拒绝覆盖，不会自动清空、升级或替换用户内容。可运行 ` .\Files\Prepare-NapCat.ps1 -VerifyOnly` 重新核对官方压缩包、ZIP 路径及必要启动文件是否存在；该选项不声称已逐文件比对展开目录的全部内容。首次准备时保存的清单可用于进一步比对。

## 启动入口和依赖

本路线默认使用**包最外层的 `napcat.bat`**，其内容是在包目录调用自带 `node.exe index.js`。`index.js` 将 wrapper、QQ 版本配置和 NapCat 模块指向包内文件，不调用系统安装的 `E:\QQ\QQ.exe`。本机缺失连接回调时另有默认关闭的 `-LoginFallback`，只选择经过固定哈希校验生成的副本；见 [群消息说明的可选启动补救](group-messaging-guide.md#napcat账号接入)。原官方文件不修改，去掉参数恢复原入口。

静态检查确认包内已有 Node 22.11.0、QQ 9.9.31-49738 的 wrapper/DLL、NapCat 模块、生产 `node_modules`、WebUI 静态资源及 Windows x64 原生模块，包含 FFmpeg addon。文字/图片路线无须先安装全局 Node、npm、LiteLoader 或插件；准备脚本也不会运行 `npm install`。当前 Windows System32 已有 VC++ 运行库 `vcruntime140.dll`、`vcruntime140_1.dll`、`msvcp140.dll`，版本 14.51.36247.0。**本机已实际加载原生模块并完成登录及群消息测试。**若之后出现缺少 DLL，先保留错误定位，再决定是否需要额外安装，不自动安装系统组件。

包内 `napcat/launcher.bat` 等是另一套传统注入启动器，不用于本次 Node 路线；`napcat/KillQQ.bat` 会强制结束所有 `QQ.exe`，不要把它当作本项目的停止按钮。

本路线的正常准备与外层启动脚本没有管理员提权命令；本轮未为启动安装系统组件或修改管理员设置。遇到权限错误应先定位，不把“以管理员运行”作为默认办法。

## 账号与数据位置

推荐专用普通 QQ 账号。登录、扫码、系统权限弹窗及测试群授权由用户完成。相同账号在现有桌面 QQ 和 NapCat 两个 Windows 会话中稳定共存没有验证；选择已有账号时，应先由用户退出该账号的桌面 QQ 会话，再让 NapCat 接管，避免互踢。

`NAPCAT_WORKDIR` 可把 NapCat 自身配置、日志、缓存及插件目录放在 `Results/local/napcat/`，此路径已被项目忽略。**它不隔离 QQ 登录数据**：当前 Shell 源码仍从 QQ wrapper 读取 QQ 数据根，Windows 的全局路径位于其 `nt_qq/global`。程序目录独立不能保证账号数据库独立；不复制、不删除现有 QQ 登录数据库。[数据路径源码](https://github.com/NapNeko/NapCatQQ/blob/v4.18.38/packages/napcat-shell/base.ts#L57)、[NAPCAT_WORKDIR 定义](https://github.com/NapNeko/NapCatQQ/blob/v4.18.38/packages/napcat-common/src/path.ts)、[官方 Desktop 的数据隔离说明](https://github.com/NapNeko/NapCatQQ-Desktop/blob/main/docs/user/README.md)。

## 账号选定后，先配置再启动

以下为重新部署时的配置说明；本机已按用户选择的1667533356完成配置和扫码，配置由 `Files/Start-NapCat.ps1` 备份后维护，不需要重复手写或覆盖。

1. 在项目 `Results/local/napcat/config/` 建立 `webui.json`，先将监听绑定到回环。用自行生成的随机本地密码替换占位文字；不要将占位值作为真实密码，不要覆盖已有配置。

   ```json
   {
     "host": "127.0.0.1",
     "port": 6099,
     "token": "替换为独立的随机WebUI密码",
     "disableWebUI": false,
     "autoLoginAccount": ""
   }
   ```

   当前包内实际 schema 的默认 host 是 `::`，旧示例中还有 `0.0.0.0`；二者都不适合直接照抄为本机专用面板。其余字段由 schema 补全。WebUI 密码与下面的 OneBot Token 是两套凭据；扫码后面板的实际登录 Token 可能变化，以本地控制台提示为准，不把含 Token 的日志或链接发到群里。

2. 在**专用 PowerShell 窗口**中设置仅该进程继承的工作目录，然后调用包最外层入口，明确传入用户选择的 QQ 号：

   ```powershell
   Set-Location 'E:\Desktop\WorkSpace\deepseek-qq-bot'
   $env:NAPCAT_WORKDIR = Join-Path (Get-Location) 'Results/local/napcat'
   & '.\Cache\NapCat\v4.18.38\napcat.bat' -q '替换为用户选择的QQ号'
   ```

   这条命令会加载普通 QQ 账号协议组件；此时才由用户扫码。仅访问 [本机 WebUI](http://127.0.0.1:6099/)；若当前会话已有其他账号的快捷登录记录，核对目标账号后再继续，不自动沿用。

3. 登录后，在 WebUI 配置 OneBot HTTP 服务端与 HTTP 客户端。账户配置文件是工作目录下 `config/onebot11_<QQ号>.json`，也可能从 `onebot11.json` 读取默认值；优先由 WebUI 保存，不误改包根目录用于 QQ 版本的 `config.json`。

## OneBot 两个方向要分别配置

| 方向 | 配置项 | 本机地址 | 用途 |
| --- | --- | --- | --- |
| 本项目 → NapCat | HTTP 服务端 `httpServers` | `127.0.0.1:3000` | 查询登录状态、调用 `send_group_msg` |
| NapCat → 本项目 | HTTP 客户端 `httpClients` | `http://127.0.0.1:17801/onebot/` | 上报群文字、图片和表情事件 |

事件 URL 必须保留结尾 `/onebot/`；当前程序不是 `/onebot/events`。17800 是旧图片专用入口，本阶段群聊使用配置中的 17801。下例是经过 v4.18.38 tagged schema 和已下载 bundle 静态核对的字段结构；所有 `token` 占位值必须换为同一个本地随机 OneBot Token：

```json
{
  "network": {
    "httpServers": [{
      "name": "local-api", "enable": true,
      "host": "127.0.0.1", "port": 3000,
      "enableCors": false, "enableWebsocket": false,
      "messagePostFormat": "array", "token": "替换为随机OneBotToken", "debug": false
    }],
    "httpSseServers": [],
    "httpClients": [{
      "name": "local-events", "enable": true,
      "url": "http://127.0.0.1:17801/onebot/",
      "messagePostFormat": "array", "reportSelfMessage": false,
      "token": "替换为随机OneBotToken", "debug": false
    }],
    "websocketServers": [], "websocketClients": [], "plugins": []
  },
  "musicSignUrl": "", "enableLocalFile2Url": false,
  "parseMultMsg": false, "imageDownloadProxy": ""
}
```

本项目通过环境变量 `ONEBOT_ACCESS_TOKEN` 或项目忽略的 `.env` 中同名项读取该 Token。保留 `.env` 原有 DeepSeek Key，不用示例文件覆盖现有凭据。本项目的 `group-chat.local.json` 则从 `Files/group-chat-config.example.json` **另存**后填写 `selfId`、授权群白名单和机器人名称。未填写真实授权群之前不发送。

验证顺序为：先仅检查登录状态与 API 鉴权，再运行本项目预览模式，确认事件来自所选群、账号匹配且能够选择预置表情；最后仅在用户同意的测试群与时段开启实际发送。具体群聊命令以项目启动帮助及主使用说明为准；此文档不自动开启任何发送模式。

## 表情包与失败处理

- 文字用 `text`，QQ 内置表情用 `face`，本地图片/GIF 用 `image`；发送内容只引用本项目允许的素材清单。不要让模型给出任意本地路径、群号、URL 或 OneBot action。
- GIF 源文件按 GIF 类型上传；`sub_type: 1` 表示自定义表情类型，不是动画开关。接收端动效、体积限制和原生收藏表情外观仍需真实发送验证。WebP/APNG 的动画效果没有据此得到保证。
- 商城表情 `mface` 另外需要表情包 ID、表情 ID 和 key，不能从普通 GIF 凭空生成，不能预设账号有付费包资格。第一版先验证本地白名单素材。
- 发消息必须同时检查 HTTP 成功、JSON `status == "ok"`、`retcode == 0` 及 `data.message_id`。HTTP 200 也可能装着失败结果；403 通常应检查 OneBot Token。超时可能已投递，先看 QQ，不自动重发。
- 连接拒绝先检查 NapCat 是否启动、HTTP **服务端**是否启用以及3000端口是否被占用；收不到事件则检查 HTTP **客户端**URL、17801监听、Token和 `messagePostFormat: "array"`。它们与 WebUI 6099 互不替代。
- 本项目群聊监听若遇到 Windows URL ACL 权限错误，应保留完整本地错误先定位，再决定是否需要范围明确的 URL ACL 设置；当前没有为此添加系统规则或请求管理员权限。

停止本项目群聊用其 `Q`/Ctrl+C；停止 NapCat 在专用启动窗口用 Ctrl+C，并核对该窗口启动的 Node 子进程已退出。不要结束所有 QQ 或 Node 进程。关闭该专用 PowerShell 后，`NAPCAT_WORKDIR` 不会持续影响其他终端；当前准备过程没有改变系统环境变量或默认设备，因此没有这些系统配置的恢复步骤。

需要移除时先停止这一实例，再按已核对的绝对路径处理项目内 `Cache/NapCat/v4.18.38/` 和对应下载缓存；保留 `Results/local/napcat/` 及准备证据以便恢复。QQ 登录数据可能位于原 QQ 数据根，不能当作本项目缓存递归删除。

## 当前验证状态

- 已完成：官方包实际下载、官方大小与 SHA-256 核对、全部 ZIP 条目路径检查、实际解压、逐文件清单、静态入口/QQ版本/Node版本/配置 schema 检查。
- 已通过：准备脚本 PowerShell 5.1 语法解析、仅校验模式、已有目标拒绝覆盖。
- 已通过真实QQ：包内Node/QQ原生模块启动、用户扫码登录、API账号核对、指定群事件、DeepSeek主动文字与PNG表情图投递，用户确认接收端可见。暂停和Q退出已实测，事件端口已释放。
- 尚未通过：GIF动效、长时间重连稳定性、真实群图片问答及第一条单独测试消息的接收端可见性原因。
- 语音暂停：上述准备及消息接入不采集、不播放音频，不恢复群通话联调。

依据：[官方发布工作流](https://github.com/NapNeko/NapCatQQ/blob/v4.18.38/.github/workflows/release-publish.yml)、[启动脚本](https://github.com/NapNeko/NapCatQQ/blob/v4.18.38/packages/napcat-develop/napcat.bat)、[WebUI schema](https://github.com/NapNeko/NapCatQQ/blob/v4.18.38/packages/napcat-webui-backend/src/helper/config.ts)、[OneBot schema](https://github.com/NapNeko/NapCatQQ/blob/v4.18.38/packages/napcat-onebot/config/config.ts)、[消息段定义](https://github.com/NapNeko/NapCatQQ/blob/v4.18.38/packages/napcat-onebot/types/message.ts)、[GIF类型判定](https://github.com/NapNeko/NapCatQQ/blob/v4.18.38/packages/napcat-core/helper/msg.ts)。
