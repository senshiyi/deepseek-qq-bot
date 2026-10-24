# 机器人自己的安卓虚拟手机

这套产物对应「机器人看屏幕、点击、滑动、输入、打开手机里的 App/游戏」。使用 Google 官方 Android Emulator 和单独的 AVD，不登录宿主的 Google、QQ 或浏览器账号，不挂载宿主文件目录。2026-10-23 已完成官方组件下载校验、专属 AVD 真机启动、PNG 截图、基本触控与输入，以及部分真实网络隔离验收。**AI 自主操作循环尚未接入**；当前视觉 API 不可用，首个试玩 App 尚未指定，双向剪贴板等剩余边界仍待验证。

## 已确定的范围与目录

| 项目 | 当前方案 |
| --- | --- |
| 设备 | `deepseek-phone`，Android 15 / API 35 Google APIs x86_64；非 Play Store 生产镜像 |
| 显示与资源 | 720×1280，4 个虚拟 CPU，4096 MiB RAM，userdata 最大初始配置 6 GiB |
| SDK | `E:\Desktop\WorkSpace\deepseek-qq-bot\Cache\AndroidSdk` |
| 归档 | `E:\Desktop\WorkSpace\deepseek-qq-bot\Cache\AndroidPhonePackages` |
| AVD 与状态 | `E:\Desktop\WorkSpace\deepseek-qq-bot\Results\local\android-phone` |
| 截图 | 上述状态目录下 `captures`，工具最多保留 30 张自产 PNG；单次限制 8 MiB，检查完整块序列/CRC/IEND并有界解码 |
| ADB | 专用 `localhost:5038` 启动、客户端访问 `127.0.0.1:5038`，不操作默认 5037 服务；关闭模拟器端口扫描/mDNS 自动连接，限制 USB 选择器为保留的模拟器序号 |
| 模拟器 | console 5560 / ADB transport 5561，只接受 `emulator-5560` 且 AVD 名为 `deepseek-phone` |

Google 官方仓库 2026-10-23 的稳定元数据固定在 [packages.lock.json](AndroidPhone/packages.lock.json)。平台工具 37.0.1、模拟器 **37.2.12 / channel-0**、系统镜像 r9 三包共 2,202,203,760 字节，约 2.05 GiB。安装解压、系统镜像和后续 userdata 需要额外空间；不是只有下载大小。当前不安装 Android Studio，不选择第三方模拟器，不登录账号，也不自行安装第三方/收费 App。首个试玩 App 仍待用户指定，可以先用系统界面确认基本动作。

## 脚本的两类入口

管理者入口是 [Manage-AndroidPhone.ps1](AndroidPhone/Manage-AndroidPhone.ps1)，提供 `Initialize`、`CheckHost`、`Start`、`Stop`、`Status`、`InspectGuestNetwork`。这些操作不开放给模型。脚本没有下载、启用 WHPX、安装驱动、修改宿主防火墙或设置系统环境变量的逻辑。其 `Initialize` 会写 AVD 配置；模拟器首次 `Start` 才可能创建 userdata/缓存等磁盘文件，且两者都只应在实施授权后运行。

未来供宿主管理程序封装的模型动作入口仅为 [Invoke-PhoneAction.ps1](AndroidPhone/Invoke-PhoneAction.ps1)，目前由管理者调用验证，未注册给聊天模型：

| 动作 | 可输入内容 | 限制 |
| --- | --- | --- |
| `Screenshot` | 无路径参数 | 只抓固定设备，PNG 必须为 720×1280；文件名和目标目录由工具产生 |
| `Tap` | `X`、`Y` | X 为 0–719，Y 为 0–1279 |
| `Swipe` | `X`、`Y`、`EndX`、`EndY`、`DurationMs` | 坐标同上，时长 50–2000 ms |
| `Text` | `Text` | 1–160 字符；初版仅 ASCII 字母数字及空格 `. , @ _ : / + -`，拒绝 shell 元字符 |
| `Back` / `Home` | 无 | 固定 Android 按键值 |

