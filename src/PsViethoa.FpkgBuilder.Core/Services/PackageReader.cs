using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Mở ảnh PPR-PFS trong của một gói FIH debug để liệt kê và giải nén có chọn lọc.
/// <para>
/// PLAINTEXT_NOAUTH: đọc trực tiếp từ tệp .pkg — lớp PFS ngoài không mã hoá nên chỉ giải nén NAPS
/// theo từng khối 4 MiB (bộ đệm LRU 64 MiB), không bao giờ dựng lại toàn bộ ảnh trong.
/// Native (AES-XTS): giải mã lớp ngoài ra một tệp tạm (≈ dung lượng gói) rồi đọc theo khối y như trên.
/// </para>
/// </summary>
public sealed class PackageReader : IDisposable
{
    private const int OuterBlockSize = 65536;
    private const int ChunkSize = 4 << 20;
    private const int CacheChunks = 16;
    private const long SuperblockScanWindow = 512L << 20;
    private const string UrootPrefix = "/uroot/";

    private readonly string _packagePath;
    private readonly string? _decryptedOuterPath;
    private readonly NapsImageReader _image;
    private readonly PfsReader _pfs;
    private readonly Dictionary<string, PfsReader.File> _files;
    private bool _disposed;

    private PackageReader(string packagePath, string? decryptedOuterPath, PackageImageMode imageMode, NapsImageReader image, PfsReader pfs, long superblockOffset)
    {
        _packagePath = packagePath;
        _decryptedOuterPath = decryptedOuterPath;
        ImageMode = imageMode;
        _image = image;
        _pfs = pfs;
        SuperblockOffset = superblockOffset;
        LogicalImageSize = image.Size;
        _files = new Dictionary<string, PfsReader.File>(StringComparer.Ordinal);

        var entries = new List<PackageEntry>();
        Walk(pfs.GetURoot(), string.Empty, entries);
        Entries = entries;
        FileCount = entries.Count(e => !e.IsDirectory);
        TotalBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
    }

    public string PackagePath => _packagePath;

    public PackageImageMode ImageMode { get; }

    /// <summary>Kích thước ảnh trong logic (đã giải nén NAPS).</summary>
    public long LogicalImageSize { get; }

    public long SuperblockOffset { get; }

    /// <summary>Mọi tệp và thư mục trong ảnh trong (đường dẫn tương đối, dấu '/').</summary>
    public IReadOnlyList<PackageEntry> Entries { get; }

    public int FileCount { get; }

    public long TotalBytes { get; }

    /// <summary>
    /// Mở gói. Với gói Native, lớp ngoài được giải mã ra <paramref name="temporaryFolder"/>
    /// (hoặc thư mục tạm hệ thống) — bước này không thể huỷ giữa chừng nhưng tệp tạm luôn được xoá khi Dispose.
    /// </summary>
    public static PackageReader Open(string packagePath, string passcode, string? temporaryFolder, CancellationToken cancellationToken, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            throw new FileNotFoundException(Loc.T("Extract.FileMissing"), packagePath);
        }

        var type = ProsperoPkgReader.DetectType(packagePath);
        switch (type)
        {
            case ProsperoPkgType.FullDebug:
                break;
            case ProsperoPkgType.FullRetail:
                throw new NotSupportedException(Loc.T("Extract.RetailBlocked"));
            case ProsperoPkgType.Meta:
                throw new NotSupportedException(Loc.T("Extract.MetaBlocked"));
            default:
                throw new InvalidDataException(Loc.T("Extract.NotPkg"));
        }

        var map = ProsperoPackageArchive.Inspect(packagePath);
        byte[] fih;
        PackageVerifier.FihSummary summary;
        using (var stream = File.OpenRead(packagePath))
        {
            summary = PackageVerifier.ReadFihSummary(stream);
            fih = new byte[OuterBlockSize];
            stream.Position = 0;
            stream.ReadExactly(fih, 0, (int)Math.Min(fih.Length, stream.Length));
        }

        var imageMode = string.Equals(summary.Marker, PackageVerifier.PlaintextMarker, StringComparison.Ordinal)
            ? PackageImageMode.PlaintextNoAuth
            : PackageImageMode.Native;

        string? decrypted = null;
        Func<Stream> outerFactory;
        if (imageMode == PackageImageMode.PlaintextNoAuth)
        {
            var offset = map.OuterPfsOffset;
            var size = map.OuterPfsSize;
            // SubStream của thư viện là cửa sổ "không sở hữu" luồng gốc: phải tự đóng FileStream khi kênh giải nén bị huỷ,
            // nếu không mỗi kênh để lại một handle mở trên tệp .pkg (Windows sẽ không cho xoá/đổi tên tệp cho tới khi GC dọn).
            outerFactory = () => new OwnedWindowStream(OpenSequential(packagePath), offset, size);
        }
        else
        {
            ValidatePasscode(passcode);
            cancellationToken.ThrowIfCancellationRequested();
            decrypted = Path.Combine(ResolveTemporaryFolder(temporaryFolder), ".psviethoa-outer-" + Guid.NewGuid().ToString("N") + ".tmp");
            log?.Invoke(Loc.F("Extract.Decrypting", Formatters.Size(map.OuterPfsSize), decrypted));
            var watch = Stopwatch.StartNew();
            try
            {
                ProsperoPackageArchive.DecryptOuterPfs(packagePath, decrypted, passcode);
            }
            catch (Exception)
            {
                TryDelete(decrypted);
                throw;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                TryDelete(decrypted);
                cancellationToken.ThrowIfCancellationRequested();
            }

            log?.Invoke(Loc.F("Extract.Decrypted", Formatters.Duration(watch.Elapsed)));
            var path = decrypted;
            outerFactory = () => OpenSequential(path);
        }

