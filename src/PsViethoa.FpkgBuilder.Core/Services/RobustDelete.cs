using System.Text;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Xoá tệp/thư mục chịu được lỗi chuẩn hoá Unicode của trình điều khiển exFAT/FAT mới trên macOS (FSKit, macOS 26): tên có dấu được
/// liệt kê ở dạng NFD, nhưng unlink bằng đúng tên NFD đó báo thành công mà tệp vẫn còn; chỉ tên NFC mới xoá thật. Directory.Delete
/// đệ quy vì vậy dừng ở "Directory not empty" và bỏ lại cả thư mục giải nén tạm. Ở đây mỗi lần xoá đều kiểm tra lại, còn thì thử
/// dạng chuẩn hoá khác. Chỉ dùng cho thư mục thuộc về công cụ (thư mục tạm), không bao giờ cho nguồn của người dùng.
/// </summary>
public static class RobustDelete
{
    private static readonly NormalizationForm[] Forms = { NormalizationForm.FormC, NormalizationForm.FormD };

    /// <summary>Xoá một tệp; true nếu sau đó tệp không còn.</summary>
    public static bool File(string path)
    {
        System.IO.File.Delete(path);
        if (!System.IO.File.Exists(path) && !IsListed(path))
        {
            return true;
        }

        foreach (var alternative in Alternatives(path))
        {
            System.IO.File.Delete(alternative);
            if (!IsListed(path))
            {
                return true;
            }
        }

        return !IsListed(path);
    }

    /// <summary>
    /// Xoá cả cây thư mục. Thư mục liên kết (symlink/junction) chỉ bị gỡ liên kết, không bao giờ đi xuyên vào trong.
    /// </summary>
    public static void Tree(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            return;
        }

        if (directory.LinkTarget != null)
        {
            directory.Delete();
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path).ToArray())
        {
            var child = new DirectoryInfo(entry);
            if (child.Exists)
            {
                Tree(entry);
            }
            else
            {
                File(entry);
            }
        }

        try
        {
            Directory.Delete(path);
        }
        catch (IOException)
        {
            foreach (var alternative in Alternatives(path))
            {
                try
                {
                    Directory.Delete(alternative);
                    return;
                }
                catch (IOException)
                {
                }
            }

            throw;
        }
    }

    /// <summary>Tệp còn thật sự nằm trong thư mục cha không (so sánh không phân biệt dạng chuẩn hoá).</summary>
    private static bool IsListed(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return false;
        }

        var name = Path.GetFileName(path).Normalize(NormalizationForm.FormC);
        return Directory.EnumerateFileSystemEntries(parent)
            .Any(entry => string.Equals(Path.GetFileName(entry).Normalize(NormalizationForm.FormC), name, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Alternatives(string path)
    {
        var parent = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);
        foreach (var form in Forms)
        {
            var normalized = name.Normalize(form);
            if (!string.Equals(normalized, name, StringComparison.Ordinal))
            {
                yield return Path.Combine(parent, normalized);
            }
        }
    }
}