入口没有任意 ADB、shell、APK 安装、主机文件路径、App 包名或模拟器序号参数。模型应通过截图识别图标后点击打开 App。中文文字输入尚未实现；不能把宿主共享剪贴板当作替代。后续若需要中文，应单独采用可审核的来宾输入法方案。

每个有效动作先检查该轮启动回执，再查专用 ADB server 的设备列表、AVD 名、实际非 root UID、内核 boot ID 和显示大小。出现其他设备、错误 AVD、root/未知 UID、设备重启或尺寸改变即拒绝；root 或 UID 查询失败还会撤销旧控制回执。初版单次原生命令通常限制 12 秒，触控/文本参数在访问 ADB 前检查。未来控制平面仍须限制每轮动作数和总时长；仅有这些脚本并不代表模型已经接入并会自主玩手机。

这些脚本是供宿主管理程序封装的工具实现。不能把任意 PowerShell、Core 模块函数、SDK 二进制或管理入口同时开放给模型，再声称已限制模型能力。模型只应获得结构化动作接口和截图结果；宿主凭据、SDK 路径、控制回执也不发送给模型。

## 无共享与网络策略

Android NAT 默认能访问宿主别名 `10.0.2.2` 以及局域网，因此 NAT 本身不满足隔离目标。已实现的启动流程在 Google APIs debug 镜像内由管理者临时 `adb root`，推送并执行固定的 [guest-network-policy.sh](AndroidPhone/guest-network-policy.sh)，随后 `adb unroot`。模型没有 root/ADB/shell 入口。

IPv4 输出规则仅放行来宾自身 loopback、`10.0.2.3` 的 TCP/UDP 53 DNS、DHCP 广播、公网 TCP 80/443 和 UDP 123；先拒绝 10/8、172.16/12、192.168/16、100.64/10、169.254/16 等地址段，再处理已建立连接。因此宿主 `10.0.2.2` 也被覆盖，DNS 例外不允许其余端口。IPv6 的 INPUT/OUTPUT/FORWARD 默认由独立链阻断，只保留来宾 loopback。INPUT 只放本地、已建立/关联响应及 DHCP。规则只修改来宾，不更改宿主全局网络或防火墙。

启动管理脚本移除自身重复 jump，将限制链插在 INPUT/OUTPUT 等对应链的第一条规则，并 readback 检查首位，而不是只检查 jump 存在；之后才产生 `control-ready.json`，并在回执中明确记 `realNetworkAcceptance=pending`。回执只说明管理脚本完成控制准备，不证明所有网络路径已隔离。设备每次冷启动均需重新执行此流程，旧 boot ID 的回执无效。Android 的网络服务可能重排规则，未来真实验收还应在运行 App 后重复核查规则位置与命中计数。

管理者的 `InspectGuestNetwork` 将 root 请求放在受保护的 `try` 内；退出时 `Restore-PhoneUnroot` 等待设备重新上线并读取实际 UID，而不是只认为 `unroot` 请求成功就已恢复。超时/失败且无法确认非 root 时删除控制回执并报错。真实网络测试也采用同一恢复逻辑，恢复失败仍写出证据后以失败退出，避免留下可继续控制的旧回执。

来宾可连接公网 Web，仍可以向公网发送它在来宾内持有的数据。公网代理、VPN、某些游戏 UDP、账号登录和付费能力都不属于当前开放范围。普通 App 无 root，但调试镜像及来宾内防火墙不是对恶意 root/kernel 攻击的独立强边界。不要向来宾放宿主密钥或个人资料；后续若允许不可信 APK，应重新审核模拟器版本、root 能力与网络边界。

