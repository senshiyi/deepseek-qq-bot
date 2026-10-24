namespace AudioBridge;

public sealed record ChatTurn(string Role, string Content);

public interface IStreamingReply
{
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, CancellationToken ct);
}

public interface IVoiceSpeaker
{
    // Return only after this utterance has stopped and its output owner has exited.
    // Cancellation must also prevent any late synthesis result from starting playback.
    Task SpeakAsync(string text, CancellationToken ct);
}

public sealed record VoiceConversationSnapshot(long Generation, bool Generating, bool Speaking,
    bool Muted, bool Interrupted, bool Disposed, int PendingSentences, string? LastError,
    IReadOnlyList<ChatTurn> History);
