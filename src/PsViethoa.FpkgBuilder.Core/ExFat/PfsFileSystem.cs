using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using LibProsperoPkg.PFS;
using LibProsperoPkg.Util;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>
/// Ảnh PFS PS5 mà bên trong là thẳng các tệp của game (không phải một ảnh exFAT), đọc qua LibProsperoPkg:
/// <list type="bullet">
/// <item><c>.ffpfsc</c> chứa <c>pfs_image.dat</c> (PFS thô lồng trong PFS nén) — bố cục ShadowMountPlus khuyên dùng, do MkPFS <c>--raw</c>,
/// ps5-ffpfs-cli, ps5-exfat-builder… tạo ra;</item>
/// <item><c>.ffpfs</c> / <c>pfs_image.dat</c>: ảnh PFS thô chứa thẳng thư mục game.</item>
/// </list>
/// Lớp này giả làm <see cref="IImageReader"/> để <see cref="ExFatImage"/> dùng chung toàn bộ đường xử lý ảnh (liệt kê, đọc tệp,
/// giải nén ra thư mục tạm, ổ ảo Dokan). Mỗi mục được đánh số; số đó nằm trong <see cref="ExFatEntry.FirstCluster"/>.
/// </summary>
internal sealed class PfsFileSystem : IImageReader
{
    private const long PfsMagic = 0x1332A0B;
    private const uint FirstId = 2; // ExFatEntry.IsEmpty coi FirstCluster < 2 là rỗng

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly IMemoryReader? _innerSource;
    private readonly List<PfsReader.Node> _nodes = [];
    private readonly Dictionary<PfsReader.Node, uint> _ids = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    private PfsFileSystem(MemoryMappedFile file, MemoryMappedViewAccessor view, PfsReader reader, IMemoryReader? innerSource,
        string? entryName, long? storedLength, long length)
    {
        _file = file;
        _view = view;
        _innerSource = innerSource;
        EntryName = entryName;
        StoredLength = storedLength;
        Length = length;
        RootDir = reader.GetURoot();
        Register(RootDir);
    }

    /// <summary>Tên ảnh PFS lồng bên trong (ví dụ "pfs_image.dat"); null khi ảnh PFS thô nằm thẳng ở tệp nguồn.</summary>
    public string? EntryName { get; }

    /// <summary>Kích thước lưu trên đĩa (đã nén) của ảnh lồng; null khi không lồng.</summary>
    public long? StoredLength { get; }

    /// <summary>Kích thước logic của ảnh PFS chứa game.</summary>
    public long Length { get; }

    public PfsReader.Dir RootDir { get; }

    public uint RootId => _ids[RootDir];