当前启动参数选用官方支持的 `-no-window`，由截图提供可见手机画面；不调用 Qt 可见窗口的共享剪贴板交互，也不传共享目录、SD 卡、USB 透传、摄像头、麦克风或音频参数。源码未发现 `-no-clipboard` 选项，因此没有编造该参数。**无窗口设计仍需用真实工具验证双向剪贴板未互通。** 若需要可见独立模拟器窗口，先核实真实版本的 clipboard sharing 设置并做双向测试，再调整启动方式。宿主文件到来宾的唯一管理推送目前只有固定网络策略脚本；截图作为明确的工具输出写回项目固定目录。

## 启动与剩余实施顺序

以下命令供管理者使用。当前机器的官方 `emulator -accel-check` 已返回退出码 0、`WHPX(10.0.26200) is installed and usable`；无需启用 Windows 功能、管理员安装或重启。不根据 WMI 的旧功能标志重复修改系统。

1. 下载、校验与项目内 SDK 解压已经完成，归档和 userdata 均保留在上表目录。不要重复 `Initialize`；已有 AVD 时它会拒绝覆盖。
2. 运行 `CheckHost`，再运行 `Start`。启动采用 `-accel on`；不满足时不自动退回其他驱动或更改宿主设置。仅允许专属空白设备与固定端口。
3. `Start` 冷启动后重新应用来宾规则并恢复 shell UID 2000。`control-ready.json` 的 `modelControlEnabled=false`，只表示受限动作准备完成，不表示模型或全部隔离验收已启用。
4. 需要复测网络时运行 `Test-PhoneLiveNetwork.ps1 -ConfirmDedicatedBlankPhone`。它临时监听本机环回和当前默认路由的本机私网地址，先验证服务可接受本机连接，再检查来宾阻断和计数；结束关闭监听并 `adb unroot`。不修改宿主防火墙，不测试其他机器。
5. 补齐公网 HTTPS 应用层、IPv6 有效对照、双向剪贴板验证；运行目标 App 后重查规则位置与计数。IPv4 样本通过不代表所有网络路径已验证。
6. 待用户提供可识图的 DeepSeek API、指定首个 App 后，再接屏幕观察与有限动作循环。无关 SDK、真机和个人账号不得参与。

管理示例：

```powershell
pwsh -File .\Files\AndroidPhone\Test-PhonePackages.ps1
pwsh -File .\Files\AndroidPhone\Manage-AndroidPhone.ps1 -Action CheckHost
# Initialize 仅首次使用；当前机器已完成，不要重复执行
pwsh -File .\Files\AndroidPhone\Manage-AndroidPhone.ps1 -Action Start
pwsh -File .\Files\AndroidPhone\Manage-AndroidPhone.ps1 -Action Status
pwsh -File .\Files\AndroidPhone\Test-PhoneLiveNetwork.ps1 -ConfirmDedicatedBlankPhone
pwsh -File .\Files\AndroidPhone\Manage-AndroidPhone.ps1 -Action Stop
```

模型工具动作的本地调用形式：

```powershell
pwsh -File .\Files\AndroidPhone\Invoke-PhoneAction.ps1 -Action Screenshot
pwsh -File .\Files\AndroidPhone\Invoke-PhoneAction.ps1 -Action Tap -X 350 -Y 600
pwsh -File .\Files\AndroidPhone\Invoke-PhoneAction.ps1 -Action Swipe -X 360 -Y 1000 -EndX 360 -EndY 300 -DurationMs 400
pwsh -File .\Files\AndroidPhone\Invoke-PhoneAction.ps1 -Action Text -Text 'hello phone'
```

截图与触控不需要宿主鼠标、屏幕截图或焦点；但本地电脑关机、休眠，模拟器都会停止或暂停，不能提供关机后持续在线。单独的 AVD 也不改变这一点。

## 已完成验证与待验证项

