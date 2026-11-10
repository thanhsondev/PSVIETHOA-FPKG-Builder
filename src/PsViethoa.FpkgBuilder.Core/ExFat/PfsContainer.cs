using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using LibProsperoPkg.PFS;
using LibProsperoPkg.Util;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>
/// Container .ffpfsc: một ảnh PFS PS5 thuần (superblock phiên bản 2, magic 0x1332A0B, khối 64 KiB) chứa đúng một tệp —
/// ảnh exFAT của game — thường được nén theo kiểu PFSC (từng khối). Thư viện LibProsperoPkg đọc và giải nén ngẫu nhiên
/// được nên bộ đọc exFAT của ứng dụng chỉ cần đặt lên trên lớp này.
/// </summary>
public sealed class PfsContainer : IDisposable
{
    public const string Extension = ".ffpfsc";

    /// <summary>Đuôi .ffpfs — ảnh PFS không nén chứa thẳng thư mục game.</summary>
    public const string RawExtension = ".ffpfs";

    private const long PfsVersion2 = 2;
    private const long PfsMagic = 0x1332A0B;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly PfsReader _pfs;
    private readonly PfsReader.File _entry;
    private bool _disposed;

    private PfsContainer(string path, MemoryMappedFile file, MemoryMappedViewAccessor view, PfsReader pfs, PfsReader.File entry, string entryName)
    {
        Path = path;
        _file = file;
        _view = view;
        _pfs = pfs;
        _entry = entry;
        EntryName = entryName;
        IsCompressed = (entry.flags & InodeFlags.compressed) != 0;
        StoredLength = entry.size;
        Length = IsCompressed ? entry.compressed_size : entry.size;
    }

    public string Path { get; }

    /// <summary>Tên tệp bên trong container (ví dụ "PPSA02849.com (01.003.000).exfat").</summary>
    public string EntryName { get; }

    public bool IsCompressed { get; }

    /// <summary>Kích thước lưu trên đĩa của tệp bên trong (đã nén nếu IsCompressed).</summary>
    public long StoredLength { get; }

    /// <summary>Kích thước logic (sau giải nén) của tệp bên trong.</summary>
    public long Length { get; }

    private const int BlockSize = 65536;
    private const int MaxBackwardProbes = 1_000_000; // ~64 GB vùng metadata — đủ cho mọi ảnh thực tế

