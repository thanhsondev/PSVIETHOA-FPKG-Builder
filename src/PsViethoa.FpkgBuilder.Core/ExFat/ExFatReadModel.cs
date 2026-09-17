using System.Collections.Concurrent;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>Kết quả đọc, không phụ thuộc nền tảng (Dokan ánh xạ sang NtStatus, FUSE sang -errno).</summary>
public enum ExFatReadStatus
{
    Success,
    NotFound,
    IsDirectory,
    NotADirectory,
    InvalidParameter,
    Failure,
}

/// <summary>Một mục trong cây ảo: tệp/thư mục trong ảnh, thư mục bọc, hoặc tệp đè trong bộ nhớ.</summary>
public sealed class ExFatNode
{
    public required string Name { get; init; }

    public required string VirtualPath { get; init; }

    public required bool IsDirectory { get; init; }

    public long Length { get; init; }

    public DateTime? Modified { get; init; }

    /// <summary>Mục trong ảnh (null với thư mục bọc hoặc tệp đè chưa có trong ảnh).</summary>
    public ExFatEntry? Entry { get; init; }

    /// <summary>Nội dung đè (null = đọc từ ảnh).</summary>
    public byte[]? Overlay { get; init; }
}

/// <summary>Tệp đang mở: giữ luồng đọc lười để lần đọc kế tiếp không phải mở lại.</summary>
public sealed class ExFatFileHandle : IDisposable
{
    internal ExFatFileHandle(ExFatNode node)
    {
        Node = node;
    }

    public ExFatNode Node { get; }

    internal object Gate { get; } = new();

    internal Stream? Stream { get; set; }

    public void Dispose()
    {
        lock (Gate)
        {
            Stream?.Dispose();
            Stream = null;
        }
    }
}

/// <summary>
/// Mô hình đọc chỉ-đọc của thư mục ứng dụng trong ảnh exFAT (.exfat hoặc .ffpfsc) — thuần .NET, không biết gì về
/// Dokan hay FUSE. Ẩn tệp rác hệ điều hành, "đè" được vài tệp bằng dữ liệu trong bộ nhớ (ép DRM trong
/// sce_sys/param.json) mà không đụng ảnh gốc, và bọc thư mục ứng dụng trong một thư mục con để thư mục nguồn đưa cho
/// thư viện không bao giờ là gốc ổ đĩa.
///
/// <para>Các lớp gắn ổ (<see cref="ExFatVirtualFileSystem"/> cho Dokan/Windows,
/// <see cref="FuseVirtualFileSystem"/> cho FUSE/Linux) chỉ là lớp vỏ mỏng ánh xạ callback của từng nền tảng sang
/// các phương thức ở đây; mọi ngữ nghĩa đường dẫn, liệt kê và đọc đều nằm trong lớp này nên kiểm thử được ở mọi hệ
/// điều hành mà không cần driver.</para>
/// </summary>
public sealed class ExFatReadModel
{
    public const string FileSystemName = "exFAT";

    private const int MaxLabelLength = 32;

    private readonly ExFatImage _image;
    private readonly ExFatEntry _appRoot;
    private readonly string? _wrapper;
    private readonly bool _hideJunk;
    private readonly Dictionary<string, byte[]> _overlays;
    private readonly HashSet<string> _hidden;
    private readonly ConcurrentDictionary<string, Listing> _listings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ExFatNode?> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ExFatNode _root;

    /// <param name="image">Ảnh exFAT đã mở (không thuộc sở hữu của lớp này).</param>
    /// <param name="appRoot">Thư mục ứng dụng trong ảnh (chứa sce_sys/).</param>
    /// <param name="wrapperName">Tên thư mục bọc ở gốc; null/rỗng = phơi thẳng thư mục ứng dụng ở gốc.</param>
    /// <param name="hideJunk">Ẩn tệp/thư mục rác hệ điều hành (.DS_Store, ._*, Thumbs.db…).</param>
    /// <param name="overlays">Tệp đè: khoá là đường dẫn tương đối so với thư mục ứng dụng ("sce_sys/param.json").</param>
    /// <param name="volumeLabel">Nhãn ổ.</param>
    /// <param name="hiddenPaths">Đường dẫn (tương đối thư mục ứng dụng) bị ẩn hẳn, ví dụ tàn dư AMPR emu.</param>
    public ExFatReadModel(
        ExFatImage image,
        ExFatEntry appRoot,
        string? wrapperName,
        bool hideJunk,
        IReadOnlyDictionary<string, byte[]>? overlays,
        string? volumeLabel,
        IReadOnlyCollection<string>? hiddenPaths = null)
    {
        _image = image;
        _appRoot = appRoot;
        _wrapper = string.IsNullOrWhiteSpace(wrapperName) ? null : wrapperName.Trim().Trim('/', '\\');
        _hideJunk = hideJunk;
        _overlays = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (overlays != null)
        {
            foreach (var (key, bytes) in overlays)
            {
                var normalized = NormalizePath(key);
                if (normalized.Length > 0)
                {
                    _overlays[normalized] = bytes;
                }
            }
        }

        _hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hiddenPaths != null)
        {
            foreach (var path in hiddenPaths)
            {
                var normalized = NormalizePath(path);
                if (normalized.Length > 0)
                {
                    _hidden.Add(normalized);
                }
            }
        }

