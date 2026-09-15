using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>Cách gắn ảnh không sao chép có trên hệ thống này.</summary>
public enum MountBackend
{
    None,

    /// <summary>hdiutil của macOS: gắn ảnh .exfat thuần (chỉ đọc, không đổi được nội dung, không nhận .ffpfsc).</summary>
    Hdiutil,

    /// <summary>Ổ ảo Dokan trên Windows: bộ đọc exFAT của ứng dụng phơi ảnh (.exfat lẫn .ffpfsc) thành ổ đĩa, ẩn được tệp rác và đè được tệp.</summary>
    Dokan,

    /// <summary>FUSE trên Linux: cùng bộ đọc exFAT managed như Dokan, nên cũng gắn được .ffpfsc, ẩn tệp rác và đè tệp.</summary>
    Fuse,
}

/// <summary>Yêu cầu khi gắn: ẩn tệp rác hệ điều hành và/hoặc đè tệp bằng dữ liệu trong bộ nhớ (chỉ ổ ảo Dokan làm được).</summary>
public sealed record ImageMountRequest(bool HideJunk = true, IReadOnlyDictionary<string, byte[]>? Overlays = null, IReadOnlyCollection<string>? HiddenPaths = null);

/// <summary>Ảnh đã gắn: điểm gắn, thư mục ứng dụng bên trong và backend đã dùng. Dispose để tháo.</summary>
public sealed class ImageMount : IDisposable
{
    private readonly IDisposable _handle;
    private bool _disposed;

    internal ImageMount(string mountPoint, string sourceFolder, MountBackend backend, IDisposable handle)
    {
        MountPoint = mountPoint;
        SourceFolder = sourceFolder;
        Backend = backend;
        _handle = handle;
    }

    public string MountPoint { get; }

    /// <summary>Thư mục ứng dụng (chứa sce_sys/) nhìn thấy qua điểm gắn — đưa thẳng cho thư viện.</summary>
    public string SourceFolder { get; }

    public MountBackend Backend { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}

/// <summary>
/// Gắn ảnh exFAT để tạo gói mà không sao chép dữ liệu: hdiutil trên macOS, ổ ảo Dokan trên Windows. Khả năng của từng
/// backend khác nhau (container .ffpfsc, ẩn tệp rác, đè param.json) nên BuildEngine hỏi ở đây trước khi chọn gắn hay giải nén.
/// </summary>
public static class ImageMounter
{
    public static MountBackend Backend =>
        ExFatMounter.IsAvailable ? MountBackend.Hdiutil
        : DokanImageMounter.IsSupportedPlatform && DokanImageMounter.IsDriverInstalled ? MountBackend.Dokan
        : FuseImageMounter.IsAvailable ? MountBackend.Fuse
        : MountBackend.None;

    public static bool IsAvailable => Backend != MountBackend.None;

    /// <summary>Gắn được cả container .ffpfsc (bộ đọc exFAT của ứng dụng nằm trên lớp giải nén PFS).</summary>
    public static bool CanMountContainers => Backend is MountBackend.Dokan or MountBackend.Fuse;

    /// <summary>Ẩn được tệp rác ngay trên ổ gắn (không cần giải nén để lọc).</summary>
    public static bool CanHideJunk => Backend is MountBackend.Dokan or MountBackend.Fuse;

    /// <summary>Đè được tệp (param.json ép DRM) ngay trên ổ gắn mà không đụng ảnh gốc.</summary>
    public static bool CanOverlayFiles => Backend is MountBackend.Dokan or MountBackend.Fuse;

    /// <summary>Windows chưa cài driver Dokan — có thể gắn nếu người dùng cài.</summary>
    public static bool DokanMissingOnWindows => DokanImageMounter.IsSupportedPlatform && !DokanImageMounter.IsDriverInstalled;

    public static bool CanMount(SourceInfo source) => source.IsExFat && (source.IsPfsContainer ? CanMountContainers : IsAvailable);

