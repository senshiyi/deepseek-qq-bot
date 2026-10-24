# 学校引擎识图接入（2026-10-23）

用户问"学校API不是支持识图了吗，为什么小鱼还不会看"。查证结果和这一轮改动如下。

## 为什么原来不会看

- 学校网关实际只有两个模型：`deepseek-v4-flash`（日常聊天）和 `qwen3.8-27b`（能看图）。本轮实测把合成图发给 `deepseek-v4-flash` 直接返回 HTTP 500（upstream Internal Server Error），所以聊天模型本身不能读图。
- `SchoolModelReply` 原来写死了"只能读取文字"的提示词，学校引擎整条路径没有任何图片能力。
- 私聊图片在入口就被丢掉：`GroupImageInbox` 只接受 `message_type=group`，`GroupChatRuntime` 里也只对非私聊调用 `AddEvent`。
- 群聊图片还要消息里出现"图片/截图/这张/图里/看图"等关键词才去取最近 3 分钟的图，普通人发图问"这是啥"根本不触发。

## 现在的做法

1. `SchoolModelSettings.VisionModel`（可选，当前 `qwen3.8-27b`）：配置学校上网关里能看图的模型；留空即保持纯文字。
2. `SchoolVision`：一条图片消息先用视觉模型转述成不超过 400 字的中文描述，提示词要求客观、不评价、不扮演；思考过程占用的 token 由 768 输出额度兜住，空描述直接判失败而不会编造。
3. `GroupChatBrain` 新增图片资料钩子：本轮先解析图片，再把结果交给引擎——
   - 能直读图片的模型（DeepSeek 直连）通过 `IInlineImageSink` 附上原图；
   - 学校文字模型只拿到"图片转述"，提示里明确标注这是转述、不许声称亲眼看过原图；
   - 需要澄清时直接回一句"请重发"，不再调用模型。
4. 取图规则：私聊和群聊共用一套解析，按会话键 `群号` / `private:<QQ>` 分别记录。触发消息自己带图→只看那条消息；否则最近一张还没看过的图自动附上；同一张图只自动看一次，明确问"这张图"才重看；多条图片消息且没指认时仍会要求重发。
5. 私聊图片入口：`GroupImageInbox` 增加授权私聊联系人白名单，只收白名单内 `message_type=private` 且 `self_id` 匹配的图片元数据。

## 验证

- 真实网关探测：`deepseek-v4-flash` 带图 → HTTP 500；`qwen3.8-27b` 带同一张 256×128 合成图 → HTTP 200、2.5 秒、正确说出"左边红色圆形，右边蓝色正方形"，用量 145 输入/224 输出（其中 211 是推理 token）。`/models` 只返回这两个模型。
- 无硬件套件 47/47 通过，新增/影响的检查：私聊图片白名单与串会话隔离、聊天自动看图一次且不重复下载、新图再附、语音式关键词路径不变、学校视觉轮次只在该轮带图片块、视觉转述用视觉模型并拒绝空描述、图片资料只进入本轮提示且澄清不调用模型。
- 部署：面板 stop → 替换 DLL → 独立进程重启，读回 running、QQ connected、engine=school、model=deepseek-v4-flash、lastError 为空。

## 尚未验证

- 没有在真实 QQ 里发过图片：私聊图片事件是否真的带 `message_type=private` 与 `url` 元数据、以及转述质量，都要等用户实际发一张才成立。
- 只认真图里的静态内容；动图、OCR 质量未实测。尺寸上限本轮已按实测放宽（见下）。
- 每条带图消息会多一次视觉模型调用（点数照样记进学校账本），并发图片多时没有排队上限。
- 转述不会写入日记原文；日记整理仍只看文字聊天记录。

## 真实网关探测（2026-10-24 10:02）

用产品代码路径（`SchoolVision` → `SchoolModelReply` → 网关）对合成图发了一次真实请求，未发送任何 QQ 消息、未上传用户图片：

