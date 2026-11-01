using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LibOrbisPkg.PFS;
using LibOrbisPkg.PKG;
using LibOrbisPkg.Util;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Ps4;

/// <summary>
/// Đọc PS4 PKG bằng LibOrbisPkg: thông tin gói, danh sách tệp, metadata sce_sys và xả dữ liệu PFS (passcode hoặc khoá FPKG khôi
/// phục được). PKG nguồn luôn mở chỉ đọc.
/// </summary>
public static class Ps4PackageReader
{
    /// <summary>Một tệp trong gói. <see cref="IsMetadata"/>: mục sce_sys nằm ngoài PFS.</summary>
    public sealed record PackageFileInfo(string Path, long Bytes, bool IsMetadata);

    public static IReadOnlyList<PackageFileInfo> ListFiles(string path, string passcode = Ps4Defaults.Passcode)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pkg = Read(source);
        if (pkg.Header.pfs_image_size == 0)
        {
            return Array.Empty<PackageFileInfo>();
        }

        using var mmf = MemoryMappedFile.CreateFromFile(source, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        using var accessor = mmf.CreateViewAccessor((long)pkg.Header.pfs_image_offset, (long)pkg.Header.pfs_image_size, MemoryMappedFileAccess.Read);
        var pfs = OpenPfs(pkg, accessor, passcode, _ => { });
        var results = new Dictionary<string, PackageFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in pfs.GetAllFiles())
        {
            var relative = Relative(file);
            results[relative] = new PackageFileInfo(relative, file.size, false);
        }

        foreach (var meta in pkg.Metas.Metas.Where(m => (uint)m.id >= 0x400))
        {
            var name = MetaName(meta);
            if (meta.Encrypted && !pkg.CheckPasscode(passcode) && meta.KeyIndex != 3)
            {
                name += ".encrypted";
            }

            var relative = "sce_sys/" + name;
            results[relative] = new PackageFileInfo(relative, meta.DataSize, true);
        }

        return results.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Đọc header PKG, kiểm tra magic, bảng entry, vùng PFS và metadata nằm trong tệp.</summary>
    internal static Pkg Read(FileStream stream)
    {
        if (stream.Length < 0x1100)
        {
            throw new InvalidDataException(Loc.T("Ps4.Core.TooSmall"));
        }

        Span<byte> header = stackalloc byte[32];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual(new byte[] { 0x7f, 0x43, 0x4e, 0x54 }))
        {
            throw new InvalidDataException(Loc.T("Ps4.Core.NotPkg"));
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(header[0x10..]);
        var table = BinaryPrimitives.ReadUInt32BigEndian(header[0x18..]);
        if (count > 100_000 || table + (long)count * 32 > stream.Length)
        {
            throw new InvalidDataException(Loc.T("Ps4.Core.BadEntryTable"));
        }

        stream.Position = 0;
        var pkg = new PkgReader(stream).ReadPkg();
        if (pkg.Header.pfs_image_offset > (ulong)stream.Length || pkg.Header.pfs_image_size > (ulong)stream.Length - pkg.Header.pfs_image_offset)
        {
            throw new InvalidDataException(Loc.T("Ps4.Core.Truncated"));
        }

        foreach (var meta in pkg.Metas.Metas)
        {
            long size = meta.Encrypted ? ((long)meta.DataSize + 15) & ~15L : meta.DataSize;
            if ((long)meta.DataOffset + size > stream.Length || size > 256L * 1024 * 1024)
            {
                throw new InvalidDataException(Loc.F("Ps4.Core.BadMeta", ((uint)meta.id).ToString("X", CultureInfo.InvariantCulture)));
            }
        }

        return pkg;
    }

