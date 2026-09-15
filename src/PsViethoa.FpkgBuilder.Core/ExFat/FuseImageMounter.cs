using System.Runtime.InteropServices;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>Một ảnh exFAT đã gắn qua FUSE (Linux); Dispose để tháo.</summary>
public sealed class FuseMount : IDisposable
{
    private readonly FuseImageMounter.Session _session;
    private bool _disposed;

    internal FuseMount(string mountPoint, string sourceFolder, FuseImageMounter.Session session)
    {
        MountPoint = mountPoint;
        SourceFolder = sourceFolder;
        _session = session;
    }

    public string MountPoint { get; }

    /// <summary>Thư mục ứng dụng (chứa sce_sys/) nhìn thấy qua điểm gắn.</summary>
    public string SourceFolder { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }
}

/// <summary>
/// Gắn ảnh exFAT (.exfat và container .ffpfsc) thành thư mục chỉ đọc bằng libfuse3 trên Linux, dùng chung bộ đọc
/// managed với ổ ảo Dokan trên Windows — nên ẩn được tệp rác và đè được param.json y hệt.
///
/// <para>Không cần quyền root: libfuse3 gọi <c>fusermount3</c> (setuid) để xin kernel gắn. Máy thiếu libfuse3 hoặc
/// fusermount3 thì <see cref="IsAvailable"/> = false và BuildEngine tự quay về đường giải nén.</para>
/// </summary>
public static class FuseImageMounter
{
    /// <summary>libfuse3 nạp được và có fusermount3 để gắn mà không cần root.</summary>
    public static bool IsAvailable => OperatingSystem.IsLinux() && FuseNative.IsAvailable && FindFusermount() != null;