- 模型 `qwen3.8-27b`，耗时 4037 ms，HTTP 200，返回："图片为白色背景，左侧有一个红色的圆形，右侧有一个蓝色的正方形，图中没有可见的文字。"与合成图一致。
- 学校账本：575 → 576 次调用，累计点数 7.887614 → 7.895298（按配置单价保守预留，不是官方账单）。
- 记录：`Results/local/vision-probe-20261024-100252.json`。运行方式：`AudioBridge.Tests.exe --vision-probe`。

## 部署（2026-10-24 10:02）

- 面板 stop 停掉旧宿主，替换 AudioBridge.dll/.exe，独立进程重启；旧文件备份在 `Results/local/vision-before-20261024-100146`。
- 读回：running=true、paused=false、QQ connected、engine=school、model=deepseek-v4-flash、lastError 为空；新宿主 PID 54416；运行 DLL SHA-256 = 8433F84DAD1ED06EE5305F7AEE795CD6B1E02C0B65138AE90D6A70B8FD2AC97F，与验证候选一致。

## 昨夜心脑的真实记录（顺带核实）

同一台机器上日记与唤醒已经在实际工作：`Results/local/companion/companion-29070481.json` 有 4 条真实日记（含"主人发了好些图给我，可我看不到"和"主人说要把我的眼睛接上"），当前心情"暖暖的，等着主人给我安眼睛"；23:36 有一次真实主动消息（wakeupsToday=1）。凌晨的两次 `companion_diary_failed` 之后按 10 分钟间隔重试成功，说明失败重试生效，但那两次的具体原因（格式还是传输）没有留存，未定位。

## 2026-10-24 修正：真实图片被"地址不符合读取规范"拒掉

用户发三张图，她回"地址不符合读取规范"。原因不是网络也不是权限，而是**图片主机白名单写了两份且已经分叉**：

- 收图/收藏用的 `QqEmojiClient.TryImageUri` 在 2026-10-22 已按真实图片元数据补上 `multimedia.nt.qq.com.cn`（见 `Files/admin-panel-results.md`）。
- 问答用的 `ImageQuestionContext.IsAllowedUrl` 仍只允许 `multimedia.nt.qq` 的老主机，于是每一张真实私聊图片都在下载前就被判越界。

处理：删掉问答路径自己那份白名单，统一走 `QqEmojiClient.TryImageUri`（同时补回旧客户端的 `multimedia.nt.qq.com`）；并在拒绝时只把主机名写进日志 `image_url_rejected`，路径和签名参数不落盘、不发给用户。

验证：新增检查"真实 QQ 图片主机被接受、非 QQ 主机不会下载且会报告主机名"；全套 47/47 通过。已再次部署（PID 39632，running、QQ connected、engine=school、lastError 空），DLL SHA-256 = 786DA5584060CE2FC6F3EC43257B0A358F2896CC0D81994F8F96386F9560C512，备份 `Results/local/vision-host-fix-before-20261024-100907`。

仍未验证：修好之后真实私聊图片能否走到转述（要用户重发一张）。

## 2026-10-24 第二轮：尺寸放宽 + 失败改成正常口吻

### 尺寸

旧上限（每边 1024 像素、1 MP、2 MiB）把普通手机截图直接判死。用真实网关逐级实测：

| 输入 | 请求体 | 结果 |
| --- | --- | --- |
| 1170×2532 截图风合成图（15 KiB） | 0.02 MiB | HTTP 200、6.0 秒，正确说出顶部红色横条、左下蓝色方块；输入 2998 token |
| 1200×1200 噪点 4.12 MiB | 5.50 MiB | HTTP 200、1.6 秒，正确描述为噪点/雪花屏；输入 1505 token |
| 1600×1350 噪点 6.18 MiB | 8.24 MiB | 502（网关侧失败） |
| 1800×1700 噪点 8.76 MiB | 11.68 MiB | 413 Request Entity Too Large |

