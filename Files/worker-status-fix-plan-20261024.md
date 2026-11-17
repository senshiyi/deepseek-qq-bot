# 2026-10-24 消息处理状态与错误原因修复

用户看到消息处理 `failed / InvalidDataException` 后要求修复。本轮沿用已批准维护机制，修复状态混淆与错误诊断，不猜测历史错误的具体来源。

## 范围及接口

- 生命周期 `state` 只由循环启动、终止、宿主取消决定；单次任务失败不表示循环终止。
- 后台快照保留既有字段，新增 `lastResult`、`lastResultAt`、`lastErrorCode`、`lastErrorMessage`，分别表达最近任务结果、发生时间和固定脱敏原因。
- 后续正常任务清除本次错误；真正终止的监控拒绝迟到回调，避免失败被掩盖。初始、暂停、退避、取消不伪造处理成功。
- 模型 JSON/结构/正文/表情 ID 拒绝与本地表情数据错误区分，稳定白名单码映射固定中文；不记录模型原文、聊天正文、路径、地址、凭据或任意异常 Message。
- 普通与定时消息共用同一监控来源，概览平级错误字段与后台行一致；定时处理返回失败时也不能记录为成功。
- 两个页面分别显示循环状态和最近处理结果；兼容旧版本快照时不推断循环仍在运行。
- 保留严格回复校验、预算、消息准入、取消和投递不确定性规则。不新增模型重试、自动重发或收费测试。

## 步骤与验收

1. [ ] 修复监控生命周期、脱敏分类和普通/定时消息接线，补单次失败后继续、真正退出、取消、晚回调及来源分类回归。
2. [ ] 修复概览和心脑页面展示，补运行中但最近任务失败、真正终止、下一次成功清错、旧接口兼容和纯文本渲染回归。
3. [ ] 隔离构建和无硬件回归，独立审查，必要修复后仅重跑受影响检查。
4. [ ] 生成独立发布版本、核对哈希与帮助入口，备份并更新下次启动选择，保留当前运行进程；写入实际结果和部署边界。

## 文件分工

- `Files/AudioBridge/BackgroundTaskMonitor.cs`：循环生命周期及最近任务结果。
- `Files/AudioBridge/ReplyFailure.cs`：固定错误码和中文原因（如实际采用该文件名）。
- `Files/AudioBridge/GroupChatBrain.cs`、`GroupChatResponder.cs`、`GroupChatRuntime.cs`：分类来源与宿主接线。
- `Files/AudioBridge/AdminWeb/app.js`、`index.html`：两个页面统一展示。
- `Files/AudioBridge.Tests/BackgroundRuntimeChecks.cs`、`GroupChatFlowChecks.cs`、`AdminWeb/companion.test.cjs`：行为回归。
- `Results/worker-status-fix-20261024.md`：本轮结果；历史维护记录保持原样。
