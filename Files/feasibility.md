# 能力核查与后续实施依据

核查日期：2026-10-19。以下是公开文档能力与本机条件，不是已经通过的真实 QQ 验收。

## 已确定的路线

用户选择只用普通 QQ 账号收图；实时通话由 Windows 桌面 QQ 承担，图片入口评估 NapCat。用户选择先完成固定音频通路，暂不选择识别/合成服务。项目位置沿用当前目录。

| 路线 | 已确认 | 尚无公开支持证据/实施限制 |
| --- | --- | --- |
| 腾讯官方机器人 | 群消息、图片和语音文件消息 | 未发现加入群实时通话或双向连续音频流接口；本项目不采用它收图 |
| NapCat / OneBot | 图片/record 消息、图片读取、消息事件 | record 是语音文件消息；未发现公开实时群通话音频接口 |
| 普通账号桌面 QQ + 音频桥 | 客户端人工加入通话，系统按进程采集/虚拟麦克风可构成候选链路 | 本机真实群通话仍需验证，混合音轨不能可靠对应到 QQ 成员 |

依据：[腾讯消息收发概述](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/message/overview.html)、[腾讯 SDK](https://github.com/tencent-connect/qqbot-nodejs)、[NapCat 消息格式](https://doc.napneko.icu/develop/msg)、[接口](https://doc.napneko.icu/develop/api)、[事件](https://doc.napneko.icu/develop/event)。没有发现公开支持证据，不等于断言任何私有实现都不可能。

微软的[按应用音频采集示例](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)明确可包含目标进程及子进程，排除其他进程；最低 build 20348。本机 Windows 11 build 26200 满足版本要求。使用 QQ 根进程树便于覆盖其音频子进程，不捕获机器人的独立播放进程；QQ 通知音和远端回音仍须单独处理。

## NapCat 和桌面 QQ 共存门槛

[Framework 官方说明](https://doc.napneko.icu/guide/boot/Framework)是已有 LiteLoaderQQNT 的桌面 QQ 插件模式，可评估同一桌面客户端负责通话和消息，不能假设同账号两套客户端稳定同时在线。

当前 [NapCat v4.18.38，2026-10-12](https://github.com/NapNeko/NapCatQQ/releases/tag/v4.18.38)提供 Framework 包。但 [LiteLoaderQQNT 原仓库](https://github.com/LiteLoaderQQNT/LiteLoaderQQNT)已于 2026-05-17 归档，其安装涉及 QQ 加载入口及完整性校验相关组件；这会改动客户端，不在当前固定音频阶段直接执行。

本机 QQ 9.9.25.42941 在 [Issue #1100](https://github.com/LiteLoaderQQNT/LiteLoaderQQNT/issues/1100)有无法启动的报告，后续换电脑可用。它不能证明全面不兼容，也不能证明本机稳定兼容。官方 Framework 文档没有群通话稳定性承诺。后续须保护 QQ 原文件、明确恢复方式、验证图片与群通话共存；遇到不兼容不自动升级/降级或替换当前 QQ。

后续 OneBot 入口按 `self_id/group_id/message_id/sender/time` 记录图片，绑定用户明确指定的群，过期图和同话题多图需确认。仅收到真实群事件才计为自动收图完成。入口使用回环地址及 Token，故障与通话隔离；不根据群文字执行命令。

## DeepSeek 当前支持图片

当前[官方模型价格页](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)列出 `deepseek-flash`（DeepSeek-V4.1-Flash-1017）支持图像理解，`deepseek-v4-pro` 不支持。不能沿用旧“DeepSeek 不支持图片”的判断。

待用户选择语音服务后，可采用 `POST https://api.deepseek.com/chat/completions`，`model=deepseek-flash`、`thinking.type=disabled`、`stream=true`；只朗读 `delta.content`，不朗读推理字段。图片使用同一模型的 `image_url` 块和内联图片，避免 QQ 临时 URL 过期。依据：[对话 API](https://api-docs.deepseek.com/zh-cn/api/create-chat-completion/)、[图像理解](https://api-docs.deepseek.com/zh-cn/guides/vision/)。本阶段无实际调用，不代表当前账号已获模型权限。

## 语音服务待选；费用只作为后续依据

本机 i9-13900HX、31.8 GiB RAM、RTX 4060 Laptop GPU 具备评估本地模型的硬件条件，但尚未测显存可用量、资源占用或中文识别质量，因此未部署模型。

腾讯云[实时 ASR](https://cloud.tencent.com/document/product/1093/48982)和[流式文本 TTS](https://cloud.tencent.com/document/api/1073/108595)可作为云端候选。TTS 支持流式文本输入与二进制音频输出，按句子合成，必须及时提供标点；`ACTION_RESET` 不会取消正在合成的旧音频，故后续仍需取消旧连接、清本地队列、拒绝过期结果。打断由有效语音触发还没有实现。

当前官方计费来源：

- [DeepSeek](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)：Flash 缓存未命中输入每百万 token 1/2 元、输出 4/8 元，分别对应空闲/高峰时段；图片折算 token 计费。
- [腾讯 ASR](https://cloud.tencent.com/document/product/1093/35686)：标准实时每月免费 5 小时，后付费首档 3.20 元/小时；V2 大模型 2.0 无免费额度、1.0 元/小时。
- [腾讯 TTS](https://cloud.tencent.com/document/product/1073/34112/)：精品首档 0.3 元/万字符，大模型首档 1.2 元/万字符；一次性免费资源需在控制台核实适用音色、是否领取、有效期和剩余量。

服务开通状态、计费开关和未来价格以用户控制台及届时官方页面为准。尚未开通、购买或调用；不使用当前文字当作付费授权。
