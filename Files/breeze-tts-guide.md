# Breeze TTS 2 语音接入（2026-10-27）

把 QQ 语音从"只有微软 SAPI"升级成"Breeze TTS 2 为主、SAPI 兜底"，并让语气跟着回复的情绪走。
原有文字聊天、表情、图片、私聊/群聊协议一律没动。

## 原来的链路

1. 模型在回复 JSON 里给 `"voice":true`，正文当朗读稿。
2. `GroupChatResponder` 在**发送闸门之外**先调合成（失败只降级为不发语音，文字照发）。
3. `QqVoice.SpeechWavAsync` → `LocalSpeechSynthesis`（System.Speech / SAPI）→ 16kHz/16位/单声道 PCM。
4. `QqVoice.Wav` 包成 44 字节头的 WAV → `OneBotSegment.Record` 只接受这一种格式 → base64 → NapCat。
5. 语音作为**独立一条消息**在文字之后发出（中间隔 500ms），不混在文字气泡里。

## 现在的链路

```
模型回复(voice, emotion) → GroupChatResponder → SpeechEngine.SynthesizeAsync
                                                        ├─ BreezeTtsClient  POST /v1/audio/speech
                                                        │    ↳ pcm/wav/mp3 解出 → 重采样 16kHz → QqVoice.Wav
                                                        └─ 失败且允许回退 → QqVoice.SpeechWavAsync(SAPI)
```

- 新文件 `Files/AudioBridge/BreezeTts.cs`：`VoiceEmotion`（8 种语气 + 指令文本）、`BreezeTtsClient`（HTTP、串行、409 重试、冷却、解码重采样）、`SpeechEngine`（选引擎、兜底语气、回退）。
- `VoiceSettings`（`QqVoice.cs`）新增 Breeze 配置项，全部有默认值，旧 `voice.local.json` 不改也能跑（默认仍是 edge）。
- `GroupChatBrain`：回复 JSON 允许一个可选的 `emotion` 字段（解析宽松，认不出就当没给），提示词只在"开了语音且开了情绪"时才介绍它。
- `GroupChatResponder` / `GroupChatRuntime`：把情绪和"这是谁的一轮"传给语音引擎；合成在发送闸门外先完成，成功后只发语音，失败才发文字。
- 全程内存，不写临时文件：没有同名覆盖、半截文件、清理失败这些事。QQ 发送协议、`WaveForm` 校验、文字聊天都没改。
- **语音只发语音**（2026-10-28 晚起）：模型一给 `voice:true` 就先合成，成功就只发这条语音，文字（朗读稿）不再重复发一遍；合成失败才退回发文字。代价是语音回复要等合成完才到。

## 备份与回滚

- 本次只改了代码和 `voice.local.json`。配置开关就是回滚开关：把 `ttsProvider` 改回 `"edge"`（或把 `enabled` 设 false）重启即可，Breeze 代码路径完全不会走。
- 上一个可用版本：`Results/local/bridge-release.json` 指向的目录；旧版本目录都留在 `Cache/releases/` 下。

## 在 Windows 上装 Breeze TTS 2

服务是独立进程，不加载进机器人主进程（模型只在服务启动时加载一次）。

