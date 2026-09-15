using System.Security.AccessControl;
using DokanNet;
using DokanFileAccess = DokanNet.FileAccess;

namespace PsViethoa.FpkgBuilder.Core.ExFat;

/// <summary>
/// Lớp vỏ Dokan (Windows) phơi <see cref="ExFatReadModel"/> thành một ổ đĩa chỉ đọc: thư viện đọc thẳng từ ảnh, không
/// sao chép. Mọi ngữ nghĩa (đường dẫn, ẩn tệp rác, tệp đè, đọc) nằm trong mô hình đọc; lớp này chỉ ánh xạ callback
/// của Dokan sang mô hình và đổi <see cref="ExFatReadStatus"/> thành <see cref="NtStatus"/>. Lớp này thuần .NET nên
/// kiểm thử được ở mọi hệ điều hành; chỉ việc gắn ổ (<see cref="DokanImageMounter"/>) cần driver Dokan.
/// </summary>
public sealed class ExFatVirtualFileSystem : IDokanOperations
{
    public const string FileSystemName = ExFatReadModel.FileSystemName;

    private const uint MaxComponentLength = 255;

    /// <summary>STATUS_FILE_IS_A_DIRECTORY — không có tên trong enum NtStatus của DokanNet 2.3.</summary>
    private const NtStatus FileIsADirectoryStatus = (NtStatus)0xC00000BA;

    private const DokanFileAccess WriteAccessMask =
        DokanFileAccess.WriteData | DokanFileAccess.AppendData | DokanFileAccess.Delete | DokanFileAccess.DeleteChild |
        DokanFileAccess.WriteExtendedAttributes | DokanFileAccess.ChangePermissions | DokanFileAccess.SetOwnership |
        DokanFileAccess.GenericWrite | DokanFileAccess.GenericAll;

    private readonly ExFatReadModel _model;
    private readonly ManualResetEventSlim _mounted = new(false);

    /// <param name="image">Ảnh exFAT đã mở (không thuộc sở hữu của lớp này).</param>
    /// <param name="appRoot">Thư mục ứng dụng trong ảnh (chứa sce_sys/).</param>
    /// <param name="wrapperName">Tên thư mục bọc ở gốc ổ ảo; null/rỗng = phơi thẳng thư mục ứng dụng ở gốc.</param>
    /// <param name="hideJunk">Ẩn tệp/thư mục rác hệ điều hành (.DS_Store, ._*, Thumbs.db…).</param>
    /// <param name="overlays">Tệp đè: khoá là đường dẫn tương đối so với thư mục ứng dụng ("sce_sys/param.json").</param>
    /// <param name="volumeLabel">Nhãn ổ ảo.</param>
    /// <param name="hiddenPaths">Đường dẫn (tương đối thư mục ứng dụng) bị ẩn hẳn khỏi ổ ảo, ví dụ tàn dư AMPR emu.</param>
    public ExFatVirtualFileSystem(
        ExFatImage image,
        ExFatEntry appRoot,
        string? wrapperName,
        bool hideJunk,
        IReadOnlyDictionary<string, byte[]>? overlays,
        string? volumeLabel,
        IReadOnlyCollection<string>? hiddenPaths = null)
        : this(new ExFatReadModel(image, appRoot, wrapperName, hideJunk, overlays, volumeLabel, hiddenPaths))
    {
    }

    public ExFatVirtualFileSystem(ExFatReadModel model)
    {
        _model = model;
    }

    /// <summary>Mô hình đọc bên dưới (dùng chung với lớp vỏ FUSE).</summary>
    public ExFatReadModel Model => _model;

    /// <summary>Tên thư mục bọc (null khi thư mục ứng dụng nằm ngay gốc ổ ảo).</summary>
    public string? WrapperName => _model.WrapperName;

    /// <summary>Đường dẫn (trong ổ ảo) tới thư mục ứng dụng: tên thư mục bọc, hoặc rỗng.</summary>
    public string SourceRelativePath => _model.SourceRelativePath;

    public bool IsMounted => _mounted.IsSet;

    public bool WaitForMount(TimeSpan timeout, CancellationToken cancellationToken) => _mounted.Wait(timeout, cancellationToken);

    /// <summary>Chuẩn hoá đường dẫn Dokan ("\a\b" hoặc "a/b") thành "a/b"; gốc = chuỗi rỗng.</summary>
    public static string NormalizePath(string path) => ExFatReadModel.NormalizePath(path);

    private static NtStatus ToNtStatus(ExFatReadStatus status) => status switch
    {
        ExFatReadStatus.Success => NtStatus.Success,
        ExFatReadStatus.NotFound => NtStatus.ObjectNameNotFound,
        ExFatReadStatus.IsDirectory => FileIsADirectoryStatus,
        ExFatReadStatus.NotADirectory => NtStatus.NotADirectory,
        ExFatReadStatus.InvalidParameter => NtStatus.InvalidParameter,
        _ => NtStatus.Unsuccessful,
    };