    /// <summary>
    /// Có phải ảnh PFS PS5 (superblock v2, magic 0x1332A0B) không. Với mọi tệp chỉ xem 16 byte đầu (bố cục superblock-đầu
    /// như .ffpfsc); riêng tệp có đuôi .ffpfsc còn dò ngược từ cuối để nhận cả bố cục dữ liệu-trước (superblock nằm sau vùng dữ liệu).
    /// </summary>
    public static bool IsContainer(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var ext = System.IO.Path.GetExtension(path);
            return TryLocateSuperblock(handle, string.Equals(ext, Extension, StringComparison.OrdinalIgnoreCase) || string.Equals(ext, RawExtension, StringComparison.OrdinalIgnoreCase), out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSuperblockAt(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset)
    {
        Span<byte> head = stackalloc byte[16];
        return RandomAccess.Read(handle, head, offset) == 16
               && BinaryPrimitives.ReadInt64LittleEndian(head) == PfsVersion2
               && BinaryPrimitives.ReadInt64LittleEndian(head[8..]) == PfsMagic;
    }

    /// <summary>Tìm superblock: offset 0 trước; nếu cho phép thì dò ngược từng khối 64 KiB từ cuối tệp.</summary>
    private static bool TryLocateSuperblock(Microsoft.Win32.SafeHandles.SafeFileHandle handle, bool allowBackwardScan, out long offset)
    {
        offset = 0;
        var length = RandomAccess.GetLength(handle);
        if (length < 2L * BlockSize)
        {
            return false;
        }

        if (IsSuperblockAt(handle, 0))
        {
            return true;
        }

        if (!allowBackwardScan)
        {
            return false;
        }

        var probes = 0;
        for (var candidate = (length / BlockSize - 1) * BlockSize; candidate > 0 && probes < MaxBackwardProbes; candidate -= BlockSize, probes++)
        {
            if (IsSuperblockAt(handle, candidate))
            {
                offset = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Vị trí superblock PFS của tệp (dò ngược từ cuối với đuôi .ffpfsc / .ffpfs).</summary>
    internal static bool TryLocate(string path, out long offset)
    {
        offset = 0;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var ext = System.IO.Path.GetExtension(path);
            var scan = string.Equals(ext, Extension, StringComparison.OrdinalIgnoreCase) || string.Equals(ext, RawExtension, StringComparison.OrdinalIgnoreCase);
            return TryLocateSuperblock(handle, scan, out offset);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Mở container và chọn tệp exFAT bên trong (ưu tiên đuôi .exfat, không có thì lấy tệp lớn nhất).</summary>
    public static PfsContainer Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        long superblock;
        using (var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (!TryLocateSuperblock(handle, allowBackwardScan: true, out superblock))
            {
                throw new InvalidDataException(Loc.T("Val.ExFatInvalid"));
            }
        }

        var file = MemoryMappedFile.CreateFromFile(full, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var pfs = new PfsReader(view, superblock, true, 0, null, null, null);
            var files = new List<(string Name, PfsReader.File File)>();
            Collect(pfs.GetURoot(), string.Empty, files);
            if (files.Count == 0)
            {
                throw new InvalidDataException(Loc.T("Val.PfsContainerEmpty"));
            }

            var chosen = files.FirstOrDefault(f => f.Name.EndsWith(ExFatImage.Extension, StringComparison.OrdinalIgnoreCase));
            if (chosen.File == null)
            {
                chosen = files.OrderByDescending(f => (f.File.flags & InodeFlags.compressed) != 0 ? f.File.compressed_size : f.File.size).First();
            }

            return new PfsContainer(full, file, view, pfs, chosen.File, chosen.Name);
        }
        catch
        {
            view?.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>Bộ đọc theo vị trí lên tệp bên trong (giải nén PFSC trong suốt); sở hữu container — Dispose sẽ đóng container.</summary>
    public IImageReader CreateEntryReader()
    {
        IMemoryReader reader = _entry.GetView();
        if (IsCompressed)
        {
            reader = new PFSCReader(reader);
        }

        return new PfsEntryReader(this, reader, Length, EntryName, StoredLength);
    }

    private static void Collect(PfsReader.Dir directory, string prefix, List<(string Name, PfsReader.File File)> files)
    {
        foreach (var node in directory.children)
        {
            var name = node.name ?? string.Empty;
            if (name is "." or ".." or "")
            {
                continue;
            }

            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            switch (node)
            {
                case PfsReader.Dir child:
                    Collect(child, path, files);
                    break;
                case PfsReader.File file:
                    files.Add((path, file));
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>
/// IImageReader lên một tệp trong container PFS. PFSCReader giữ bộ đệm giải nén riêng nên các lần đọc được tuần tự hoá bằng khoá;
/// giải nén PFSC của thư viện đạt ~900 MB/s một luồng nên đủ nhanh cho việc giải nén exFAT ra thư mục tạm.
/// </summary>
internal sealed class PfsEntryReader : IImageReader
{
    private readonly PfsContainer _container;
    private readonly IMemoryReader _reader;
    private readonly object _gate = new();
    private bool _disposed;

    public PfsEntryReader(PfsContainer container, IMemoryReader reader, long length, string entryName, long storedLength)
    {
        _container = container;
        _reader = reader;
        Length = length;
        EntryName = entryName;
        StoredLength = storedLength;
    }

    public long Length { get; }

    public string EntryName { get; }

    public long StoredLength { get; }

    public int ReadAt(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || offset >= Length || buffer.Length == 0)
        {
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, Length - offset);
        var rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            lock (_gate)
            {
                _reader.Read(offset, rented, 0, count);
            }

            rented.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_reader as IDisposable)?.Dispose();
        _container.Dispose();
    }
}
