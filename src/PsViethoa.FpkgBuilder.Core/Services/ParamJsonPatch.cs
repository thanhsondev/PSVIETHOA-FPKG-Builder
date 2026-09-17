using System.Text.Json;
using System.Text.Json.Nodes;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Các sửa đổi tạm thời trên sce_sys/param.json trong lúc tạo gói.</summary>
/// <param name="ForceStandardDrm">
/// applicationDrmType → "standard" (fpkg-gui 0.6.5: "Forces the DRM mode to be redefined as standard — resolves the lock issue";
/// gói mang DRM "free" hiện biểu tượng khoá trên PS5 và không chạy).
/// </param>
/// <param name="ClearVersionFileUri">versionFileUri → "" (bỏ URL kiểm tra phiên bản của gói gốc lúc khởi động).</param>
/// <param name="ClearPlayGoAttributes">attribute3 → 0 (xoá cờ thuộc tính của gói gốc; xem <see cref="ParamJsonPatch"/>).</param>
/// <param name="LowerRequiredSystemVersion">
/// requiredSystemSoftwareVersion → phiên bản SDK của game (dạng major/minor của sdkVersion) khi nó đang cao hơn. fpkg-gui 0.6.8:
/// "Automatic downgrading of the required software version to the SDK-specified version". Engine chỉ tự hạ khi có chọn SDK;
/// tuỳ chọn này làm điều tương tự khi giữ SDK của game. Không bao giờ nâng phiên bản lên.
/// </param>
public readonly record struct ParamJsonPatchOptions(bool ForceStandardDrm, bool ClearVersionFileUri, bool ClearPlayGoAttributes, bool LowerRequiredSystemVersion = false)
{
    public static readonly ParamJsonPatchOptions None = new(false, false, false);

    /// <summary>Chỉ ép DRM (mặc định của thư viện trước 2.1.7).</summary>
    public static readonly ParamJsonPatchOptions DrmOnly = new(true, false, false);

    public bool Any => ForceStandardDrm || ClearVersionFileUri || ClearPlayGoAttributes || LowerRequiredSystemVersion;
}

/// <summary>
/// Sửa tạm sce_sys/param.json trong lúc tạo gói rồi khôi phục nguyên vẹn từng byte khi Dispose (kể cả khi tạo gói lỗi hoặc bị huỷ) —
/// thư viện đọc param.json thẳng từ thư mục mà nó sắp đóng gói nên không có cách nào khác ngoài ghi đè tạm.
/// <list type="bullet">
/// <item><c>applicationDrmType</c> → "standard": gói tạo với DRM "free" hiện biểu tượng khoá trên PS5.</item>
/// <item><c>versionFileUri</c> → "": bỏ URL kiểm tra phiên bản của gói gốc (gói tự tạo không cập nhật qua đường đó).</item>
/// <item>
/// <c>attribute3</c> → 0: hướng dẫn cộng đồng về lỗi "playgo"/màn hình đen yêu cầu xoá cờ PlayGo trong attribute3. Tài liệu công
/// khai (psdevwiki, 2026-09) KHÔNG mô tả bit PlayGo nào trong attribute3 — các bit đã biết là nhận thông tin video-out (bit 2),
/// Share Library Capture API (bit 4), HFR (bit 6), High Framerate Mode (bit 12) và Auto Scaling (bit 18) — nên ở đây cả trường
/// được đặt về 0 đúng như thao tác "bỏ tích hết cờ" trong trình sửa param. Vì vậy tuỳ chọn này mặc định tắt: chỉ bật khi game
/// không khởi chạy, và biết rằng các cờ hiển thị ở trên cũng bị tắt theo trong gói (thư mục nguồn không đổi).
/// </item>
/// </list>
/// </summary>
public sealed class ParamJsonPatch : IDisposable
{
    public const string StandardDrm = "standard";

    public const string DrmField = "applicationDrmType";

    public const string VersionFileUriField = "versionFileUri";

    public const string Attribute3Field = "attribute3";

    public const string SdkVersionField = "sdkVersion";

    public const string RequiredSystemVersionField = "requiredSystemSoftwareVersion";

    /// <summary>Phần major/minor của phiên bản gói (engine: ProsperoSdkVersions.ToPackageVersion).</summary>
    public const ulong PackageVersionMask = 0xFFFF000000000000UL;

    private readonly string _path;
    private readonly byte[] _original;
    private bool _restored;

    private ParamJsonPatch(string path, byte[] original, IReadOnlyList<string> changes)
    {
        _path = path;
        _original = original;
        Changes = changes;
    }