    private FileInformation ToFileInformation(ExFatNode node)
    {
        var time = node.Modified ?? _model.FallbackTime;
        return new FileInformation
        {
            FileName = node.Name,
            Attributes = node.IsDirectory ? FileAttributes.Directory : FileAttributes.ReadOnly,
            Length = node.Length,
            CreationTime = time,
            LastAccessTime = time,
            LastWriteTime = time,
        };
    }

    // ===================== IDokanOperations =====================

    public NtStatus CreateFile(
        string fileName,
        DokanFileAccess access,
        FileShare share,
        FileMode mode,
        FileOptions options,
        FileAttributes attributes,
        IDokanFileInfo info)
    {
        var node = _model.LookupPath(fileName);
        if (node == null)
        {
            // Ổ chỉ đọc: không tạo mới được; mở tệp không tồn tại thì báo thiếu.
            return mode is FileMode.Open or FileMode.Truncate ? NtStatus.ObjectNameNotFound : NtStatus.AccessDenied;
        }

        if (node.IsDirectory)
        {
            if (mode == FileMode.CreateNew)
            {
                return NtStatus.ObjectNameCollision;
            }

            if ((access & (DokanFileAccess.Delete | DokanFileAccess.DeleteChild)) != 0)
            {
                return NtStatus.AccessDenied;
            }

            info.IsDirectory = true;
            return NtStatus.Success;
        }

        if (info.IsDirectory)
        {
            return NtStatus.NotADirectory;
        }

        if (mode == FileMode.CreateNew)
        {
            return NtStatus.ObjectNameCollision;
        }

        if (mode is FileMode.Create or FileMode.Truncate or FileMode.Append || (access & WriteAccessMask) != 0)
        {
            return NtStatus.AccessDenied;
        }

        info.Context = _model.OpenHandle(node);
        return NtStatus.Success;
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        if (info.Context is ExFatFileHandle handle)
        {
            handle.Dispose();
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        if (info.Context is ExFatFileHandle handle)
        {
            handle.Dispose();
        }

        info.Context = null;
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        if (info.Context is ExFatFileHandle handle)
        {
            return ToNtStatus(_model.Read(handle, buffer, offset, out bytesRead));
        }

        bytesRead = 0;
        var node = _model.LookupPath(fileName);
        if (node == null)
        {
            return NtStatus.ObjectNameNotFound;
        }

        if (node.IsDirectory)
        {
            return FileIsADirectoryStatus;
        }

        using var temporary = _model.OpenHandle(node);
        return ToNtStatus(_model.Read(temporary, buffer, offset, out bytesRead));
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        return NtStatus.AccessDenied;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        var node = (info.Context as ExFatFileHandle)?.Node ?? _model.LookupPath(fileName);
        if (node == null)
        {
            fileInfo = default;
            return NtStatus.ObjectNameNotFound;
        }

        fileInfo = ToFileInformation(node);
        return NtStatus.Success;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info) =>
        FindFilesWithPattern(fileName, "*", out files, info);

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        var node = _model.LookupPath(fileName);
        if (node == null)
        {
            return NtStatus.ObjectPathNotFound;
        }

        if (!node.IsDirectory)
        {
            return NtStatus.NotADirectory;
        }

        var all = string.IsNullOrEmpty(searchPattern) || searchPattern == "*";
        foreach (var child in _model.ListChildren(node))
        {
            if (all || DokanHelper.DokanIsNameInExpression(searchPattern, child.Name, true))
            {
                files.Add(ToFileInformation(child));
            }
        }

        return NtStatus.Success;
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info) =>
        NtStatus.Success;

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info) => NtStatus.AccessDenied;

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info) => NtStatus.AccessDenied;

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info) => NtStatus.AccessDenied;

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info) => NtStatus.AccessDenied;

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info) => NtStatus.AccessDenied;

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        freeBytesAvailable = 0;
        totalNumberOfBytes = _model.VolumeLengthBytes;
        totalNumberOfFreeBytes = 0;
        return NtStatus.Success;
    }

    public NtStatus GetVolumeInformation(
        out string volumeLabel,
        out FileSystemFeatures features,
        out string fileSystemName,
        out uint maximumComponentLength,
        IDokanFileInfo info)
    {
        volumeLabel = _model.VolumeLabel;
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk | FileSystemFeatures.ReadOnlyVolume;
        fileSystemName = FileSystemName;
        maximumComponentLength = MaxComponentLength;
        return NtStatus.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        return NtStatus.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info) =>
        NtStatus.AccessDenied;

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        _mounted.Set();
        return NtStatus.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info) => NtStatus.Success;

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = new List<FileInformation>();
        return NtStatus.NotImplemented;
    }
}
