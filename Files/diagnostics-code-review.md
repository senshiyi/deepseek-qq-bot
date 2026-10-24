# 有限诊断命令增量审查

结论：当前新增 probe/tone 实现未发现阻断缺陷。只审查新增 DiagnosticsCommands.cs 与 Program.cs 的命令接入，原 M1 审查结论复用；没有重复执行旧测试套件。

## 代码核查

- DiagnosticsCommands.cs:11-16：seconds 限有限的 1～30 秒；tone-at 限有限、非负并给完整测试音留时长；音量、测试音长度继续使用原配置校验。
- :18-20、:25-45：probe 期限关联调用者取消，采集结束时同步取消输出 token；预定 tone 在延时之后、ToneAsync 入口、播放 worker 和原生播放入口均检查取消。先采集失败不能留下稍后播放的定时音。
- :49-66：等待采集和可选测试音共同结束；正常到期与外部取消区分；底层异常/超时不报告已确认释放。
- :79-98、:102-116：单次 tone 直接等待 NativeAudio.PlayAsync；调用者取消或播放期限后取消播放，再最多等待 5 秒清理。仍未退出则明确抛出“资源尚未确认释放”的 TimeoutException，后台异常被观察。
- Program.cs:41-62：probe/tone 配置及测试群检查、Ctrl+C 事件转取消 token、finally 解除事件订阅；命令不依赖交互按键循环。

## 独立无硬件复验

使用当前 DiagnosticsCommands.cs 的源码副本，只替换命名空间并为 NativeAudio 提供纯内存替身。7/7 通过：

1. NaN、无穷大、越界 seconds/tone-at 均拒绝；1/30 秒及 tone-at 合法端点接受，校验不访问音频。
2. 采集先失败，原 IOException 传播，随后播放调用次数为 0。
3. 1 秒 probe 期限取消采集并等待结束，没有可选 tone 时不播放。
4. probe 外部 token 取消传播 OperationCanceledException，预定 tone 被取消，播放调用次数为 0。
5. 单次 tone 在内存播放任务完成前不返回，完成后仅一次播放。
6. 单次 tone 外部 token 取消被观察，未伪报正常播放结束。
7. 播放不响应取消时，约 5 秒后返回 TimeoutException，消息明确资源未确认释放；后台迟到异常由观察续接处理。

证据：Cache/diagnostics-review-probe/verification-output.txt（7/7 结果）；同目录 command-logs.txt（实际命令状态日志）。模拟工程及源码副本也保留于该目录。

## 边界

本审查未打开音频设备、未收音/播放、未操作 QQ 或安装驱动。Ctrl+C 的事件接线经过静态核查，取消行为通过等价 token 模拟；没有发送真实控制台 Ctrl+C。原生永久挂起仍无法被安全强制终止，超时不等于已释放。普通 QQ 真实音频、VB-CABLE 路由和远端可听性均待用户实际入群后按已有授权验收，模拟结果不构成这些证据。

## 审查快照 SHA-256
DiagnosticsCommands.cs: B2EBEDB7764E0E741A1F26D25295D33037DFC4B8F8D5212AA4273D9B6FE9D69F

Program.cs: 8B0B188A13EEC4AACF42BC407ED6BEA0A0D064374F5966BF926467EA78D869D3

