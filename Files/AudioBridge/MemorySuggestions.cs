namespace AudioBridge;

public sealed record MemorySuggestion(string SourceId, long UserId, string SourceMessageId, string Text, DateTimeOffset At);

/// <summary>Derives review candidates from already retention-filtered private history; never writes or calls a model.</summary>
public static class MemorySuggestions
{
    private static readonly string[] Prefixes = ["记住", "请记住", "帮我记住", "请帮我记住", "记一下", "请记一下", "记下来", "请记下来"];

    public static string? ExtractExplicitRequest(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var request = text.Trim();
        var separator = request.IndexOfAny(['：', ':']);
        if (separator < 0 || !Prefixes.Contains(request[..separator].Trim(), StringComparer.Ordinal)) return null;
        var content = request[(separator + 1)..].Trim();
        if (content.Length is < 1 or > 1000 || content.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            return null;
        return content;
    }

    /// <summary>Pass ContactContextStore.Recent(userId, 1000) so candidates share the existing seven-day retention.</summary>
    public static IReadOnlyList<MemorySuggestion> ForContact(long userId, IEnumerable<StoredPrivateMessage> history,
        IEnumerable<ContactMemory> memories, int maximum = 32)
    {
        if (userId <= 0) throw new ArgumentException("联系人QQ号必须是正整数。");
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(memories);
        if (maximum is < 1 or > 32) throw new ArgumentException("待审核记忆最多显示1～32条。");
        var seen = memories.Where(memory => memory.UserId == userId).Select(memory => memory.Text.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var result = new List<MemorySuggestion>();
        foreach (var message in history.Where(message => message.UserId == userId && message.Role == "user")
                     .OrderByDescending(message => message.At).ThenBy(message => message.Id, StringComparer.Ordinal))
        {
            var text = ExtractExplicitRequest(message.Text);
            if (text is null || !seen.Add(text)) continue;
            result.Add(new(message.Id, userId, message.MessageId, text, message.At));
            if (result.Count == maximum) break;
        }
        return result;
    }
}