    public static Ps4PackageInfo Inspect(string path, string passcode = Ps4Defaults.Passcode)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pkg = Read(input);
        var sfo = pkg.ParamSfo?.ParamSfo;
        var versionKey = pkg.Header.content_type == ContentType.AC ? "VERSION" : "APP_VER";
        return new Ps4PackageInfo(
            System.IO.Path.GetFullPath(path),
            sfo?["TITLE"]?.ToString() ?? Loc.T("Ps4.Core.NoTitle"),
            sfo?["TITLE_ID"]?.ToString() ?? string.Empty,
            pkg.Header.content_id,
            sfo?[versionKey]?.ToString() ?? string.Empty,
            sfo?["CATEGORY"]?.ToString() ?? string.Empty,
            input.Length,
            pkg.CheckPasscode(passcode),
            pkg.GetEkpfs() != null,
            sfo?["VERSION"]?.ToString() ?? string.Empty);
    }

    /// <summary>Tên thư mục xả: CUSA12345-base / -update / -dlc (bố cục image0 + Sc0 quen thuộc của PS4).</summary>
    public static string? ExtractFolderName(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var pkg = Read(input);
            var sfo = pkg.ParamSfo?.ParamSfo;
            var titleId = sfo?["TITLE_ID"]?.ToString() ?? string.Empty;
            var contentId = pkg.Header.content_id ?? string.Empty;
            if (!Regex.IsMatch(titleId, "^[A-Z]{4}[0-9]{5}$") && contentId.Length >= 16)
            {
                titleId = contentId.Substring(7, 9);
            }

            if (!Regex.IsMatch(titleId, "^[A-Z]{4}[0-9]{5}$"))
            {
                return null;
            }

            var category = sfo?["CATEGORY"]?.ToString() ?? string.Empty;
            var suffix = pkg.Header.content_type == ContentType.AC || category.StartsWith("ac", StringComparison.Ordinal) ? "dlc"
                : category == "gp" ? "update" : "base";
            return $"{titleId}-{suffix}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    public static byte[]? ReadIcon(string path) => ReadMeta(path, "icon0.png");

    public static byte[]? ReadParamSfo(string path) => ReadMeta(path, "param.sfo");

    /// <summary>Mục metadata ngoài PKG (sce_sys/&lt;tên&gt;), giải mã bằng passcode hoặc khoá FPKG như khi xả.</summary>
    public static byte[]? ReadMeta(string path, string metaName, string passcode = Ps4Defaults.Passcode)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pkg = Read(input);
        var entry = pkg.Metas.Metas.FirstOrDefault(m => EntryNames.IdToName.TryGetValue(m.id, out var name) && name == metaName);
        if (entry == null || entry.DataSize > 16 * 1024 * 1024)
        {
            return null;
        }

        var data = new byte[entry.Encrypted ? ((long)entry.DataSize + 15) & ~15L : entry.DataSize];
        input.Position = entry.DataOffset;
        input.ReadExactly(data);
        if (entry.Encrypted)
        {
            if (pkg.CheckPasscode(passcode))
            {
                data = Entry.Decrypt(data, pkg.Header.content_id, passcode, entry);
            }
            else if (entry.KeyIndex == 3)
            {
                data = Entry.Decrypt(data, pkg, entry);
            }
            else
            {
                return null;
            }
        }

        Array.Resize(ref data, (int)entry.DataSize);
        return data;
    }

    private static string Relative(PfsReader.File file) => file.FullName.StartsWith("/uroot/", StringComparison.Ordinal) ? file.FullName[7..] : file.FullName.TrimStart('/');

    private static string MetaName(MetaEntry meta) =>
        EntryNames.IdToName.TryGetValue(meta.id, out var known) ? known : $"_unknown/meta_{(uint)meta.id:X8}.bin";

    /// <summary>Mở PFS bằng khoá passcode và khoá FPKG khôi phục, thử cả cờ KDF cũ; không sửa header PKG.</summary>
    internal static PfsReader OpenPfs(Pkg pkg, MemoryMappedViewAccessor accessor, string passcode, Action<string> log)
    {
        var keys = new List<byte[]>();
        if (pkg.CheckPasscode(passcode))
        {
            keys.Add(Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1));
        }

        var recovered = pkg.GetEkpfs();
        if (recovered != null && !keys.Any(k => k.SequenceEqual(recovered)))
        {
            keys.Add(recovered);
        }

        if (keys.Count == 0)
        {
            throw new InvalidDataException(Loc.T("Ps4.Core.NoKey"));
        }

        Exception? last = null;
        foreach (var key in keys)
        {
            foreach (var flags in new[] { pkg.Header.pfs_flags, pkg.Header.pfs_flags ^ 0x2000000000000000UL }.Distinct())
            {
                try
                {
                    var outer = new PfsReader(accessor, flags, key);
                    var innerFile = outer.GetFile("pfs_image.dat");
                    var inner = innerFile != null ? new PfsReader(new PFSCReader(innerFile.GetView())) : outer;
                    _ = inner.GetAllFiles().Take(1).ToArray();
                    log(Loc.T(flags == pkg.Header.pfs_flags ? "Ps4.Core.PfsOpened" : "Ps4.Core.PfsOpenedLegacy"));
                    return inner;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    last = ex;
                }
            }
        }

        throw new InvalidDataException(Loc.F("Ps4.Core.PfsFailed", last?.Message), last);
    }

    /// <param name="skipFiles">Tệp không xả (gộp: tệp base đã có bản thay từ update).</param>
    /// <param name="keepAllMetadata">Xả chọn lọc nhưng vẫn ghi mọi metadata sce_sys (dựng base tham chiếu).</param>
    internal static Ps4JobResult Extract(Ps4JobRequest request, Ps4JobContext job, ISet<string>? skipFiles = null, bool keepAllMetadata = false)
    {
        using var source = new FileStream(request.Input, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pkg = Read(source);
        var matches = pkg.CheckPasscode(request.Passcode);
        var versionKey = pkg.Header.content_type == ContentType.AC ? "VERSION" : "APP_VER";
        job.Log($"{pkg.Header.content_id} • {pkg.ParamSfo?.ParamSfo[versionKey]} • {(source.Length / 1e9).ToString("F2", CultureInfo.InvariantCulture)} GB");
        job.Log(Loc.T(matches ? "Ps4.Core.PasscodeMatch" : "Ps4.Core.PasscodeRecover"));
        if (pkg.Header.pfs_image_size == 0)
        {
            throw new InvalidDataException(Loc.T("Ps4.Core.NoPfs"));
        }

        using var mmf = MemoryMappedFile.CreateFromFile(source, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        using var accessor = mmf.CreateViewAccessor((long)pkg.Header.pfs_image_offset, (long)pkg.Header.pfs_image_size, MemoryMappedFileAccess.Read);
        var pfs = OpenPfs(pkg, accessor, request.Passcode, line => job.Log(line));
        var files = pfs.GetAllFiles().ToArray();
        if (skipFiles?.Count > 0)
        {
            var before = files.Length;
            files = files.Where(f => !skipFiles.Contains(Relative(f))).ToArray();
            job.Log(Loc.F("Ps4.Core.MergeSkipped", before - files.Length));
        }

        HashSet<string>? selected = request.ExtractOnly?.Length > 0
            ? new HashSet<string>(request.ExtractOnly.Select(x => x.Replace('\\', '/').TrimStart('/')), StringComparer.OrdinalIgnoreCase)
            : null;
        if (selected != null)
        {
            files = files.Where(f => selected.Contains(Relative(f))).ToArray();
            job.Log(Loc.F("Ps4.Core.Selective", selected.Count));
        }

        var total = files.Sum(f => f.size);
        Ps4Files.CheckSpace(request.Output, total + 256L * 1024 * 1024);
        var image = System.IO.Path.Combine(job.Folder, "image0");
        Directory.CreateDirectory(image);
        var records = new Dictionary<string, Ps4ManifestFile>(StringComparer.OrdinalIgnoreCase);
        var meter = new Ps4RateMeter();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double lastReport = -1;
        var buffer = new byte[2 * 1024 * 1024];
        long completed = 0;
        var index = 0;
        var phase = Loc.T("Ps4.Phase.Extracting");
        foreach (var file in files)
        {
            job.Token.ThrowIfCancellationRequested();
            var relative = Relative(file);
            var destination = Ps4Files.SafeChild(image, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, buffer.Length))
            using (var view = file.GetView())
            {
                for (long position = 0; position < file.size;)
                {
                    job.Token.ThrowIfCancellationRequested();
                    var length = (int)Math.Min(buffer.Length, file.size - position);
                    view.Read(position, buffer, 0, length);
                    output.Write(buffer, 0, length);
                    hash.AppendData(buffer, 0, length);
                    position += length;
                    if (watch.Elapsed.TotalSeconds - lastReport >= 0.2)
                    {
                        var done = completed + position;
                        var rate = meter.Update(done, total);
                        job.Progress(new Ps4Progress(phase, relative, (request.Verify ? 80 : 97) * done / (double)Math.Max(1, total), done, total, rate.Speed, rate.Eta));
                        lastReport = watch.Elapsed.TotalSeconds;
                    }
                }
            }

            completed += file.size;
            index++;
            records[relative] = new Ps4ManifestFile(relative, file.size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            job.Log(Loc.F("Ps4.Core.FileExtracted", index, files.Length, relative, file.size.ToString("N0", CultureInfo.InvariantCulture)));
        }

        foreach (var meta in pkg.Metas.Metas.Where(m => (uint)m.id >= 0x400))
        {
            job.Token.ThrowIfCancellationRequested();
            var name = MetaName(meta);
            if (selected != null && !keepAllMetadata)
            {
                var listed = meta.Encrypted && !matches && meta.KeyIndex != 3 ? name + ".encrypted" : name;
                if (!selected.Contains("sce_sys/" + listed))
                {
                    continue;
                }
            }

            var data = new byte[checked((int)(meta.Encrypted ? ((long)meta.DataSize + 15) & ~15L : meta.DataSize))];
            source.Position = meta.DataOffset;
            source.ReadExactly(data);
            if (meta.Encrypted)
            {
                if (matches)
                {
                    data = Entry.Decrypt(data, pkg.Header.content_id, request.Passcode, meta);
                }
                else if (meta.KeyIndex == 3)
                {
                    data = Entry.Decrypt(data, pkg, meta);
                }
                else
                {
                    name += ".encrypted";
                    job.Log(Loc.F("Ps4.Core.MetaKeptEncrypted", name));
                }
            }

            Array.Resize(ref data, checked((int)meta.DataSize));
            var relative = "sce_sys/" + name;
            var destination = Ps4Files.SafeChild(image, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) && !File.ReadAllBytes(destination).SequenceEqual(data))
            {
                var backup = Ps4Files.SafeChild(System.IO.Path.Combine(job.Folder, "metadata_inner_original"), relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!);
                File.Copy(destination, backup, false);
                job.Log(Loc.F("Ps4.Core.OuterMetaPreferred", relative));
            }

            File.WriteAllBytes(destination, data);

            // Sc0 giữ metadata ngoài PKG cạnh image0 (bố cục xả PS4 chuẩn).
            var sc0 = Ps4Files.SafeChild(System.IO.Path.Combine(job.Folder, "Sc0"), name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sc0)!);
            File.WriteAllBytes(sc0, data);
            records[relative] = new Ps4ManifestFile(relative, data.Length, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        }

        if (selected != null && !keepAllMetadata)
        {
            var missing = selected.Where(x => !records.ContainsKey(x)).ToArray();
            if (missing.Length > 0)
            {
                throw new FileNotFoundException(Loc.F("Ps4.Core.SelectedMissing", string.Join(", ", missing.Take(5))));
            }
        }

        job.Save("extraction_manifest.json", records.Values.OrderBy(r => r.Path, StringComparer.Ordinal));
        if (pkg.Header.content_type == ContentType.AC)
        {
            var key = Ps4DlcMetadata.KeyFromPackage(pkg);
            if (key != null)
            {
                job.Save("dlc_rebuild.json", new Ps4DlcRebuildInfo(pkg.Header.content_id, pkg.ParamSfo?.ParamSfo["TITLE_ID"]?.ToString() ?? string.Empty, key,
                    System.IO.Path.GetFullPath(request.Input)));
                job.Log(Loc.T("Ps4.Core.DlcKeySaved"), Ps4LogLevel.Ok);
            }
            else
            {
                job.Log(Loc.T("Ps4.Core.DlcNoLicense"), Ps4LogLevel.Warn);
            }
        }

        if (request.Verify)
        {
            job.Log(Loc.T("Ps4.Core.VerifyingExtract"));
            meter = new Ps4RateMeter();
            total = records.Values.Sum(r => r.Bytes);
            completed = 0;
            var verifyPhase = Loc.T("Ps4.Phase.VerifyExtract");
            foreach (var record in records.Values)
            {
                using var file = File.OpenRead(Ps4Files.SafeChild(image, record.Path));
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int length;
                while ((length = file.Read(buffer)) != 0)
                {
                    job.Token.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, length);
                    completed += length;
                    if (watch.Elapsed.TotalSeconds - lastReport >= 0.2)
                    {
                        var rate = meter.Update(completed, total);
                        job.Progress(new Ps4Progress(verifyPhase, record.Path, 80 + 19 * completed / (double)Math.Max(1, total), completed, total, rate.Speed, rate.Eta));
                        lastReport = watch.Elapsed.TotalSeconds;
                    }
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (file.Length != record.Bytes || actual != record.Sha256)
                {
                    throw new IOException(Loc.F("Ps4.Core.HashMismatch", record.Path));
                }
            }
        }

        job.Log(Loc.F("Ps4.Core.ExtractDone", records.Count), Ps4LogLevel.Ok);
        job.Progress(new Ps4Progress(Loc.T("Ps4.Phase.Done"), image, 100, completed, completed, 0, 0));
        return new Ps4JobResult(job.Folder, null, null, job.LogPath, request.Verify, records.Count, records.Values.Sum(r => r.Bytes), "completed");
    }
}
