using System.Text.Json.Nodes;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Gói gốc dùng làm tham chiếu khi tạo bản vá: Content ID và contentVersion đọc được từ gói.</summary>
public sealed record SonySdkReferenceInfo(string Path, string ContentId, string? ContentVersion, long Length);

/// <summary>
/// Bản vá (delta) của bộ công cụ fix8: <c>img_create --ref_pkg_path gói_gốc.pkg</c> tạo gói mới rồi so với gói gốc, chỉ giữ phần thay
/// đổi trong <c>&lt;tên&gt;.pkg</c> và ghi gói đầy đủ tương ứng ra <c>&lt;tên&gt;.pkg.remastered.pkg</c>. Điều kiện của Publishing
/// Tools: cùng Content ID và contentVersion của bản mới phải cao hơn gói gốc — kiểm tra trước để khỏi chờ SDK nén xong mới báo lỗi.
/// </summary>
public static class SonySdkPatchReference
{
    public const string RemasteredSuffix = ".remastered.pkg";

    /// <summary>Đọc gói gốc (chỉ header/CNT). Ném <see cref="InvalidDataException"/> (đã dịch) khi không dùng được làm tham chiếu.</summary>
    public static SonySdkReferenceInfo Inspect(string referencePath, string passcode, CancellationToken cancellationToken)
    {
        var full = System.IO.Path.GetFullPath(referencePath);
        if (!File.Exists(full))
        {
            throw new InvalidDataException(Loc.F("Patch.ReferenceMissing", full));
        }

        if (!string.Equals(System.IO.Path.GetExtension(full), ".pkg", StringComparison.OrdinalIgnoreCase) || full.EndsWith(RemasteredSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Loc.F("Patch.ReferenceNotPkg", System.IO.Path.GetFileName(full)));
        }

        Models.PackageInfo info;
        try
        {
            info = PackageInspector.Inspect(full, passcode, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Luôn nêu tên tệp: lỗi của trình đọc gói ("không phải PKG hợp lệ"…) không nói đang đọc tệp nào.
            throw new InvalidDataException(Loc.F("Patch.ReferenceUnreadable", System.IO.Path.GetFileName(full), ex.Message), ex);
        }

        var contentId = info.ContentId;
        if (string.IsNullOrEmpty(contentId))
        {
            throw new InvalidDataException(Loc.F("Patch.ReferenceUnreadable", System.IO.Path.GetFileName(full), "Content ID"));
        }

        return new SonySdkReferenceInfo(full, contentId, info.Params?.ContentVersion, new FileInfo(full).Length);
    }

    /// <summary>Lý do (đã dịch) gói gốc không hợp với bản mới, null khi hợp lệ.</summary>
    public static string? Mismatch(SonySdkReferenceInfo reference, string contentId, string? newContentVersion)
    {
        if (!string.Equals(reference.ContentId, contentId, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("Patch.ContentIdMismatch", reference.ContentId, contentId);
        }

        if (reference.ContentVersion != null && newContentVersion != null && CompareVersions(newContentVersion, reference.ContentVersion) <= 0)
        {
            return Loc.F("Patch.VersionNotHigher", newContentVersion, reference.ContentVersion);
        }

        return null;
    }

    /// <summary>contentVersion trong sce_sys/param.json của thư mục nguồn; null khi không đọc được.</summary>
    public static string? SourceContentVersion(string appFolder)
    {
        try
        {
            var path = System.IO.Path.Combine(appFolder, "sce_sys", "param.json");
            return JsonNode.Parse(File.ReadAllBytes(path)) is JsonObject node && node["contentVersion"] is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>So "01.002.000" theo từng nhóm số; nhóm thiếu coi là 0, phần không phải số so theo chữ.</summary>
    /// <summary>
    /// Phiên bản kế tiếp để gợi ý cho bản vá: tăng nhóm giữa của NN.NNN.NNN (01.000.000 → 01.001.000; 01.999.xxx → 02.000.000).
    /// Null khi <paramref name="version"/> không đọc được.
    /// </summary>
    public static string? NextVersion(string? version)
    {
        if (!VersionHelper.TryCanonicalize(version ?? string.Empty, out var canonical))
        {
            return null;
        }

        var parts = canonical.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return null;
        }

        if (++minor > 999)
        {
            minor = 0;
            major++;
        }

        return major > 99 ? null : $"{major:00}.{minor:000}.000";
    }

    /// <summary>Gói đầy đủ đi kèm của một bản vá UPDATE_&lt;tên&gt;.pkg: &lt;tên&gt;.remastered.pkg cùng thư mục.</summary>
    public static string CompanionPathOf(string patchPath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(patchPath);
        if (name.StartsWith(SonySdkBuilder.PatchPrefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[SonySdkBuilder.PatchPrefix.Length..];
        }

        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(patchPath) ?? string.Empty, name + SonySdkBuilder.CompanionExtension);
    }

    public static int CompareVersions(string left, string right)
    {
        var a = left.Trim().Split('.');
        var b = right.Trim().Split('.');
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var x = index < a.Length ? a[index] : "0";
            var y = index < b.Length ? b[index] : "0";
            var comparison = long.TryParse(x, out var nx) && long.TryParse(y, out var ny) ? nx.CompareTo(ny) : string.CompareOrdinal(x, y);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }
}
