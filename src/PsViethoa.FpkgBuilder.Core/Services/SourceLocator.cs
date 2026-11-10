using PsViethoa.FpkgBuilder.Core.ExFat;

namespace PsViethoa.FpkgBuilder.Core.Services;

public enum SourceKind
{
    None,
    Folder,
    ExFatImage,

    /// <summary>Ảnh UFS2 (.ffpkg) — hệ tệp FreeBSD mà thư mục gốc chính là thư mục ứng dụng.</summary>
    UfsImage,
    Gp5Project,
}

/// <summary>Mô tả nguồn đã chọn: thư mục, ảnh exFAT (kèm thư mục ứng dụng bên trong ảnh) hoặc tệp dự án GP5.</summary>
public sealed record SourceInfo(SourceKind Kind, string Path, string AppRootInImage, string? VolumeLabel, bool IsPfsContainer = false)
{
    public bool IsExFat => Kind == SourceKind.ExFatImage;

    /// <summary>Ảnh UFS2 (.ffpkg).</summary>
    public bool IsUfs => Kind == SourceKind.UfsImage;

    /// <summary>Nguồn là một tệp ảnh (exFAT, container .ffpfsc hoặc UFS2).</summary>
    public bool IsImage => IsExFat || IsUfs;

    /// <summary>
    /// Ảnh exFAT nằm trong container .ffpfsc: chỉ giải nén được, không gắn (mount) được. Ảnh UFS2 cũng vậy — cả macOS lẫn
    /// Windows đều không có driver UFS, nên phải đọc bằng bộ đọc riêng.
    /// </summary>
    public bool CanMount => IsExFat && !IsPfsContainer;

    public bool IsGp5 => Kind == SourceKind.Gp5Project;

    /// <summary>Thư mục chứa tệp .gp5 (thư viện nhận nó làm SourceFolder khi tạo gói từ dự án GP5).</summary>
    public string ProjectDirectory => System.IO.Path.GetDirectoryName(Path) ?? Path;

    public string DisplayName => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
}

/// <summary>Nhận diện loại nguồn (thư mục, ảnh exFAT, dự án GP5) và tìm thư mục ứng dụng (chứa sce_sys) trong thư mục hoặc ảnh exFAT.</summary>
public static class SourceLocator
{
    public const int MaxImageSearchDepth = 3;

    public const string Gp5Extension = ".gp5";

