# 学校大模型接口

核实日期：2026-10-20。用户已选择学校平台的DeepSeek V4 Flash。初期测试授权为累计最多0.10点、最多10次，用户本轮明确取消学校累计点数和请求次数上限；平台仍按真实用量计费，原账本保留。语音继续暂停。

## 已核实的接口

- 指南：[学校API接入指南](http://10.3.68.122:30088/apikeys/guide)。页面明示的 `/api/config/gateway` 返回当前网关 `https://myai.bupt.edu.cn/llm-gw/v1`。
- 对话：`POST https://myai.bupt.edu.cn/llm-gw/v1/chat/completions`，Bearer API Key。
- 已使用本地密钥只读查询 `/models`，返回 `deepseek-v4-flash` 与 `qwen3.8-27b`；本项目按用户选择使用前者。
- 用户从模型计费页提供输入和输出均为1.00点/百万Token，上下文256K。学校按点计量，不能当作人民币，也不据此认定免费。
- 指南的thinking/reasoning_effort暂不支持。JSON模式、图片输入没有得到文档确认；不发送这些扩展参数。

## 本地配置

密钥放项目忽略的 `.env`：

```dotenv
SCHOOL_API_KEY=在本机填写，不发送到聊天或群里
```

配置放忽略的 `school-api.local.json`；公共模板是 `Files/school-api-config.example.json`。本机按本次授权使用：

```json
{
  "baseUrl": "https://myai.bupt.edu.cn/llm-gw/v1/",
  "model": "deepseek-v4-flash",
  "inputPointsPerMillion": 1,
  "outputPointsPerMillion": 1,
  "maxPoints": null,
  "maxCalls": null,
  "stream": false
}
```

模型ID、输入/输出点价必须明确，未知价格不会按零处理。`maxPoints` 和 `maxCalls` 显式设为 `null` 表示分别取消累计点数或次数上限。省略 `maxCalls` 时默认仍为10次，省略 `maxPoints` 时该项为null；建议两项都明确填写，避免误解。原有正数上限配置继续支持，达到任一已设置的上限后停止新调用；不要删除账本。学校只允许已核实的HTTPS网关，禁止重定向与Cookie；沿用系统已有网络代理，不修改系统网络设置。

`group-chat.local.json` 的 `engine` 为 `school` 时，双击 `start-chat.cmd` 和未显式指定engine的真实chat命令都会选择学校入口。显式 `--engine deepseek` 可使用保留的DeepSeek直连；失败时不会自动转用其他供应商或重复提交。

## 启动和检查

```powershell
# 只读本地配置和账本，输出密钥是否配置；不联网
.\start.cmd school-check

# 一次正式学校模型测试，消耗学校额度，不发QQ
.\start.cmd school-ai-check

# 已登录NapCat后持续处理授权群和私聊白名单
.\start.cmd chat --group 1095601728 --mode active --engine school --live true --seconds 0
```

普通使用双击根目录 `start-chat.cmd` 即可，群与私聊由同一程序处理。P暂停/恢复、X暂停并取消、S状态、Q退出，对群与私聊统一生效。重启不清零学校点数账本。

要确认正在运行的聊天程序是否使用学校入口，在该窗口按 **S**，或查看启动时的 `started`。实际状态应显示 `engine: school`、`model: deepseek-v4-flash`、`apiBaseUrl: https://myai.bupt.edu.cn/llm-gw/v1/`、`keyEnvironment: SCHOOL_API_KEY`。若使用直连，则显示 `engine: deepseek`、API根地址 `https://api.deepseek.com/`、环境变量名 `DEEPSEEK_API_KEY`。这些字段只展示变量名，不展示Key值。

`chat-check`显示本地默认引擎及对应入口，`engineScope`标明其配置范围；显式命令行 `--engine` 会覆盖默认值，以运行窗口S为准。`school-check`只反映学校配置和账本，不查询正在运行的引擎；模型自称“DeepSeek”或其他身份不能证明请求实际经过哪个网关。

本机私聊白名单仅为用户QQ29070481；联系人无需@，至少间隔8秒、每小时最多30次，各联系人的上下文和限频独立，也不与群共享。配置键为 `allowedPrivateUserIds`、`privateIntervalSeconds`、`privatePerHour`；公共模板默认空白名单关闭私聊。上下文只在内存保存最近20条/4000字符，10分钟过期，重启清空。配置片段和操作说明见 [群消息与表情包说明](group-messaging-guide.md)。

私聊在当前回复尚未完成或8秒冷却内收到的新消息会暂存，等待回复结束且冷却满足后合并为下一轮；仍每位联系人最多30次/小时。待处理区最多20条/4000字符、2分钟过期，暂停清空，达到小时上限不积压到下一小时；S状态显示 `PendingMessages`。群消息的忙碌/冷却跳过策略保持原样。

## 回复与费用边界

学校正式聊天入口目前只发送文字上下文，能选择并发送本地表情库里的图片，尚未接通学校模型的图片问答。2026-10-23 按新授权对 `qwen3.8-27b` 的单张合成图试用已成功，约1.54秒，未替换日常DeepSeek或上传聊天图片；详见 [本轮探测记录](shared-persona-batch-results.md)。收到看图问题时仍应说明正式入口无法读图，不擅自上传。群与私聊的短期对话仍分开；长期笔记可按角色页开关共享当前QQ的内容，私聊原文不进群请求。此限制不影响本地表情图发送。

默认使用非流式Chat Completions，取得完整回复后严格验证 `send/text/memeId`，只发送通过校验的正文与表情。保留最多256输出Token、25秒总期限、512KiB响应上限；不会朗读或发送推理字段、半截JSON、工具调用和截断答案。`stream=true` 的解析通过替身验证，实际学校流式体验尚未验收。

费用保存在独立的 `Results/local/school-api-budget.json`，按配置点价与文本大小保守预留；不是实际消费账单或账户余额。失败、取消、超时不自动退款或重试，防止结果不明时重复计费。当前本机取消累计上限后仍持续记账，不重置初期的调用次数和预留点数；群与私聊共用该学校账本，持续运行可能继续产生费用。DeepSeek原账本为 `Results/local/deepseek-budget.json`，额度仍是0.30元/10次，两者不互相重置。

## 验证与恢复

正式 `school-ai-check` 已返回正文“已收到”和 `roger` 表情选择，约5.668秒完成，首次保守预留0.006006点。这只证明学校模型到本地决策链路，不把它算成群友可见的消息。

2026-10-20，用户针对群1095601728内收到第一条回复后隔8～15秒再次@乖乖追问的测试要求，明确确认“两次都收到回复”。学校API与新限频策略的真实群两轮连续追问已通过；精确间隔和逐轮端到端延迟没有测量。完整验收边界见 `Files/group-messaging-results.md`。

用户随后确认原私聊两轮均收到并接上上下文，范围限于间隔至少8秒的场景。本轮新增的快速连发暂存/合并不能沿用该确认，其自动化与真实私聊验证另行记录。

上一阶段学校适配器16项替身检查，以及当时消息专项共69项检查通过，覆盖限频交叉、预算并发、取消和晚到结果、HTTP失败、结束标志、超限与脱敏。原始记录：`Results/local/school-integration-verification-20261020.txt`、`Results/local/school-api-live-check-20261020.txt`。这组历史结果只覆盖当时实现；后续私聊、取消累计上限及快速连发合并的检查和对端确认另见 `Files/group-messaging-results.md`。

恢复有限学校额度时，为 `maxPoints`、`maxCalls` 填写明确正数后停止并重启聊天程序，继续沿用现有账本；已有累计超过所设上限会停止新调用。关闭私聊时把 `allowedPrivateUserIds` 改为空列表并重启，群消息配置保留。

恢复直连时将本地群配置的engine改为 `deepseek`，停止当前群聊再启动；继续使用原有DeepSeek累计账本。切换前的群配置备份位于 `Results/local/group-chat.before-school-*.json`。不需要改QQ账号、OneBot Token、系统音频或注册表。
