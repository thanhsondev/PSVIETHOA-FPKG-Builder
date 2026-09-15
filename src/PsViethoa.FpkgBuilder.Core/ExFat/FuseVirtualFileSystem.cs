using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>
/// Lớp vỏ FUSE (Linux) phơi <see cref="ExFatReadModel"/> thành một thư mục gắn chỉ đọc — đối xứng với
/// <see cref="ExFatVirtualFileSystem"/> bên Dokan. Lớp này chỉ ánh xạ callback của libfuse sang mô hình đọc và đổi
/// <see cref="ExFatReadStatus"/> thành -errno; mọi ngữ nghĩa nằm trong mô hình dùng chung.
///
/// <para>Bảng con trỏ hàm được cấp phát bền (các delegate được giữ trong trường để GC không thu hồi khi libfuse
/// còn gọi từ luồng native).</para>
/// </summary>
internal sealed class FuseVirtualFileSystem : IDisposable
{
    private readonly ExFatReadModel _model;
    private readonly uint _uid;
    private readonly uint _gid;
    private readonly ConcurrentDictionary<ulong, ExFatFileHandle> _handles = new();
    private long _nextHandle;

    // Giữ tham chiếu tới delegate: nếu để GC thu hồi, libfuse sẽ gọi vào con trỏ chết.
    private readonly FuseNative.GetattrDelegate _getattr;
    private readonly FuseNative.OpenDelegate _open;
    private readonly FuseNative.ReadDelegate _read;
    private readonly FuseNative.ReleaseDelegate _release;
    private readonly FuseNative.ReaddirDelegate _readdir;
    private readonly FuseNative.StatfsDelegate _statfs;
    private readonly FuseNative.AccessDelegate _access;

    private IntPtr _operations;
    private bool _disposed;

    internal FuseVirtualFileSystem(ExFatReadModel model)
    {
        _model = model;
        _uid = FuseNative.GetUid();
        _gid = FuseNative.GetGid();

        _getattr = Getattr;
        _open = Open;
        _read = Read;
        _release = Release;
        _readdir = Readdir;
        _statfs = Statfs;
        _access = Access;

        _operations = Marshal.AllocHGlobal(FuseNative.OperationsSlotCount * IntPtr.Size);
        for (var slot = 0; slot < FuseNative.OperationsSlotCount; slot++)
        {
            Marshal.WriteIntPtr(_operations, slot * IntPtr.Size, IntPtr.Zero);
        }

        Set(FuseNative.OpGetattr, _getattr);
        Set(FuseNative.OpOpen, _open);
        Set(FuseNative.OpRead, _read);
        Set(FuseNative.OpRelease, _release);
        Set(FuseNative.OpReaddir, _readdir);
        Set(FuseNative.OpStatfs, _statfs);
        Set(FuseNative.OpAccess, _access);
    }

    /// <summary>Con trỏ tới struct fuse_operations đã điền.</summary>
    internal IntPtr Operations => _operations;

    internal static int OperationsSize => FuseNative.OperationsSlotCount * IntPtr.Size;

    private void Set(int slot, Delegate callback) =>
        Marshal.WriteIntPtr(_operations, slot * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(callback));

    private static string ReadPath(IntPtr path) => Marshal.PtrToStringUTF8(path) ?? string.Empty;

    private static int ToErrno(ExFatReadStatus status) => status switch
    {
        ExFatReadStatus.Success => 0,
        ExFatReadStatus.NotFound => -FuseNative.ENOENT,
        ExFatReadStatus.IsDirectory => -FuseNative.EISDIR,
        ExFatReadStatus.NotADirectory => -FuseNative.ENOTDIR,
        ExFatReadStatus.InvalidParameter => -FuseNative.EINVAL,
        _ => -FuseNative.EIO,
    };

    // ===================== Callback =====================

    private int Getattr(IntPtr path, IntPtr stat, IntPtr fileInfo)
    {
        try
        {
            var node = _model.LookupPath(ReadPath(path));
            if (node == null)
            {
                return -FuseNative.ENOENT;
            }

            FuseNative.WriteStat(stat, node.IsDirectory, node.Length, node.Modified ?? _model.FallbackTime, _uid, _gid);
            return 0;
        }
        catch (Exception)
        {
            return -FuseNative.EIO;
        }
    }

    private int Open(IntPtr path, IntPtr fileInfo)
    {
        try
        {
            // Ổ chỉ đọc: từ chối mọi cờ mở có ghi.
            var flags = Marshal.ReadInt32(fileInfo);
            if ((flags & FuseNative.O_ACCMODE) != FuseNative.O_RDONLY)
            {
                return -FuseNative.EROFS;
            }

            var node = _model.LookupPath(ReadPath(path));
            if (node == null)
            {
                return -FuseNative.ENOENT;
            }

            if (node.IsDirectory)
            {
                return -FuseNative.EISDIR;
            }

            var id = unchecked((ulong)Interlocked.Increment(ref _nextHandle));
            _handles[id] = _model.OpenHandle(node);
            Marshal.WriteInt64(fileInfo, FuseNative.FileInfoFhOffset, unchecked((long)id));
            return 0;
        }
        catch (Exception)
        {
            return -FuseNative.EIO;
        }
    }