        FallbackTime = ReadImageTime(image.Path);
        var label = string.IsNullOrWhiteSpace(volumeLabel) ? "EXFAT" : volumeLabel.Trim();
        VolumeLabel = label.Length > MaxLabelLength ? label[..MaxLabelLength] : label;
        _root = new ExFatNode
        {
            Name = string.Empty,
            VirtualPath = string.Empty,
            IsDirectory = true,
            Entry = _wrapper == null ? appRoot : null,
            Modified = appRoot.Modified,
        };
    }

    /// <summary>Tên thư mục bọc (null khi thư mục ứng dụng nằm ngay gốc).</summary>
    public string? WrapperName => _wrapper;

    /// <summary>Đường dẫn (trong ổ ảo) tới thư mục ứng dụng: tên thư mục bọc, hoặc rỗng.</summary>
    public string SourceRelativePath => _wrapper ?? string.Empty;

    public string VolumeLabel { get; }

    public long VolumeLengthBytes => Math.Max(_image.VolumeLengthBytes, 0);

    /// <summary>Thời điểm dùng cho mục không có mốc thời gian riêng (lấy theo tệp ảnh).</summary>
    public DateTime FallbackTime { get; }

    public ExFatNode Root => _root;

    // ===================== Đường dẫn =====================

    /// <summary>Chuẩn hoá đường dẫn ("\a\b" hoặc "a/b") thành "a/b"; gốc = chuỗi rỗng.</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var unified = path.Replace('\\', '/');
        var parts = unified.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : string.Join('/', parts);
    }

    internal static string ParentOf(string normalized)
    {
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    internal static string NameOf(string normalized)
    {
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    /// <summary>Đường dẫn tương đối so với thư mục ứng dụng (bỏ thư mục bọc).</summary>
    private string ToAppRelative(string virtualPath)
    {
        if (_wrapper == null)
        {
            return virtualPath;
        }

        if (virtualPath.Length == _wrapper.Length)
        {
            return string.Empty;
        }

        return virtualPath.Length > _wrapper.Length ? virtualPath[(_wrapper.Length + 1)..] : virtualPath;
    }

    // ===================== Cây thư mục =====================

    private sealed class Listing
    {
        public required Dictionary<string, ExFatNode> ByName { get; init; }

        public required List<ExFatNode> Ordered { get; init; }
    }

    /// <summary>Tra mục theo đường dẫn đã chuẩn hoá; null nếu không có.</summary>
    public ExFatNode? Lookup(string normalizedPath)
    {
        if (normalizedPath.Length == 0)
        {
            return _root;
        }

        return _nodes.GetOrAdd(normalizedPath, path =>
        {
            var parent = Lookup(ParentOf(path));
            if (parent == null || !parent.IsDirectory)
            {
                return null;
            }

            return GetListing(parent).ByName.TryGetValue(NameOf(path), out var node) ? node : null;
        });
    }

    /// <summary>Tra mục theo đường dẫn thô (tự chuẩn hoá).</summary>
    public ExFatNode? LookupPath(string path) => Lookup(NormalizePath(path));

    /// <summary>Danh sách con của một thư mục, theo thứ tự trong ảnh.</summary>
    public IReadOnlyList<ExFatNode> ListChildren(ExFatNode directory) => GetListing(directory).Ordered;

    private Listing GetListing(ExFatNode directory) => _listings.GetOrAdd(directory.VirtualPath, _ => BuildListing(directory));

    private Listing BuildListing(ExFatNode directory)
    {
        var byName = new Dictionary<string, ExFatNode>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ExFatNode>();

        if (directory.VirtualPath.Length == 0 && _wrapper != null)
        {
            var wrapper = new ExFatNode
            {
                Name = _wrapper,
                VirtualPath = _wrapper,
                IsDirectory = true,
                Entry = _appRoot,
                Modified = _appRoot.Modified ?? FallbackTime,
            };
            byName[wrapper.Name] = wrapper;
            ordered.Add(wrapper);
            return new Listing { ByName = byName, Ordered = ordered };
        }

        if (directory.Entry == null)
        {
            return new Listing { ByName = byName, Ordered = ordered };
        }

        var appRelative = ToAppRelative(directory.VirtualPath);
        foreach (var child in _image.Enumerate(directory.Entry))
        {
            if (_hideJunk && (child.IsDirectory ? JunkFileFinder.IsJunkDirectoryName(child.Name) : JunkFileFinder.IsJunkFileName(child.Name)))
            {
                continue;
            }

            if (_hidden.Count > 0 && _hidden.Contains(Join(appRelative, child.Name)))
            {
                continue;
            }

            var virtualPath = Join(directory.VirtualPath, child.Name);
            var overlay = !child.IsDirectory && _overlays.TryGetValue(Join(appRelative, child.Name), out var bytes) ? bytes : null;
            var node = new ExFatNode
            {
                Name = child.Name,
                VirtualPath = virtualPath,
                IsDirectory = child.IsDirectory,
                Length = child.IsDirectory ? 0 : overlay?.Length ?? child.Length,
                Modified = child.Modified ?? FallbackTime,
                Entry = child,
                Overlay = overlay,
            };

            // exFAT không phân biệt hoa/thường; nếu ảnh hỏng chứa hai tên chỉ khác hoa/thường thì giữ mục đầu.
            if (byName.TryAdd(node.Name, node))
            {
                ordered.Add(node);
            }
        }

        // Tệp đè chưa tồn tại trong thư mục này (ví dụ param.json thiếu) — thêm như tệp mới.
        foreach (var (key, bytes) in _overlays)
        {
            if (!string.Equals(ParentOf(key), appRelative, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = NameOf(key);
            if (byName.ContainsKey(name))
            {
                continue;
            }

            var node = new ExFatNode
            {
                Name = name,
                VirtualPath = Join(directory.VirtualPath, name),
                IsDirectory = false,
                Length = bytes.Length,
                Modified = FallbackTime,
                Overlay = bytes,
            };
            byName[name] = node;
            ordered.Add(node);
        }

        return new Listing { ByName = byName, Ordered = ordered };
    }

    private static DateTime ReadImageTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.UtcNow;
        }
    }

    // ===================== Đọc =====================

    /// <summary>Mở một tệp để đọc nhiều lần (luồng được mở lười ở lần đọc đầu).</summary>
    public ExFatFileHandle OpenHandle(ExFatNode node) => new(node);

    /// <summary>Đọc <paramref name="count"/> byte từ <paramref name="offset"/> vào <paramref name="buffer"/>.</summary>
    public ExFatReadStatus Read(ExFatFileHandle handle, byte[] buffer, int bufferOffset, int count, long offset, out int bytesRead)
    {
        bytesRead = 0;
        if (offset < 0 || bufferOffset < 0 || count < 0 || bufferOffset + count > buffer.Length)
        {
            return ExFatReadStatus.InvalidParameter;
        }

        var node = handle.Node;
        if (node.Overlay is { } bytes)
        {
            if (offset >= bytes.Length)
            {
                return ExFatReadStatus.Success;
            }

            bytesRead = (int)Math.Min(count, bytes.Length - offset);
            Buffer.BlockCopy(bytes, (int)offset, buffer, bufferOffset, bytesRead);
            return ExFatReadStatus.Success;
        }

        if (node.Entry == null || node.IsDirectory)
        {
            return ExFatReadStatus.IsDirectory;
        }

        if (offset >= node.Length)
        {
            return ExFatReadStatus.Success;
        }

        try
        {
            lock (handle.Gate)
            {
                handle.Stream ??= _image.OpenRead(node.Entry);
                handle.Stream.Position = offset;
                var total = 0;
                while (total < count)
                {
                    var read = handle.Stream.Read(buffer, bufferOffset + total, count - total);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                bytesRead = total;
            }

            return ExFatReadStatus.Success;
        }
        catch (Exception)
        {
            bytesRead = 0;
            return ExFatReadStatus.Failure;
        }
    }

    /// <summary>Đọc một lần vào toàn bộ <paramref name="buffer"/> (tiện cho Dokan).</summary>
    public ExFatReadStatus Read(ExFatFileHandle handle, byte[] buffer, long offset, out int bytesRead) =>
        Read(handle, buffer, 0, buffer.Length, offset, out bytesRead);
}
