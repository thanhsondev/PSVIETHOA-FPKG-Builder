using System.Runtime.InteropServices;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>
/// Lớp P/Invoke tối thiểu cho libfuse3 (Linux). Chỉ khai báo đúng những gì một hệ thống tệp chỉ-đọc cần.
///
/// <para>Bố cục struct và chỉ số bảng hàm được xác nhận bằng cách biên dịch <c>offsetof</c> trên
/// &lt;fuse3/fuse.h&gt; của máy (libfuse 3.18, x86-64, _FILE_OFFSET_BITS=64) chứ không đoán theo tài liệu.</para>
///
/// <para>Thư viện được nạp qua <see cref="NativeLibrary"/> nên máy không có libfuse3 chỉ nhận
/// <see cref="IsAvailable"/> = false, không ném lỗi lúc nạp assembly.</para>
/// </summary>
internal static class FuseNative
{
    internal const string LibraryName = "libfuse3.so.4";

    // struct stat (x86-64, _FILE_OFFSET_BITS=64): sizeof = 144.
    internal const int StatSize = 144;
    private const int StatNlinkOffset = 16;
    private const int StatModeOffset = 24;
    private const int StatUidOffset = 28;
    private const int StatGidOffset = 32;
    private const int StatSizeOffset = 48;
    private const int StatBlksizeOffset = 56;
    private const int StatBlocksOffset = 64;
    private const int StatAtimOffset = 72;
    private const int StatMtimOffset = 88;
    private const int StatCtimOffset = 104;

    // struct statvfs (x86-64): sizeof = 112.
    internal const int StatvfsSize = 112;
    private const int StatvfsBsizeOffset = 0;
    private const int StatvfsFrsizeOffset = 8;
    private const int StatvfsBlocksOffset = 16;
    private const int StatvfsBfreeOffset = 24;
    private const int StatvfsBavailOffset = 32;
    private const int StatvfsNamemaxOffset = 80;

    /// <summary>struct fuse_operations: 43 con trỏ hàm (sizeof = 344).</summary>
    internal const int OperationsSlotCount = 43;

    internal const int OpGetattr = 0;
    internal const int OpOpen = 12;
    internal const int OpRead = 13;
    internal const int OpStatfs = 15;
    internal const int OpRelease = 17;
    internal const int OpOpendir = 23;
    internal const int OpReaddir = 24;
    internal const int OpReleasedir = 25;
    internal const int OpInit = 27;
    internal const int OpDestroy = 28;
    internal const int OpAccess = 29;

    /// <summary>struct fuse_file_info: fh nằm ở offset 16, flags ở offset 0.</summary>
    internal const int FileInfoFhOffset = 16;

    internal const uint S_IFDIR = 0x4000;
    internal const uint S_IFREG = 0x8000;

    internal const int EPERM = 1;
    internal const int ENOENT = 2;
    internal const int EIO = 5;
    internal const int EACCES = 13;
    internal const int EINVAL = 22;
    internal const int ENOTDIR = 20;
    internal const int EISDIR = 21;
    internal const int EROFS = 30;

    internal const int O_ACCMODE = 3;
    internal const int O_RDONLY = 0;

