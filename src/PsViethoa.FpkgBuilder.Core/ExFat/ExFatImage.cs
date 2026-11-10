using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>Một mục (tệp hoặc thư mục) trong ảnh exFAT.</summary>
public sealed record ExFatEntry(
    string Path,
    string Name,
    bool IsDirectory,
    long Length,
    long ValidLength,
    uint FirstCluster,
    bool NoFatChain,
    ushort Attributes,
    DateTime? Modified)
{
    public bool IsEmpty => FirstCluster < 2 || (!IsDirectory && Length == 0);

    /// <summary>Đường dẫn tương đối so với một thư mục gốc trong ảnh (dấu "/" phân cách).</summary>
    public string RelativeTo(ExFatEntry root)
    {
        if (root.Path.Length == 0)
        {
            return Path.TrimStart('/');
        }

        return Path.StartsWith(root.Path + "/", StringComparison.Ordinal) ? Path[(root.Path.Length + 1)..] : Path.TrimStart('/');
    }
}

/// <summary>
/// Bộ đọc exFAT chỉ-đọc thuần .NET: nhận ảnh volume thuần, ảnh có MBR hoặc GPT.
/// Đọc theo vị trí (RandomAccess) nên an toàn khi dùng song song từ nhiều luồng.
/// </summary>
public sealed class ExFatImage : IDisposable
{
    public const string Extension = ".exfat";

    private const uint EndOfChain = 0xFFFFFFFF;
    private const uint BadCluster = 0xFFFFFFF7;
    private const int EntrySize = 32;
    private const long MaxDirectoryBytes = 512L * 1024 * 1024;
    private const long MaxFatBytes = 1024L * 1024 * 1024;

    private static ReadOnlySpan<byte> Signature => "EXFAT   "u8;

    private readonly IImageReader _reader;
    private readonly long _fileLength;
    private readonly int _bytesPerSectorShift;
    private readonly int _clusterShift;
    private readonly object _fatGate = new();
    private byte[]? _fat;
    private bool _disposed;

