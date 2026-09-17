using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Những gì thư mục gương phải khác với thư mục nguồn.</summary>
/// <param name="Skip">Đường dẫn tương đối (phân cách '/') không được vào gói.</param>
/// <param name="Replace">Đường dẫn tương đối → nội dung thay thế (ví dụ sce_sys/param.json đã sửa).</param>
/// <param name="Add">Đường dẫn tương đối → tệp nguồn trên đĩa để thêm vào gói.</param>
/// <param name="Copy">
/// Thư mục phải dựng lại bằng bản sao THẬT thay vì liên kết. Dùng cho thư mục mà thư viện có thể tự ghi vào (sce_sys): liên kết
/// tượng trưng sẽ dẫn thẳng vào nguồn, còn liên kết cứng dùng chung inode nên ghi đè cũng sửa luôn tệp gốc.
/// </param>
public sealed record MirrorPlan(
    IReadOnlyCollection<string> Skip,
    IReadOnlyDictionary<string, byte[]> Replace,
    IReadOnlyDictionary<string, string> Add,
    IReadOnlyCollection<string> Copy)
{
    public static readonly MirrorPlan Empty = new(Array.Empty<string>(), new Dictionary<string, byte[]>(), new Dictionary<string, string>(), Array.Empty<string>());

    public bool Any => Skip.Count > 0 || Replace.Count > 0 || Add.Count > 0 || Copy.Count > 0;
}

/// <summary>
/// Thư mục "gương" của bản dump, dựng trong thư mục tạm, để thư viện đọc thay cho thư mục nguồn.
/// <para>
/// Quy tắc bất di bất dịch của công cụ: KHÔNG bao giờ chạm, xoá hay sửa bất cứ thứ gì trong thư mục nguồn của người dùng.
/// Nhưng thư viện luôn đọc <c>sce_sys/param.json</c> thẳng từ thư mục nó được trỏ tới, và không có tuỳ chọn nào bỏ tệp khỏi gói.
/// Nên thay vì sửa nguồn rồi khôi phục, công cụ dựng một thư mục gương: mỗi mục cấp một của nguồn là một liên kết trỏ về bản gốc,
/// còn mục nào cần bỏ hoặc cần sửa thì được dựng lại bằng bản sao trong thư mục tạm. Thư viện đóng gói từ gương; nguồn chỉ được đọc.
/// </para>
/// <para>
/// Chi phí: chỉ sao chép đúng những tệp cần sửa (thường là <c>sce_sys</c>, vài chục MB), phần dữ liệu game khổng lồ chỉ là liên kết.
/// Gương bị xoá khi Dispose; xoá gương không bao giờ đụng tới tệp thật vì chỉ liên kết bị gỡ.
/// </para>
/// </summary>
public sealed class SourceMirror : IDisposable
{
    private readonly string _root;
    private bool _removed;

    private SourceMirror(string root)
    {
        _root = root;
    }

    /// <summary>Thư mục mà thư viện sẽ đọc.</summary>
    public string Path => _root;

