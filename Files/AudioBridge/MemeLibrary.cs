using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AudioBridge;

public sealed record MemeEntry(string Id, string File, IReadOnlyList<string> Tags,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FaceId = null);
public sealed record MemeAsset(string Id, IReadOnlyList<string> Tags, byte[] Content, string MediaType);

/// <summary>Curated manifest IDs only. Imports are explicit local management operations, never a group command parser.</summary>
public sealed class MemeLibrary
{
    private readonly string directory;
    private readonly string manifest;
    private readonly string writerMutexName;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public MemeLibrary(string projectDirectory, string relativeDirectory = "Files/Memes")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (string.IsNullOrWhiteSpace(relativeDirectory) || Path.IsPathRooted(relativeDirectory))
            throw new ArgumentException("表情库必须是项目内的相对目录。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relativeDirectory)));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("表情库目录不得逃出项目。");
        EnsureNoLinks(directory);
        writerMutexName = "Global\\DeepseekQqBot.MemeImport." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        manifest = Path.Combine(directory, "manifest.json");
        _ = Load();
    }

    public IReadOnlyList<MemeEntry> List() { lock (gate) return Load(); }

    /// <summary>QQ 原生表情的 ID 约定；提示词要靠它区分"普通小表情"和图片表情包。</summary>
    public static bool IsFaceId(string id) => id.StartsWith("qq_face_", StringComparison.Ordinal);

    internal string ImportMutexName => writerMutexName;

    public MemeAsset Get(string id)
    {
        ValidateId(id);
        lock (gate)
        {
            var entry = Load().SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("没有这个已收录的表情 ID。");
            return ReadAsset(entry);
        }
    }

    public OneBotSegment ResolveSegment(string id)
    {
        ValidateId(id);
        lock (gate)
        {
            var entry = Load().SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("没有这个已收录的表情 ID。");
            return entry.FaceId is { } faceId ? OneBotSegment.Face(faceId) : OneBotSegment.Image(ReadAsset(entry).Content);
        }
    }

    public MemeEntry AddFace(int faceId, IEnumerable<string> tags)
    {
        var id = FaceEntryId(faceId);
        var cleanTags = ValidateTags(tags);
        return WithWriterLock(() =>
        {
            var entries = Load().ToList();
            if (entries.Any(x => x.Id == id)) throw new ArgumentException("表情 ID 已存在，不会覆盖原条目。");
            EnsureNoLinks(directory);
            Directory.CreateDirectory(directory);
            var entry = new MemeEntry(id, "", cleanTags, faceId);
            entries.Add(entry);
            SaveManifest(entries, "QQ 原生表情清单保存未完成；请核对本地库后明确重试。");
            return entry;
        }, "表情库正在被另一个写入占用，5秒内未取得写入锁；未添加QQ原生表情，请稍后明确重试。");
    }

    private MemeAsset ReadAsset(MemeEntry entry)
    {
        if (entry.FaceId is not null) throw new ArgumentException("QQ 原生表情不含本地图片，请使用消息段解析接口。");
        var bytes = ReadLimited(AssetPath(entry.File), OneBotMessageClient.MaximumImageBytes);
        var mime = MemeImageFormat.Validate(bytes);
        ValidateExtension(entry.File, mime);
        return new(entry.Id, entry.Tags, bytes, mime);
    }

