# 初始表情包库

`roger.png`（收到）、`yay.png`（好耶）、`thinking.png`（让我想想）由项目脚本生成，是用于首版发送和按语境选择的原创文字反应图，不使用第三方人物或现成表情素材。

可使用 `Files/Create-ReactionMemes.ps1` 重建缺失的默认图片，脚本不覆盖已有图片。清单为 `manifest.json`，群消息只能选择清单中的ID，不能指定任意本地文件。后续可以通过明确的导入命令加入自己的PNG/JPEG/GIF/WebP。

这些图片经 OneBot `image` 消息段发送。客户端实际显示和GIF动画需要真实QQ测试；不宣称自动成为收藏表情或商城表情。
