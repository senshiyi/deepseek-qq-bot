# 第一里程碑独立代码审查

审查时间：2026-10-19 10:02（Asia/Shanghai）。审查方式：只读审查产品代码；只在 Cache 创建无硬件复现。项目起点没有 Git 历史，按当前新增文件全集审查。

## 结论

当前审查范围内未发现尚未解决的 M1 阻断缺陷。审查过程中确认 1 项 P1、1 项 P2，均已由实现者修复，并通过本审查者独立的无硬件针对复验。可以交付联调工具；这不构成真实 QQ 通话音频已验收的结论。

## 范围与边界

- 需求依据：Files/milestone-1-plan.md。
- 审查源码：Files/AudioBridge 下 BridgeConfig.cs、NativeAudio.cs、PcmQueue.cs、PlaybackCoordinator.cs、Program.cs、QqProcessTree.cs、SignalMeter.cs、TestTone.cs，以及项目文件/锁文件。
- 审查验证入口：Files/AudioBridge.Tests/Program.cs、测试项目文件，项目根 run.ps1、start.cmd。
- 关注取消、会话到期、迟到结果、队列边界、Starting/Dispose 原生竞态、异常与资源清理、CLI 配置。
- 没有采集真实 QQ、没有打开或播放硬件音频、没有登录/操作 QQ、没有安装 VB-CABLE、没有改系统默认设备或其他设置。替身采集只引用当前测试进程 ID 并监视其退出事件。
- 普通 QQ 固定音频 M1 是本次范围；未来 NapCat 收图、语音识别/合成或 DeepSeek API 均未作为当前缺陷。

## 已修复的问题

### P1：会话到期不能独立取消正在播放的音频（已关闭）

原问题位置：Program.cs 播放分支及 PlaybackCoordinator.cs 的播放 CancellationTokenSource 创建。先前播放 token 与会话 lifetime 无关联；若命令循环正在等待 L 的 StopListening，播放要等循环进入 finally 才停止。底层激活/清理慢或卡住时，长 WAV 可以越过测试时限继续输出。

初始独立复现：复制当时 Program 控制流程到 Cache，控制台和 NativeAudio 全部改为内存替身；依次输入 P、L、L，并将收听停止任务挂起。设会话 200ms，实测 496ms 时 fake_player_still_active=True、run_completed=False，释放收听后 503ms 才停止。该结果证明取消依赖循环推进，未打开硬件。

修复位置：

- Files/AudioBridge/Program.cs:94 将 lifetime.Token 传给 PlaybackCoordinator。
- Files/AudioBridge/PlaybackCoordinator.cs:15 接收 lifetime；:28 拒绝过期会话启动；:33 创建关联 lifetime 的播放 token。
- Files/AudioBridge.Tests/Program.cs:127 的 Lifetime 回归覆盖不调用 StopAsync、没有命令循环推进时的取消及过期启动拒绝。

独立复验：当前 Program 和 PlaybackCoordinator 源码的同一替身场景，200ms 到期，fake_player_stopped_at=212ms；497ms 时收听仍挂起、run_completed=False，但 fake_player_still_active=False。取消已不再依赖命令循环。具体毫秒只表示该无声进程调度观测，不表示 QQ/远端音频停止延迟承诺。

### P2：异步激活及同步初始化前半段绕过取消/超时（已关闭）

原问题位置：旧 NativeAudio.cs:48-51 的 BuildAsync 不可取消、位于 try/finally 和超时防护之前；StartRecording 内同步 InitializeAudioClient 也在原先 startupWait 计时前执行。DisposeRecorderAsync 的同步前半若阻塞，后续 cleanup.WaitAsync 也尚未开始。由此 L 停止、退出或到期可能长期等待，且无法进入“资源未确认释放”的故障提示。

旧版独立复现：内存构建器让 BuildAsync 保持未完成，取消 token 后仍 capture_completed=False。这里复现的是取消控制流程缺口，没有声称本机驱动目前实际发生挂起。

修复位置：

- Files/AudioBridge/NativeAudio.cs:44-55：被放弃的激活只在迟到后 Dispose，绝不 Start；后台任务异常由 ContinueWith 读取 Exception。
- :67-94：整个 BuildAsync 调用放在 Task.Run；外层 5 秒和取消等待；尚未确认完成时返回明确 TimeoutException，并保留原始激活失败。
- :142-168：清理先等待唯一一次 startup 结束；初始化失败留在 Starting、未创建线程的情况不无限等待 Starting；成功 Start 后等待 Starting 交接再 Dispose。
- :178-207：整个 StartRecording 放在 Task.Run，并为前台等待设取消/超时；排队但尚未执行的 Start 先检查 completion，不在会话已结束后启动。
- :214-236：先终止 completion 使迟到 OnData 不交付；整个清理函数放在 Task.Run 覆盖同步前半；放弃启动或清理超时后持续观察后台异常。
- Files/AudioBridge/Program.cs:112-129、:185-209：底层 Timeout 后禁止同会话重开，并准确提示尚未确认释放。