    public MemeAsset Import(string id, string sourcePath, IEnumerable<string> tags)
    {
        ValidateId(id);
        var cleanTags = ValidateTags(tags);
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath) || sourcePath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("导入必须明确指定本机文件的完整路径，不接受 URL 或网络路径。");
        var bytes = ReadLimited(Path.GetFullPath(sourcePath), OneBotMessageClient.MaximumImageBytes);
        return ImportValidatedBytes(id, bytes, cleanTags);
    }

    public MemeAsset ImportBytes(string id, byte[] bytes, IEnumerable<string> tags)
    {
        ValidateId(id);
        var cleanTags = ValidateTags(tags);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > OneBotMessageClient.MaximumImageBytes) throw new InvalidDataException("文件为空或超过大小限制。");
        // Validate and persist one owned snapshot, even if the caller changes its upload buffer.
        return ImportValidatedBytes(id, bytes.ToArray(), cleanTags);
    }

    private MemeAsset ImportValidatedBytes(string id, byte[] bytes, string[] cleanTags)
    {
        var mime = MemeImageFormat.Validate(bytes);
        var extension = mime switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", _ => ".webp" };
        return WithWriterLock(() =>
        {
            var entries = Load().ToList();
            if (entries.Any(x => x.Id == id)) throw new ArgumentException("表情 ID 已存在，不会覆盖原素材。");
            EnsureNoLinks(directory);
            Directory.CreateDirectory(directory);
            var file = id + extension;
            var target = AssetPath(file);
            entries.Add(new(id, file, cleanTags));
            _ = SerializeManifest(entries); // Check capacity before creating any new asset.
            var created = false;
            try
            {
                using (var asset = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { created = true; asset.Write(bytes); }
                SaveManifest(entries, "表情清单保存未完成；原清单保留，请稍后重试。");
            }
            catch
            {
                // Only this call's CreateNew target may be removed; never delete a source or pre-existing file.
                if (created) { EnsureNoLinks(target); File.Delete(target); }
                throw;
            }
            return new MemeAsset(id, cleanTags, bytes, mime);
        }, "表情库正在被另一个导入占用，5秒内未取得写入锁；未复制新素材，请稍后明确重试。");
    }

    public MemeEntry UpdateTags(string id, IEnumerable<string> tags)
    {
        ValidateId(id);
        var cleanTags = ValidateTags(tags);
        return WithWriterLock(() =>
        {
            var entries = Load().ToList();
            var index = entries.FindIndex(x => x.Id == id);
            if (index < 0) throw new ArgumentException("没有这个已收录的表情 ID。");
            var updated = entries[index] with { Tags = cleanTags };
            entries[index] = updated;
            SaveManifest(entries, "表情标签清单保存未完成；原素材未改动，请稍后明确重试。");
            return updated;
        }, "表情库正在被另一个写入占用，5秒内未取得写入锁；标签未改动，请稍后明确重试。");
    }

    /// <summary>Remove a curated entry only; keep image bytes untouched so the operator can recover the source.</summary>
    public bool Delete(string id)
    {
        ValidateId(id);
        return WithWriterLock(() =>
        {
            var entries = Load().ToList();
            var index = entries.FindIndex(x => x.Id == id);
            if (index < 0) return false;
            entries.RemoveAt(index);
            SaveManifest(entries, "表情删除清单保存未完成；原清单和图片保留，请稍后明确重试。");
            return true;
        }, "表情库正在被另一个写入占用，5秒内未取得写入锁；表情未删除，请稍后明确重试。");
    }

    private T WithWriterLock<T>(Func<T> write, string timeoutMessage)
    {
        // All manifest writers share the normalized library's cross-process mutex.
        // There is no unbounded instance monitor wait before this bounded writer wait.
        using var writer = new Mutex(false, writerMutexName);
        var acquired = false;
        try
        {
            try { acquired = writer.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; } // ownership is granted; revalidate on disk
            if (!acquired) throw new TimeoutException(timeoutMessage);
            return write();
        }
        finally { if (acquired) writer.ReleaseMutex(); }
    }

    private void SaveManifest(IReadOnlyList<MemeEntry> entries, string failureMessage)
    {
        var content = SerializeManifest(entries);
        var temporary = Path.Combine(directory, ".manifest-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            EnsureNoLinks(directory);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(content);
            EnsureNoLinks(manifest);
            File.Move(temporary, manifest, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw new IOException(failureMessage);
        }
    }

    private static byte[] SerializeManifest(IReadOnlyList<MemeEntry> entries)
    {
        if (entries.Count > 500) throw new InvalidDataException("表情清单最多 500 条，不会保存超限清单。");
        var content = JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, entries), Json);
        if (content.Length > 64 * 1024) throw new InvalidDataException("表情清单超过64 KiB，不会覆盖原清单。");
        return content;
    }

    private MemeEntry[] Load()
    {
        EnsureNoLinks(directory);
        EnsureNoLinks(manifest);
        if (!File.Exists(manifest)) return [];
        Manifest parsed;
        try { parsed = JsonSerializer.Deserialize<Manifest>(ReadLimited(manifest, 64 * 1024), Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException("表情清单不是有效的 version 1 JSON 清单。"); }
        if (parsed.Version != 1 || parsed.Items is null || parsed.Items.Count > 500)
            throw new InvalidDataException("表情清单版本或条目数量无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<MemeEntry>();
        foreach (var entry in parsed.Items)
        {
            if (entry is null) throw new InvalidDataException("表情清单包含无效条目。");
            ValidateId(entry.Id);
            if (!ids.Add(entry.Id)) throw new InvalidDataException("表情清单包含重复 ID。");
            if (entry.FaceId is { } faceId)
            {
                if (faceId is < 0 or > 65535 || entry.File != "" || entry.Id != FaceEntryId(faceId))
                    throw new InvalidDataException("QQ 原生表情条目必须使用匹配的 qq_face_数字 ID、空文件名及0～65535编号。");
            }
            else _ = AssetPath(entry.File);
            entries.Add(new(entry.Id, entry.File, ValidateTags(entry.Tags), entry.FaceId));
        }
        return entries.ToArray();
    }

    private string AssetPath(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Length > 96 || !Regex.IsMatch(file, "^[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.CultureInvariant) ||
            file.Contains("..", StringComparison.Ordinal) || Reserved(Path.GetFileNameWithoutExtension(file)) ||
            !new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
            throw new InvalidDataException("清单素材只能引用库内 PNG/JPEG/GIF/WebP 文件名。");
        var path = Path.Combine(directory, file);
        EnsureNoLinks(path);
        return path;
    }

    private static byte[] ReadLimited(string path, int maximum)
    {
        EnsureNoLinks(path);
        // An open reader keeps its snapshot; it must not prevent an atomic manifest replacement.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 || input.Length > maximum) throw new InvalidDataException("文件为空或超过大小限制。");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("文件在读取时改变。");
        EnsureNoLinks(path);
        return bytes;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("表情库不允许符号链接或目录联接路径。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateId(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{0,47}$", RegexOptions.CultureInvariant) || Reserved(id))
            throw new ArgumentException("表情 ID 只能是 1～48 位小写英文、数字、下划线或短横线。");
    }

    private static bool Reserved(string value) => Regex.IsMatch(value, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string FaceEntryId(int faceId)
    {
        if (faceId is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(faceId), "QQ 原生表情编号必须为0～65535。");
        return "qq_face_" + faceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] ValidateTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var result = tags.Take(9).ToArray();
        if (result.Length > 8 || result.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 24 || x.Any(char.IsControl)))
            throw new InvalidDataException("每条表情最多 8 个标签，每个标签 1～24 字符。");
        return result.Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateExtension(string file, string mime)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (!(mime == "image/png" && extension == ".png" || mime == "image/jpeg" && extension is ".jpg" or ".jpeg" ||
              mime == "image/gif" && extension == ".gif" || mime == "image/webp" && extension == ".webp"))
            throw new InvalidDataException("素材扩展名与实际图片格式不一致。");
    }

    private sealed record Manifest(int Version, IReadOnlyList<MemeEntry> Items);
}

