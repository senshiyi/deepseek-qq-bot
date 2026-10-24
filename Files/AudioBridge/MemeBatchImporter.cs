using System.Text.RegularExpressions;

namespace AudioBridge;

public sealed record MemeBatchImportRequest(IReadOnlyList<MemeBatchImportItem?>? Items);
public sealed record MemeBatchImportItem(string? ClientId, string? Id, IReadOnlyList<string>? Tags, string? Base64);
public sealed record MemeBatchImportResult(string ClientId, string Id, string Status, string? ErrorCode = null,
    string? Message = null, string? MediaType = null);
public sealed record MemeBatchImportResponse(IReadOnlyList<MemeBatchImportResult> Results, int ImportedCount,
    int FailedCount, int NotAttemptedCount, bool Cancelled);

/// <summary>A bounded batch of explicit local uploads, with independent per-item commits and results.</summary>
public sealed class MemeBatchImporter(MemeLibrary library)
{
    public const int MaximumItems = 20;
    public const int MaximumBatchImageBytes = 20 * 1024 * 1024;

    public MemeBatchImportResponse Import(MemeBatchImportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items is null || request.Items.Count is < 1 or > MaximumItems)
            throw new ArgumentException("每批请选择 1～20 张图片。");
        // Reject an oversized batch before the first commit, without allocating decoded buffers.
        long total = 0;
        foreach (var item in request.Items)
        {
            total += DecodedSizeUpperBound(item?.Base64);
            if (total > MaximumBatchImageBytes) throw new ArgumentException("每批图片总大小最多 20 MiB；本批未写入。");
        }
        var results = new List<MemeBatchImportResult>();
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Items.Count; index++)
        {
            var item = request.Items[index];
            var validClientId = item?.ClientId is { Length: > 0 and <= 64 } clientId &&
                Regex.IsMatch(clientId, "^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant);
            var row = validClientId ? item!.ClientId! : "row-" + (index + 1);
            var id = item?.Id is { Length: <= 48 } ? item.Id : "";
            if (ct.IsCancellationRequested)
            {
                results.Add(new(row, id ?? "", "not_attempted", "cancelled", "导入已取消，此项尚未尝试。"));
                continue;
            }
            if (item is null || !validClientId || !clientIds.Add(row) || item.Id is null || item.Tags is null || item.Base64 is null)
            {
                results.Add(new(row, id ?? "", "failed", "invalid_item", "此项字段无效或行编号重复，请检查后重试。"));
                continue;
            }
            if (DecodedSizeUpperBound(item.Base64) > OneBotMessageClient.MaximumImageBytes)
            {
                results.Add(new(row, id!, "failed", "image_too_large", "单张图片最多 5 MiB。"));
                continue;
            }
            try
            {
                var bytes = Convert.FromBase64String(item.Base64);
                var asset = library.ImportBytes(item.Id, bytes, item.Tags);
                results.Add(new(row, asset.Id, "imported", MediaType: asset.MediaType));
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or FormatException or IOException or UnauthorizedAccessException or TimeoutException)
            {
                var (code, message) = error switch
                {
                    TimeoutException => ("library_busy", "表情库正忙，此项未完成，请稍后重试。"),
                    IOException or UnauthorizedAccessException => ("storage_error", "此项未完成，请检查本地存储及清单后重试。"),
                    FormatException => ("invalid_image", "图片编码无效，请重新选择原文件。"),
                    InvalidDataException => ("invalid_image_or_tags", "图片、标签或清单不符合限制，请检查后重试。"),
                    _ => ("invalid_or_duplicate_id", "ID 已存在或字段无效，不会覆盖原素材；请修改后重试。")
                };
                results.Add(new(row, id!, "failed", code, message));
            }
        }
        return new(results, results.Count(x => x.Status == "imported"), results.Count(x => x.Status == "failed"),
            results.Count(x => x.Status == "not_attempted"), results.Any(x => x.Status == "not_attempted"));
    }

    private static long DecodedSizeUpperBound(string? encoded)
    {
        if (encoded is null) return 0;
        var padding = encoded.EndsWith("==", StringComparison.Ordinal) ? 2 : encoded.EndsWith('=') ? 1 : 0;
        return Math.Max(0, ((long)encoded.Length + 3) / 4 * 3 - padding);
    }
}
