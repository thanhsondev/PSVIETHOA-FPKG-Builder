namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Xử lý khi thư mục xuất đã có gói cùng tên.</summary>
public enum OutputConflictChoice
{
    /// <summary>Ghi đè lên tệp cũ.</summary>
    Overwrite,

    /// <summary>Giữ tệp cũ bằng cách đổi tên nó, gói mới ghi vào tên gốc.</summary>
    KeepExisting,

    /// <summary>Không tạo gói.</summary>
    Cancel,
}

/// <summary>
/// Tìm gói đã có cùng Content ID trong thư mục xuất và giữ lại bản cũ khi người dùng muốn.
/// Thư viện luôn ghi đè tệp trùng tên, nên phải hỏi và xử lý trước khi gọi nó.
/// </summary>
public static class OutputConflict
{
    /// <summary>Các tệp .pkg trong thư mục xuất thuộc về Content ID này (tên gói là &lt;contentId&gt;-A….pkg).</summary>
    /// <param name="namePrefix">Tiền tố tên gói của lượt này ("UPDATE_" cho bản vá) — chỉ tệp cùng tiền tố mới là trùng.</param>
    /// <param name="exclude">Tệp không bao giờ được coi là trùng (gói gốc của bản vá: đổi tên/xoá nó là hỏng lượt tạo bản vá).</param>
    public static IReadOnlyList<string> Find(string outputFolder, string contentId, string namePrefix = "", string? exclude = null)
    {
        if (string.IsNullOrWhiteSpace(outputFolder) || string.IsNullOrWhiteSpace(contentId) || !Directory.Exists(outputFolder))
        {
            return Array.Empty<string>();
        }

        string? excluded = null;
        try
        {
            excluded = string.IsNullOrWhiteSpace(exclude) ? null : Path.GetFullPath(exclude);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        try
        {
            return Directory.EnumerateFiles(outputFolder, "*.pkg", SearchOption.TopDirectoryOnly)
                .Where(path => IsMatch(Path.GetFileName(path), contentId, namePrefix))
                .Where(path => excluded == null || !string.Equals(Path.GetFullPath(path), excluded, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Đổi tên tệp cũ thành "&lt;tên&gt; (1).pkg" (tăng dần cho tới khi trống). Trả về tên mới, null nếu không đổi được.</summary>
    /// <summary>
    /// Gói thường: &lt;contentId&gt;-….pkg trừ gói đi kèm bản vá (.remastered.pkg). Bản vá (<paramref name="namePrefix"/> = "UPDATE_"):
    /// UPDATE_&lt;contentId&gt;-….pkg và gói đi kèm &lt;contentId&gt;-….remastered.pkg của nó.
    /// </summary>
    private static bool IsMatch(string name, string contentId, string namePrefix)
    {
        var companion = name.EndsWith(SonySdkBuilder.CompanionExtension, StringComparison.OrdinalIgnoreCase);
        var plain = name.StartsWith(contentId + "-", StringComparison.OrdinalIgnoreCase);
        return namePrefix.Length == 0
            ? plain && !companion
            : name.StartsWith(namePrefix + contentId + "-", StringComparison.OrdinalIgnoreCase) || (plain && companion);
    }

    public static string? KeepExisting(string path)
    {
        var folder = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        if (folder == null || name.Length == 0)
        {
            return null;
        }

        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(folder, $"{name} ({index}){extension}");
            if (File.Exists(candidate))
            {
                continue;
            }

            try
            {
                File.Move(path, candidate);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Áp dụng lựa chọn cho tất cả tệp trùng; trả về false khi người dùng huỷ.</summary>
    public static bool Apply(IReadOnlyList<string> existing, OutputConflictChoice choice, Action<string, string?>? renamed)
    {
        if (choice == OutputConflictChoice.Cancel)
        {
            return false;
        }

        if (choice == OutputConflictChoice.KeepExisting)
        {
            foreach (var path in existing)
            {
                var target = KeepExisting(path);
                renamed?.Invoke(path, target);
                if (target == null)
                {
                    // Không đổi tên được bản cũ: dừng hẳn. Tạo tiếp sẽ ghi đè lên đúng tệp người dùng vừa bảo là muốn giữ.
                    return false;
                }
            }
        }

        return true;
    }
}