    // ===================== Kiểu callback =====================

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetattrDelegate(IntPtr path, IntPtr stat, IntPtr fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int OpenDelegate(IntPtr path, IntPtr fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReadDelegate(IntPtr path, IntPtr buffer, UIntPtr size, long offset, IntPtr fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReleaseDelegate(IntPtr path, IntPtr fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReaddirDelegate(IntPtr path, IntPtr buffer, IntPtr filler, long offset, IntPtr fileInfo, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int StatfsDelegate(IntPtr path, IntPtr statvfs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int AccessDelegate(IntPtr path, int mask);

    /// <summary>fuse_fill_dir_t: int (*)(void *buf, const char *name, const struct stat *st, off_t off, int flags).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int FillDirDelegate(IntPtr buffer, IntPtr name, IntPtr stat, long offset, int flags);

    // ===================== Nạp thư viện =====================

    private static readonly object Gate = new();
    private static bool _probed;
    private static IntPtr _handle;

    /// <summary>Nạp libfuse3 một lần; IntPtr.Zero nếu máy không có.</summary>
    internal static IntPtr Handle
    {
        get
        {
            lock (Gate)
            {
                if (!_probed)
                {
                    _probed = true;
                    if (OperatingSystem.IsLinux())
                    {
                        foreach (var name in new[] { LibraryName, "libfuse3.so.3", "libfuse3.so" })
                        {
                            if (NativeLibrary.TryLoad(name, out var handle))
                            {
                                _handle = handle;
                                break;
                            }
                        }
                    }
                }

                return _handle;
            }
        }
    }

    /// <summary>libfuse3 nạp được và các hàm cần dùng đều có.</summary>
    internal static bool IsAvailable
    {
        get
        {
            var handle = Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            foreach (var symbol in new[] { "fuse_new_31", "fuse_mount", "fuse_unmount", "fuse_destroy", "fuse_loop", "fuse_exit" })
            {
                if (!NativeLibrary.TryGetExport(handle, symbol, out _))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(Handle, name, out var address)
            ? address
            : throw new EntryPointNotFoundException($"libfuse3 is missing '{name}'.");

    // fuse_new là một symbol có phiên bản (FUSE_3.0 và FUSE_3.1); dlsym không giải được tên trần,
    // nên phải gọi thẳng alias fuse_new_31 — đây là API tương ứng FUSE_USE_VERSION 31.
    private delegate IntPtr FuseNew31Delegate(IntPtr args, IntPtr operations, UIntPtr operationsSize, IntPtr privateData);

    private delegate int FuseMountDelegate(IntPtr fuse, IntPtr mountPoint);

    private delegate void FuseUnmountDelegate(IntPtr fuse);

    private delegate void FuseDestroyDelegate(IntPtr fuse);

    private delegate int FuseLoopDelegate(IntPtr fuse);

    private delegate void FuseExitDelegate(IntPtr fuse);

    internal static IntPtr New(IntPtr args, IntPtr operations, int operationsSize, IntPtr privateData) =>
        Marshal.GetDelegateForFunctionPointer<FuseNew31Delegate>(Export("fuse_new_31"))(args, operations, (UIntPtr)operationsSize, privateData);

    internal static int Mount(IntPtr fuse, IntPtr mountPoint) =>
        Marshal.GetDelegateForFunctionPointer<FuseMountDelegate>(Export("fuse_mount"))(fuse, mountPoint);

    internal static void Unmount(IntPtr fuse) =>
        Marshal.GetDelegateForFunctionPointer<FuseUnmountDelegate>(Export("fuse_unmount"))(fuse);

    internal static void Destroy(IntPtr fuse) =>
        Marshal.GetDelegateForFunctionPointer<FuseDestroyDelegate>(Export("fuse_destroy"))(fuse);

    internal static int Loop(IntPtr fuse) =>
        Marshal.GetDelegateForFunctionPointer<FuseLoopDelegate>(Export("fuse_loop"))(fuse);

    internal static void Exit(IntPtr fuse) =>
        Marshal.GetDelegateForFunctionPointer<FuseExitDelegate>(Export("fuse_exit"))(fuse);

    [DllImport("libc", EntryPoint = "getuid")]
    internal static extern uint GetUid();

    [DllImport("libc", EntryPoint = "getgid")]
    internal static extern uint GetGid();

    // ===================== Ghi struct =====================

    /// <summary>Điền struct stat cho một mục (chỉ đọc: thư mục 0555, tệp 0444).</summary>
    internal static void WriteStat(IntPtr stat, bool isDirectory, long length, DateTime modifiedUtc, uint uid, uint gid)
    {
        for (var offset = 0; offset < StatSize; offset += 8)
        {
            Marshal.WriteInt64(stat, offset, 0);
        }

        var mode = isDirectory ? S_IFDIR | 0b101_101_101u : S_IFREG | 0b100_100_100u;
        Marshal.WriteInt32(stat, StatModeOffset, unchecked((int)mode));
        Marshal.WriteInt64(stat, StatNlinkOffset, isDirectory ? 2 : 1);
        Marshal.WriteInt32(stat, StatUidOffset, unchecked((int)uid));
        Marshal.WriteInt32(stat, StatGidOffset, unchecked((int)gid));
        Marshal.WriteInt64(stat, StatSizeOffset, length);
        Marshal.WriteInt64(stat, StatBlksizeOffset, 4096);
        Marshal.WriteInt64(stat, StatBlocksOffset, (length + 511) / 512);

        var seconds = ToUnixSeconds(modifiedUtc);
        foreach (var offset in new[] { StatAtimOffset, StatMtimOffset, StatCtimOffset })
        {
            Marshal.WriteInt64(stat, offset, seconds);
            Marshal.WriteInt64(stat, offset + 8, 0);
        }
    }

    private static long ToUnixSeconds(DateTime value)
    {
        try
        {
            var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
            var seconds = new DateTimeOffset(utc).ToUnixTimeSeconds();
            return seconds < 0 ? 0 : seconds;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Điền struct statvfs cho một ổ chỉ đọc đã đầy.</summary>
    internal static void WriteStatvfs(IntPtr statvfs, long volumeLengthBytes)
    {
        for (var offset = 0; offset < StatvfsSize; offset += 8)
        {
            Marshal.WriteInt64(statvfs, offset, 0);
        }

        const long blockSize = 4096;
        Marshal.WriteInt64(statvfs, StatvfsBsizeOffset, blockSize);
        Marshal.WriteInt64(statvfs, StatvfsFrsizeOffset, blockSize);
        Marshal.WriteInt64(statvfs, StatvfsBlocksOffset, Math.Max(volumeLengthBytes, 0) / blockSize);
        Marshal.WriteInt64(statvfs, StatvfsBfreeOffset, 0);
        Marshal.WriteInt64(statvfs, StatvfsBavailOffset, 0);
        Marshal.WriteInt64(statvfs, StatvfsNamemaxOffset, 255);
    }
}
