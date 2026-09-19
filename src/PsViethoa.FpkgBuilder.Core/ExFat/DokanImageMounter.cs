using System.Diagnostics;
using System.Runtime.Versioning;
using DokanNet;
using DokanNet.Logging;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>Ổ ảo Dokan đang gắn một ảnh exFAT; Dispose để tháo ổ và đóng ảnh.</summary>
public sealed class DokanMount : IDisposable
{
    private readonly DokanInstance _instance;
    private readonly Dokan _dokan;
    private readonly ExFatImage _image;
    private bool _disposed;

    internal DokanMount(string mountPoint, string sourceFolder, DokanInstance instance, Dokan dokan, ExFatImage image)
    {
        MountPoint = mountPoint;
        SourceFolder = sourceFolder;
        _instance = instance;
        _dokan = dokan;
        _image = image;
    }

    /// <summary>Ký tự ổ đĩa đã gắn, ví dụ <c>Z:\</c>.</summary>
    public string MountPoint { get; }

    /// <summary>Thư mục ứng dụng bên trong ổ ảo (thư mục bọc, không phải gốc ổ đĩa).</summary>
    public string SourceFolder { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (OperatingSystem.IsWindows())
        {
            Unmount();
        }

        _image.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private void Unmount()
    {
        try
        {
            _instance.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            _dokan.RemoveMountPoint(MountPoint);
        }
        catch (Exception)
        {
        }

        try
        {
            _dokan.Dispose();
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// Gắn ảnh exFAT (.exfat hoặc .ffpfsc) thành ổ đĩa ảo chỉ đọc qua driver Dokan trên Windows — tương đương hdiutil trên macOS:
/// thư viện đọc thẳng từ ảnh, không cần giải nén ra ổ tạm. Driver do người dùng cài (miễn phí, mã nguồn mở); ứng dụng chỉ
/// kiểm tra <c>dokan2.dll</c> trong System32 và gọi khi có.
/// </summary>
public static class DokanImageMounter
{
    public const string DownloadUrl = "https://github.com/dokan-dev/dokany/releases/latest";

    private const string DriverLibrary = "dokan2.dll";
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(30);

    public static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    /// <summary>Đường dẫn thư viện người dùng của Dokan (chỉ Windows).</summary>
    public static string? DriverLibraryPath
    {
        get
        {
            if (!IsSupportedPlatform)
            {
                return null;
            }

            try
            {
                return Path.Combine(Environment.SystemDirectory, DriverLibrary);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Driver Dokan đã cài chưa (kiểm tra lại mỗi lần gọi để nhận ra ngay sau khi người dùng cài).</summary>
    public static bool IsDriverInstalled => DriverLibraryPath is { } path && File.Exists(path);

    /// <summary>Phiên bản Dokan đã cài (theo tệp dokan2.dll), null khi chưa cài.</summary>
    public static string? DriverVersion
    {
        get
        {
            try
            {
                var path = DriverLibraryPath;
                if (path == null || !File.Exists(path))
                {
                    return null;
                }

                var info = FileVersionInfo.GetVersionInfo(path);
                var version = info.FileVersion ?? info.ProductVersion;
                return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Kết quả thăm dò driver: đã cài (dokan2.dll), đang chạy (driver trả lời qua dokan2.dll) và phiên bản.</summary>
    public sealed record DokanProbe(bool Installed, bool Running, string? LibraryVersion, string? DriverVersion, string? Error);

    /// <summary>
    /// Thăm dò thật: nạp dokan2.dll và hỏi phiên bản driver. Driver trả lời (&gt; 0) nghĩa là đã cài và đang chạy — đủ để gắn ổ;
    /// chưa cài hoặc chưa khởi động lại thì báo tương ứng. Không ném ngoại lệ.
    /// </summary>
    public static DokanProbe Probe()
    {
        if (!OperatingSystem.IsWindows() || !IsDriverInstalled)
        {
            return new DokanProbe(false, false, null, null, null);
        }

        return ProbeWindows();
    }

    [SupportedOSPlatform("windows")]
    private static DokanProbe ProbeWindows()
    {
        try
        {
            using var dokan = new Dokan(new NullLogger());
            var library = dokan.Version;
            var driver = dokan.DriverVersion;
            return new DokanProbe(true, driver > 0, FormatVersion(library), FormatVersion(driver), null);
        }
        catch (Exception ex)
        {
            return new DokanProbe(true, false, null, null, ex.Message);
        }
    }

    /// <summary>DokanVersion/DokanDriverVersion trả về số dạng phiên bản × 100 (ví dụ 230 = 2.3.0).</summary>
    private static string? FormatVersion(int value) =>
        value <= 0 ? null : $"{value / 100}.{value % 100 / 10}.{value % 10}";

    /// <summary>
    /// Gắn thư mục ứng dụng của ảnh thành ổ ảo. Ổ ảo chứa một thư mục bọc (<paramref name="wrapperName"/>) trỏ tới thư mục
    /// ứng dụng, nên thư mục nguồn đưa cho thư viện không bao giờ là gốc ổ đĩa. Ảnh <paramref name="image"/> thuộc về ổ ảo và
    /// được đóng khi tháo.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static DokanMount Mount(
        ExFatImage image,
        ExFatEntry appRoot,
        string wrapperName,
        bool hideJunk,
        IReadOnlyDictionary<string, byte[]>? overlays,
        IReadOnlyCollection<string>? hiddenPaths,
        string? volumeLabel,
        CancellationToken cancellationToken)
    {
        if (!IsDriverInstalled)
        {
            throw new IOException("Dokan driver (dokan2.dll) is not installed.");
        }

        var letter = PickDriveLetter(UsedDriveLetters()) ?? throw new IOException("No free drive letter for the virtual disk.");
        var mountPoint = letter + ":\\";
        var fileSystem = new ExFatVirtualFileSystem(image, appRoot, wrapperName, hideJunk, overlays, volumeLabel, hiddenPaths);

        Dokan? dokan = null;
        DokanInstance? instance = null;
        try
        {
            dokan = new Dokan(new NullLogger());
            instance = new DokanInstanceBuilder(dokan)
                .ConfigureOptions(options =>
                {
                    options.MountPoint = mountPoint;
                    options.Options = DokanOptions.WriteProtection | DokanOptions.RemovableDrive;
                    options.SingleThread = false;
                    options.TimeOut = TimeSpan.FromSeconds(120);
                    options.AllocationUnitSize = 4096;
                    options.SectorSize = 512;
                })
                .Build(fileSystem);

            if (!fileSystem.WaitForMount(MountTimeout, cancellationToken))
            {
                throw new IOException($"Dokan did not mount {mountPoint} within {MountTimeout.TotalSeconds:0} s.");
            }

            var sourceFolder = Path.Combine(mountPoint, fileSystem.SourceRelativePath);
            var deadline = DateTime.UtcNow + MountTimeout;
            while (!Directory.Exists(sourceFolder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline || !instance.IsFileSystemRunning())
                {
                    throw new IOException($"The virtual disk {mountPoint} did not become visible.");
                }

                Thread.Sleep(100);
            }

            return new DokanMount(mountPoint, sourceFolder, instance, dokan, image);
        }
        catch (Exception ex)
        {
            try
            {
                instance?.Dispose();
            }
            catch (Exception)
            {
            }

            try
            {
                dokan?.Dispose();
            }
            catch (Exception)
            {
            }

            if (ex is DokanException)
            {
                throw new IOException("Dokan: " + ex.Message, ex);
            }

            throw;
        }
    }

    /// <summary>Chọn ký tự ổ trống cao nhất (Z → D) để không đụng các ổ thật thường nằm ở đầu bảng chữ cái.</summary>
    public static char? PickDriveLetter(IEnumerable<char> used)
    {
        var taken = new HashSet<char>(used.Select(char.ToUpperInvariant));
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!taken.Contains(letter))
            {
                return letter;
            }
        }

        return null;
    }

    private static IEnumerable<char> UsedDriveLetters()
    {
        var letters = new List<char>();
        try
        {
            foreach (var drive in Services.DiskSpaceAdvisor.AllDrives(fresh: true))
            {
                if (drive.Name.Length > 0)
                {
                    letters.Add(drive.Name[0]);
                }
            }
        }
        catch (Exception)
        {
        }

        // Ổ mạng ngắt kết nối hoặc điểm gắn khác có thể không xuất hiện trong DriveInfo — kiểm tra thêm bằng thư mục gốc.
        for (var letter = 'D'; letter <= 'Z'; letter++)
        {
            if (!letters.Contains(letter) && Directory.Exists(letter + ":\\"))
            {
                letters.Add(letter);
            }
        }

        return letters;
    }
}
