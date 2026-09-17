using System.IO.Enumeration;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Tìm tệp rác do hệ điều hành sinh ra (.DS_Store, Thumbs.db, ._AppleDouble…) và lối tắt Internet <c>*.url</c> mà trang chia sẻ bản
/// dump chèn vào (ví dụ "更多资源请访问 2468c.com.url" trong sce_sys) – những tệp này sẽ bị đóng vào gói PKG nếu không dọn trước. Tên
/// ngoài ASCII của lối tắt còn làm Publishing Tools từ chối cả GP5; game PS5 không bao giờ chứa tệp .url.
/// </summary>
public static class JunkFileFinder
{
    private static readonly string[] JunkFileNames =
    [
        ".DS_Store", "Thumbs.db", "ehthumbs.db", "ehthumbs_vista.db", "desktop.ini", ".localized",
    ];

    private static readonly string[] JunkFileExtensions = [".url"];

    private static readonly string[] JunkDirectoryNames =
    [
        "__MACOSX", ".Spotlight-V100", ".fseventsd", ".Trashes", ".TemporaryItems", ".AppleDouble", "$RECYCLE.BIN", "System Volume Information",
    ];

    public static bool IsJunkFileName(ReadOnlySpan<char> name)
    {
        if (name.StartsWith("._", StringComparison.Ordinal) && name.Length > 2)
        {
            return true;
        }

        foreach (var junk in JunkFileNames)
        {
            if (name.Equals(junk, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var extension in JunkFileExtensions)
        {
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsJunkDirectoryName(ReadOnlySpan<char> name)
    {
        foreach (var junk in JunkDirectoryNames)
        {
            if (name.Equals(junk, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tìm tệp rác trong thư mục hoặc bên trong ảnh exFAT (các mục trong ảnh là chỉ đọc). Dự án GP5 không quét (chỉ đóng gói tệp được liệt kê).</summary>
    public static IReadOnlyList<JunkFile> Find(string sourcePath, CancellationToken cancellationToken)
    {
        switch (SourceLocator.Detect(sourcePath))
        {
            case SourceKind.ExFatImage:
                return FindInImage(sourcePath, cancellationToken);
            case SourceKind.UfsImage:
                return FindInUfsImage(sourcePath, cancellationToken);
            case SourceKind.Gp5Project:
                return Array.Empty<JunkFile>();
            default:
                return FindInFolder(sourcePath, cancellationToken);
        }
    }

    private static IReadOnlyList<JunkFile> FindInImage(string imagePath, CancellationToken cancellationToken)
    {
        var results = new List<JunkFile>();
        using var image = PsViethoa.FpkgBuilder.Core.ExFat.ExFatImage.Open(imagePath);
        var root = SourceLocator.FindAppRoot(image) ?? image.Root;
        foreach (var entry in image.Walk(root, child => !IsJunkDirectoryName(child.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var junk = entry.IsDirectory ? IsJunkDirectoryName(entry.Name) : IsJunkFileName(entry.Name);
            if (junk)
            {
                results.Add(new JunkFile(imagePath + "!" + entry.Path, entry.Length, entry.IsDirectory, IsReadOnly: true));
            }
        }

        return results;
    }

    /// <summary>Tệp rác trong ảnh UFS2 (.ffpkg): chỉ liệt kê, không xoá được vì ảnh chỉ đọc.</summary>
    private static IReadOnlyList<JunkFile> FindInUfsImage(string imagePath, CancellationToken cancellationToken)
    {
        var results = new List<JunkFile>();
        using var image = PsViethoa.FpkgBuilder.Core.ExFat.UfsImage.Open(imagePath);
        var root = SourceLocator.FindAppRoot(image) ?? image.Root;
        foreach (var entry in image.Walk(root, child => !IsJunkDirectoryName(child.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var junk = entry.IsDirectory ? IsJunkDirectoryName(entry.Name) : IsJunkFileName(entry.Name);
            if (junk)
            {
                results.Add(new JunkFile(imagePath + "!" + entry.Path, entry.Length, entry.IsDirectory, IsReadOnly: true));
            }
        }

        return results;
    }

    public static IReadOnlyList<JunkFile> FindInFolder(string root, CancellationToken cancellationToken)
    {
        var results = new List<JunkFile>();
        var enumerable = new FileSystemEnumerable<JunkFile>(
            root,
            (ref FileSystemEntry entry) => new JunkFile(entry.ToFullPath(), entry.IsDirectory ? 0 : entry.Length, entry.IsDirectory),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                entry.IsDirectory ? IsJunkDirectoryName(entry.FileName) : IsJunkFileName(entry.FileName),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsJunkDirectoryName(entry.FileName),
        };

        foreach (var junk in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(junk);
        }

        return results;
    }

    /// <summary>Xoá các mục rác; trả về số mục đã xoá và danh sách lỗi.</summary>
    public static (int Deleted, IReadOnlyList<string> Errors) Delete(IEnumerable<JunkFile> items)
    {
        var deleted = 0;
        var errors = new List<string>();
        foreach (var item in items)
        {
            if (item.IsReadOnly)
            {
                errors.Add(item.Path + ": " + Localization.Loc.T("Junk.CannotDeleteExFat"));
                continue;
            }

            try
            {
                if (item.IsDirectory)
                {
                    if (Directory.Exists(item.Path))
                    {
                        Directory.Delete(item.Path, recursive: true);
                        deleted++;
                    }
                }
                else if (File.Exists(item.Path))
                {
                    File.SetAttributes(item.Path, FileAttributes.Normal);
                    File.Delete(item.Path);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{item.Path}: {ex.Message}");
            }
        }

        return (deleted, errors);
    }
}