    private int Read(IntPtr path, IntPtr buffer, UIntPtr size, long offset, IntPtr fileInfo)
    {
        try
        {
            var count = (int)Math.Min((ulong)size, int.MaxValue);
            if (count <= 0)
            {
                return 0;
            }

            ExFatFileHandle? handle = null;
            var owned = false;
            if (fileInfo != IntPtr.Zero)
            {
                var id = unchecked((ulong)Marshal.ReadInt64(fileInfo, FuseNative.FileInfoFhOffset));
                _handles.TryGetValue(id, out handle);
            }

            if (handle == null)
            {
                // libfuse có thể đọc mà không qua open (ví dụ khi kernel đọc trang); mở tạm.
                var node = _model.LookupPath(ReadPath(path));
                if (node == null)
                {
                    return -FuseNative.ENOENT;
                }

                if (node.IsDirectory)
                {
                    return -FuseNative.EISDIR;
                }

                handle = _model.OpenHandle(node);
                owned = true;
            }

            try
            {
                var managed = new byte[count];
                var status = _model.Read(handle, managed, 0, count, offset, out var bytesRead);
                if (status != ExFatReadStatus.Success)
                {
                    return ToErrno(status);
                }

                if (bytesRead > 0)
                {
                    Marshal.Copy(managed, 0, buffer, bytesRead);
                }

                return bytesRead;
            }
            finally
            {
                if (owned)
                {
                    handle.Dispose();
                }
            }
        }
        catch (Exception)
        {
            return -FuseNative.EIO;
        }
    }

    private int Release(IntPtr path, IntPtr fileInfo)
    {
        try
        {
            if (fileInfo != IntPtr.Zero)
            {
                var id = unchecked((ulong)Marshal.ReadInt64(fileInfo, FuseNative.FileInfoFhOffset));
                if (_handles.TryRemove(id, out var handle))
                {
                    handle.Dispose();
                }
            }

            return 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private int Readdir(IntPtr path, IntPtr buffer, IntPtr filler, long offset, IntPtr fileInfo, int flags)
    {
        try
        {
            var node = _model.LookupPath(ReadPath(path));
            if (node == null)
            {
                return -FuseNative.ENOENT;
            }

            if (!node.IsDirectory)
            {
                return -FuseNative.ENOTDIR;
            }

            var fill = Marshal.GetDelegateForFunctionPointer<FuseNative.FillDirDelegate>(filler);
            var stat = Marshal.AllocHGlobal(FuseNative.StatSize);
            try
            {
                foreach (var name in new[] { ".", ".." })
                {
                    var namePtr = Marshal.StringToCoTaskMemUTF8(name);
                    try
                    {
                        FuseNative.WriteStat(stat, true, 0, node.Modified ?? _model.FallbackTime, _uid, _gid);
                        if (fill(buffer, namePtr, stat, 0, 0) != 0)
                        {
                            return 0;
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(namePtr);
                    }
                }

                foreach (var child in _model.ListChildren(node))
                {
                    var namePtr = Marshal.StringToCoTaskMemUTF8(child.Name);
                    try
                    {
                        FuseNative.WriteStat(stat, child.IsDirectory, child.Length, child.Modified ?? _model.FallbackTime, _uid, _gid);
                        if (fill(buffer, namePtr, stat, 0, 0) != 0)
                        {
                            // Bộ đệm đầy: kernel sẽ gọi lại.
                            return 0;
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(namePtr);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(stat);
            }

            return 0;
        }
        catch (Exception)
        {
            return -FuseNative.EIO;
        }
    }

    private int Statfs(IntPtr path, IntPtr statvfs)
    {
        try
        {
            FuseNative.WriteStatvfs(statvfs, _model.VolumeLengthBytes);
            return 0;
        }
        catch (Exception)
        {
            return -FuseNative.EIO;
        }
    }

    private int Access(IntPtr path, int mask)
    {
        try
        {
            const int wOk = 2;
            if ((mask & wOk) != 0)
            {
                return -FuseNative.EACCES;
            }

            return _model.LookupPath(ReadPath(path)) == null ? -FuseNative.ENOENT : 0;
        }
        catch (Exception)
        {
            return -FuseNative.EIO;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var handle in _handles.Values)
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception)
            {
            }
        }

        _handles.Clear();

        if (_operations != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_operations);
            _operations = IntPtr.Zero;
        }
    }
}