    public static SourceKind Detect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SourceKind.None;
        }

        if (Directory.Exists(path))
        {
            return SourceKind.Folder;
        }

        if (!File.Exists(path))
        {
            return SourceKind.None;
        }

        if (HasGp5Extension(path))
        {
            return SourceKind.Gp5Project;
        }

        if (HasUfsExtension(path) || UfsImage.IsUfsFile(path))
        {
            return SourceKind.UfsImage;
        }

        if (HasExFatExtension(path) || HasPfsContainerExtension(path) || ExFatImage.IsExFatFile(path) || PfsContainer.IsContainer(path))
        {
            return SourceKind.ExFatImage;
        }

        return SourceKind.None;
    }

    public static bool HasExFatExtension(string path) =>
        string.Equals(Path.GetExtension(path), ExFatImage.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Đuôi .ffpfsc — container PFS chứa ảnh exFAT nén.</summary>
    public static bool HasPfsContainerExtension(string path) =>
        string.Equals(Path.GetExtension(path), PfsContainer.Extension, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), PfsContainer.RawExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Đuôi .ffpkg — ảnh UFS2 của FreeBSD.</summary>
    public static bool HasUfsExtension(string path) => UfsImage.HasExtension(path);

    /// <summary>Tệp ảnh (.exfat, .ffpfsc hoặc .ffpkg) theo đuôi, không cần mở tệp.</summary>
    public static bool HasImageExtension(string path) =>
        HasExFatExtension(path) || HasPfsContainerExtension(path) || HasUfsExtension(path);

    public static bool HasGp5Extension(string path) =>
        string.Equals(Path.GetExtension(path), Gp5Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Phân giải nguồn; với ảnh exFAT sẽ mở ảnh để tìm thư mục ứng dụng; với dự án GP5 chỉ trả về đường dẫn đầy đủ của tệp
    /// (nội dung dự án được kiểm tra ở BuildPreparer.Validate). Ném lỗi nếu không hợp lệ.
    /// </summary>
    public static SourceInfo Resolve(string path)
    {
        switch (Detect(path))
        {
            case SourceKind.Folder:
                return new SourceInfo(SourceKind.Folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), string.Empty, null);
            case SourceKind.Gp5Project:
                return new SourceInfo(SourceKind.Gp5Project, Path.GetFullPath(path), string.Empty, null);
            case SourceKind.ExFatImage:
            {
                using var image = ExFatImage.Open(path);
                var appRoot = FindAppRoot(image) ?? throw new InvalidDataException(Localization.Loc.T("Val.ExFatNoApp"));
                return new SourceInfo(SourceKind.ExFatImage, Path.GetFullPath(path), appRoot.Path.TrimStart('/'), image.VolumeLabel, image.IsPfsContainer);
            }

            case SourceKind.UfsImage:
            {
                using var image = UfsImage.Open(path);
                var appRoot = FindAppRoot(image) ?? throw new InvalidDataException(Localization.Loc.T("Val.ExFatNoApp"));
                return new SourceInfo(SourceKind.UfsImage, Path.GetFullPath(path), appRoot.Path.TrimStart('/'), null);
            }
            default:
                throw new FileNotFoundException(Localization.Loc.T("Val.SourceMissing"), path);
        }
    }

    /// <summary>Tìm thư mục ứng dụng trong ảnh UFS2: giống ảnh exFAT, ưu tiên gốc rồi tìm dần theo chiều sâu.</summary>
    public static UfsEntry? FindAppRoot(UfsImage image)
    {
        var level = new List<UfsEntry> { image.Root };
        for (var depth = 0; depth <= MaxImageSearchDepth && level.Count > 0; depth++)
        {
            var next = new List<UfsEntry>();
            foreach (var directory in level)
            {
                var children = image.Enumerate(directory).ToList();
                if (children.Any(c => c.IsDirectory && c.Name.Equals("sce_sys", StringComparison.OrdinalIgnoreCase)))
                {
                    return directory;
                }

                next.AddRange(children.Where(c => c.IsDirectory && !JunkFileFinder.IsJunkDirectoryName(c.Name)));
            }

            level = next;
        }

        return null;
    }

    /// <summary>Thư mục ứng dụng của một ảnh UFS2 đã phân giải.</summary>
    public static UfsEntry ResolveAppRoot(UfsImage image, SourceInfo source) =>
        source.AppRootInImage.Length == 0
            ? image.Root
            : image.Find(source.AppRootInImage) ?? throw new InvalidDataException(Localization.Loc.T("Val.ExFatNoApp"));

    /// <summary>Tìm thư mục ứng dụng trong ảnh: ưu tiên gốc, rồi tìm dần theo chiều sâu (bỏ qua thư mục rác).</summary>
    public static ExFatEntry? FindAppRoot(ExFatImage image)
    {
        var level = new List<ExFatEntry> { image.Root };
        for (var depth = 0; depth <= MaxImageSearchDepth && level.Count > 0; depth++)
        {
            var next = new List<ExFatEntry>();
            foreach (var directory in level)
            {
                var children = image.Enumerate(directory).ToList();
                if (children.Any(c => c.IsDirectory && string.Equals(c.Name, "sce_sys", StringComparison.OrdinalIgnoreCase)))
                {
                    return directory;
                }

                next.AddRange(children
                    .Where(c => c.IsDirectory && !JunkFileFinder.IsJunkDirectoryName(c.Name))
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));
            }

            level = next;
        }

        return null;
    }

    /// <summary>Mở ảnh và trả về mục thư mục ứng dụng tương ứng với SourceInfo.</summary>
    public static ExFatEntry ResolveAppRoot(ExFatImage image, SourceInfo source)
    {
        if (string.IsNullOrEmpty(source.AppRootInImage))
        {
            return image.Root;
        }

        return image.Find(source.AppRootInImage) ?? throw new InvalidDataException(Localization.Loc.T("Val.ExFatNoApp"));
    }
}
