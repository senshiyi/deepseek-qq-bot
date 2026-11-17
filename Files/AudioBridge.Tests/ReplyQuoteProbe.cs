using AudioBridge;

/// <summary>真实发一条带引用回复的群消息，确认 NapCat 接受 reply 段；会真的出现在群里。</summary>
public static class ReplyQuoteProbe
{
    public static async Task<int> RunAsync(long groupId, long messageId)
    {
        var project = Environment.CurrentDirectory;
        var token = BotSettings.LoadSecret(project, "ONEBOT_ACCESS_TOKEN") ?? throw new InvalidOperationException("缺少 ONEBOT_ACCESS_TOKEN。");
        using var client = new OneBotMessageClient("http://127.0.0.1:3000/", token, [groupId.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        try
        {
            var receipt = await client.SendGroupAsync(new(groupId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [OneBotSegment.Reply(messageId), OneBotSegment.Text("（这条是引用测试，忽略即可）")]), CancellationToken.None);
            Console.WriteLine("quoted send accepted message_id=" + receipt.MessageId);
            return 0;
        }
        catch (OneBotMessageException error)
        {
            Console.WriteLine("quoted send refused uncertain=" + error.DeliveryMayHaveOccurred + " : " + error.Message);
            return 1;
        }
    }
}