        NapsImageReader? image = null;
        try
        {
            image = new NapsImageReader(outerFactory, map.OuterSuperblockIndex, Math.Max(1, Math.Min(Environment.ProcessorCount, 8)));
            cancellationToken.ThrowIfCancellationRequested();

            var superblock = LocateSuperblock(image, fih);
            var pfs = new PfsReader(image, 0, null, null, null, superblock, encryptedDataAlreadyDecrypted: true);
            return new PackageReader(packagePath, decrypted, imageMode, image, pfs, superblock);
        }
        catch (Exception)
        {
            image?.Dispose();
            if (decrypted != null)
            {
                TryDelete(decrypted);
            }

            throw;
        }
    }

    /// <summary>Đọc toàn bộ một tệp vào bộ nhớ (dùng cho xem trước/kiểm thử; không dùng với tệp lớn).</summary>
    public byte[] ReadAllBytes(PackageEntry entry)
    {
        ThrowIfDisposed();
        if (!_files.TryGetValue(entry.Path, out var file))
        {
            throw new FileNotFoundException(entry.Path);
        }

        return file.ReadAllBytes(decompress: true);
    }

    /// <summary>Đọc một đoạn của tệp (xem trước): chỉ các khối chứa đoạn đó được giải nén; trả về số byte đọc được.</summary>
    public int ReadRange(PackageEntry entry, long offset, byte[] buffer, int count)
    {
        ThrowIfDisposed();
        if (!_files.TryGetValue(entry.Path, out var file))
        {
            throw new FileNotFoundException(entry.Path);
        }

        var compressed = (file.flags & InodeFlags.compressed) != 0;
        var length = compressed ? file.compressed_size : file.size;
        if (offset < 0 || offset >= length)
        {
            return 0;
        }

        count = (int)Math.Min(count, length - offset);
        IMemoryReader view = file.GetView();
        if (compressed)
        {
            view = new PFSCReader(view);
        }

        view.Read(offset, buffer, 0, count);
        return count;
    }

    /// <summary>
    /// Giải nén các mục đã chọn (thư mục được tạo, tệp con của thư mục KHÔNG tự động thêm — giao diện tự chọn).
    /// <paramref name="decompress"/> = false giữ nguyên tệp nén PFSC (hiếm gặp trên PS5) ở dạng đã nén.
    /// </summary>
    public ExtractResult Extract(
        IEnumerable<PackageEntry> entries,
        string outputFolder,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        int parallelism = 0,
        bool decompress = true)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            throw new ArgumentException(Loc.T("Extract.NeedOutput"), nameof(outputFolder));
        }

        var root = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(root);
        var selected = entries.Distinct().ToList();
        var workers = parallelism <= 0 ? Math.Clamp(Environment.ProcessorCount / 2, 2, 4) : Math.Clamp(parallelism, 1, 32);
        var warnings = new ConcurrentBag<string>();
        var watch = Stopwatch.StartNew();

        foreach (var directory in selected.Where(e => e.IsDirectory))
        {
            Directory.CreateDirectory(SafeTarget(root, directory.Path));
        }

        // Đọc theo thứ tự vị trí trong ảnh để các worker dùng chung khối vừa giải nén.
        var files = selected.Where(e => !e.IsDirectory).OrderBy(e => e.Offset).ThenBy(e => e.Path, StringComparer.Ordinal).ToArray();
        var totalBytes = files.Sum(e => e.Size);
        var queue = new ConcurrentQueue<PackageEntry>(files);
        long doneBytes = 0;
        var doneFiles = 0;
        string? current = null;
        var lastReport = Stopwatch.StartNew();
        var reportGate = new object();

        void Report(bool force)
        {
            if (progress == null)
            {
                return;
            }

            if (!force && lastReport.ElapsedMilliseconds < 100)
            {
                return;
            }

            lock (reportGate)
            {
                if (!force && lastReport.ElapsedMilliseconds < 100)
                {
                    return;
                }

                lastReport.Restart();
                progress.Report(new ExtractProgress(Interlocked.Read(ref doneBytes), totalBytes, Volatile.Read(ref doneFiles), files.Length, Volatile.Read(ref current)));
            }
        }

        Report(true);
        // Token liên kết: một worker lỗi thì các worker còn lại dừng ngay thay vì tiếp tục ghi cho tới hết hàng đợi.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var tasks = new Task[Math.Min(workers, Math.Max(1, files.Length))];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    while (queue.TryDequeue(out var entry))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!_files.TryGetValue(entry.Path, out var file))
                        {
                            warnings.Add(Loc.F("Extract.MissingEntry", entry.Path));
                            continue;
                        }

                        Volatile.Write(ref current, entry.Path);
                        var target = SafeTarget(root, entry.Path, out var sanitized);
                        if (sanitized)
                        {
                            warnings.Add(Loc.F("Extract.SanitizedName", entry.Path, Path.GetRelativePath(root, target).Replace(Path.DirectorySeparatorChar, '/')));
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        try
                        {
                            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
                            using var counted = new ProgressStream(output, n =>
                            {
                                Interlocked.Add(ref doneBytes, n);
                                Report(false);
                            }, token);
                            file.CopyTo(counted, decompress: decompress);
                        }
                        catch (Exception)
                        {
                            // Huỷ/lỗi giữa chừng: không để lại tệp ghi dở trông như đã hoàn chỉnh (luồng đã đóng trước khi tới đây).
                            TryDelete(target);
                            throw;
                        }

                        Interlocked.Increment(ref doneFiles);
                        Report(false);
                    }
                }
                catch (Exception)
                {
                    linked.Cancel();
                    throw;
                }
            }, CancellationToken.None);
        }

        // Luôn đợi mọi worker kết thúc (chúng kiểm tra token ở từng lần ghi) để khi hàm này trả về/ném lỗi
        // không còn luồng nào đang dùng reader hay ghi vào thư mục đích.
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex)
        {
            var inner = ex.Flatten().InnerExceptions;
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var failure = inner.FirstOrDefault(e => e is not OperationCanceledException) ?? inner[0];
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
        watch.Stop();
        Report(true);
        return new ExtractResult(root, files.Length, totalBytes, watch.Elapsed, warnings.ToArray());
    }

    /// <summary>Giải nén toàn bộ ảnh trong.</summary>
    public ExtractResult ExtractAll(string outputFolder, IProgress<ExtractProgress>? progress, CancellationToken cancellationToken, int parallelism = 0, bool decompress = true) =>
        Extract(Entries, outputFolder, progress, cancellationToken, parallelism, decompress);

    /// <summary>
    /// Xuất các entry của CNT (param.json, icon0.png, playgo-*.dat, các bảng .digests/.entry_keys…) vào thư mục đích.
    /// Thư viện từ chối đường dẫn có symlink (ví dụ /var trên macOS): khi đó xuất ra thư mục tạm "thật" rồi chuyển sang.
    /// </summary>
    public static IReadOnlyList<string> ExportCntEntries(string packagePath, string outputFolder, string passcode, CancellationToken cancellationToken)
    {
        ValidatePasscode(passcode);
        return ExportWithStaging(outputFolder, "psviethoa-cnt-", cancellationToken, target => ProsperoPackageArchive.ExtractCntEntries(packagePath, target, passcode, includeEncrypted: true));
    }

    /// <summary>
    /// Ghép các tệp sce_sys nằm trong CNT (param.json, icon0.png, pic0.png, playgo-*.dat, trophy2/…, uds/…) vào
    /// &lt;thư mục đích&gt;/sce_sys để cây ứng dụng trích ra đúng bố cục Sony và dùng lại được làm nguồn tạo gói FPKG.
    /// Các bảng nội bộ của CNT (.digests, .entry_keys, .metas… và entry-XXXXXXXX.bin) bị bỏ qua.
    /// Trả về danh sách đường dẫn tương đối (dùng '/') đã ghi.
    /// </summary>
    public static IReadOnlyList<string> ExportSceSys(string packagePath, string outputFolder, string passcode, CancellationToken cancellationToken)
    {
        var staging = ExportCntEntriesToTemp(packagePath, passcode, cancellationToken);
        try
        {
            var sceSys = Path.Combine(Path.GetFullPath(outputFolder), "sce_sys");
            var written = new List<string>();
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(staging, file);
                if (!IsSceSysPayload(relative))
                {
                    continue;
                }

                var target = Path.Combine(sceSys, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                written.Add("sce_sys/" + relative.Replace(Path.DirectorySeparatorChar, '/'));
            }

            return written;
        }
        finally
        {
            BuildEngine.TryDeleteDirectory(staging);
        }
    }

    /// <summary>Tệp CNT nào là dữ liệu sce_sys thật (không phải bảng nội bộ của container).</summary>
    public static bool IsSceSysPayload(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (name.Length == 0 || name[0] == '.')
        {
            return false;
        }

        // entry-0000040a.bin: mục CNT không có tên chuẩn → giữ nguyên trong cnt/ (xuất thô), không đưa vào sce_sys.
        return !System.Text.RegularExpressions.Regex.IsMatch(name, "^entry-[0-9a-fA-F]{8}\\.bin$");
    }

    /// <summary>Xuất các mục SI (supplement: naps_meta_*.dat, playgo-chunk.crc…) nếu gói có; trả về danh sách rỗng nếu không có.</summary>
    public static IReadOnlyList<string> ExportSiEntries(string packagePath, string outputFolder, CancellationToken cancellationToken)
    {
        var map = ProsperoPackageArchive.Inspect(packagePath);
        if (map.SupplementSize <= 0)
        {
            return Array.Empty<string>();
        }

        return ExportWithStaging(outputFolder, "psviethoa-si-", cancellationToken, target => ProsperoPackageArchive.ExtractSiEntries(packagePath, target));
    }

    /// <summary>
    /// Xuất các tệp của lớp PFS ngoài (pfs_image.dat, naps_pkg_layout.dat…). Với gói Native thư viện tự giải mã bằng passcode.
    /// Nếu thư viện không giải nén được (tệp ngoài không phải PFSC dù có cờ nén) thì xuất thô.
    /// </summary>
    public static IReadOnlyList<string> ExportOuterFiles(string packagePath, string outputFolder, string passcode, bool decompress, CancellationToken cancellationToken)
    {
        ValidatePasscode(passcode);
        return ExportWithStaging(outputFolder, "psviethoa-outer-", cancellationToken, target =>
        {
            // Chỉ dọn những tệp do lần thử giải nén hỏng tạo ra — không đụng tới tệp có sẵn của người dùng trong thư mục đích.
            var before = new HashSet<string>(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories), StringComparer.Ordinal);
            try
            {
                ProsperoPackageArchive.ExtractOuterFiles(packagePath, target, passcode, decompress);
            }
            catch (Exception ex) when (decompress && ex is ArgumentException or InvalidDataException)
            {
                foreach (var stale in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Where(f => !before.Contains(f)).ToList())
                {
                    File.Delete(stale);
                }

                ProsperoPackageArchive.ExtractOuterFiles(packagePath, target, passcode, decompress: false);
            }
        });
    }

    /// <summary>
    /// Passcode phải đúng 32 ký tự ASCII in được (như khi tạo gói). Kiểm tra trước khi đưa vào thư viện để báo lỗi
    /// rõ ràng thay vì lỗi giải mã khó hiểu (lớp ngoài "giải mã" bằng khoá sai chỉ cho ra rác).
    /// </summary>
    public static void ValidatePasscode(string? passcode)
    {
        if (passcode == null || passcode.Length != BuildRequest.PasscodeLength || passcode.Any(c => c > 127 || char.IsControl(c)))
        {
            throw new ArgumentException(Loc.T("Val.Passcode"));
        }
    }

    /// <summary>Xuất thẳng vào thư mục đích khi đường dẫn không chứa symlink; nếu có thì qua thư mục tạm rồi chuyển sang.</summary>
    private static IReadOnlyList<string> ExportWithStaging(string outputFolder, string stagingPrefix, CancellationToken cancellationToken, Action<string> export)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(destination);
        if (!ContainsReparsePoint(destination))
        {
            var before = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                .Select(f => (f, new FileInfo(f).LastWriteTimeUtc))
                .ToDictionary(t => t.f, t => t.Item2, StringComparer.Ordinal);
            export(destination);
            var written = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                .Where(f => !before.TryGetValue(f, out var stamp) || new FileInfo(f).LastWriteTimeUtc != stamp)
                .Select(f => Path.GetRelativePath(destination, f).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            return written;
        }

        var staging = CreateRealTemporaryDirectory(stagingPrefix);
        try
        {
            export(staging);
            return MoveTree(staging, destination);
        }
        finally
        {
            BuildEngine.TryDeleteDirectory(staging);
        }
    }

    /// <summary>Thư mục gợi ý cho mẫu DLC: "&lt;tên gói&gt;-dlc-template" cạnh tệp .pkg, thêm " (2)", " (3)"… nếu đã có và không rỗng.</summary>
    public static string SuggestDlcTemplateFolder(string packagePath)
    {
        var full = Path.GetFullPath(packagePath);
        var parent = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        var baseName = Path.GetFileNameWithoutExtension(full) + "-dlc-template";
        var candidate = Path.Combine(parent, baseName);
        for (var index = 2; Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any(); index++)
        {
            candidate = Path.Combine(parent, $"{baseName} ({index})");
        }

        return candidate;
    }

    /// <summary>
    /// Xuất mẫu DLC từ một gói DLC có dữ liệu (fpkg-gui 0.6.8 "Export DLC template", engine ExportAdditionalContentTemplate): các tệp
    /// sce_sys cần để đóng gói lại (param.json, license.dat/info, icon…) cùng tệp dự án "&lt;Content ID&gt;.gp5" (loại prospero_ac,
    /// Content ID, passcode, entitlement key lấy từ license.info, số khối/kịch bản PlayGo của gói gốc, rootdir = thư mục mẫu). Dữ
    /// liệu game bên trong không bị đọc. Thư mục đích phải chưa có hoặc rỗng. Trả về đường dẫn tương đối đã ghi.
    /// </summary>
    public static IReadOnlyList<string> ExportDlcTemplate(string packagePath, string outputFolder, string passcode, CancellationToken cancellationToken, Action<int>? progress = null)
    {
        ValidatePasscode(passcode);
        var destination = Path.GetFullPath(outputFolder);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException(Localization.Loc.F("DlcTemplate.NotEmpty", destination));
        }

        // Thư viện từ chối đường dẫn có liên kết (macOS /var → /private/var) và đòi thư mục rỗng: xuất vào một thư mục con chưa tồn
        // tại trong thư mục tạm thật rồi chuyển sang đích. Dự án GP5 dùng rootdir tương đối "." nên chuyển đi vẫn đúng.
        var staging = CreateRealTemporaryDirectory("psviethoa-dlc-");
        try
        {
            var target = Path.Combine(staging, "template");
            cancellationToken.ThrowIfCancellationRequested();
            ProsperoPackageArchive.ExportAdditionalContentTemplate(packagePath, target, passcode, cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(destination);
            return MoveTree(target, destination);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("additional-content", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Localization.Loc.T("DlcTemplate.NotDlc"), ex);
        }
        finally
        {
            BuildEngine.TryDeleteDirectory(staging);
        }
    }

    /// <summary>Xuất CNT entries ra một thư mục tạm không chứa symlink; người gọi tự xoá thư mục.</summary>
    internal static string ExportCntEntriesToTemp(string packagePath, string passcode, CancellationToken cancellationToken)
    {
        ValidatePasscode(passcode);
        var staging = CreateRealTemporaryDirectory("psviethoa-cnt-");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProsperoPackageArchive.ExtractCntEntries(packagePath, staging, passcode, includeEncrypted: true);
            return staging;
        }
        catch (Exception)
        {
            BuildEngine.TryDeleteDirectory(staging);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _image.Dispose();
        if (_decryptedOuterPath != null)
        {
            TryDelete(_decryptedOuterPath);
        }
    }

    // ===================== Nội bộ =====================

    private void Walk(PfsReader.Dir directory, string prefix, List<PackageEntry> entries)
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
                    entries.Add(new PackageEntry(path, name, PackageEntryKind.Directory, 0, 0, false, child.ino, child.offset, string.Empty));
                    Walk(child, path, entries);
                    break;
                case PfsReader.File file:
                    // Với inode nén PFSC, thư viện dùng `size` = kích thước lưu trên ảnh và `compressed_size` = kích thước sau giải nén.
                    var compressed = (file.flags & InodeFlags.compressed) != 0;
                    var logicalSize = compressed ? file.compressed_size : file.size;
                    entries.Add(new PackageEntry(path, name, PackageEntryKind.File, logicalSize, file.size, compressed, file.ino, file.offset, file.flags.ToString()));
                    _files[path] = file;
                    break;
            }
        }
    }

    private static long LocateSuperblock(NapsImageReader image, byte[] fih)
    {
        var dataBlocks = BinaryPrimitives.ReadUInt32LittleEndian(fih.AsSpan(ProsperoPkgLayout.FihDataRegionBlockCountField));
        var candidate = (long)dataBlocks * OuterBlockSize;
        var probe = new byte[16];
        if (candidate > 0 && candidate + 16 <= image.Size)
        {
            image.Read(candidate, probe, 0, 16);
            if (BinaryPrimitives.ReadInt64LittleEndian(probe) == PfsHeader.VersionPs5)
            {
                return candidate;
            }
        }

        // Dự phòng: quét ngược từ cuối ảnh theo ranh giới 64 KiB (superblock nằm ở đầu vùng metadata cuối ảnh).
        var lowest = Math.Max(0, image.Size - SuperblockScanWindow);
        for (var offset = image.Size / OuterBlockSize * OuterBlockSize; offset >= lowest; offset -= OuterBlockSize)
        {
            if (offset + 16 > image.Size)
            {
                continue;
            }

            image.Read(offset, probe, 0, 16);
            if (BinaryPrimitives.ReadInt64LittleEndian(probe) == PfsHeader.VersionPs5)
            {
                return offset;
            }
        }

        throw new InvalidDataException(Loc.T("Extract.NoInnerSuperblock"));
    }

    private static FileStream OpenSequential(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess);

    private static string ResolveTemporaryFolder(string? temporaryFolder)
    {
        if (!string.IsNullOrWhiteSpace(temporaryFolder))
        {
            Directory.CreateDirectory(temporaryFolder);
            return Path.GetFullPath(temporaryFolder);
        }

        return Path.GetTempPath();
    }

    /// <summary>Đường dẫn "thật" (không qua symlink) — thư viện từ chối thư mục đích có symlink ở bất kỳ cấp nào.</summary>
    public static string GetRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            try
            {
                var target = Directory.ResolveLinkTarget(current, returnFinalTarget: true) ?? File.ResolveLinkTarget(current, returnFinalTarget: true);
                if (target != null)
                {
                    current = target.FullName;
                }
            }
            catch (Exception)
            {
                // Không phân giải được thì giữ nguyên.
            }
        }

        return current;
    }

    private static bool ContainsReparsePoint(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        return false;
    }

    /// <summary>Thư mục tạm mới, không có symlink trên đường dẫn (thử temp hệ thống, rồi dữ liệu ứng dụng, rồi thư mục hiện tại).</summary>
    internal static string CreateRealTemporaryDirectory(string prefix)
    {
        var candidates = new[]
        {
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Directory.GetCurrentDirectory(),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            try
            {
                var real = GetRealPath(candidate);
                var directory = Path.Combine(real, prefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                if (!ContainsReparsePoint(directory))
                {
                    return directory;
                }

                Directory.Delete(directory);
            }
            catch (Exception)
            {
                // thử ứng viên tiếp theo
            }
        }

        throw new IOException(Loc.T("Extract.NoTempFolder"));
    }

    private static IReadOnlyList<string> MoveTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var moved = new List<string>();
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
            moved.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
        }

        moved.Sort(StringComparer.Ordinal);
        return moved;
    }

    // Ký tự không được phép trong tên tệp của hệ thống hiện tại; ':' luôn bị loại (ADS "tệp:stream" trên Windows).
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars().Append(':').Distinct().ToArray();

    /// <summary>Ghép đường dẫn đích và chặn mọi kiểu vượt thư mục ("..", ".", đường dẫn tuyệt đối, đoạn rỗng).</summary>
    public static string SafeTarget(string root, string relativePath) => SafeTarget(root, relativePath, out _);

    /// <summary>
    /// Như <see cref="SafeTarget(string, string)"/>; ký tự không hợp lệ với hệ tệp hiện tại (Windows: ? * : " &lt; &gt; | …
    /// — trình tạo gói thay ký tự ngoài ASCII trong tên tệp bằng '?') được thay bằng '_' thay vì làm hỏng cả lượt giải nén;
    /// <paramref name="sanitized"/> = true khi có thay thế để người gọi cảnh báo.
    /// </summary>
    public static string SafeTarget(string root, string relativePath, out bool sanitized)
    {
        sanitized = false;
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Length == 0 || normalized[0] == '/' || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(Loc.F("Extract.UnsafePath", relativePath));
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidDataException(Loc.F("Extract.UnsafePath", relativePath));
        }

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment is "." or "..")
            {
                throw new InvalidDataException(Loc.F("Extract.UnsafePath", relativePath));
            }

            if (segment.IndexOfAny(InvalidNameChars) >= 0)
            {
                var chars = segment.ToCharArray();
                for (var k = 0; k < chars.Length; k++)
                {
                    if (Array.IndexOf(InvalidNameChars, chars[k]) >= 0)
                    {
                        chars[k] = '_';
                    }
                }

                segments[i] = new string(chars);
                sanitized = true;
            }
        }

        var combined = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidDataException(Loc.F("Extract.UnsafePath", relativePath));
        }

        return combined;
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception)
            {
                Thread.Sleep(200);
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Stream ghi đếm số byte đã ghi (để báo tiến độ) và kiểm tra huỷ.</summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _onWrite;
        private readonly CancellationToken _cancellation;

        public ProgressStream(Stream inner, Action<long> onWrite, CancellationToken cancellation)
        {
            _inner = inner;
            _onWrite = onWrite;
            _cancellation = cancellation;
        }

        public override bool CanRead => false;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _cancellation.ThrowIfCancellationRequested();
            _inner.Write(buffer, offset, count);
            _onWrite(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _cancellation.ThrowIfCancellationRequested();
            _inner.Write(buffer);
            _onWrite(buffer.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Cửa sổ chỉ đọc lên một luồng và SỞ HỮU luồng đó (SubStream của thư viện không đóng luồng gốc khi Dispose).</summary>
    private sealed class OwnedWindowStream : Stream
    {
        private readonly Stream _owner;
        private readonly Stream _window;

        public OwnedWindowStream(Stream owner, long offset, long length)
        {
            _owner = owner;
            try
            {
                _window = new SubStream(owner, offset, length);
            }
            catch (Exception)
            {
                owner.Dispose();
                throw;
            }
        }

        public override bool CanRead => _window.CanRead;
        public override bool CanSeek => _window.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _window.Length;
        public override long Position { get => _window.Position; set => _window.Position = value; }
        public override void Flush() => _window.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _window.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _window.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => _window.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _window.Dispose();
                _owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// IMemoryReader cho ảnh PPR-PFS trong logic: giải nén NAPS theo khối cố định từ pfs_image.dat trong lớp PFS ngoài
/// (đã là plaintext hoặc đã giải mã ra tệp tạm), có bộ đệm LRU và nhiều "kênh" giải nén song song.
/// Các khoảng AFID thưa (không lưu trong NAPS) được trả về toàn số 0 theo bảng offset của naps_pkg_layout.dat.
/// </summary>
internal sealed class NapsImageReader : IMemoryReader
{
    private const int ChunkSize = 4 << 20;
    private const int MaxCachedChunks = 16;

    private readonly Func<Stream> _outerFactory;
    private readonly long _outerSuperblockOffset;
    private readonly int _maxChannels;
    private readonly NapsLayoutDocument _layout;
    private readonly (long Start, long End)[] _holes;
    private readonly ConcurrentBag<DecodeChannel> _idle = new();
    private readonly List<DecodeChannel> _all = new();
    private readonly SemaphoreSlim _channelSlots;
    private readonly Dictionary<long, Lazy<byte[]>> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private readonly object _gate = new();
    private long _decodes;
    private bool _disposed;

    public NapsImageReader(Func<Stream> outerFactory, int outerSuperblockIndex, int maxChannels)
    {
        _outerFactory = outerFactory;
        _outerSuperblockOffset = (long)outerSuperblockIndex * 65536L;
        _maxChannels = Math.Max(1, maxChannels);
        _channelSlots = new SemaphoreSlim(_maxChannels, _maxChannels);

        // Kênh đầu tiên cũng dùng để đọc naps_pkg_layout.dat một lần.
        var first = new DecodeChannel(_outerFactory(), _outerSuperblockOffset, null);
        _layout = first.ReadLayout();
        first.Layout = _layout;
        _all.Add(first);
        _idle.Add(first);

        Size = (long)_layout.FileOffsets[^1].UncompressedOffsetStart;
        var holes = new List<(long, long)>();
        for (var i = 0; i + 1 < _layout.FileOffsets.Count; i++)
        {
            if (_layout.FileOffsets[i].Continuation)
            {
                holes.Add(((long)_layout.FileOffsets[i].UncompressedOffsetStart, (long)_layout.FileOffsets[i + 1].UncompressedOffsetStart));
            }
        }

        _holes = holes.Where(h => h.Item2 > h.Item1).OrderBy(h => h.Item1).ToArray();
    }

    /// <summary>Kích thước ảnh logic theo bảng NAPS.</summary>
    public long Size { get; }

    /// <summary>Số lần giải nén khối (thống kê).</summary>
    public long Decodes => Interlocked.Read(ref _decodes);

    public void Read(long pos, byte[] buf, int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pos);
        var done = 0;
        while (done < count)
        {
            var at = pos + done;
            if (at >= Size)
            {
                Array.Clear(buf, offset + done, count - done);
                return;
            }

            var chunkStart = at - at % ChunkSize;
            var chunk = GetChunk(chunkStart);
            var inChunk = (int)(at - chunkStart);
            var n = Math.Min(count - done, chunk.Length - inChunk);
            if (n <= 0)
            {
                Array.Clear(buf, offset + done, count - done);
                return;
            }

            Buffer.BlockCopy(chunk, inChunk, buf, offset + done, n);
            done += n;
        }
    }

    private byte[] GetChunk(long start)
    {
        Lazy<byte[]> lazy;
        lock (_gate)
        {
            if (_cache.TryGetValue(start, out var hit))
            {
                _lru.Remove(start);
                _lru.AddFirst(start);
                lazy = hit;
            }
            else
            {
                lazy = new Lazy<byte[]>(() => Decode(start), LazyThreadSafetyMode.ExecutionAndPublication);
                _cache[start] = lazy;
                _lru.AddFirst(start);
                while (_cache.Count > MaxCachedChunks)
                {
                    // Loại khối cũ nhất đã giải nén xong (không loại khối đang được giải nén).
                    var node = _lru.Last;
                    while (node != null && _cache.TryGetValue(node.Value, out var candidate) && !candidate.IsValueCreated)
                    {
                        node = node.Previous;
                    }

                    if (node == null)
                    {
                        break;
                    }

                    _cache.Remove(node.Value);
                    _lru.Remove(node);
                }
            }
        }

        try
        {
            return lazy.Value;
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(start, out var failed) && ReferenceEquals(failed, lazy))
                {
                    _cache.Remove(start);
                    _lru.Remove(start);
                }
            }

            throw;
        }
    }

    private byte[] Decode(long start)
    {
        Interlocked.Increment(ref _decodes);
        var length = (int)Math.Min(ChunkSize, Size - start);
        var end = start + length;
        var overlapping = _holes.Where(h => h.Start < end && h.End > start).ToArray();
        if (overlapping.Length == 0)
        {
            return DecodeStored(start, length);
        }

        // Khối có khoảng thưa: chỉ giải nén các đoạn được lưu, phần còn lại để 0.
        var result = new byte[length];
        var cursor = start;
        foreach (var hole in overlapping)
        {
            var storedEnd = Math.Min(hole.Start, end);
            if (storedEnd > cursor)
            {
                var part = DecodeStored(cursor, (int)(storedEnd - cursor));
                Buffer.BlockCopy(part, 0, result, (int)(cursor - start), part.Length);
            }

            cursor = Math.Max(cursor, Math.Min(hole.End, end));
        }

        if (cursor < end)
        {
            var part = DecodeStored(cursor, (int)(end - cursor));
            Buffer.BlockCopy(part, 0, result, (int)(cursor - start), part.Length);
        }

        return result;
    }

    private byte[] DecodeStored(long offset, int length)
    {
        var channel = Rent();
        try
        {
            return channel.DecompressRange(offset, length);
        }
        finally
        {
            Return(channel);
        }
    }

    private DecodeChannel Rent()
    {
        _channelSlots.Wait();
        try
        {
            if (_idle.TryTake(out var channel))
            {
                return channel;
            }

            lock (_all)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                channel = new DecodeChannel(_outerFactory(), _outerSuperblockOffset, _layout);
                _all.Add(channel);
                return channel;
            }
        }
        catch (Exception)
        {
            // Không tạo được kênh (tệp bị xoá, reader đã Dispose…): trả lại chỗ trong semaphore để các worker khác không kẹt.
            _channelSlots.Release();
            throw;
        }
    }

    private void Return(DecodeChannel channel)
    {
        _idle.Add(channel);
        _channelSlots.Release();
    }

    public void Dispose()
    {
        lock (_all)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var channel in _all)
            {
                channel.Dispose();
            }

            _all.Clear();
        }

        lock (_gate)
        {
            _cache.Clear();
            _lru.Clear();
        }

        _channelSlots.Dispose();
    }

    /// <summary>Một chuỗi luồng độc lập: lớp ngoài → PfsReader ngoài → pfs_image.dat → giải nén NAPS theo khoảng.</summary>
    private sealed class DecodeChannel : IDisposable
    {
        private readonly Stream _outer;
        private readonly LibProsperoPkg.Util.StreamReader _outerReader;
        private readonly PfsReader _outerPfs;
        private readonly IMemoryReader _imageView;
        private readonly StreamWrapper _image;

        public DecodeChannel(Stream outer, long outerSuperblockOffset, NapsLayoutDocument? layout)
        {
            _outer = outer;
            LibProsperoPkg.Util.StreamReader? outerReader = null;
            IMemoryReader? imageView = null;
            StreamWrapper? image = null;
            try
            {
                outerReader = new LibProsperoPkg.Util.StreamReader(outer, 0L, false);
                var outerPfs = new PfsReader(outerReader, 0, null, null, null, outerSuperblockOffset, encryptedDataAlreadyDecrypted: true);
                var imageFile = FindFile(outerPfs, "pfs_image.dat");
                imageView = imageFile.GetView();
                image = new StreamWrapper(imageView, imageFile.size);
                _outerReader = outerReader;
                _outerPfs = outerPfs;
                _imageView = imageView;
                _image = image;
            }
            catch (Exception)
            {
                // Lớp ngoài không hợp lệ (passcode sai, tệp hỏng…): đóng mọi thứ đã mở để không lọt handle/tệp tạm.
                image?.Dispose();
                imageView?.Dispose();
                outerReader?.Dispose();
                outer.Dispose();
                throw;
            }

            Layout = layout;
        }

        private NapsLayoutDocument? _layout;
        private Stream? _logical;

        public NapsLayoutDocument? Layout
        {
            get => _layout;
            set
            {
                _layout = value;
                _logical?.Dispose();
                _logical = null;
            }
        }

        public NapsLayoutDocument ReadLayout() =>
            ProsperoNapsLayout.Parse(FindFile(_outerPfs, "naps_pkg_layout.dat").ReadAllBytes());

        /// <summary>
        /// Giải một khoảng của ảnh logic. ProsperoNapsImage.DecompressRange dựng lại toàn bộ kế hoạch span và quét tuyến tính ở MỖI lần gọi
        /// — gói 127 GB có ~500 000 span × ~32 000 khối 4 MiB là hàng chục tỉ bước (giải nén chỉ còn ~20 MB/s). Luồng OpenRead của thư viện
        /// dựng kế hoạch một lần và tìm span bằng tìm nhị phân; mỗi kênh giữ một luồng (kênh chỉ do một luồng dùng tại một thời điểm).
        /// </summary>
        public byte[] DecompressRange(long offset, int length)
        {
            var layout = Layout ?? throw new InvalidOperationException("layout");
            try
            {
                _logical ??= ProsperoNapsImage.OpenRead(_image, layout, CancellationToken.None);
                if (offset >= 0 && offset + length <= _logical.Length)
                {
                    var buffer = new byte[length];
                    _logical.Position = offset;
                    _logical.ReadExactly(buffer);
                    return buffer;
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or EndOfStreamException)
            {
                _logical?.Dispose();
                _logical = null;
            }

            return ProsperoNapsImage.DecompressRange(_image, layout, offset, length);
        }

        private static PfsReader.File FindFile(PfsReader outerPfs, string name) =>
            outerPfs.GetAllFiles().FirstOrDefault(f => string.Equals(f.name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(Loc.F("Extract.OuterFileMissing", name));

        public void Dispose()
        {
            _logical?.Dispose();
            _image.Dispose();
            _imageView.Dispose();
            _outerReader.Dispose();
            _outer.Dispose();
        }
    }
}