    /// <summary>
    /// Mở <paramref name="path"/> nếu là ảnh PFS chứa thẳng tệp game; trả về null khi đó là container chứa ảnh exFAT (đường cũ của
    /// <see cref="PfsContainer"/>) hoặc không phải ảnh PFS.
    /// </summary>
    public static PfsFileSystem? TryOpen(string path)
    {
        var full = Path.GetFullPath(path);
        if (!PfsContainer.TryLocate(full, out var superblock))
        {
            return null;
        }

        var file = MemoryMappedFile.CreateFromFile(full, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? view = null;
        IMemoryReader? inner = null;
        try
        {
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var outer = new PfsReader(view, superblock, true, 0, null, null, null);
            var files = new List<(string Name, PfsReader.File File)>();
            Collect(outer.GetURoot(), string.Empty, files);
            if (files.Count == 0 || files.Any(f => f.Name.EndsWith(ExFatImage.Extension, StringComparison.OrdinalIgnoreCase)))
            {
                Release(view, file, null);
                return null;
            }

            if (files.Count == 1)
            {
                var entry = files[0].File;
                var compressed = (entry.flags & InodeFlags.compressed) != 0;
                IMemoryReader reader = entry.GetView();
                if (compressed)
                {
                    reader = new PFSCReader(reader);
                }

                var locked = new LockedMemoryReader(reader);
                var head = new byte[16];
                var logical = LogicalLength(entry);
                if (logical >= 16)
                {
                    locked.Read(0, head, 0, 16);
                }

                if (logical >= 16 && BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8)) == PfsMagic)
                {
                    inner = locked;
                    var nested = new PfsReader(locked, 0, null, null, null, 0, true);
                    return new PfsFileSystem(file, view, nested, inner, files[0].Name, entry.size, logical);
                }

                locked.Dispose();
                // Một tệp nhưng không phải PFS (thường là exFAT không mang đuôi .exfat): để PfsContainer xử lý như cũ.
                Release(view, file, null);
                return null;
            }

            // Nhiều tệp ngay trong ảnh: chính ảnh này là thư mục game (.ffpfs, pfs_image.dat).
            return new PfsFileSystem(file, view, outer, null, null, null, new FileInfo(full).Length);
        }
        catch
        {
            Release(view, file, inner);
            throw;
        }
    }

    private static void Release(MemoryMappedViewAccessor? view, MemoryMappedFile file, IMemoryReader? inner)
    {
        inner?.Dispose();
        view?.Dispose();
        file.Dispose();
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
            if (node is PfsReader.Dir child)
            {
                Collect(child, path, files);
            }
            else if (node is PfsReader.File f)
            {
                files.Add((path, f));
            }
        }
    }

    /// <summary>
    /// Kích thước logic của tệp. Với tệp nén PFSC, LibProsperoPkg để kích thước sau giải nén ở <c>compressed_size</c>
    /// (giống <see cref="PfsContainer"/>).
    /// </summary>
    private static long LogicalLength(PfsReader.File file) =>
        (file.flags & InodeFlags.compressed) != 0 ? file.compressed_size : file.size;

    private uint Register(PfsReader.Node node)
    {
        if (_ids.TryGetValue(node, out var id))
        {
            return id;
        }

        id = FirstId + (uint)_nodes.Count;
        _nodes.Add(node);
        _ids[node] = id;
        return id;
    }

    private PfsReader.Node Node(ExFatEntry entry)
    {
        var index = (long)entry.FirstCluster - FirstId;
        if (index < 0 || index >= _nodes.Count)
        {
            throw new ArgumentException("Unknown PFS entry", nameof(entry));
        }

        return _nodes[(int)index];
    }

    public IEnumerable<ExFatEntry> Enumerate(ExFatEntry directory)
    {
        if (Node(directory) is not PfsReader.Dir dir)
        {
            throw new ArgumentException("Not a directory", nameof(directory));
        }

        foreach (var child in dir.children)
        {
            var name = child.name ?? string.Empty;
            if (name is "." or ".." or "")
            {
                continue;
            }

            var id = Register(child);
            var isDirectory = child is PfsReader.Dir;
            var length = child is PfsReader.File file ? LogicalLength(file) : 0;
            yield return new ExFatEntry(directory.Path + "/" + name, name, isDirectory, length, length, id, false,
                (ushort)(isDirectory ? 0x10 : 0x20), null);
        }
    }

    public Stream OpenRead(ExFatEntry entry)
    {
        if (Node(entry) is not PfsReader.File file)
        {
            throw new ArgumentException("Not a file", nameof(entry));
        }

        IMemoryReader reader = file.GetView();
        if ((file.flags & InodeFlags.compressed) != 0)
        {
            reader = new PFSCReader(reader);
        }

        return new PfsNodeStream(reader, LogicalLength(file));
    }

    /// <summary>Đọc thô ảnh nguồn — chỉ để thoả <see cref="IImageReader"/>; ExFatImage không đọc cluster khi ở chế độ PFS.</summary>
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || offset >= _view.Capacity || buffer.Length == 0)
        {
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, _view.Capacity - offset);
        var temp = new byte[count];
        _view.ReadArray(offset, temp, 0, count);
        temp.CopyTo(buffer);
        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release(_view, _file, _innerSource);
    }
}

/// <summary>Khoá quanh một IMemoryReader dùng chung (PFSCReader giữ bộ đệm giải nén riêng, không an toàn đa luồng).</summary>
internal sealed class LockedMemoryReader(IMemoryReader inner) : IMemoryReader
{
    private readonly object _gate = new();

    public void Read(long pos, byte[] buf, int offset, int count)
    {
        lock (_gate)
        {
            inner.Read(pos, buf, offset, count);
        }
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>Stream chỉ-đọc, có seek, lên một tệp trong ảnh PFS.</summary>
internal sealed class PfsNodeStream(IMemoryReader reader, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, length - _position);
        if (n <= 0)
        {
            return 0;
        }

        reader.Read(_position, buffer, offset, n);
        _position += n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = (int)Math.Min(buffer.Length, length - _position);
        if (n <= 0)
        {
            return 0;
        }

        var temp = System.Buffers.ArrayPool<byte>.Shared.Rent(n);
        try
        {
            reader.Read(_position, temp, 0, n);
            temp.AsSpan(0, n).CopyTo(buffer);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(temp);
        }

        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // Không Dispose reader: view của LibProsperoPkg có thể dùng chung bộ đọc nền của ảnh (đóng cùng PfsFileSystem).
}