    /// <summary>Gắn được tệp ảnh này không (theo phần mở rộng/đầu tệp, không cần SourceInfo).</summary>
    public static bool CanMountPath(string path)
    {
        switch (Backend)
        {
            case MountBackend.Dokan:
            case MountBackend.Fuse:
                return true;
            case MountBackend.Hdiutil:
                return !SourceLocator.HasPfsContainerExtension(path) && !PfsContainer.IsContainer(path);
            default:
                return false;
        }
    }

    /// <summary>Nhãn ngắn cho nhật ký / lệnh info.</summary>
    public static string BackendLabel => Backend switch
    {
        MountBackend.Hdiutil => "hdiutil (macOS)",
        MountBackend.Dokan => "Dokan" + (DokanImageMounter.DriverVersion is { } version ? " " + version : string.Empty) + " (Windows)",
        MountBackend.Fuse => "libfuse" + (FuseImageMounter.LibraryVersion is { } fuseVersion ? " " + fuseVersion : string.Empty) + " (Linux)",
        _ => "—",
    };

    public static ImageMount Mount(SourceInfo source, ImageMountRequest request, CancellationToken cancellationToken)
    {
        if (!source.IsExFat)
        {
            throw new ArgumentException("Not an exFAT image source.", nameof(source));
        }

        switch (Backend)
        {
            case MountBackend.Hdiutil:
            {
                if (source.IsPfsContainer)
                {
                    throw new NotSupportedException("hdiutil cannot mount a PFS container (.ffpfsc).");
                }

                var mount = ExFatMounter.Mount(source.Path, cancellationToken);
                var folder = Path.Combine(mount.MountPoint, source.AppRootInImage.Replace('/', Path.DirectorySeparatorChar));
                return new ImageMount(mount.MountPoint, folder, MountBackend.Hdiutil, mount);
            }

            case MountBackend.Dokan:
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Dokan is only available on Windows.");
                }

                var image = ExFatImage.Open(source.Path);
                try
                {
                    var appRoot = SourceLocator.ResolveAppRoot(image, source);
                    var wrapper = WrapperNameFor(source);
                    var mount = DokanImageMounter.Mount(image, appRoot, wrapper, request.HideJunk, request.Overlays, request.HiddenPaths, image.VolumeLabel ?? wrapper, cancellationToken);
                    return new ImageMount(mount.MountPoint, mount.SourceFolder, MountBackend.Dokan, mount);
                }
                catch
                {
                    image.Dispose();
                    throw;
                }
            }

            case MountBackend.Fuse:
            {
                var image = ExFatImage.Open(source.Path);
                try
                {
                    var appRoot = SourceLocator.ResolveAppRoot(image, source);
                    var wrapper = WrapperNameFor(source);
                    var mount = FuseImageMounter.Mount(image, appRoot, wrapper, request.HideJunk, request.Overlays, request.HiddenPaths, image.VolumeLabel ?? wrapper, cancellationToken);
                    return new ImageMount(mount.MountPoint, mount.SourceFolder, MountBackend.Fuse, new FuseMountHandle(mount, image));
                }
                catch
                {
                    image.Dispose();
                    throw;
                }
            }

            default:
                throw new PlatformNotSupportedException("No image mount backend is available on this system.");
        }
    }

    /// <summary>Tháo ổ FUSE rồi mới đóng ảnh (vòng lặp FUSE còn đọc ảnh cho tới khi tháo xong).</summary>
    private sealed class FuseMountHandle : IDisposable
    {
        private readonly FuseMount _mount;
        private readonly ExFatImage _image;

        internal FuseMountHandle(FuseMount mount, ExFatImage image)
        {
            _mount = mount;
            _image = image;
        }

        public void Dispose()
        {
            try
            {
                _mount.Dispose();
            }
            finally
            {
                _image.Dispose();
            }
        }
    }

    /// <summary>Tên thư mục bọc trong ổ ảo: tên tệp ảnh bỏ phần mở rộng, thay ký tự không hợp lệ.</summary>
    public static string WrapperNameFor(SourceInfo source)
    {
        var name = Path.GetFileNameWithoutExtension(source.DisplayName);
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "image" : cleaned;
    }
}