    /// <summary>Mô tả (đã dịch) từng thay đổi đã áp dụng, để ghi nhật ký.</summary>
    public IReadOnlyList<string> Changes { get; }

    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Đọc applicationDrmType của một param.json (null nếu thiếu trường hoặc tệp không đọc được).</summary>
    public static string? ReadDrmType(string paramJsonPath)
    {
        try
        {
            return ReadDrmType(File.ReadAllBytes(paramJsonPath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Đọc applicationDrmType từ nội dung param.json trong bộ nhớ (null nếu thiếu trường hoặc JSON hỏng).</summary>
    public static string? ReadDrmType(ReadOnlyMemory<byte> paramJson)
    {
        try
        {
            using var document = JsonDocument.Parse(paramJson, DocumentOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(DrmField, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Cần ép DRM không: trường có mặt và khác "standard" (thiếu trường thì thư viện tự dùng "standard").</summary>
    public static bool NeedsDrmRewrite(string? drmType) =>
        !string.IsNullOrWhiteSpace(drmType) && !string.Equals(drmType.Trim(), StandardDrm, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tạo bản param.json đã sửa từ nội dung gốc (dùng để đè tệp trên ổ ảo mà không đụng ảnh). Trả về null khi không có gì phải đổi;
    /// <paramref name="changes"/> mô tả các thay đổi đã áp dụng.
    /// </summary>
    public static byte[]? Rewrite(byte[] original, ParamJsonPatchOptions options, out IReadOnlyList<string> changes)
    {
        changes = Array.Empty<string>();
        if (!options.Any)
        {
            return null;
        }

        var node = JsonNode.Parse(original, documentOptions: DocumentOptions) as JsonObject;
        if (node == null)
        {
            return null;
        }

        var applied = ApplyTo(node, options);
        if (applied.Count == 0)
        {
            return null;
        }

        changes = applied;
        return JsonSerializer.SerializeToUtf8Bytes(node, WriteOptions);
    }

    /// <summary>Áp các sửa đổi lên cây JSON tại chỗ; trả về mô tả (đã dịch) của từng thay đổi thật sự xảy ra.</summary>
    public static IReadOnlyList<string> ApplyTo(JsonObject node, ParamJsonPatchOptions options)
    {
        var applied = new List<string>();
        if (options.ForceStandardDrm)
        {
            var drm = node[DrmField]?.GetValue<string>();
            if (NeedsDrmRewrite(drm))
            {
                node[DrmField] = StandardDrm;
                applied.Add(Loc.F("Plan.PatchDrm", drm!, StandardDrm));
            }
        }

        if (options.ClearVersionFileUri && ReadNonEmptyString(node, VersionFileUriField) is { } uri)
        {
            node[VersionFileUriField] = string.Empty;
            applied.Add(Loc.F("Plan.PatchVersionUri", Shorten(uri)));
        }

        if (options.ClearPlayGoAttributes && ReadNonZeroInt(node, Attribute3Field) is { } attribute3)
        {
            node[Attribute3Field] = 0;
            applied.Add(Loc.F("Plan.PatchAttribute3", attribute3));
        }

        if (options.LowerRequiredSystemVersion &&
            ReadHexVersion(node, SdkVersionField) is { } sdk &&
            ReadHexVersion(node, RequiredSystemVersionField) is { } required &&
            (sdk & PackageVersionMask) is var target && target != 0 && required > target)
        {
            node[RequiredSystemVersionField] = FormatHexVersion(target);
            applied.Add(Loc.F("Plan.PatchRequiredFw", FormatFirmware(required), FormatFirmware(target)));
        }

        return applied;
    }

    /// <summary>Có thay đổi nào sẽ được áp dụng lên nội dung param.json này không (dùng để quyết định gắn hay giải nén ảnh).</summary>
    public static bool NeedsRewrite(byte[] paramJson, ParamJsonPatchOptions options)
    {
        try
        {
            return Rewrite(paramJson, options, out _) != null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ghi tạm các sửa đổi nếu cần. Trả về null khi không có gì phải đổi; ném IOException khi tệp không ghi được (ví dụ ảnh gắn
    /// chỉ đọc) — người gọi quyết định bỏ qua hay đổi chiến lược.
    /// </summary>
    public static ParamJsonPatch? Apply(string paramJsonPath, ParamJsonPatchOptions options, Action<LogEntry>? log)
    {
        var original = File.ReadAllBytes(paramJsonPath);
        var rewritten = Rewrite(original, options, out var changes);
        if (rewritten == null)
        {
            return null;
        }

        File.WriteAllBytes(paramJsonPath, rewritten);
        foreach (var change in changes)
        {
            log?.Invoke(new LogEntry(LogLevel.Info, change));
        }

        return new ParamJsonPatch(paramJsonPath, original, changes);
    }

    public void Dispose()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        try
        {
            File.WriteAllBytes(_path, _original);
        }
        catch (Exception)
        {
            // Không khôi phục được (rất hiếm): tệp nguồn còn giữ bản đã sửa — vô hại cho lần tạo gói sau.
        }
    }

    private static string? ReadNonEmptyString(JsonObject node, string field)
    {
        if (node[field] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetValue<string>();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static long? ReadNonZeroInt(JsonObject node, string field)
    {
        if (node[field] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !value.TryGetValue<long>(out var number))
        {
            return null;
        }

        return number == 0 ? null : number;
    }

    /// <summary>Đọc một phiên bản dạng "0x0450000000000000" (null nếu thiếu hoặc sai dạng).</summary>
    public static ulong? ParseHexVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = text.Trim();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return digits.Length is > 0 and <= 16 &&
               ulong.TryParse(digits, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Định dạng giống engine: "0x" + 16 chữ số hex thường.</summary>
    public static string FormatHexVersion(ulong value) => "0x" + value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Phiên bản hệ thống dễ đọc: hai byte cao viết theo BCD — 0x1040000000000000 → "10.40", 0x0450000000000000 → "4.50".</summary>
    public static string FormatFirmware(ulong value) => $"{(value >> 56) & 0xFF:X}.{(value >> 48) & 0xFF:X2}";

    private static ulong? ReadHexVersion(JsonObject node, string field) =>
        node[field] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? ParseHexVersion(value.GetValue<string>()) : null;

    /// <summary>URL dài trong nhật ký: giữ đầu và cuối cho dễ đọc.</summary>
    private static string Shorten(string text) => text.Length <= 64 ? text : text[..40] + "…" + text[^20..];
}