/// <summary>Bounded, format-specific container checks; never treats a filename or magic prefix alone as an image.</summary>
internal static class MemeImageFormat
{
    public static string Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 14 or > OneBotMessageClient.MaximumImageBytes) throw Invalid();
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) { Png(bytes); return "image/png"; }
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) { Gif(bytes); return "image/gif"; }
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8 })) { Jpeg(bytes); return "image/jpeg"; }
        if (bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) { Webp(bytes); return "image/webp"; }
        throw Invalid();
    }

    private static void Dimensions(int width, int height)
    { if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 16_777_216) throw Invalid(); }

    private static void Png(ReadOnlySpan<byte> b)
    {
        if (b.Length < 57 || U32(b[8..]) != 13 || !b.Slice(12, 4).SequenceEqual("IHDR"u8)) throw Invalid();
        if (U32(b[16..]) > 4096 || U32(b[20..]) > 4096) throw Invalid();
        Dimensions((int)U32(b[16..]), (int)U32(b[20..]));
        if (b[26] != 0 || b[27] != 0 || b[28] > 1 || !(b[25] switch
            { 0 => b[24] is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => b[24] is 8 or 16, 3 => b[24] is 1 or 2 or 4 or 8, _ => false })) throw Invalid();
        var position = 8;
        var data = false;
        while (position <= b.Length - 12)
        {
            var size = U32(b[position..]);
            if (size > b.Length - position - 12) throw Invalid();
            var length = (int)size;
            var kind = b.Slice(position + 4, 4);
            var crc = uint.MaxValue;
            foreach (var value in b.Slice(position + 4, length + 4))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            if (~crc != U32(b[(position + 8 + length)..]) || position != 8 && kind.SequenceEqual("IHDR"u8)) throw Invalid();
            if (kind.SequenceEqual("IDAT"u8) && length > 0) data = true;
            position += length + 12;
            if (kind.SequenceEqual("IEND"u8))
            { if (length == 0 && data && position == b.Length) return; throw Invalid(); }
        }
        throw Invalid();
    }

    private static void Gif(ReadOnlySpan<byte> b)
    {
        var width = U16(b[6..]); var height = U16(b[8..]);
        Dimensions(width, height);
        var p = 13;
        if ((b[10] & 0x80) != 0) p += 3 * (1 << ((b[10] & 7) + 1));
        var frames = 0;
        while (p < b.Length)
        {
            var marker = b[p++];
            if (marker == 0x3b) { if (frames > 0 && p == b.Length) return; throw Invalid(); }
            if (marker == 0x21)
            {
                if (p >= b.Length) throw Invalid();
                var kind = b[p++];
                if (kind == 0xf9)
                { if (p + 6 > b.Length || b[p] != 4 || b[p + 5] != 0) throw Invalid(); p += 6; }
                else
                {
                    if (kind == 0xff && (p >= b.Length || b[p] != 11) || kind == 0x01 && (p >= b.Length || b[p] != 12) || kind is not (0xff or 0x01 or 0xfe)) throw Invalid();
                    SkipSubBlocks(b, ref p, false);
                }
            }
            else if (marker == 0x2c)
            {
                if (p + 9 >= b.Length) throw Invalid();
                var frameWidth = U16(b[(p + 4)..]); var frameHeight = U16(b[(p + 6)..]);
                Dimensions(frameWidth, frameHeight);
                if (U16(b[p..]) + frameWidth > width || U16(b[(p + 2)..]) + frameHeight > height) throw Invalid();
                var packed = b[p + 8]; p += 9;
                if ((packed & 0x80) != 0) p += 3 * (1 << ((packed & 7) + 1));
                if (p >= b.Length || b[p++] is < 2 or > 8) throw Invalid();
                SkipSubBlocks(b, ref p, true);
                if (++frames > 1000) throw Invalid();
            }
            else throw Invalid();
        }
        throw Invalid();
    }

    private static void SkipSubBlocks(ReadOnlySpan<byte> b, ref int p, bool requireData)
    {
        var data = false;
        while (p < b.Length)
        {
            var length = b[p++];
            if (length == 0) { if (requireData && !data) throw Invalid(); return; }
            if (p + length > b.Length) throw Invalid();
            p += length; data = true;
        }
        throw Invalid();
    }

    private static void Jpeg(ReadOnlySpan<byte> b)
    {
        var p = 2; var frame = false; var scan = false;
        while (p < b.Length)
        {
            if (b[p++] != 0xff) throw Invalid();
            while (p < b.Length && b[p] == 0xff) p++;
            if (p >= b.Length) throw Invalid();
            var marker = b[p++];
            if (marker == 0xd9) { if (p == b.Length && frame && scan) return; throw Invalid(); }
            if (marker is 0x00 or 0xd8 || marker is >= 0xd0 and <= 0xd7) throw Invalid();
            if (marker == 0x01) continue;
            if (p + 2 > b.Length) throw Invalid();
            var length = BinaryPrimitives.ReadUInt16BigEndian(b[p..]);
            if (length < 2 || p + length > b.Length) throw Invalid();
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 11 || b[p + 7] is < 1 or > 4 || length != 8 + 3 * b[p + 7]) throw Invalid();
                Dimensions(BinaryPrimitives.ReadUInt16BigEndian(b[(p + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(p + 3)..]));
                frame = true;
            }
            p += length;
            if (marker != 0xda) continue;
            if (!frame || length < 6) throw Invalid();
            var start = p;
            while (p < b.Length)
            {
                if (b[p] != 0xff) { p++; continue; }
                if (p + 1 >= b.Length) throw Invalid();
                if (b[p + 1] == 0 || b[p + 1] is >= 0xd0 and <= 0xd7) { p += 2; continue; }
                break;
            }
            if (p == start) throw Invalid();
            scan = true;
        }
        throw Invalid();
    }

    private static void Webp(ReadOnlySpan<byte> b)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) != b.Length - 8) throw Invalid();
        var p = 12; var image = false; var animation = false; var animationHeader = false; var frames = 0;
        while (p <= b.Length - 8)
        {
            var kind = b.Slice(p, 4); var size = BinaryPrimitives.ReadUInt32LittleEndian(b[(p + 4)..]);
            if (size > b.Length - p - 8) throw Invalid();
            var payload = b.Slice(p + 8, (int)size);
            if (kind.SequenceEqual("VP8X"u8))
            {
                if (p != 12 || size != 10 || (payload[0] & 0xc1) != 0 || payload[1] != 0 || payload[2] != 0 || payload[3] != 0) throw Invalid();
                Dimensions(U24(payload[4..]) + 1, U24(payload[7..]) + 1);
                animation = (payload[0] & 2) != 0;
            }
            else if (kind.SequenceEqual("VP8 "u8) || kind.SequenceEqual("VP8L"u8))
            { if (animation || image) throw Invalid(); WebpFrame(kind, payload); image = true; }
            else if (kind.SequenceEqual("ANIM"u8))
            { if (!animation || animationHeader || size != 6) throw Invalid(); animationHeader = true; }
            else if (kind.SequenceEqual("ANMF"u8))
            {
                if (!animationHeader || size < 24 || (payload[15] & 0xfc) != 0) throw Invalid();
                Dimensions(U24(payload[6..]) + 1, U24(payload[9..]) + 1);
                var inner = 16; var found = false;
                while (inner <= payload.Length - 8)
                {
                    var frameKind = payload.Slice(inner, 4); var frameSize = BinaryPrimitives.ReadUInt32LittleEndian(payload[(inner + 4)..]);
                    if (frameSize > payload.Length - inner - 8) throw Invalid();
                    if (frameKind.SequenceEqual("VP8 "u8) || frameKind.SequenceEqual("VP8L"u8))
                    { if (found) throw Invalid(); WebpFrame(frameKind, payload.Slice(inner + 8, (int)frameSize)); found = true; }
                    else if (!frameKind.SequenceEqual("ALPH"u8)) throw Invalid();
                    inner += 8 + (int)frameSize + ((int)frameSize & 1);
                }
                if (!found || inner != payload.Length || ++frames > 1000) throw Invalid();
            }
            else if (!kind.SequenceEqual("ALPH"u8) && !kind.SequenceEqual("ICCP"u8) && !kind.SequenceEqual("EXIF"u8) && !kind.SequenceEqual("XMP "u8)) throw Invalid();
            p += 8 + (int)size + ((int)size & 1);
        }
        if (p != b.Length || !(image || animation && frames > 0)) throw Invalid();
    }

    private static void WebpFrame(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> p)
    {
        if (kind.SequenceEqual("VP8 "u8))
        {
            if (p.Length < 11 || (p[0] & 1) != 0 || !p.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 1, 0x2a })) throw Invalid();
            Dimensions(U16(p[6..]) & 0x3fff, U16(p[8..]) & 0x3fff);
        }
        else
        {
            if (p.Length < 6 || p[0] != 0x2f || (p[4] & 0xe0) != 0) throw Invalid();
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(p[1..]);
            Dimensions((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
        }
    }

    private static int U16(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt16LittleEndian(b);
    private static int U24(ReadOnlySpan<byte> b) => b[0] | b[1] << 8 | b[2] << 16;
    private static uint U32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32BigEndian(b);
    private static InvalidDataException Invalid() => new("素材不是结构完整且在限制内的 PNG/JPEG/GIF/WebP 图片（最多5 MiB、每边4096像素）。");
}