独立针对复验结果：

| 场景 | 实测结果 |
|---|---|
| 激活未完成时取消，随后迟到返回的对象在 Dispose 抛错 | 前台返回 TimeoutException；迟到对象 Starts=0、Disposes=1；数据回调=0 |
| StartRecording 同步阻塞时取消，随后解除阻塞 | 前台返回 TimeoutException；Starts=1、Disposes=1；迟到数据回调=0 |
| StartRecording 先置 Starting 后同步抛初始化错误 | 原 IOException 保留；Dispose 恰好一次；未无限等待 Starting |
| 迟到清理异常观察 | 静态确认所有放弃后台任务的 fault 续接均读取 Exception；触发迟到 Dispose 抛错并强制 GC 的辅助观测中未出现未观察异常 |

测试对“前台已经完成”另有检查，避免把测试自身 WaitAsync 超时误判成产品返回 TimeoutException。GC 辅助观测不能单独证明任意调度下的异常观察，因此最终结论同时依据源码异常续接检查。

## 其他核查结果

- PcmQueue 对代号、已完成流、容量和 PCM 帧对齐实施锁内检查；重置会清除旧队列，旧代号入队/完成不能污染新流。当前固定音源每次创建新队列，不复用旧播放 provider。
- PlaybackCoordinator 串行处理开始/替换/停止/静音；新播放器等待旧播放器清理；解除静音不恢复旧声音。当前调用路径没有发现过期音频串入新播放的额外缺口。
- 配置限制增益 0～0.5、固定音 100～5000ms，run 限制有限正时长且最多 30 分钟；选择明确的 QQ 根进程与活动 render 设备，未修改系统默认设备。
- run 不自动收音/播放；录音不落盘；收听数据只流向电平统计，没有直接回送到输出的代码路径。
- 启动脚本沿用项目目录并向程序转发参数；未发现影响 M1 的配置/入口缺陷。本审查未重复主代理已完成的只读设备枚举和全套模拟测试。

## 验证材料和实际限制

- 独立复现目录：Cache/milestone-review-probe/。该目录内 CopiedProgram.cs、CopiedPlaybackCoordinator.cs、CopiedNativeAudio.cs 是审查用副本，命名空间/控制台/音频构建器替换为内存替身，不属于产品代码。
- 最终针对复验输出：Cache/milestone-review-probe/review-probe-final-output.txt。
- 修复 P1 后、修复 P2 前的中间输出：Cache/milestone-review-probe/review-probe-output.txt。
- 全套模拟测试 12/12 由主代理报告；本审查者没有重复运行该套件。
- 原生驱动或系统调用若永久不返回，应用不能安全强制终止该原生执行；当前实现返回资源未确认状态、抑制迟到数据、禁止同会话重开并保留后台清理。该限制没有被无硬件测试消除。
- 未验证真实 QQ 远端讲话采集、远端听见固定音、进程采集排除本机输出、VB-CABLE 真实路由、QQ/远端缓冲及耳机防回声效果。这些仍需指定群及获同意时段的人工作业。

## 最终审查快照 SHA-256

| 文件 | SHA-256 |
|---|---|
| Files/AudioBridge/NativeAudio.cs | 39267599D409849C924CD0B9654B424422BA177394E99A1AF31B1648DAD69E16 |
| Files/AudioBridge/PlaybackCoordinator.cs | A033414B0C587E2D77BF3E6C4CE72CF6A2EBA1150475A078830EF59348E14132 |
| Files/AudioBridge/Program.cs | 2BAE096DABDD846856CDF5FF7DFB7ECF8D0403A155B3723C4A5B0C3B60B27A60 |
| Files/AudioBridge.Tests/Program.cs | 0366CC507638BF06B30D3156E69E5241E7B246BC27B84BBDDBCA28B2B1EFF969 |
| run.ps1 | 4598DF3A040105425C57E26EFE5E3998A59E0C912FFC0E6D60ECFD20354D87C4 |
| start.cmd | 700DB31B5F22DCE33A917BF9D6A4C094DFF683A5BE69137A392E03D535902E3C |