    /// <summary>Đường dẫn fusermount3 (libfuse gọi khi tiến trình không phải root).</summary>
    public static string? FindFusermount()
    {
        foreach (var candidate in new[] { "/usr/bin/fusermount3", "/bin/fusermount3", "/usr/local/bin/fusermount3", "/usr/bin/fusermount", "/bin/fusermount" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    /// <summary>Phiên bản libfuse (đọc từ tên tệp thư viện nạp được), hoặc null.</summary>
    public static string? LibraryVersion
    {
        get
        {
            if (!OperatingSystem.IsLinux() || !FuseNative.IsAvailable)
            {
                return null;
            }

            foreach (var path in new[] { "/usr/lib/libfuse3.so.4", "/usr/lib64/libfuse3.so.4", "/usr/lib/x86_64-linux-gnu/libfuse3.so.4" })
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    // libfuse3.so.4 thường là liên kết tới libfuse3.so.3.<minor>.<patch>
                    var target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.Name ?? Path.GetFileName(path);
                    var marker = "libfuse3.so.";
                    var index = target.IndexOf(marker, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        var version = target[(index + marker.Length)..];
                        if (version.Contains('.'))
                        {
                            return version;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }

            return "3";
        }
    }

    /// <summary>Thư mục gốc cho điểm gắn tạm: $XDG_RUNTIME_DIR nếu có, không thì thư mục tạm.</summary>
    private static string MountRoot()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtime))
        {
            try
            {
                if (Directory.Exists(runtime))
                {
                    return runtime;
                }
            }
            catch (Exception)
            {
            }
        }

        return Path.GetTempPath();
    }

    /// <summary>Gắn ảnh; ném <see cref="IOException"/> hoặc <see cref="PlatformNotSupportedException"/> khi hỏng.</summary>
    public static FuseMount Mount(
        ExFatImage image,
        ExFatEntry appRoot,
        string? wrapperName,
        bool hideJunk,
        IReadOnlyDictionary<string, byte[]>? overlays,
        IReadOnlyCollection<string>? hiddenPaths,
        string? volumeLabel,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("FUSE mounting is only available on Linux.");
        }

        if (!FuseNative.IsAvailable)
        {
            throw new PlatformNotSupportedException("libfuse3 is not available on this system.");
        }

        if (FindFusermount() == null)
        {
            throw new PlatformNotSupportedException("fusermount3 was not found — FUSE mounting needs it to mount without root.");
        }

        var model = new ExFatReadModel(image, appRoot, wrapperName, hideJunk, overlays, volumeLabel, hiddenPaths);
        var mountPoint = Path.Combine(MountRoot(), "psviethoa-fpkg-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mountPoint);

        var session = new Session(model, mountPoint);
        try
        {
            session.Start(cancellationToken);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        var folder = model.WrapperName == null ? mountPoint : Path.Combine(mountPoint, model.WrapperName);
        return new FuseMount(mountPoint, folder, session);
    }

    /// <summary>Vòng đời một phiên FUSE: fuse_new -> fuse_mount -> fuse_loop (luồng riêng) -> unmount/destroy.</summary>
    public sealed class Session : IDisposable
    {
        private readonly ExFatReadModel _model;
        private readonly string _mountPoint;
        private readonly ManualResetEventSlim _ready = new(false);

        private FuseVirtualFileSystem? _fileSystem;
        private IntPtr _fuse;
        private Thread? _loop;
        private Exception? _failure;
        private volatile bool _mounted;
        private bool _disposed;

        internal Session(ExFatReadModel model, string mountPoint)
        {
            _model = model;
            _mountPoint = mountPoint;
        }

        internal void Start(CancellationToken cancellationToken)
        {
            _fileSystem = new FuseVirtualFileSystem(_model);

            // fuse_args: argv[0] là tên chương trình; "-o ro" ép chỉ đọc ngay ở tầng kernel.
            var arguments = new[] { "psviethoa-fpkg", "-o", "ro,noatime,fsname=psviethoa-exfat,subtype=exfat" };
            var argv = Marshal.AllocHGlobal(IntPtr.Size * arguments.Length);
            var argPointers = new IntPtr[arguments.Length];
            var args = Marshal.AllocHGlobal(24); // struct fuse_args { int argc; char **argv; int allocated; }
            var mountPointPtr = Marshal.StringToCoTaskMemUTF8(_mountPoint);

            try
            {
                for (var index = 0; index < arguments.Length; index++)
                {
                    argPointers[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                    Marshal.WriteIntPtr(argv, index * IntPtr.Size, argPointers[index]);
                }

                Marshal.WriteInt32(args, 0, arguments.Length);
                Marshal.WriteIntPtr(args, 8, argv);
                Marshal.WriteInt32(args, 16, 0);

                _fuse = FuseNative.New(args, _fileSystem.Operations, FuseVirtualFileSystem.OperationsSize, IntPtr.Zero);
                if (_fuse == IntPtr.Zero)
                {
                    throw new IOException("fuse_new failed — libfuse could not create the filesystem.");
                }

                var mountResult = FuseNative.Mount(_fuse, mountPointPtr);
                if (mountResult != 0)
                {
                    throw new IOException($"fuse_mount failed ({mountResult}) for '{_mountPoint}' — check that /dev/fuse exists and is accessible.");
                }

                _mounted = true;

                _loop = new Thread(() =>
                {
                    try
                    {
                        _ready.Set();
                        FuseNative.Loop(_fuse);
                    }
                    catch (Exception ex)
                    {
                        _failure = ex;
                    }
                })
                {
                    IsBackground = true,
                    Name = "PSVIETHOA.FuseLoop",
                };
                _loop.Start();

                _ready.Wait(TimeSpan.FromSeconds(10), cancellationToken);
                WaitUntilVisible(cancellationToken);
            }
            catch
            {
                throw;
            }
            finally
            {
                Marshal.FreeCoTaskMem(mountPointPtr);
                foreach (var pointer in argPointers)
                {
                    if (pointer != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(pointer);
                    }
                }

                Marshal.FreeHGlobal(argv);
                Marshal.FreeHGlobal(args);
            }
        }

        /// <summary>Chờ kernel thật sự phơi nội dung (fuse_mount trả về trước khi vòng lặp phục vụ xong).</summary>
        private void WaitUntilVisible(CancellationToken cancellationToken)
        {
            var expected = _model.WrapperName;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            Exception? last = null;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_failure != null)
                {
                    throw new IOException("The FUSE loop stopped unexpectedly.", _failure);
                }

                try
                {
                    if (expected == null)
                    {
                        if (Directory.EnumerateFileSystemEntries(_mountPoint).Any())
                        {
                            return;
                        }
                    }
                    else if (Directory.Exists(Path.Combine(_mountPoint, expected)))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                Thread.Sleep(50);
            }

            throw new IOException("The FUSE mount did not become visible in time." + (last != null ? " " + last.Message : string.Empty));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_fuse != IntPtr.Zero)
                {
                    FuseNative.Exit(_fuse);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                if (_mounted && _fuse != IntPtr.Zero)
                {
                    FuseNative.Unmount(_fuse);
                    _mounted = false;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                _loop?.Join(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }

            try
            {
                if (_fuse != IntPtr.Zero)
                {
                    FuseNative.Destroy(_fuse);
                    _fuse = IntPtr.Zero;
                }
            }
            catch (Exception)
            {
            }

            // Phòng khi fuse_unmount không dọn được (tiến trình còn giữ tệp): gọi thẳng fusermount3 -u -z.
            try
            {
                if (IsStillMounted())
                {
                    ForceUnmount();
                }
            }
            catch (Exception)
            {
            }

            _fileSystem?.Dispose();
            _fileSystem = null;

            try
            {
                Directory.Delete(_mountPoint, recursive: false);
            }
            catch (Exception)
            {
            }

            _ready.Dispose();
        }

        private bool IsStillMounted()
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/self/mounts"))
                {
                    var fields = line.Split(' ');
                    if (fields.Length > 1 && Unescape(fields[1]) == _mountPoint)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        /// <summary>/proc/self/mounts thoát khoảng trắng và vài ký tự thành \040 kiểu bát phân.</summary>
        private static string Unescape(string value)
        {
            if (!value.Contains('\\'))
            {
                return value;
            }

            var builder = new System.Text.StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == '\\' && index + 3 < value.Length &&
                    int.TryParse(value.Substring(index + 1, 3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var octal))
                {
                    builder.Append((char)Convert.ToInt32(octal.ToString(), 8));
                    index += 3;
                }
                else
                {
                    builder.Append(value[index]);
                }
            }

            return builder.ToString();
        }

        private void ForceUnmount()
        {
            var fusermount = FindFusermount();
            if (fusermount == null)
            {
                return;
            }

            var info = new System.Diagnostics.ProcessStartInfo(fusermount)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add("-z");
            info.ArgumentList.Add(_mountPoint);

            using var process = System.Diagnostics.Process.Start(info);
            process?.WaitForExit(10_000);
        }
    }
}
