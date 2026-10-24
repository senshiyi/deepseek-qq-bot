using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

// The callback only parses/enqueues bounded metadata. Model generation never holds an HTTP request open.
public sealed class OneBotEventServer(int port, string token, Func<JsonElement, bool> accept)
{
    public string Prefix { get; } = port is >= 1024 and <= 65535 ? $"http://127.0.0.1:{port}/onebot/" : throw new ArgumentOutOfRangeException(nameof(port));
    public bool IsRunning { get; private set; }
    public async Task RunAsync(CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ct.ThrowIfCancellationRequested();
        using var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        IsRunning = true;
        using var stop = ct.Register(listener.Close);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                await HandleAsync(context, ct).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (ct.IsCancellationRequested && error is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        finally { IsRunning = false; }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; return; }
            if (context.Request.Url?.AbsolutePath != "/onebot/") { context.Response.StatusCode = 404; return; }
            var bearer = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers["Authorization"] ?? ""), Encoding.UTF8.GetBytes("Bearer " + token));
            byte[]? signature = null;
            var header = context.Request.Headers["X-Signature"];
            if (header is { Length: 45 } && header.StartsWith("sha1=", StringComparison.Ordinal))
                try { signature = Convert.FromHexString(header.AsSpan(5)); } catch (FormatException) { }
            if (!bearer && signature is null) { context.Response.StatusCode = 401; return; }
            if (context.Request.ContentLength64 > GroupImageInbox.MaximumBodyBytes) { context.Response.StatusCode = 413; return; }
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await context.Request.InputStream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + count > GroupImageInbox.MaximumBodyBytes) { context.Response.StatusCode = 413; return; }
                body.Write(buffer, 0, count);
            }
            var bytes = body.ToArray();
            if (!bearer && !CryptographicOperations.FixedTimeEquals(HMACSHA1.HashData(Encoding.UTF8.GetBytes(token), bytes), signature!))
            { context.Response.StatusCode = 401; return; }
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var accepted = accept(json.RootElement);
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(accepted ? "{\"accepted\":true}" : "{\"accepted\":false}"), deadline.Token);
        }
        catch (JsonException) { TryStatus(context, 400); }
        catch (OperationCanceledException) { TryStatus(context, 408); }
        catch { TryStatus(context, 400); }
        finally { try { context.Response.Close(); } catch (ObjectDisposedException) { } }
    }
    private static void TryStatus(HttpListenerContext context, int status)
    { try { context.Response.StatusCode = status; } catch (ObjectDisposedException) { } }
}
