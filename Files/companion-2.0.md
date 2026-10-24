# 心脑增量：心情、三级日记与三种主动（2026-10-23）

来源：用户供稿《大肥鱼教程1：增加活人感和高级功能》描述的活人感设计。本轮只补代码里原本没有的三块：日记与遗忘、心情、定时主动唤醒；人设文本、好感度数值、云端部署、语音均未改动。

## 与教程的对应关系

| 教程里的 | 本轮实现 |
| --- | --- |
| 日记分普通/重要/非常重要，分别一周、一个月、永不遗忘 | `DiaryLevels` 固定这三级的生命周期，过期条目由清理循环删除 |
| 日记记录当时的心情和不好意思当面说的话 | 每条日记带 `Mood` 与 `Unsaid` 字段 |
| 每天凌晨 4 点检查当天聊天记录补记 | `CompanionRuntime.AuditAsync`：过整理时间后按联系人把新私聊整理成日记与心情，一天一次 |
| 心情由聊天内容决定，影响语气和想不想继续聊 | 模型整理时一并输出心情短语，作为私聊回复的内部状态参考 |
| 三种主动：约定的、想念的、随性的 | 本地只到点唤醒，三条理由写进提示词，要不要开口仍由模型决定 |
| 好感度 | 按教程 2.0 的说法不做数值好感度，本轮不引入 |

## 新增与改动

- `Files/AudioBridge/CompanionStore.cs`：按联系人的心情与日记，独立文件 `Results/local/companion/companion-<QQ>.json`；互斥锁、原子写、拒绝符号链接、1MiB/200 条/64 人上限；`ShouldWake` 为纯函数闸门，`AuditBoundary` 计算当天的整理时刻。
- `Files/AudioBridge/CompanionJournal.cs`：日记整理的提示词、严格 JSON 解析（多余字段、未知等级、超长内容一律拒绝，且报错不回显模型原文）。
- `Files/AudioBridge/CompanionRuntime.cs`：30 秒一轮；先整理日记再判断要不要唤醒。闸门 = 启用 + 未暂停 + 联系人不忙且无待处理消息 + 距上次消息超过 `IdleMinutes` + 距上次主动超过 `CooldownMinutes` + 当日次数未达 `DailyLimit`。生成期间对方又说话了就放弃这条主动消息，改走正常回复。
- `Files/AudioBridge/GroupChatRuntime.cs`：创建心脑存储，把心情/日记拼进**明确触发的私聊**参考数据；主动消息复用同一个 OneBot 客户端、私聊限频记录和原文归档；回执未知时只记一次不重发。群聊请求不读取心情与日记。
- `Files/AudioBridge/LocalAdminCompanion.cs` 与 `LocalAdminServer.cs`：`GET /api/companion`、`GET/PUT /api/companion-settings`、`DELETE /api/companion/{QQ}/entries/{id}`。
- 面板新增“心脑”页：五个开关/数值（整理日记、主动唤醒、整理小时、未聊阈值、最短间隔、每日上限）与按联系人的心情、整理/主动时间、日记条目和删除按钮。设置存 `companion.local.json`，保存即生效，不需要“应用设置”。
- `Files/AudioBridge.Tests/CompanionChecks.cs` 与 `AdminHttpChecks.CompanionApi`：12 项心脑检查 + 1 项面板接口检查。

## 验证

- 以 -p:BaseOutputPath 输出到 Cachecompanion-build 构建（正式 bin 下的 DLL 被运行中的宿主 20032 锁定，未停服务）。
- 全量无硬件套件 47/47 通过（含新增 13 项 companion 与 1 项 admin HTTP companion），0 警告、0 错误。
- 覆盖：三级过期与遗忘清理、损坏文件不重置、心情与计数按联系人隔离、唤醒闸门（空闲/冷却/上限/繁忙/暂停/模型拒绝）、每日整理只写一次、面板 GET/PUT/DELETE 与非法小时拒绝。

## 尚未验证

- 没有用真实模型跑过整理与唤醒，也没有发送过真实 QQ 主动消息；JSON 约定、闸门和发送委托均由合成提供方验证。
- 新面板页未在真实浏览器里打开过（本轮没有可用的浏览器工具），只做了 JS 语法检查与 HTTP 接口验证。
- 主动消息只发文字，不带表情；群聊不使用心情与日记；“多发几条短消息”的分条发送未实现。
- 正式宿主仍是旧程序，需要重启后才生效。

## 部署（2026-10-23 23:31）

- 面板 `stop` 正常停止旧宿主 PID 20032，替换运行目录的 AudioBridge.dll/.exe，再用 `start-chat-background.cmd` 以独立进程重启（旧文件备份在 `Results/local/companion-before-20261023-232522`）。
- 读回：running=true、paused=false、QQ connected、engine=school、model=deepseek-v4-flash、群 1095601728、restartRequired=false、lastError 为空。
- 随后把“整理日记”和“主动唤醒”拆成两个开关，又一次正常停止并替换，新宿主 PID 48692；面板读回 audit=true、enabled=false、整理小时 4。
- 运行 DLL SHA-256 = 6D969AE1E0554268954145E200B16A9D912D5790A006DCDA21855621B1153BD0，与最终验证候选一致；面板已提供新页面（含两个开关）与 `/api/companion`。默认整理日记开启、主动唤醒关闭。证据 `Results/local/companion-deployment-20261023.json`。
- 重启后用面板的人格草稿接口真实调用了一次学校模型（44 秒返回草稿），确认宿主的模型通路可用；没有发送真实 QQ 消息，草稿未保存、30 分钟后失效。
- 本机环境里同时存在 NO_PROXY 与 no_proxy，PowerShell 的 Start-Process 会因重复键失败；本次改用 WMI 以独立 cmd 启动，不影响程序本身。若以后从这种环境重启，先统一大小写再启动。