    private ExFatImage(string path, IImageReader handle, long fileLength, long volumeOffset, ReadOnlySpan<byte> boot)
    {
        Path = path;
        _reader = handle;
        _fileLength = fileLength;
        VolumeOffset = volumeOffset;

        _bytesPerSectorShift = boot[108];
        var sectorsPerClusterShift = boot[109];
        if (_bytesPerSectorShift is < 9 or > 12 || sectorsPerClusterShift > 25 - _bytesPerSectorShift)
        {
            throw new InvalidDataException(Loc.T("Val.ExFatInvalid"));
        }

        _clusterShift = _bytesPerSectorShift + sectorsPerClusterShift;
        BytesPerSector = 1 << _bytesPerSectorShift;
        SectorsPerCluster = 1 << sectorsPerClusterShift;
        ClusterSize = 1 << _clusterShift;
        VolumeLengthBytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(72, 8)) << _bytesPerSectorShift;
        FatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(80, 4));
        FatLengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(84, 4));
        ClusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(88, 4));
        ClusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(92, 4));
        RootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(96, 4));
        SerialNumber = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(100, 4));
        NumberOfFats = boot[110];

        if (RootCluster < 2 || FatOffsetSectors == 0 || ClusterHeapOffsetSectors == 0 || ClusterCount == 0)
        {
            throw new InvalidDataException(Loc.T("Val.ExFatInvalid"));
        }

        Root = new ExFatEntry(string.Empty, string.Empty, true, 0, 0, RootCluster, false, 0x10, null);
        ReadVolumeLabel();
    }

    /// <summary>Chế độ PFS: cây thư mục lấy từ ảnh PFS, không có cấu trúc exFAT.</summary>
    private ExFatImage(string path, PfsFileSystem pfs)
    {
        Path = path;
        _reader = pfs;
        _fileLength = pfs.Length;
        VolumeLengthBytes = pfs.Length;
        Root = new ExFatEntry(string.Empty, string.Empty, true, 0, 0, pfs.RootId, false, 0x10, null);
    }

    private PfsFileSystem? Pfs => _reader as PfsFileSystem;

    public string Path { get; }

    public long VolumeOffset { get; }

    public int BytesPerSector { get; }

    public int SectorsPerCluster { get; }

    public int ClusterSize { get; }

    public long VolumeLengthBytes { get; }

    public uint FatOffsetSectors { get; }

    public uint FatLengthSectors { get; }

    public uint ClusterHeapOffsetSectors { get; }

    public uint ClusterCount { get; }

    public uint RootCluster { get; }

    public uint SerialNumber { get; }

    public byte NumberOfFats { get; }

    public string? VolumeLabel { get; private set; }

    public ExFatEntry Root { get; }

    /// <summary>Kiểm tra nhanh tệp có phải ảnh exFAT (thuần, MBR hoặc GPT) hay không.</summary>
    public static bool IsExFatFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var handle = OpenReader(path);
            return TryLocateVolume(handle, handle.Length, out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static ExFatImage Open(string path)
    {
        // Ảnh PFS chứa thẳng tệp game (.ffpfsc có pfs_image.dat, .ffpfs, pfs_image.dat): đọc qua lớp PFS, không có exFAT.
        if (PfsContainer.IsContainer(path) && PfsFileSystem.TryOpen(path) is { } pfs)
        {
            return new ExFatImage(path, pfs);
        }

        var handle = OpenReader(path);
        try
        {
            var length = handle.Length;
            if (!TryLocateVolume(handle, length, out var offset))
            {
                throw new InvalidDataException(Loc.T("Val.ExFatInvalid"));
            }

            Span<byte> boot = stackalloc byte[512];
            if (ReadAt(handle, offset, boot) != 512)
            {
                throw new InvalidDataException(Loc.T("Val.ExFatInvalid"));
            }

            return new ExFatImage(path, handle, length, offset, boot);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Mở nguồn ảnh: tệp .exfat thuần, hoặc tệp .ffpfsc (ảnh PFS PS5 chứa một tệp exFAT nén PFSC) — khi đó bộ đọc exFAT
    /// được đặt lên lớp giải nén của thư viện, không cần giải nén ra tệp trung gian.
    /// </summary>
    private static IImageReader OpenReader(string path)
    {
        if (PfsContainer.IsContainer(path))
        {
            var container = PfsContainer.Open(path);
            try
            {
                return container.CreateEntryReader();
            }
            catch
            {
                container.Dispose();
                throw;
            }
        }

        return new FileImageReader(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess));
    }

    /// <summary>Ảnh này nằm trong một container PFS (.ffpfsc) chứ không phải tệp exFAT thuần.</summary>
    public bool IsPfsContainer => _reader is PfsEntryReader or PfsFileSystem;

    /// <summary>Ảnh PFS chứa thẳng tệp game (không có exFAT bên trong).</summary>
    public bool IsPfsFileSystem => _reader is PfsFileSystem;

    /// <summary>Tên tệp exFAT bên trong container PFS (null với ảnh .exfat thuần).</summary>
    public string? ContainerEntryName => (_reader as PfsEntryReader)?.EntryName ?? Pfs?.EntryName;

    /// <summary>Kích thước lưu trữ (đã nén) của tệp exFAT bên trong container PFS; null với ảnh thuần.</summary>
    public long? ContainerStoredLength => (_reader as PfsEntryReader)?.StoredLength ?? Pfs?.StoredLength;

    // ===================== Định vị volume =====================

    private static bool TryLocateVolume(IImageReader handle, long length, out long offset)
    {
        offset = 0;
        Span<byte> sector = stackalloc byte[512];
        if (ReadAt(handle, 0, sector) != 512)
        {
            return false;
        }

        if (sector.Slice(3, 8).SequenceEqual(Signature))
        {
            return true;
        }

        if (sector[510] == 0x55 && sector[511] == 0xAA)
        {
            foreach (var sectorSize in new[] { 512, 4096 })
            {
                for (var i = 0; i < 4; i++)
                {
                    var entry = sector.Slice(446 + 16 * i, 16);
                    var type = entry[4];
                    var lba = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
                    if (type == 0 || lba == 0 || lba == 0xFFFFFFFF)
                    {
                        continue;
                    }

                    if (type == 0xEE)
                    {
                        if (TryLocateGpt(handle, length, sectorSize, out offset))
                        {
                            return true;
                        }

                        continue;
                    }

                    var candidate = (long)lba * sectorSize;
                    if (HasSignature(handle, length, candidate))
                    {
                        offset = candidate;
                        return true;
                    }
                }
            }
        }

        // Một số công cụ dump bỏ bảng phân vùng nhưng giữ khoảng trống đầu đĩa.
        foreach (var candidate in new long[] { 63 * 512L, 2048 * 512L, 34 * 512L, 40 * 512L, 4096 * 512L, 1L << 20, 2L << 20, 16L << 20 })
        {
            if (HasSignature(handle, length, candidate))
            {
                offset = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryLocateGpt(IImageReader handle, long length, int sectorSize, out long offset)
    {
        offset = 0;
        Span<byte> header = stackalloc byte[512];
        if (ReadAt(handle, sectorSize, header) != 512 || !header[..8].SequenceEqual("EFI PART"u8))
        {
            return false;
        }

        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(72, 8));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(80, 4));
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(84, 4));
        if (entrySize < 128 || entrySize > 4096 || count == 0)
        {
            return false;
        }

        Span<byte> entry = stackalloc byte[128];
        for (uint i = 0; i < Math.Min(count, 128u); i++)
        {
            var entryOffset = (long)entriesLba * sectorSize + (long)i * entrySize;
            if (ReadAt(handle, entryOffset, entry) != 128)
            {
                break;
            }

            var allZero = true;
            foreach (var b in entry[..16])
            {
                if (b != 0)
                {
                    allZero = false;
                    break;
                }
            }

            if (allZero)
            {
                continue;
            }

            var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(32, 8));
            var candidate = (long)firstLba * sectorSize;
            if (HasSignature(handle, length, candidate))
            {
                offset = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool HasSignature(IImageReader handle, long length, long offset)
    {
        if (offset < 0 || offset + 512 > length)
        {
            return false;
        }

        Span<byte> signature = stackalloc byte[8];
        return ReadAt(handle, offset + 3, signature) == 8 && signature.SequenceEqual(Signature);
    }

    private static int ReadAt(IImageReader handle, long offset, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = handle.ReadAt(offset + total, buffer[total..]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    internal int ReadAt(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadAt(_reader, offset, buffer);
    }

    // ===================== FAT & cluster =====================

    internal long ClusterOffset(uint cluster) =>
        VolumeOffset + ((long)ClusterHeapOffsetSectors << _bytesPerSectorShift) + ((long)(cluster - 2) << _clusterShift);

    private bool IsValidCluster(uint cluster) => cluster >= 2 && cluster < ClusterCount + 2 && cluster != BadCluster && cluster != EndOfChain;

    private uint NextCluster(uint cluster)
    {
        var fat = EnsureFat();
        var index = (long)cluster * 4;
        if (index + 4 > fat.Length)
        {
            return EndOfChain;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan((int)index, 4));
    }

    private byte[] EnsureFat()
    {
        if (_fat != null)
        {
            return _fat;
        }

        lock (_fatGate)
        {
            if (_fat != null)
            {
                return _fat;
            }

            var bytes = Math.Min((long)FatLengthSectors << _bytesPerSectorShift, ((long)ClusterCount + 2) * 4);
            if (bytes <= 0 || bytes > MaxFatBytes)
            {
                throw new InvalidDataException(Loc.T("Val.ExFatInvalid"));
            }

            var fat = new byte[bytes];
            ReadAt(_reader, VolumeOffset + ((long)FatOffsetSectors << _bytesPerSectorShift), fat);
            _fat = fat;
            return fat;
        }
    }

    /// <summary>Danh sách các đoạn byte liên tục (offset trong tệp ảnh, độ dài) chứa dữ liệu của mục.</summary>
    internal List<(long Offset, long Length)> GetExtents(ExFatEntry entry)
    {
        var runs = new List<(long Offset, long Length)>();
        if (entry.IsEmpty)
        {
            return runs;
        }

        if (entry.NoFatChain)
        {
            var clusters = entry.IsDirectory && entry.Length == 0
                ? 1
                : (entry.Length + ClusterSize - 1) / ClusterSize;
            runs.Add((ClusterOffset(entry.FirstCluster), clusters * ClusterSize));
            return runs;
        }

        var current = entry.FirstCluster;
        var runFirst = current;
        long guard = 0;
        long accumulated = 0;
        var limit = entry.Length > 0 && !entry.IsDirectory ? entry.Length : long.MaxValue;

        while (true)
        {
            if (++guard > (long)ClusterCount + 2)
            {
                throw new InvalidDataException("exFAT: vòng lặp chuỗi cluster (ảnh hỏng).");
            }

            var next = NextCluster(current);
            accumulated += ClusterSize;
            var contiguous = next == current + 1 && accumulated < limit;
            if (!contiguous)
            {
                runs.Add((ClusterOffset(runFirst), ((long)(current - runFirst) + 1) * ClusterSize));
                if (!IsValidCluster(next) || accumulated >= limit)
                {
                    break;
                }

                runFirst = next;
            }

            current = next;
        }

        return runs;
    }

    // ===================== Thư mục =====================

    private byte[] ReadAllExtents(ExFatEntry directory)
    {
        var extents = GetExtents(directory);
        long total = 0;
        foreach (var extent in extents)
        {
            total += extent.Length;
        }

        if (total > MaxDirectoryBytes)
        {
            throw new InvalidDataException("exFAT: thư mục quá lớn.");
        }

        var data = new byte[total];
        var position = 0;
        foreach (var extent in extents)
        {
            var read = ReadAt(extent.Offset, data.AsSpan(position, (int)extent.Length));
            position += read;
            if (read < extent.Length)
            {
                break;
            }
        }

        return data;
    }

    private void ReadVolumeLabel()
    {
        try
        {
            var data = ReadAllExtents(Root);
            for (var pos = 0; pos + EntrySize <= data.Length; pos += EntrySize)
            {
                var type = data[pos];
                if (type == 0)
                {
                    break;
                }

                if (type == 0x83)
                {
                    var count = Math.Min((int)data[pos + 1], 11);
                    VolumeLabel = Encoding.Unicode.GetString(data, pos + 2, count * 2).TrimEnd('\0', ' ');
                    break;
                }
            }
        }
        catch (Exception)
        {
            VolumeLabel = null;
        }
    }

    /// <summary>Liệt kê các mục con trực tiếp của một thư mục.</summary>
    public IEnumerable<ExFatEntry> Enumerate(ExFatEntry directory)
    {
        if (!directory.IsDirectory)
        {
            throw new ArgumentException("Not a directory", nameof(directory));
        }

        return Pfs is { } pfs ? pfs.Enumerate(directory) : EnumerateExFat(directory);
    }

    private IEnumerable<ExFatEntry> EnumerateExFat(ExFatEntry directory)
    {

        var data = ReadAllExtents(directory);
        var pos = 0;
        var nameBuilder = new StringBuilder(255);
        while (pos + EntrySize <= data.Length)
        {
            var type = data[pos];
            if (type == 0)
            {
                yield break;
            }

            if ((type & 0x80) == 0 || type != 0x85)
            {
                pos += EntrySize;
                continue;
            }

            var secondaries = data[pos + 1];
            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4, 2));
            var modified = DecodeTimestamp(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12, 4)));
            var end = pos + EntrySize * (1 + secondaries);
            if (end > data.Length)
            {
                yield break;
            }

            var haveStream = false;
            byte flags = 0;
            var nameLength = 0;
            long validLength = 0;
            long dataLength = 0;
            uint firstCluster = 0;
            nameBuilder.Clear();

            for (var j = pos + EntrySize; j < end; j += EntrySize)
            {
                var secondaryType = data[j];
                if (secondaryType == 0xC0)
                {
                    haveStream = true;
                    flags = data[j + 1];
                    nameLength = data[j + 3];
                    validLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(j + 8, 8));
                    firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(j + 20, 4));
                    dataLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(j + 24, 8));
                }
                else if (secondaryType == 0xC1)
                {
                    nameBuilder.Append(Encoding.Unicode.GetString(data, j + 2, 30));
                }
            }

            pos = end;
            if (!haveStream)
            {
                continue;
            }

            var name = nameBuilder.Length >= nameLength ? nameBuilder.ToString(0, nameLength) : nameBuilder.ToString();
            name = name.TrimEnd('\0');
            if (name.Length == 0 || name == "." || name == "..")
            {
                continue;
            }

            var isDirectory = (attributes & 0x10) != 0;
            yield return new ExFatEntry(
                directory.Path + "/" + name,
                name,
                isDirectory,
                dataLength,
                validLength,
                firstCluster,
                (flags & 0x02) != 0,
                attributes,
                modified);
        }
    }

    /// <summary>Duyệt toàn bộ cây (DFS) bên dưới một thư mục; không trả về chính thư mục gốc.</summary>
    public IEnumerable<ExFatEntry> Walk(ExFatEntry root, Func<ExFatEntry, bool>? shouldRecurse = null)
    {
        var stack = new Stack<ExFatEntry>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            foreach (var child in Enumerate(directory))
            {
                yield return child;
                if (child.IsDirectory && (shouldRecurse == null || shouldRecurse(child)))
                {
                    stack.Push(child);
                }
            }
        }
    }

    /// <summary>Tìm mục theo đường dẫn tương đối (không phân biệt hoa thường).</summary>
    public ExFatEntry? Find(string relativePath, ExFatEntry? start = null)
    {
        var current = start ?? Root;
        foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!current.IsDirectory)
            {
                return null;
            }

            ExFatEntry? next = null;
            foreach (var child in Enumerate(current))
            {
                if (string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    // ===================== Dữ liệu tệp =====================

    public Stream OpenRead(ExFatEntry file)
    {
        if (file.IsDirectory)
        {
            throw new ArgumentException("Not a file", nameof(file));
        }

        return Pfs is { } pfs ? pfs.OpenRead(file) : new ExFatFileStream(this, file, GetExtents(file));
    }

    public byte[] ReadAllBytes(ExFatEntry file, long maxBytes = 64L * 1024 * 1024)
    {
        if (file.Length > maxBytes)
        {
            throw new InvalidDataException($"exFAT: tệp {file.Name} lớn hơn giới hạn {maxBytes} byte.");
        }

        using var stream = OpenRead(file);
        var buffer = new byte[file.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static DateTime? DecodeTimestamp(uint value)
    {
        if (value == 0)
        {
            return null;
        }

        try
        {
            var doubleSeconds = (int)(value & 0x1F);
            var minute = (int)((value >> 5) & 0x3F);
            var hour = (int)((value >> 11) & 0x1F);
            var day = (int)((value >> 16) & 0x1F);
            var month = (int)((value >> 21) & 0x0F);
            var year = 1980 + (int)((value >> 25) & 0x7F);
            return new DateTime(year, month, day, hour, minute, doubleSeconds * 2, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader.Dispose();
    }
}

/// <summary>Stream chỉ-đọc, có seek, ánh xạ vị trí sang các đoạn cluster của tệp trong ảnh.</summary>
internal sealed class ExFatFileStream : Stream
{
    private readonly ExFatImage _image;
    private readonly List<(long Offset, long Length)> _extents;
    private readonly long[] _starts;
    private readonly long _length;
    private readonly long _validLength;
    private long _position;

    public ExFatFileStream(ExFatImage image, ExFatEntry entry, List<(long Offset, long Length)> extents)
    {
        _image = image;
        _extents = extents;
        _length = entry.Length;
        _validLength = Math.Min(entry.ValidLength, entry.Length);
        _starts = new long[extents.Count];
        long cumulative = 0;
        for (var i = 0; i < extents.Count; i++)
        {
            _starts[i] = cumulative;
            cumulative += extents[i].Length;
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.Length == 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, _length - _position);
        var target = buffer[..toRead];

        if (_position >= _validLength)
        {
            target.Clear();
            _position += toRead;
            return toRead;
        }

        if (_position + toRead > _validLength)
        {
            toRead = (int)(_validLength - _position);
            target = buffer[..toRead];
        }

        var index = FindExtent(_position);
        var extent = _extents[index];
        var within = _position - _starts[index];
        var available = (int)Math.Min(toRead, extent.Length - within);
        var read = _image.ReadAt(extent.Offset + within, target[..available]);
        if (read <= 0)
        {
            return 0;
        }

        _position += read;
        return read;
    }

    private int FindExtent(long position)
    {
        var low = 0;
        var high = _starts.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_starts[mid] <= position)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        Position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Nguồn byte đọc theo vị trí (an toàn đa luồng) cho bộ đọc exFAT: tệp thật hoặc tệp bên trong container PFS.</summary>
public interface IImageReader : IDisposable
{
    long Length { get; }

    /// <summary>Đọc tối đa buffer.Length byte tại offset; trả về số byte đọc được (0 khi hết).</summary>
    int ReadAt(long offset, Span<byte> buffer);
}

/// <summary>Tệp trên đĩa, đọc bằng RandomAccess (không thay đổi vị trí chung, dùng song song được).</summary>
internal sealed class FileImageReader : IImageReader
{
    private readonly SafeFileHandle _handle;

    public FileImageReader(SafeFileHandle handle)
    {
        _handle = handle;
        Length = RandomAccess.GetLength(handle);
    }

    public long Length { get; }

    public int ReadAt(long offset, Span<byte> buffer) => RandomAccess.Read(_handle, buffer, offset);

    public void Dispose() => _handle.Dispose();
}
