# DeepSeek QQ 机器人

本项目当前提供授权群聊、白名单私聊、识图、表情、定时消息和本机管理面板。语音代码与历史材料保留，消息启动入口不启动语音。运行配置以本机面板和已忽略的本地配置为准；历史测试不表示当前账号在线。

## 日常使用

- 双击 `start-chat.cmd` 启动消息宿主；已有宿主时打开面板，避免重复监听。
- `start-chat-background.cmd` 在后台启动；关闭网页不会停止机器人。
- `open-panel.cmd` 打开管理面板。暂停、应用设置、停止机器人均通过面板操作。
- `start.cmd help` 只显示启动与维护帮助，不构建、不连接模型。`start.cmd bridge-help` 查看所选程序版本的全部命令。
- 更改角色或运行策略后按面板提示应用设置。心脑设置保存后直接生效。

首次使用须按 [NapCat 部署说明](Files/napcat-messaging-setup.md) 准备本地账号与配置。账号、群号、私聊联系人均以明确白名单为准，密钥放环境变量或已忽略的 `.env`；不要把本地配置、数据库和聊天数据提交到 Git。

## 构建、检查与发布

在项目目录打开 PowerShell：

```powershell
# 独立开发构建，不覆盖正在运行的版本
.\Files\Build-Bridge.ps1

# 全部无硬件回归与管理页面状态检查
.\Files\Test-Bridge.ps1

# 可选择一组后端回归；页面检查仍会运行
.\Files\Test-Bridge.ps1 -Suite admin
.\Files\Test-Bridge.ps1 -Suite messaging

# 生成独立发布目录；不改变下次启动的选择
.\Files\Build-Bridge.ps1 -Target Release

# 生成并选为下次启动版本；当前进程继续运行
.\Files\Build-Bridge.ps1 -Target Release -Activate
```

需要 .NET 10 SDK；页面检查使用 Node.js 内置测试功能，不额外安装前端框架。依赖按锁文件恢复，本地缓存留在 `Cache/`。开发与回归构建各用唯一目录，正式产物在 `Cache/releases/<版本>/app`，同目录的 `release.json` 记录源码提交、构建时工作区状态及程序哈希。下次启动选择记录在 `Results/local/bridge-release.json`，启动时核对程序哈希。尚未选择新发布版本时，兼容使用现有旧版产物，并明确显示提示。

选择发布版本不会重启已运行的服务。正式切换须先确认没有尚未完成的回复，再通过面板停止旧宿主并重新启动。若需回到旧发布版本，备份并将 `Results/local/bridge-release.json` 的 `directory` 改为已保留的 `Cache/releases/<旧版本>`，再按同样步骤启动；不要覆盖或删除仍在使用的目录。

无硬件回归的每套检查在独立子进程执行，每套上限 90 秒。超时后结束进程树并等待退出，再进入后续套件，避免旧检查继续占用端口或运行后台任务。检查使用本机模拟模型和 OneBot，不发送真实 QQ 消息、不打开音频设备、不调用收费模型。

## 心脑与后台状态

心脑页按联系人查看心情、日记和主动私聊策略。普通日记一周、重要日记一个月、非常重要日记长期保留；原始私聊记录与日记提炼是两个独立机制。群请求不读取联系人的私密日记。

每日主动次数与日记整理时刻固定采用上海时区。日记按完整消息分批，成功写入草稿、心情和精确消息位置后才推进；尚未处理的批次在后续检查中继续，仍受原始记录的保留期限和容量限制。概览及心脑页展示后台任务状态、最近成功时间和错误类型，模型选择安静仍属于成功执行；失败状态表示需查看日志，不等于普通聊天也已停止。

## 当前说明与历史证据

专题文档保留各阶段细节；当前启动与发布规则以本 README 为准，限频以面板和本地配置为准。历史固定阈值不作为现行配置。

| 用途 | 说明 |
| --- | --- |
| 面板、角色、记忆、表情与运行控制 | [管理面板使用说明](Files/admin-panel-guide.md) |
| 群聊、私聊和模型入口 | [消息使用说明](Files/group-messaging-guide.md)、[学校 API](Files/school-api-guide.md) |
| 图片问题 | [识图接入](Files/vision-guide.md) |
| 心脑功能与原始验收范围 | [心脑增量记录](Files/companion-2.0.md) |
| 本轮维护与实际检查 | [维护结果](Results/maintenance-20261024.md)、[实施清单](Files/maintenance-plan-20261024.md) |
| 面板与消息历史验收 | [面板验证记录](Files/admin-panel-results.md)、[消息验证记录](Files/group-messaging-results.md) |
| 维护前 README 原文（包含已被取代的说明） | [历史归档](Files/project-history-20261024.md) |
| 语音历史与验证边界 | [本地语音说明](Files/local-voice-guide.md)、[音频路由](Files/audio-routing.md) |

本轮源码维护、自动化回归、发布产物准备及真实业务部署分别记录。自动化通过不能替代真实 QQ 收发或语音通话验证。