据此定：**每张最多 4 MiB、每边 4096 像素、总共 12 MP**（1080×2400、1440×3200 的手机截图，常见相机照片都在内），并只采用实测通过的那一档。学校点数预留从每图 2048 token 提到 4096（实测单图已用到 2998，原预算会低估）。

### 口吻

图片读不到时，原来会直接把固定句子当回复发出去（"地址不符合读取范围""请裁剪到每边不超过1024像素"），像系统提示。现在改成：字段只作为失败原因交给模型，提示里要求"用你自己平时的口吻、一两句话自然说出来，这一轮必须 send=true；不要说系统、接口、地址、规范、模型、参数"。失败原因本身也改成人话（"我这边最近三分钟没看到图片，麻烦再发一次""最近有好几张图，我分不清你指的是哪一张"），语音兜底路径读起来也正常。

新增/更新的检查：手机截图尺寸被接受、尺寸与像素上限边界仍拒绝、失败信息进入提示且原句不再直接回复。全套 47/47 通过。
- 部署后用产品路径（`--vision-probe <图片>`）对 1170×2532 截图再测一次：HTTP 200，4 秒左右，正确描述"顶部红色横条、中部黑色等距横线、左下蓝色正方形、无可辨认文字"。运行 DLL SHA-256 = 0CEF6E067256DA9C2A84E11B5BC9F8D9D5201B786D3BBE5484E0A41FD8E1C137，宿主 PID 66688，running、QQ connected、engine=school、lastError 空，备份 `Results/local/vision-size-before-20261024-101758`。

## 2026-10-24 第三轮：表情包（GIF/WebP）与转述额度

用户又发来截图：先成功看过一张私聊图（日志 10:21:05 `image_caption`、privateChat=true、234 字），随后三次 `image_unavailable`（10:22:35、10:33:05、10:33:55，均为 InvalidDataException）。查下来是两件事叠在一起：

1. **转述额度不够**：视觉模型先输出推理 token 再输出正文。原来给 768，推理吃满后正文为空或 `finish_reason=length`，整次转述被判无效 → 记 `image_unavailable`。改为 **2048**，并且只对"图片转述"放开截断（`allowTruncated`，仍要求正文非空）；聊天回复仍严格要求 `finish_reason=stop`。
2. **表情包格式不是 PNG/JPEG**：QQ 收藏表情多为 GIF/WebP，原来的解析只认 PNG/JPEG，会直接判"读不出来"。实测网关对两者都返回 200 且描述正确（1.5 MiB 动图 GIF → 200、3.9 秒；30 KiB WebP → 200、1.1 秒；GIF 的 prompt token 只有 129，说明网关按首帧缩放处理），因此 `ParseImage` 增加 GIF（校验头、首帧尺寸、结束符 0x3B）与 WebP（VP8/VP8L/VP8X 尺寸解析，解析不出尺寸时只受字节上限约束）；PNG/JPEG/GIF 的零尺寸仍判无效。

另外补了可诊断日志：`image_clarified`（带原因：没图 / 多张 / 地址 / 读不出）与 `image_unavailable`（带异常类型和一条简短消息），下次再出问题不用猜。

验证：全套 47/47 通过（新增 GIF/WebP 接受、GIF 截断拒绝、截断转述被接受）；用产品路径对真实 GIF 与 WebP 各跑一次转述，均正确。部署后宿主 PID 64360，running、QQ connected、engine=school、lastError 空；DLL SHA-256 = 864CAB240A3725007A97D611AD411DF9B08E39D31BFEFA9E67219F29224B56FF，备份 `Results/local/vision-sticker-before-20261024-104249`。

仍未验证：真实 QQ 私聊表情包经 NapCat 下发后的原始字节（动图能不能拿到、是不是 webp）、以及 2048 额度在复杂截图上是否仍会截断——只能等下一次真实发送观察 `image_caption` / `image_unavailable`。