`Test-PhoneActions.ps1` 使用内存假 ADB、自产测试 PNG 和固定的本地 PowerShell 子进程，覆盖原生命令超时、stdout/stderr 并行有界读取、截断/CRC/假 PNG 与扫描行解压边界等契约。最终 **34/34** 通过，包含 root 残留、UID 查询失败、unroot 超时但实际恢复，以及无法确认恢复时撤销控制回执。`Test-GuestNetworkPolicy.sh` 用 shell 函数假 iptables 检查既有 ACCEPT、重复 jump 和首位 readback 失败。离线检查与下列真实验收分别记录。

| 2026-10-23 真实项目设备检查 | 结果与边界 |
| --- | --- |
| Android 冷启动、策略应用、恢复非 root | 通过；真实 boot，来宾最终 UID 2000 |
| 720×1280 PNG | 通过；由截图动作取得并人工工具视检 Pixel 桌面、应用抽屉、Settings 和搜索结果 |
| Screenshot / Tap / Swipe / Text / Back / Home | 通过；搜索框真实出现 `display`，没有使用宿主鼠标/剪贴板 |
| 宿主别名 `10.0.2.2` | 通过；已知开放的本机服务仍无法从来宾连接，对应 REJECT 规则增加 2 包 |
| 本机默认路由私网地址 | 通过；同样有本机可达服务对照，10/8 REJECT 增加 2 包；未扫描其他局域网机器 |
| `169.254.169.254` | 通过来宾规则命中验证；REJECT 增加 2 包，不宣称该地址存在服务 |
| DNS 与公网 TCP 443 | `example.com` 成功；单独 `1.1.1.1:443` 超时，未把该地址作为放行证明；尚未验证实际 TLS/HTTPS 页面 |
| 共享目录与账号 | 当前挂载表未见 9p/virtiofs/cifs 等宿主共享类型，启动参数无共享目录/SD/USB透传，来宾 `Accounts: 0` |
| 专用监听 | 5038/5560/5561 仅监听 `127.0.0.1`/`::1` |
| IPv6 | 独立阻断链 readback 通过；实际连通对照仍待验 |
| 双向剪贴板 | 待验；没有读取或改写用户宿主剪贴板 |
| AI 自动浏览或玩游戏 | 未接入，未安装试玩 App，未调用视觉模型 |

真实网络原始证据见 `Results/local/android-phone/live-network-20261023T080336Z.json` 和关闭自动发现后的复测 `live-network-20261023T080919Z.json`；基本动作截图在同目录 `captures/`。本次初次启动曾因 ADB 把数值 `127.0.0.1` 当作远程服务而无法 bootstrap，已使用 `tcp:localhost:5038` 修复；客户端仍固定数值环回访问。不会操作默认 5037，也不会为补救修改系统环境变量。

汇总记录为 `Results/local/android-phone/acceptance-20261023.json`。最终非 root 清理修复后又真实运行了一轮 `Start → InspectGuestNetwork → Screenshot → Stop`，检查与截图均成功。验收结束已正常停止专属设备、移除控制回执，并关闭本次创建的空置 5038 服务；5038/5560/5561 已无监听。userdata、官方归档和截图均保留，未让空白手机持续消耗资源。

官方参考：

- [SDK 主仓库元数据](https://dl.google.com/android/repository/repository2-1.xml)
- [Google APIs 系统镜像仓库元数据](https://dl.google.com/android/repository/sys-img/google_apis/sys-img2-1.xml)
- [Android Emulator 命令行说明](https://developer.android.com/studio/run/emulator-commandline)
- [模拟器网络地址与访问宿主说明](https://developer.android.com/studio/run/emulator-networking)
- [官方 Emulator 命令参数源码](https://android.googlesource.com/platform/external/qemu/+/emu-master-dev/android/emu/cmdline/include/android/cmdline-options.h)

官方来源元数据保存在 `Cache/AndroidSdk-metadata`，三个官方归档和解压后的 SDK 已分别保存到上表目录。动作工具可用与 AI 自主体验可用仍是两项不同验收。
