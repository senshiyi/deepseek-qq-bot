using System.Net;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class ApiChecks
{
    public static async Task ImagePayload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qq-image-api-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var inbox = new GroupImageInbox("123");
            using var message = JsonDocument.Parse(JsonSerializer.Serialize(new { post_type = "message", message_type = "group",
                group_id = 123, user_id = 456, message_id = 1, time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                message = new[] { new { type = "image", data = new { url = "https://gchat.qpic.cn/test.png" } } } }));
            Check(inbox.AddEvent(message.RootElement, DateTimeOffset.UtcNow), "fixture event rejected");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            using var images = new ImageQuestionContext(inbox, "123", new FakeHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) })));
            var handler = new FakeHandler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var messages = body.RootElement.GetProperty("messages");
                Check(messages[1].GetProperty("content").ValueKind == JsonValueKind.String, "old text turn mutated");
                var blocks = messages[messages.GetArrayLength() - 1].GetProperty("content");
                Check(blocks.ValueKind == JsonValueKind.Array && blocks.GetArrayLength() == 2, "missing image block");
                Check(blocks[1].GetProperty("image_url").GetProperty("url").GetString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "wrong inline image");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n") };
            });
            using var provider = new DeepSeekReply("fake-test-key", new ApiBudget(Path.Combine(directory, "ledger.json"), .3, 10), handler, images);
            await foreach (var _ in provider.StreamAsync([new("user", "之前的问题"), new("assistant", "之前的答复"), new("user", "看看这张图片")], default)) { }
            Check(handler.Calls == 1, "valid authorized image did not reach transport");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task BodyOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qq-api-check-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var handler = new FakeHandler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(request.RequestUri!.Host == "api.deepseek.com", "wrong official host");
                Check(body.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled", "thinking enabled");
                Check(body.RootElement.GetProperty("max_tokens").GetInt32() == 256, "unbounded output");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"must not speak\"}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{\"content\":\"你好。\"}}]}\n\n" +
                    "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
            });
            using var provider = new DeepSeekReply("fake-test-key", new ApiBudget(Path.Combine(directory, "ledger.json"), .3, 10), handler);
            var received = "";
            await foreach (var fragment in provider.StreamAsync([new("user", "你好")], default)) received += fragment;
            Check(received == "你好。" && handler.Calls == 1, "reasoning leaked or duplicate request");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static Task BudgetLimits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qq-budget-check-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "ledger.json");
            new ApiBudget(path, .3, 1).Reserve([new("user", "你好")]);
            var restored = new ApiBudget(path, .3, 1);
            Throws(() => restored.Reserve([new("user", "再问一次")]), "restart reset call ceiling");
            var tiny = new ApiBudget(Path.Combine(directory, "tiny.json"), .001, 10);
            Throws(() => tiny.Reserve([new("user", "你好")]), "budget overrun allowed");
            Check(!File.Exists(Path.Combine(directory, "tiny.json")), "rejected call mutated budget");
            File.WriteAllText(path, "{\"reservedYuan\":-1,\"calls\":1}");
            Throws(() => _ = new ApiBudget(path, .3, 10), "invalid ledger accepted");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    public static async Task Cancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qq-api-cancel-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeHandler(async (_, ct) =>
            {
                called.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new Exception("cancel ignored");
            });
            using var provider = new DeepSeekReply("fake-test-key", new ApiBudget(Path.Combine(directory, "ledger.json"), .3, 10), handler);
            using var cancellation = new CancellationTokenSource();
            var run = Task.Run(async () =>
            {
                await foreach (var _ in provider.StreamAsync([new("user", "你好")], cancellation.Token)) { }
            });
            await called.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("cancel reported success"); }
            catch (OperationCanceledException) { }
            Check(File.Exists(Path.Combine(directory, "ledger.json")), "cancel refunded uncertain request");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Throws(Action action, string reason)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or IOException) { return; }
        throw new Exception(reason);
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
}