    /// <summary>
    /// Dựng gương cho <paramref name="sourceFolder"/> trong <paramref name="workFolder"/>. Trả về null khi kế hoạch rỗng
    /// (không cần bỏ hay sửa gì, thư viện đọc thẳng nguồn) hoặc khi không dựng được liên kết (người gọi tự quyết định).
    /// </summary>
    public static SourceMirror? Create(string sourceFolder, string workFolder, MirrorPlan plan, Action<LogEntry> log, bool force = false)
    {
        if (!plan.Any && !force)
        {
            return null;
        }

        var root = System.IO.Path.Combine(workFolder, "mirror-" + Guid.NewGuid().ToString("N")[..8]);
        var mirror = new SourceMirror(root);
        try
        {
            Directory.CreateDirectory(root);
            var skip = new HashSet<string>(plan.Skip.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            var replace = plan.Replace.ToDictionary(pair => Normalize(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var add = plan.Add.ToDictionary(pair => Normalize(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var copy = new HashSet<string>(plan.Copy.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            Build(sourceFolder, root, string.Empty, skip, replace, add, copy);

            // Mục cần thêm nằm ở thư mục nguồn không có: tạo thẳng trong gương.
            foreach (var (relative, file) in add)
            {
                var target = Full(root, relative);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    CopyBytes(file, target);
                }
            }

            log(new LogEntry(LogLevel.Info, Loc.F("Plan.Mirror", root)));
            if (RemoveAppleDoubleArtifacts(root, sourceFolder) is var artifacts and > 0)
            {
                log(new LogEntry(LogLevel.Info, Loc.F("Plan.AppleDoubleRemoved", artifacts, workFolder)));
            }

            return mirror;
        }
        catch (Exception ex)
        {
            // Bắt mọi loại: Process.Start ném Win32Exception, ổ mạng ném đủ thứ khác. Dựng gương hỏng thì phải dọn sạch và
            // báo về, để người gọi dừng hẳn thay vì lặng lẽ đóng gói nguồn thô.
            mirror.Dispose();
            log(new LogEntry(LogLevel.Warning, Loc.F("Plan.MirrorFailed", ex.GetType().Name + ": " + ex.Message + " @ " + (ex.StackTrace ?? string.Empty).Split('\n')[0].Trim())));
            return null;
        }
    }

    /// <summary>
    /// Dựng gương ở chỗ đầu tiên làm được trong <paramref name="workFolders"/>. Thư mục tạm người dùng chọn có thể nằm trên ổ không
    /// tạo được liên kết (Windows: exFAT/FAT32 của ổ cắm ngoài không có junction, ổ mạng không cho liên kết) — khi đó gương, vốn rất
    /// nhỏ, được dựng ở thư mục tạm của hệ thống; dữ liệu tạm lớn của thư viện vẫn nằm ở thư mục tạm người dùng chọn.
    /// </summary>
    public static SourceMirror? CreateInAny(string sourceFolder, IReadOnlyList<string> workFolders, MirrorPlan plan, Action<LogEntry> log, bool force = false)
    {
        for (var i = 0; i < workFolders.Count; i++)
        {
            if (i > 0)
            {
                log(new LogEntry(LogLevel.Warning, Loc.F("Plan.MirrorFallback", workFolders[i - 1], workFolders[i])));
            }

            if (Create(sourceFolder, workFolders[i], plan, log, force) is { } mirror)
            {
                return mirror;
            }
        }

        return null;
    }

    /// <summary>
    /// Áp kế hoạch thẳng lên một thư mục thuộc về công cụ (bản giải nén ảnh trong thư mục tạm, KHÔNG BAO GIỜ là thư mục nguồn của
    /// người dùng): xoá tệp bỏ, ghi tệp sửa, thêm tệp mới, bỏ thư mục chỉ còn rỗng vì dọn. Không cần liên kết nên chạy được trên mọi
    /// hệ thống tệp.
    /// </summary>
    public static void ApplyInPlace(string ownedFolder, MirrorPlan plan)
    {
        var root = System.IO.Path.GetFullPath(ownedFolder);
        var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in plan.Skip.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = Full(root, relative);
            if (File.Exists(target))
            {
                RobustDelete.File(target);
                emptied.Add(System.IO.Path.GetDirectoryName(target)!);
            }
            else if (Directory.Exists(target))
            {
                RobustDelete.Tree(target);
                emptied.Add(System.IO.Path.GetDirectoryName(target)!);
            }
        }

        foreach (var (relative, content) in plan.Replace)
        {
            var target = Full(root, Normalize(relative));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }

        foreach (var (relative, file) in plan.Add)
        {
            var target = Full(root, Normalize(relative));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            CopyBytes(file, target, overwrite: true);
        }

        // Như gương: thư mục chỉ rỗng vì dọn (fakelib chỉ chứa module giả lập) thì bỏ hẳn, lan dần lên nhưng không chạm gốc.
        foreach (var folder in emptied.OrderByDescending(path => path.Length))
        {
            var current = folder;
            while (current.Length > root.Length && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
            {
                Directory.Delete(current);
                current = System.IO.Path.GetDirectoryName(current)!;
            }
        }
    }

    /// <summary>
    /// macOS trên ổ exFAT/FAT: mỗi tệp tiến trình tạo ra mang thuộc tính com.apple.provenance, mà hai hệ thống tệp đó không có chỗ
    /// chứa nên hệ điều hành ghi ra tệp AppleDouble "._tên" bên cạnh — thư viện sẽ đóng gói luôn các tệp đó (ví dụ
    /// sce_sys/._param.json). Hàm này xoá các tệp AppleDouble (magic 0x00051607) có tệp chính đi kèm trong <paramref name="root"/>,
    /// KHÔNG đi vào thư mục liên kết, và khi có <paramref name="sourceRoot"/> thì không xoá tệp nào cũng có mặt trong nguồn
    /// (thứ đó là của người dùng). Xoá "._tên" không làm hệ điều hành tạo lại. Trả về số tệp đã xoá.
    /// </summary>
    public static int RemoveAppleDoubleArtifacts(string root, string? sourceRoot = null)
    {
        if (!OperatingSystem.IsMacOS() || !Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(folder).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var info = new FileInfo(entry);
                if (info.LinkTarget != null)
                {
                    // Liên kết trỏ về nguồn: không bao giờ đi vào hay xoá qua nó.
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                var name = info.Name;
                if (!name.StartsWith("._", StringComparison.Ordinal) || name.Length <= 2 ||
                    !System.IO.Path.Exists(System.IO.Path.Combine(folder, name[2..])) || !IsAppleDouble(entry))
                {
                    continue;
                }

                if (sourceRoot != null &&
                    File.Exists(System.IO.Path.Combine(sourceRoot, System.IO.Path.GetRelativePath(root, entry))))
                {
                    continue;
                }

                try
                {
                    if (RobustDelete.File(entry))
                    {
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return removed;
    }

    private static bool IsAppleDouble(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> magic = stackalloc byte[4];
            return stream.Length >= 26 && stream.Read(magic) == 4 && magic.SequenceEqual(new byte[] { 0x00, 0x05, 0x16, 0x07 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Dựng một cấp của gương. Thư mục nào bên trong có mục cần bỏ hoặc cần sửa thì phải dựng lại từng mục con; thư mục còn lại
    /// chỉ cần một liên kết duy nhất trỏ về bản gốc.
    /// </summary>
    private static void Build(
        string source,
        string target,
        string prefix,
        HashSet<string> skip,
        Dictionary<string, byte[]> replace,
        Dictionary<string, string> add,
        HashSet<string> copy)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            try
            {
            var name = System.IO.Path.GetFileName(entry);
            var relative = prefix.Length == 0 ? name : prefix + "/" + name;
            if (skip.Contains(relative))
            {
                continue;
            }

            var destination = System.IO.Path.Combine(target, name);
            if (Directory.Exists(entry))
            {
                if (copy.Contains(relative) || NeedsOwnCopy(relative, skip, replace, add, copy))
                {
                    Build(entry, destination, relative, skip, replace, add, copy);

                    // Thư mục chỉ còn rỗng sau khi dọn (ví dụ fakelib chỉ chứa module giả lập) thì bỏ hẳn khỏi gói:
                    // một thư mục fakelib rỗng vẫn có thể làm bộ nạp của PS5 rẽ nhánh.
                    if (Directory.Exists(destination) && !Directory.EnumerateFileSystemEntries(destination).Any())
                    {
                        Directory.Delete(destination);
                    }
                }
                else
                {
                    Link(entry, destination, directory: true);
                }

                continue;
            }

            if (replace.TryGetValue(relative, out var content))
            {
                File.WriteAllBytes(destination, content);
            }
            else if (add.TryGetValue(relative, out var file))
            {
                CopyBytes(file, destination);
            }
            else if (InCopiedFolder(relative, copy))
            {
                // Bản sao thật, không phải liên kết cứng: thư viện ghi đè trong gương cũng không đụng tới tệp nguồn.
                CopyBytes(entry, destination);
            }
            else
            {
                Link(entry, destination, directory: false);
            }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nêu rõ tệp nào hỏng: thông báo trần như "Attribute not found" không cho biết phải sửa ở đâu.
                throw new IOException(entry + ": " + ex.Message, ex);
            }
        }
    }

    /// <summary>Thư mục có mục con nào cần bỏ, cần sửa hay cần thêm thì không thể chỉ là một liên kết.</summary>
    private static bool NeedsOwnCopy(string relative, HashSet<string> skip, Dictionary<string, byte[]> replace, Dictionary<string, string> add, HashSet<string> copy)
    {
        var prefix = relative + "/";
        return skip.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               || replace.Keys.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               || add.Keys.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               || copy.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tệp nằm trong (hoặc dưới) một thư mục được yêu cầu sao chép thật.</summary>
    private static bool InCopiedFolder(string relative, HashSet<string> copy)
    {
        foreach (var folder in copy)
        {
            if (relative.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Đưa một mục của nguồn vào gương mà không nhân đôi dữ liệu.
    /// <para>
    /// Thư mục dùng liên kết tượng trưng; trên Windows thiếu quyền thì lùi về junction (junction không cần quyền quản trị).
    /// Tệp thì PHẢI dùng liên kết cứng, không được dùng liên kết tượng trưng: thư viện đọc kích thước ngay trên đường dẫn được
    /// trỏ tới và với liên kết tượng trưng sẽ lệch số byte ("Plaintext source ended with N bytes remaining").
    /// Liên kết cứng chỉ tạo được trong cùng một ổ đĩa, khác ổ thì sao chép.
    /// </para>
    /// </summary>
    private static void Link(string source, string destination, bool directory)
    {
        if (!directory)
        {
            if (!TryCreateHardLink(source, destination))
            {
                CopyBytes(source, destination);
            }

            return;
        }

        try
        {
            Directory.CreateSymbolicLink(destination, source);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw;
            }
        }

        // Junction tạo thẳng qua API (không phụ thuộc cmd.exe, không vướng ký tự đặc biệt); lỗi mới thử mklink.
        Exception? nativeError = null;
        try
        {
            CreateJunction(destination, source);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            nativeError = ex;
        }

        // cmd.exe khai triển %BIEN% kể cả trong dấu nháy kép, nên đường dẫn có '%' không thể đi qua mklink an toàn.
        if (destination.Contains('%') || source.Contains('%'))
        {
            throw new IOException("junction: " + nativeError.Message);
        }

        // Junction không cần quyền quản trị, khác với liên kết tượng trưng trên Windows.
        var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{destination}\" \"{source}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new IOException("mklink");
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(destination))
        {
            throw new IOException("junction: " + nativeError.Message + " · mklink /J: " + process.StandardError.ReadToEnd().Trim());
        }
    }

    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    /// <summary>
    /// Tạo junction NTFS (IO_REPARSE_TAG_MOUNT_POINT) bằng FSCTL_SET_REPARSE_POINT — cùng cơ chế với mklink /J, không cần quyền
    /// quản trị. Junction phải nằm trên NTFS; đích là thư mục cục bộ ở bất kỳ ổ nào.
    /// </summary>
    internal static void CreateJunction(string junction, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var fullTarget = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(target));
        if (fullTarget.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new IOException("junction target cannot be a network path: " + fullTarget);
        }

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + fullTarget);
        var print = Encoding.Unicode.GetBytes(fullTarget);
        var pathBufferLength = substitute.Length + 2 + print.Length + 2;
        var buffer = new byte[8 + 8 + pathBufferLength];
        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(8 + pathBufferLength)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        Directory.CreateDirectory(junction);
        try
        {
            using var handle = CreateFileW(junction, GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch
        {
            // Thư mục rỗng vừa tạo không phải liên kết: xoá đi để lần thử sau (mklink) bắt đầu sạch.
            try
            {
                Directory.Delete(junction);
            }
            catch (Exception)
            {
            }

            throw;
        }

        if (!Directory.Exists(System.IO.Path.Combine(junction, ".")) || new DirectoryInfo(junction).LinkTarget == null)
        {
            throw new IOException("junction was not created: " + junction);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// Chép nội dung tệp, KHÔNG chép thuộc tính mở rộng. File.Copy trên macOS gọi copyfile() kèm metadata và ném
    /// "Attribute not found" khi nguồn nằm trên ổ exFAT vừa gắn bằng hdiutil.
    /// </summary>
    private static void CopyBytes(string source, string destination, bool overwrite = false)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        input.CopyTo(output, 1 << 20);
    }

    private static bool TryCreateHardLink(string source, string destination)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateHardLinkW(destination, source, IntPtr.Zero)
                : link(source, destination) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    public void Dispose()
    {
        if (_removed)
        {
            return;
        }

        _removed = true;

        // Xoá gương chỉ gỡ liên kết: thư mục là liên kết tượng trưng nên bị gỡ chứ không đi xuyên vào trong, còn tệp là liên
        // kết cứng nên xoá một tên không làm mất dữ liệu ở tên còn lại trong thư mục nguồn.
        try
        {
            if (Directory.Exists(_root))
            {
                RemoveLinksThenDelete(_root);
            }
        }
        catch (Exception)
        {
            // Còn sót gương trong thư mục tạm là vô hại; lần tạo gói sau dùng thư mục khác.
        }
    }

    /// <summary>Gỡ từng liên kết trước rồi mới xoá thư mục, để không có đường nào đi xuyên liên kết tới tệp thật.</summary>
    private static void RemoveLinksThenDelete(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            // Thư mục (kể cả liên kết tượng trưng và junction) phải xoá bằng Directory.Delete: trên Windows File.Delete
            // không xoá được liên kết thư mục và sẽ ném lỗi, để lại cả cây gương nằm trong thư mục tạm.
            var directoryInfo = new DirectoryInfo(entry);
            if (directoryInfo.Exists)
            {
                if (directoryInfo.LinkTarget != null)
                {
                    // Xoá liên kết, không đi xuyên vào trong: dữ liệu thật của người dùng không bị đụng tới.
                    directoryInfo.Delete();
                }
                else
                {
                    RemoveLinksThenDelete(entry);
                }

                continue;
            }

            RobustDelete.File(entry);
        }

        Directory.Delete(folder);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static string Full(string root, string relative) =>
        System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
}