1. 从 [releases](https://github.com/HoppouAI/Breeze-TTS-2.cpp/releases) 下 Windows x64 预编译包（Vulkan），解压到任意目录，例如 `E:\Tools\breeze\`。NVIDIA/AMD/Intel 卡都能用，没显卡会退到 CPU（需要 AVX2）。
2. 从 [HoppouAI/Breeze-TTS-2.cpp](https://huggingface.co/HoppouAI/Breeze-TTS-2.cpp) 下模型，推荐 `breeze-tts-2-q8_0.gguf`（3.3 GB，约 4 GB 显存）。
3. 启动（固定 `voices` 目录，保存过的音色才不会丢）：

   ```
   cd /d E:\Tools\breeze
   breeze-server.exe breeze-tts-2-q8_0.gguf --host 127.0.0.1 --port 8080 --voices-dir voices
   ```

4. 自检（模型加载完才开始监听，所以能回就说明热好了）：

   ```
   curl http://127.0.0.1:8080/health
   # {"status":"ok","sample_rate":24000,"ws_port":8081}
   ```

   服务没有鉴权也没有限流，只绑 `127.0.0.1`，别直接暴露到公网。

## 做大肥鱼的固定音色

**已有录音（推荐，声线最像）**：随便一段 5～15 秒清晰中文录音（任意采样率/声道），配上**逐字稿**（必须一字不差）：

```
curl -X POST http://127.0.0.1:8080/v1/voices ^
  -F "ref_audio=@dafeiyu.wav" ^
  --form-string "ref_text=这是我录音里说的那句话" ^
  --form-string "name=dafeiyu"
```

服务会写出 `voices\dafeiyu.breeze`，重启后自动加载；之后请求里带 `voice_id=dafeiyu` 就跳过参考音频编码（首包约 280ms，未保存时约 900ms）。

**还没有录音（先设计，再注册）**：

```
:: 1. 用声音描述设计一条参考音频（OpenAI 兼容 JSON 端点，直接要 wav）
curl http://127.0.0.1:8080/v1/audio/speech -H "Content-Type: application/json" ^
  -d "{\"input\":\"今天也想和你说说话，你最近过得好吗？\",\"instructions\":\"一位十八岁左右的活泼少女，声音清亮甜美，略带俏皮和傲娇感，语气灵动自然，有一点小得意。普通话标准，音色可爱但不尖锐，不要刻意卖萌。情绪变化丰富，说话像真实的年轻女孩\",\"response_format\":\"wav\"}" ^
  -o design.wav

:: 2. 把这条设计出来的音频当参考注册成固定音色（逐字稿就是第 1 步的 input）
curl -X POST http://127.0.0.1:8080/v1/voices ^
  -F "ref_audio=@design.wav" ^
  --form-string "ref_text=今天也想和你说说话，你最近过得好吗？" ^
  --form-string "name=dafeiyu"
```

两条命令只用来生成/登记音色，不经过机器人。确认注册成功：`curl http://127.0.0.1:8080/v1/voices`。

### 已知坑：v0.1.3 注册时把服务打崩

第一版我们照文档注册时报 `curl: (56) Recv failure: Connection was reset`，`breeze-server` 直接退出、日志里没有报错。原因是**服务自己吐出来的 WAV 头里长度字段是占位最大值**（RIFF/data 长度全是`0xFFFFFFFF`，文档写明它流式输出时不知道总长），`/v1/voices` 的参考音频解析信了这两个字段就越界崩了。两个可用的绕法：

1. 先把长度字段改对再注册（本机就是这么做的）：

```
$b = [IO.File]::ReadAllBytes('design.wav')
$i = [Text.Encoding]::ASCII.GetString($b).IndexOf('data', 12)
[BitConverter]::GetBytes([int]($b.Length - 8)).CopyTo($b, 4)
[BitConverter]::GetBytes([int]($b.Length - ($i + 8))).CopyTo($b, $i + 4)
[IO.File]::WriteAllBytes('design-fixed.wav', $b)
```

   再拿 `design-fixed.wav` 去 `POST /v1/voices`（建议加 `-H "Expect:"`），实测返回 `{"id":"dafeiyu","frames":40,"seconds":3.20,"encode_ms":280,"saved":true}`。
2. 或者绕开 HTTP，用同目录的 CLI 保存（要先关掉服务，两者都得占显存）：`breeze-cli.exe breeze-tts-2-q8_0.gguf --ref-audio design.wav --ref-text "逐字稿" --save-voice dafeiyu`。

服务崩了照原命令重启即可；已保存的 `.breeze` 会在启动时加载。

`breezeInstructions` 的默认值就是当前这段声音描述（十八岁左右的活泼少女，清亮甜美、俏皮傲娇、普通话标准、不刻意卖萌）。改这段描述，设计出来的声线会跟着变；但**只有保存成 voice 才会固定**，所以设计完要按上面第 2 条注册。

## 配置（voice.local.json，改完重启聊天进程）

项目里这个文件是 camelCase（下划线写法不受支持）；对应关系：`tts_provider`=`ttsProvider`、`breeze_base_url`=`breezeBaseUrl` 等。

| 键 | 默认 | 用途 |
| --- | --- | --- |
| `enabled` | true | 总开关；false 时模型被要求 `voice:false`，不发语音 |
| `ttsProvider` | `edge` | `breeze` 用本地服务，`edge` 用原来的微软 TTS |
| `voice` | `Microsoft Huihui Desktop` | edge/回退用的 SAPI 音色名 |
| `speechRate` | 0 | edge 语速（-3～3）；Breeze 不受影响 |
| `maximumCharacters` | 120 | 朗读稿上限；两条引擎共用同一条截断规则 |
| `breezeBaseUrl` | `http://127.0.0.1:8080` | 服务根地址（会拼 `/v1/audio/speech`）；不能带账号、查询、锚点 |
| `breezeVoiceId` | `""` | 已保存的音色 ID；留空=按指令现场设计（音色不固定） |
| `breezeInstructions` | 见文件 | 声音描述：设计时是声线，克隆时是表达风格；每种情绪会在后面追加一句语气 |
| `breezeResponseFormat` | `pcm` | `pcm`（最省事）/ `wav` / `mp3`；实际格式按响应字节嗅探，标错也不会误判 |
| `breezeTimeoutSeconds` | 60 | 单次请求超时（3～120 秒）。Breeze 按约 1 倍速生成，120 字要三十多秒，别设太小 |
| `ttsFallbackEnabled` | true | Breeze 失败时是否改用 SAPI |
| `breezeEmotionEnabled` | true | 是否按情绪换语气 |

切到 Breeze 的最小改动：

```json
{ "enabled": true, "ttsProvider": "breeze", "breezeVoiceId": "dafeiyu" }
```

## 情感配音

- 模型回复里可以多给 `"emotion"`，只能是 `normal`、`happy`、`shy`、`angry`、`sad`、`comfort`、`excited`、`serious` 之一。
- 它只进语音指令，绝不进正文、绝不进 QQ、不进聊天记录；正文照旧完全干净。
- 模型没给时按这位联系人已有的状态兜底（不猜关键词）：好感 ≥600 → 温柔；好感 ≥-199 → 自然；更低 → 不满抱怨；占有欲 ≥60 且好感 ≥200 → 吃醋式抱怨。
- 音色始终是同一个 `breezeVoiceId`，情绪只改 `instructions`，避免声线漂移。
- 关掉情绪：`"breezeEmotionEnabled": false` → 提示词不再要 `emotion`，固定声线自然朗读。edge 引擎没有指令通道，这个开关对它无效。

### 什么时候一定会发语音

模型本来自己决定要不要用语音，实测它经常保守选文字（连"发个语音试试"都只回文字），所以加了一层本地兜底：

- 触发词：`语音`、`唱`、`说句话`、`说话给我听`、`用声音`、`出声`、`念给我听`、`说两句`、`讲两句`、`读一下`、`念一下`、`听听你的声音`（`GroupChatBrain.ExplicitVoiceRequest`）。
- 命中且没开语音时不动；命中且开着语音时，**即使模型给 `voice:false`，也改成语音发**（正文就是朗读稿），同时在请求里加一句"这一轮 voice 必须为 true，不许用'嗓子还没好/只能先发文字'推掉"。
- 否定句豁免：`不要`、`别发`、`不用发`、`不必发`、`不想`、`讨厌`、`算了`。
- 其它场合仍由模型自己判断，不会每条都说话。

八种语气对应的指令文本在 `VoiceEmotion.Instruction`（`Files/AudioBridge/BreezeTts.cs`），想改措辞直接改那里。

## 失败与回退

- 服务忙（HTTP 409）：隔 500ms、1s 各重试一次；仍忙就回退。
- 任何失败（连不上、超时、非 200、不是音频、解码失败、超过 60 秒）：记 `breeze_voice_failed`，回退 SAPI 并发出去（`ttsFallbackEnabled` 为 true 时）。
- 失败后 30 秒内不再打 Breeze（`voice_fallback` 直接走 SAPI），避免每条消息都白等一次超时。
- `ttsFallbackEnabled=false`：这条语音不发，文字照常发（和原来"语音失败不影响文字"的规则一致）。
- 日志键：`breeze_voice`（成功，带字符数/情绪/字节）、`breeze_busy`、`breeze_voice_failed`、`voice_fallback`、`edge_voice`、`breeze_voice_design`（没配音色 ID 的提醒）、`voice_tone_unavailable`。
- 模型偶尔会把 JSON 写成字段不合约定；这类输出以前直接丢（用户看到的就是"没回"），现在和 `invalid_json` 一样**只重试一次**（还没发出去，安全）。真要还是失败，`reply_failed` 会带 `unknownField` 注明模型多写/写错哪个字段名（只记字段名，不记正文）。`reply` 日志也新增了 `voice` 布尔值，直接能看出这条到底发没发语音。

## 验证（本轮实测）

- 新增 17 项检查全过（`AudioBridge.Tests --case 42` 或 `--suite messaging`）：请求格式、24kHz→16kHz 重采样后能通过 `WaveForm` 的 QQ 校验、wav 与真实 mp3（本机 Media Foundation 编码后解码）都能读、Content-Type 标错时按真实字节认、409 重试与三次放弃、坏响应/空音频/超 60 秒拒绝、失败冷却、回退与回退关闭、按好感/占有欲兜底语气、配置校验与 JSON 归一化、解析与提示词、发送端只发语音不重复文字、语音失败不吞文字、**慢语音期间不先发文字**、真实 loopback HTTP 往返、**每条语音本地留档（归档字节与发出的 WAV 一致、朗读稿进日志）**。
- 套件：messaging 13/13、admin 10/10。
- 真实 QQ：用原有 SAPI 路径给账号 29070481 发了一条语音，回执 `message_id=346493434`、137804 字节，QQ 能正常播放——说明改造没有破坏原有语音发送。
- **真实 Breeze 服务（2026-10-28 22:4x，RTX 4060 Laptop + q8_0）**：用 `--probe-breeze 29070481` 走产品代码路径（`SpeechEngine` → 真实 HTTP → 重采样 → `OneBotSegment.Record`）连发 5 种语气，18 字的同一句话：`normal` 3.36s/2584ms、`happy` 3.04s/2149ms、`shy` 3.12s/2190ms、`angry` 3.04s/2169ms、`sad` 3.36s/2438ms（音频时长/墙钟），比实时快约 1.3 倍；5 个 WAV 的 SHA256 互不相同（说明语气确实改了声音），落在 `Results/local/breeze-probe-*.wav`；其中 `happy` 那条真的发进 QQ，回执 `message_id=608500537`、97324 字节。
- 机器人随后切到 `ttsProvider=breeze`、`breezeVoiceId=dafeiyu` 重启，面板读回 provider=breeze、voiceEmotion=true、lastError 空。
- **"就是不发语音"这一轮的根因**：线上两条回复的 `reply` 日志都是 `voice=false`、也没有任何 `breeze_voice`/`edge_voice`，即模型自己选了文字（她前一句刚在角色里说过"嗓子还没装好"）。用 `--probe-voice-reply` 复现（真实模型 + 真实提示词 + 她前一句的背景）：加规则前 `voice` 随机为 false，改完后三种触发（`你去发个语音试试`、`已经装好了`、`小鱼给我唱雨爱听听`）都拿到 `voice=true`，其中两种是本地兜底强制成的。
- 顺带发现 Breeze 服务被 `Ctrl+C` 结束过一次（`breeze-server.err.log` 尾部是 `^C`，不是崩溃）；重新以无窗口后台进程启动（PID 15724，health ok），重启后再次 5 段合成全部成功（2.3～2.7 秒/段）。

## 还没验证 / 已知限制

- **音色好不好听要人来听**：上面只证明了链路通、五段音频互不相同；像不像"十八岁活泼少女"、情绪是否自然，需要你听完 `Results/local/breeze-probe-*.wav` 和那条 QQ 语音后给结论。不满意就改 `breezeInstructions`（或换参考录音重注册）。
- 真实群/私聊里的语音回复还没触发过：模型要不要用语音由它自己判断，得你先聊一句它才可能发。
- 显存占用没精确测（q8_0 模型 3.5 GB，卡是 8 GB 的 4060 Laptop）；首包延迟只测到整条耗时，没有单独测首块音频。
- 不使用流式播放：要等整条合成完才发（第一版按要求不做流式）。
- 一条语音最多 120 字（沿用 `maximumCharacters`）、60 秒；长文本靠服务端 `split_chars` 按句切分，客户端不再二次切分。
- 服务一次只生成一条，客户端串行发送：多个会话同时要语音时会排队，后来的一条等前一条合成完（文字不受影响）。
- 语音触发以模型的判断为主；只有命中那组明确要声音的词（`语音`/`唱`/`说句话`…）才会本地强制发语音。更隐晦的表达（"来一段"）仍可能只回文字。
- Breeze 服务是独立进程：现在机器人启动时会检查并拉起（见文末 2026-10-28 晚一节）；服务被单独关掉时，机器人仍会回退 SAPI 并记 `breeze_voice_failed`/`voice_fallback`。
- PC 音箱那边（`WindowsVoiceSpeaker`）仍是 SAPI，属于另一条链路，本次没动。

## 部署（2026-10-28 22:4x）

- Breeze 服务：解压在 `E:\下载\breeze-tts-2-v0.1.3-windows-x64-vulkan\breeze-tts-2-v0.1.3-windows-x64-vulkan`，模型 q8_0，启动参数 `--host 127.0.0.1 --port 8080 --voices-dir voices`。现在以独立后台进程运行（Vulkan，warmup 27.7 s），stdout/stderr 记在 `Results/local/breeze-server.out.log`、`breeze-server.err.log`；音色文件是 `voices\dafeiyu.breeze`。机器重启后要照原命令再起一次。
- 机器人：发布版本 `Cache/releases/20261028-134926-5e3da803`，进程 31120；`ttsProvider=breeze`、`breezeVoiceId=dafeiyu`、`breezeEmotionEnabled=true`，面板读回 running、oneBot connected、provider=breeze、lastError 空。
- 切回微软 TTS：把 `ttsProvider` 改回 `edge`，面板 stop 再启动。

## 2026-10-28 晚：换成混合声线、语音只发语音、Breeze 随机器人启动

### 音色

- 现在的固定声线是 `dafeiyu2`：用候选 B 的设计音频注册（中位音高约 221 Hz，"甜"和"成熟"之间的那种）。旧 `dafeiyu`（236 Hz、十八岁少女描述）保留在 `Results/local/breeze-voices/dafeiyu.breeze`；回滚只改 `breezeVoiceId` 再重启。
- `breezeInstructions` 改成纯表达风格（原文是声音描述）："语气自然放松…不要播音腔，不要刻意卖萌或撒娇"。实测同一句话在两种指令下中位音高 224 / 220 Hz，声线没有漂。
- 试听文件在 `Results/local/voice-candidates/`：`a-current-236hz`、`b-shaoyu-180hz`、`c-blend-221hz`、`d-blend-en-228hz`、`e-loli-330hz`、`f-mixed-ref-311hz`。现在用的是 B。
- 试过"少御参考 + 萝莉参考拼成一条再注册"，想让模型自己混出中间声线：**失败**，出来 311 Hz，直接选了偏高那一侧。这条路不用再试；真要更准只能给一段 5～15 秒、喜欢的录音 + 逐字稿做真声克隆。

### 唱歌

- Breeze 2 是文字转语音，不是歌声合成：`/v1/audio/speech` 没有音高/旋律参数，`speed` 官方直接标 `ignored`。同一句歌词实测：当话念 4.5 s / 11.0 半音（音高连续滑动）；让它唱 9.8 s / 4.5 半音（音高停成几级台阶）；cfg 2.0 时 12.6 s / 4.3 半音、中位音高 394 Hz（窜到童声区且更平）。用《小星星》的旋律也试了，没有出现对应音程。
- 结论：没有接"唱"模式（按你的要求不做）。真要唱只能用 `/v1/audio/convert` 把一段真人歌声换成她的音色——官方说旋律保存是 hit-or-miss，要 `keep_acoustic` 1～2 并把歌词传给 `--text`，属于实验特性，还需要人声素材。

### 语音只发语音

- 发送端改成：`voice:true` 时先等合成，成功就只发一条 record，文字/表情/图片都不再重复发；合成失败才退回发文字。
- 代价：语音回复要等合成完才到（本机 3～6 秒），不再"文字先到、语音后补"。
- 明确要语音时本地硬兜底：模型给 `voice:false` 也改发语音；触发词新增 `说两句`、`讲两句`、`读一下`、`念一下`、`听听你的声音`。
- 测试：`--case 42` 17/17（含"只发一条 record、不重复文字""慢语音期间不先发文字""语音失败回退文字""语音留档"），messaging 13/13、admin 10/10。

### Breeze 随机器人启动

- 新增 `Files/Start-Breeze.ps1` + `Files/breeze.local.json`（本机路径，`*.local.json` 已被 gitignore）。`Start-GroupChat.ps1` 在启动机器人前调用它：健康检查通过就跳过，没运行就按配置拉起；语音没启用、引擎不是 breeze 或地址不是本机时直接跳过。这一步失败只打黄字，不影响聊天启动。
- 音色目录从安装目录的 `voices` 换成项目内 `Results/local/breeze-voices`（`dafeiyu`、`dafeiyu2` 已复制过去），启动参数 `--voices-dir` 指这里；**安装目录本身不动**——启动前后逐文件对比 0 差异（`Results/local/breeze-folder-before.txt` / `-after.txt`）。服务日志在 `Results/local/breeze-server.out.log`、`breeze-server.err.log`。
- 机器重启后仍然只需要双击 `start-chat-background.cmd`：它会先把 Breeze 拉起来再启动机器人；模型加载约半分钟，这期间语音会回退微软 TTS。
- 机器人停止不会顺带停 Breeze（服务是共享的）；要停就结束 `breeze-server.exe`。
- `Start-Process` 在某些"环境变量大小写重复"的终端里会直接报错，脚本里加了 .NET 启动兜底（那种情况不重定向日志）。

### 本轮部署与验证

- 发布版本 `Cache/releases/20261028-152008-230ec984`，机器人进程 79636；Breeze 进程 84616（新 voices 目录，health ok）；面板读回 running、oneBot connected、provider=breeze、voiceEmotion=true、lastError 空。
- 用 `--probe-breeze` 走产品路径合成 5 种语气：normal 5.04 s、happy 3.92 s、shy 3.92 s、angry 6.24 s、sad 3.36 s，SHA256 互不相同，中位音高 197～222 Hz（新声线）。
- 真发一条到 QQ：`message_id=1855472378`、125484 字节（happy 那条），证明新声线能正常发进 QQ。
- 启动脚本本身经 `Start-GroupChat.ps1` 实际执行过一次（读回"Breeze 已在运行"），并单独验证过"未运行就拉起"分支（WMI 干净环境下启动成功，health ok）。

### 再成熟一档（2026-10-28 23:32）

- 换成 `dafeiyu3`：把 B 的参考音频整体降 1 个半音（插值变慢，音高和共振峰一起降）后重新注册，还是同一把嗓子，只是更成熟。实测合成中位音高 192 Hz（B 是 221），比单纯改指令有效——指令不改声线，只能改语气。
- 已经准备好更成熟一档的 `dafeiyu4`（B 参考降 2 个半音，实测 184 Hz），换它只改 `breezeVoiceId` 再重启；两个都保留在 `Results/local/breeze-voices/`。
- 另设计了一条 196 Hz 的新声线（`Results/local/voice-lab/m3-mature.wav`），但那是另一个人，不是 B 变老，所以没默认用。
- 试听文件：`Results/local/voice-candidates/mature-1-dafeiyu3-192hz.wav`、`mature-2-dafeiyu4-184hz.wav`、`mature-3-design-196hz.wav`。
- 部署：机器人进程 95436（23:32 重启），面板读回 running、oneBot connected、provider=breeze、lastError 空；新声线发进 QQ 一条，`message_id=655423845`、122924 字节。

### 最终选定 mature-3（2026-10-28 23:38）

- 就是上面那条 `mature-3-design-196hz.wav`（m3 设计声线）：参考 196 Hz，注册成 `dafeiyu5` 后合成实测 198 Hz，音色一致。
- 复现参数：设计描述 "一位二十五六岁的年轻女性，低柔有磁性的女声，声音沉稳一些，说话从容，尾音自然，带一点傲娇，不装嫩，普通话标准"，seed 42；样本在 `Results/local/voice-lab/m3-mature.wav`，注册文件 `Results/local/breeze-voices/dafeiyu5.breeze`。
- 机器人进程 86516（23:38 重启），面板读回 running、oneBot connected、provider=breeze、lastError 空；新声线发进 QQ 一条，`message_id=125813446`、105004 字节。
- 回滚：`dafeiyu2`（B，221 Hz）、`dafeiyu3`（192 Hz）、`dafeiyu4`（184 Hz）都还在，改 `breezeVoiceId` 重启即可。

### 长语音误回退微软（2026-10-29 凌晨）

- 现象：发"你发语音哄我睡觉好不好"，回了一条 23 秒的语音，但是微软 SAPI 的声音。
- 根因：Breeze 本机生成速度约 **0.9～1.0 倍速**——82 字、21 秒音频要生成 20 秒上下。旧的 `breezeTimeoutSeconds=20` 正好卡在边界：这次服务端 00:04:22 起算，生成 20.8 秒音频到 00:04:43 才结束，客户端 20 秒到点先超时（日志 `breeze_voice_failed` → `type=TimeoutException`、`code=timeout`、`emotion=comfort`），随后 `voice_fallback` 走 SAPI。同一段文字重跑只要 19.5 秒——不是 Breeze 坏了，是超时线太紧。
- 修复：默认超时和线上配置都改成 60 秒（120 字最多三十多秒，留足余量）。会话预留是 150 秒（`replyMaxCharacters>448`），等语音不会把预留等过期。
- 23 秒那条 SAPI 输出本身就是 82 字、747404 字节；留档为 `voice-sent/20261029-000444-534-edge-none-82.wav`，日志里连朗读稿都有，能直接对照。

### 语音留档（2026-10-28 深夜）

- 起因：QQ 自己不保存「已发出的语音」，本地也没有任何一份，想回听只能去聊天记录里翻。
- 现在 `SpeechEngine` 只要配置了归档目录（`GroupChatRuntime` 传 `Results/local/voice-sent`），**每条合成成功的语音都写一份**，文件名 `yyyyMMdd-HHmmss-fff-<breeze|edge>-<情绪>-<字数>.wav`，例如 `20261028-234501-120-breeze-happy-27.wav`。
- 同时记 `voice_saved` 日志：`file`、`engine`、`emotion`、`characters`、`bytes`、`text`（**朗读稿正文**，之前日志里没有）。写盘失败只记 `voice_save_failed`，不影响发送。
- 留档点在合成成功之后、发送之前：正常聊天每条都对应 QQ 里真实发出去的那条；`preview`/`--probe-*` 自己 new 的 `SpeechEngine` 不带这个目录，所以不会写。
- 开启前的语音没有本地副本，补不回来；只从现在起生效。
- **只留七天**：启动时清一次，之后每 6 小时清一次，删掉 `voice-sent` 里超过 7 天的 `.wav`，日志记 `voice_archive_cleaned`（带删除数量）。要长期保存就自己另存——这份留档只是给她回听用的临时副本。
- 清理只动 `voice-sent` 顶层的 `*.wav`，不碰 Breeze 音色（`breeze-voices/*.breeze`）、探针样本和聊天日志。

