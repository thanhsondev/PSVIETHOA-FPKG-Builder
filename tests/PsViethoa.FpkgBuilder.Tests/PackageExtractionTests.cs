using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Tạo một thư mục ứng dụng mẫu và (khi có khoá) build gói PLAINTEXT_NOAUTH / Native một lần cho cả lớp kiểm thử.</summary>
public sealed class PackageExtractionFixture : IDisposable
{
    public const string ContentId = "UP9000-PPSA00004_00-PSVIETHOAEXTRACT";
    public const string Title = "PSVIETHOA Extract Test";

    private readonly object _gate = new();
    private readonly Dictionary<OuterImageMode, string?> _packages = new();

    public PackageExtractionFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "psviethoa-extract-" + Guid.NewGuid().ToString("N"));
        Source = Path.Combine(Root, "app");
        Directory.CreateDirectory(Path.Combine(Source, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(Source, "data", "levels", "deep"));
        Directory.CreateDirectory(Path.Combine(Source, "data", "small"));

        File.WriteAllText(Path.Combine(Source, "sce_sys", "param.json"),
            "{\"applicationCategoryType\":0,\"attribute\":0,\"contentId\":\"" + ContentId + "\",\"contentVersion\":\"01.002.003\",\"masterVersion\":\"01.02\"," +
            "\"downloadDataSize\":0,\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"" + Title + "\"},\"vi-VN\":{\"titleName\":\"Kiểm thử giải nén\"}}," +
            "\"requiredSystemSoftwareVersion\":\"0x0450000000000000\",\"sdkVersion\":\"0x0450000000000000\",\"titleId\":\"PPSA00004\",\"pubtools\":{\"tool_version\":\"9.020.001\"},\"versionFileUri\":\"\"}");
        File.WriteAllBytes(Path.Combine(Source, "sce_sys", "icon0.png"), ReadFixtureIcon());

        var random = new Random(20260913);
        File.WriteAllBytes(Path.Combine(Source, "eboot.bin"), Pattern(1024 * 1024 + 4096, random));

        // > 1 MiB dễ nén, 1 MiB ngẫu nhiên, vài tệp nhỏ và một tệp rỗng ở nhiều cấp thư mục.
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("PSVIETHOA extract test line\n", 60000)));
        File.WriteAllBytes(Path.Combine(Source, "data", "levels", "compressible.txt"), text);
        var noise = new byte[1024 * 1024 + 777];
        random.NextBytes(noise);
        File.WriteAllBytes(Path.Combine(Source, "data", "levels", "deep", "random.bin"), noise);
        File.WriteAllBytes(Path.Combine(Source, "data", "levels", "deep", "empty.bin"), Array.Empty<byte>());
        for (var i = 0; i < 12; i++)
        {
            File.WriteAllText(Path.Combine(Source, "data", "small", $"file_{i:00}.txt"), string.Concat(Enumerable.Repeat($"small {i}\n", i + 1)));
        }

        // Thư viện chỉ giữ tên tệp ASCII trong ảnh PFS (ký tự khác bị thay bằng '?'), nên dùng tên có khoảng trắng thay vì dấu.
        File.WriteAllText(Path.Combine(Source, "data", "Tep tieng Viet.txt"), "xin chào\n");
    }

    public string Root { get; }

    public string Source { get; }

    /// <summary>Đường dẫn gói đã build cho chế độ ảnh, hoặc null khi thư viện không có khoá.</summary>
    public string? GetPackage(OuterImageMode mode)
    {
        lock (_gate)
        {
            if (_packages.TryGetValue(mode, out var cached))
            {
                return cached;
            }

            if (!BuildEngine.KeysAvailable)
            {
                _packages[mode] = null;
                return null;
            }

            var request = new BuildRequest
            {
                SourcePath = Source,
                OutputFolder = Path.Combine(Root, "out-" + mode),
                TemporaryFolder = Path.Combine(Root, "tmp-" + mode),
                ContentId = ContentId,
                Title = Title,
                ImageMode = mode,
                KrakenBackend = KrakenBackendKind.BuiltIn,
                KrakenLevel = 2,
                PreventSleep = false,
            };

            var outcome = new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None).GetAwaiter().GetResult();
            _packages[mode] = outcome.OutputPath;
            return outcome.OutputPath;
        }
    }

    public IReadOnlyDictionary<string, byte[]> SourceFiles() =>
        Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Source, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private static byte[] Pattern(int length, Random random)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i % 251 == 0 ? random.Next(256) : (i * 7) & 0xFF);
        }

        return data;
    }

    /// <summary>icon0.png thật lấy từ ảnh exFAT mẫu (bộ mã hoá DDS của thư viện cần PNG hợp lệ).</summary>
    private static byte[] ReadFixtureIcon()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "small-bare.exfat.gz");
        var temporary = Path.Combine(Path.GetTempPath(), "psviethoa-icon-" + Guid.NewGuid().ToString("N") + ".exfat");
        try
        {
            using (var input = new GZipStream(File.OpenRead(source), CompressionMode.Decompress))
            using (var output = File.Create(temporary))
            {
                input.CopyTo(output);
            }

            using var image = ExFatImage.Open(temporary);
            return image.ReadAllBytes(image.Find("sce_sys/icon0.png")!);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
            }
        }
    }
}

public sealed class PackageExtractionTests : IClassFixture<PackageExtractionFixture>
{
    private static readonly string Passcode = new('0', 32);
    private readonly PackageExtractionFixture _fixture;

    public PackageExtractionTests(PackageExtractionFixture fixture)
    {
        _fixture = fixture;
    }

    private string TempFolder(string name)
    {
        var path = Path.Combine(_fixture.Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Inspect_ReadsHeaderParamsAndIcon()
    {
        var package = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (package == null)
        {
            return;
        }

        var info = PackageInspector.Inspect(package, Passcode, CancellationToken.None);
        Assert.Equal(PackageContainerKind.FullDebug, info.Kind);
        Assert.Equal(PackageImageMode.PlaintextNoAuth, info.ImageMode);
        Assert.True(info.CanExtract);
        Assert.Null(info.ExtractBlockedReason);
        Assert.Equal(PackageExtractionFixture.ContentId, info.ContentId);
        Assert.Equal(PackageExtractionFixture.Title, info.Title);
        Assert.NotNull(info.Fih);
        Assert.Equal(0, info.Fih!.SignedByte);
        Assert.False(info.Fih.IsOfficial);
        Assert.Equal(PackageVerifier.PlaintextMarker, info.Fih.Marker);
        Assert.True(info.Fih.DataRegionBlockCount > 0);
        Assert.True(info.Fih.InnerImageSize > (ulong)info.Fih.DataRegionBlockCount * 65536);
        Assert.NotNull(info.Map);
        Assert.True(info.Map!.CntSize > 0);
        Assert.NotNull(info.Cnt);
        Assert.Equal(PackageExtractionFixture.ContentId, info.Cnt!.ContentId);
        Assert.True(info.Cnt.Entries.Count >= 10);
        Assert.Contains("param.json", info.SceSysFiles);
        Assert.Contains("icon0.png", info.SceSysFiles);
        Assert.NotNull(info.IconBytes);
        Assert.Equal(0x89, info.IconBytes![0]);

        Assert.NotNull(info.Params);
        var p = info.Params!;
        Assert.Equal("01.002.003", p.ContentVersion);
        Assert.Equal("01.02", p.MasterVersion);
        Assert.Equal(4, p.SdkMajor);
        Assert.Equal("0x0450000000000000", p.RequiredSystemSoftwareVersion);
        Assert.Equal(0, p.ApplicationCategoryType);
        Assert.Contains(p.Fields, f => f.Key == "contentId" && f.Value == PackageExtractionFixture.ContentId);
        Assert.Contains(p.Fields, f => f.Key == "titleName (en-US)" && f.Value == PackageExtractionFixture.Title);
        Assert.Contains(p.Fields, f => f.Key == "applicationCategoryType" && f.Value == "0");
        Assert.Contains(p.Fields, f => f.Key.StartsWith("pubtools.", StringComparison.Ordinal));

        var rows = PackageInspector.GeneralRows(info);
        Assert.Contains(rows, r => r.Key == "Content ID" && r.Value == PackageExtractionFixture.ContentId);
        Assert.Contains(PackageExtractionFixture.ContentId, PackageInspector.Describe(info));
    }

    [Fact]
    public void Reader_ListsEverySourceFileWithSizes()
    {
        var package = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (package == null)
        {
            return;
        }

        using var reader = PackageReader.Open(package, Passcode, _fixture.Root, CancellationToken.None);
        Assert.Equal(PackageImageMode.PlaintextNoAuth, reader.ImageMode);
        Assert.True(reader.SuperblockOffset > 0);
        Assert.True(reader.LogicalImageSize > reader.SuperblockOffset);

        var listed = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var source = _fixture.SourceFiles();
        foreach (var (path, bytes) in source)
        {
            if (path == "sce_sys/param.json" || path == "sce_sys/icon0.png")
            {
                // param.json/icon0.png nằm trong CNT; ảnh trong có thể chứa bản do trình tạo sinh ra.
                continue;
            }

            Assert.True(listed.ContainsKey(path), "thiếu trong gói: " + path);
            Assert.Equal(bytes.Length, listed[path].Size);
            Assert.Equal(PackageEntryKind.File, listed[path].Kind);
        }

        Assert.Contains(reader.Entries, e => e.IsDirectory && e.Path == "data/levels/deep");
        Assert.Contains(reader.Entries, e => e.Path == "data/Tep tieng Viet.txt");
        Assert.Equal(reader.FileCount, listed.Count);
        Assert.Equal(listed.Values.Sum(e => e.Size), reader.TotalBytes);
        Assert.All(reader.Entries, e => Assert.DoesNotContain("uroot", e.Path));
    }

    [Fact]
    public void Extract_SelectionAndAll_ReproduceBytes()
    {
        var package = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (package == null)
        {
            return;
        }

        var source = _fixture.SourceFiles();
        using var reader = PackageReader.Open(package, Passcode, _fixture.Root, CancellationToken.None);

        var wanted = new[] { "data/levels/deep/random.bin", "data/levels/compressible.txt", "data/small/file_07.txt", "data/levels/deep/empty.bin" };
        var selection = reader.Entries.Where(e => wanted.Contains(e.Path)).ToList();
        Assert.Equal(wanted.Length, selection.Count);

        var partial = TempFolder("partial");
        var snapshots = new List<ExtractProgress>();
        var result = reader.Extract(selection, partial, new SyncProgress(snapshots.Add), CancellationToken.None, parallelism: 3);
        Assert.Equal(wanted.Length, result.FileCount);
        Assert.Empty(result.Warnings);
        Assert.Equal(source["data/levels/deep/random.bin"].Length + source["data/levels/compressible.txt"].Length + source["data/small/file_07.txt"].Length, result.TotalBytes);
        Assert.True(snapshots.Count >= 2);
        Assert.Equal(result.TotalBytes, snapshots[^1].DoneBytes);
        Assert.Equal(wanted.Length, snapshots[^1].DoneFiles);
        foreach (var path in wanted)
        {
            Assert.Equal(source[path], File.ReadAllBytes(Path.Combine(partial, path)));
        }

        Assert.False(File.Exists(Path.Combine(partial, "eboot.bin")));

        var all = TempFolder("all");
        var everything = reader.ExtractAll(all, null, CancellationToken.None);
        Assert.Equal(reader.FileCount, everything.FileCount);
        foreach (var (path, bytes) in source)
        {
            if (path == "sce_sys/param.json" || path == "sce_sys/icon0.png")
            {
                continue;
            }

            var extracted = Path.Combine(all, path);
            Assert.True(File.Exists(extracted), "chưa giải nén: " + path);
            Assert.Equal(bytes, File.ReadAllBytes(extracted));
        }

        // Đọc thẳng vào bộ nhớ cũng phải khớp.
        var random = reader.Entries.Single(e => e.Path == "data/levels/deep/random.bin");
        Assert.Equal(SHA256.HashData(source["data/levels/deep/random.bin"]), SHA256.HashData(reader.ReadAllBytes(random)));
    }

    [Fact]
    public void Extract_CanBeCancelled()
    {
        var package = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (package == null)
        {
            return;
        }

        using var reader = PackageReader.Open(package, Passcode, _fixture.Root, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.ExtractAll(TempFolder("cancel"), null, cancellation.Token));
    }

    [Fact]
    public void NativePackage_DecryptsAndExtractsIdentically()
    {
        var package = _fixture.GetPackage(OuterImageMode.Native);
        if (package == null)
        {
            return;
        }

        var info = PackageInspector.Inspect(package, Passcode, CancellationToken.None);
        Assert.Equal(PackageImageMode.Native, info.ImageMode);
        Assert.Null(info.Fih!.Marker);

        var log = new List<string>();
        var temporary = TempFolder("native-tmp");
        string outputFolder;
        using (var reader = PackageReader.Open(package, Passcode, temporary, CancellationToken.None, log.Add))
        {
            Assert.Equal(PackageImageMode.Native, reader.ImageMode);
            Assert.Single(Directory.GetFiles(temporary, ".psviethoa-outer-*.tmp"));
            Assert.NotEmpty(log);

            outputFolder = TempFolder("native-out");
            var result = reader.ExtractAll(outputFolder, null, CancellationToken.None, parallelism: 2);
            Assert.Equal(reader.FileCount, result.FileCount);
        }

        Assert.Empty(Directory.GetFiles(temporary, ".psviethoa-outer-*.tmp"));
        foreach (var (path, bytes) in _fixture.SourceFiles())
        {
            if (path == "sce_sys/param.json" || path == "sce_sys/icon0.png")
            {
                continue;
            }

            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(outputFolder, path)));
        }
    }

    [Fact]
    public void ExportSceSys_ProducesRebuildableSonyLayout()
    {
        var package = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (package == null)
        {
            return;
        }

        var folder = TempFolder("sony");
        using (var reader = PackageReader.Open(package, Passcode, null, CancellationToken.None, null))
        {
            reader.ExtractAll(folder, null, CancellationToken.None);
        }

        var merged = PackageReader.ExportSceSys(package, folder, Passcode, CancellationToken.None);
        Assert.Contains("sce_sys/param.json", merged);
        Assert.Contains("sce_sys/icon0.png", merged);
        Assert.DoesNotContain(merged, f => Path.GetFileName(f).StartsWith('.'));
        Assert.DoesNotContain(merged, f => Path.GetFileName(f).StartsWith("entry-", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(folder, "cnt")));

        // Thư mục kết quả phải là nguồn hợp lệ để tạo gói lại: có sce_sys/param.json với đúng Content ID.
        Assert.Equal(SourceKind.Folder, SourceLocator.Detect(folder));
        var metadata = MetadataReader.Read(folder, CancellationToken.None);
        Assert.True(metadata.HasParamJson);
        Assert.Equal(PackageExtractionFixture.ContentId, metadata.ContentId);
        Assert.True(File.Exists(Path.Combine(folder, "eboot.bin")));
        Assert.Empty(BuildPreparer.Validate(new BuildRequest
        {
            SourcePath = folder,
            OutputFolder = TempFolder("sony-out"),
            ContentId = metadata.ContentId!,
            KrakenBackend = KrakenBackendKind.BuiltIn,
        }));
    }

    [Theory]
    [InlineData("param.json", true)]
    [InlineData("trophy2/trophy00.ucp", true)]
    [InlineData(".digests", false)]
    [InlineData("uds/.hidden", false)]
    [InlineData("entry-0000040a.bin", false)]
    public void IsSceSysPayload_FiltersInternalCntTables(string relative, bool expected) =>
        Assert.Equal(expected, PackageReader.IsSceSysPayload(relative));

    [Fact]
    public void ExportCntEntries_YieldsOriginalParamJson()
    {
        var package = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (package == null)
        {
            return;
        }

        var folder = TempFolder("cnt");
        var exported = PackageReader.ExportCntEntries(package, folder, Passcode, CancellationToken.None);
        Assert.Contains("param.json", exported);
        Assert.Contains("icon0.png", exported);
        var source = _fixture.SourceFiles();
        Assert.Equal(source["sce_sys/icon0.png"], File.ReadAllBytes(Path.Combine(folder, "icon0.png")));

        // Trình tạo gói chuẩn hoá param.json (thêm ageLevel, pubtools…, đệm chuỗi) nên so sánh theo nội dung:
        // mọi trường gốc phải có mặt với cùng giá trị, tên/Content ID/phiên bản phải khớp.
        var exportedBytes = File.ReadAllBytes(Path.Combine(folder, "param.json"));
        var exportedParams = PackageInspector.ParseParamJson(exportedBytes);
        var original = PackageInspector.ParseParamJson(source["sce_sys/param.json"]);
        Assert.Equal(original.ContentId, exportedParams.ContentId);
        Assert.Equal(original.Title, exportedParams.Title);
        Assert.Equal(original.ContentVersion, exportedParams.ContentVersion);
        Assert.Equal(original.MasterVersion, exportedParams.MasterVersion);
        Assert.Equal(original.SdkVersion, exportedParams.SdkVersion);
        foreach (var field in original.Fields.Where(f => !f.Key.StartsWith("pubtools.", StringComparison.Ordinal)))
        {
            Assert.Contains(exportedParams.Fields, f => f.Key == field.Key && f.Value == field.Value);
        }

        // Đọc trực tiếp từ vùng CNT khi Inspect cũng phải cho đúng nội dung (byte-đúng với entry đã xuất).
        var info = PackageInspector.Inspect(package, Passcode, CancellationToken.None);
        Assert.Equal(exportedBytes, info.ParamJsonBytes);

        var si = PackageReader.ExportSiEntries(package, Path.Combine(folder, "si"), CancellationToken.None);
        Assert.Equal(info.HasSupplement, si.Count > 0);
    }

    [Fact]
    public void GarbageAndRetailFilesAreRejectedClearly()
    {
        var garbage = Path.Combine(TempFolder("garbage"), "not-a-package.pkg");
        var noise = new byte[16384];
        new Random(7).NextBytes(noise);
        File.WriteAllBytes(garbage, noise);
        var ex = Assert.Throws<InvalidDataException>(() => PackageInspector.Inspect(garbage, Passcode, CancellationToken.None));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.ThrowsAny<Exception>(() => PackageReader.Open(garbage, Passcode, null, CancellationToken.None));
        Assert.Throws<FileNotFoundException>(() => PackageInspector.Inspect(Path.Combine(_fixture.Root, "missing.pkg"), Passcode, CancellationToken.None));

        var package = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (package == null)
        {
            return;
        }

        // Giả lập gói retail: chép gói debug rồi đặt signed byte = 0x80 (giá trị thư viện nhận là FullRetail).
        var retail = Path.Combine(TempFolder("retail"), "retail.pkg");
        File.Copy(package, retail);
        using (var stream = new FileStream(retail, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 5;
            stream.WriteByte(0x80);
        }

        var info = PackageInspector.Inspect(retail, Passcode, CancellationToken.None);
        Assert.Equal(PackageContainerKind.FullRetail, info.Kind);
        Assert.False(info.CanExtract);
        Assert.False(string.IsNullOrWhiteSpace(info.ExtractBlockedReason));
        var blocked = Assert.Throws<NotSupportedException>(() => PackageReader.Open(retail, Passcode, null, CancellationToken.None));
        Assert.Equal(info.ExtractBlockedReason, blocked.Message);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("data/../../x")]
    [InlineData("/abs/path")]
    [InlineData("data/./x")]
    [InlineData("data\\..\\evil")]
    [InlineData(".")]
    [InlineData("")]
    public void SafeTarget_RejectsTraversal(string relative)
    {
        var root = TempFolder("safe");
        Assert.Throws<InvalidDataException>(() => PackageReader.SafeTarget(root, relative));
        Assert.StartsWith(root, PackageReader.SafeTarget(root, "data/levels/x.bin"));
    }

    [Fact]
    public void SafeTarget_SanitizesCharactersInvalidOnThisSystem()
    {
        var root = TempFolder("sanitize");
        var clean = PackageReader.SafeTarget(root, "data/levels/x.bin", out var sanitized);
        Assert.False(sanitized);
        Assert.Equal(Path.Combine(root, "data", "levels", "x.bin"), clean);

        // ':' bị thay ở mọi hệ điều hành (ADS trên Windows); '?' chỉ khi hệ tệp không cho phép (Windows).
        var stream = PackageReader.SafeTarget(root, "data/con:stream", out sanitized);
        Assert.True(sanitized);
        Assert.Equal(Path.Combine(root, "data", "con_stream"), stream);

        var question = PackageReader.SafeTarget(root, "data/T?p.txt", out sanitized);
        Assert.Equal(OperatingSystem.IsWindows(), sanitized);
        Assert.StartsWith(Path.Combine(root, "data") + Path.DirectorySeparatorChar, question);
    }

    [Fact]
    public void Passcode_IsValidatedBeforeTheLibraryTouchesAnything()
    {
        PackageReader.ValidatePasscode(new string('0', 32));
        Assert.Throws<ArgumentException>(() => PackageReader.ValidatePasscode("short"));
        Assert.Throws<ArgumentException>(() => PackageReader.ValidatePasscode(new string('é', 32)));
        Assert.Throws<ArgumentException>(() => PackageReader.ValidatePasscode(null));

        var plaintext = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (plaintext != null)
        {
            var folder = TempFolder("badpass");
            var ex = Assert.Throws<ArgumentException>(() => PackageReader.ExportCntEntries(plaintext, folder, "0000", CancellationToken.None));
            Assert.Equal(Loc.T("Val.Passcode"), ex.Message);
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }

        var native = _fixture.GetPackage(OuterImageMode.Native);
        if (native != null)
        {
            // Passcode sai định dạng bị chặn trước khi giải mã: không có tệp tạm nào được tạo.
            var temporary = TempFolder("badpass-tmp");
            Assert.Throws<ArgumentException>(() => PackageReader.Open(native, "wrong", temporary, CancellationToken.None));
            Assert.Empty(Directory.GetFileSystemEntries(temporary));
        }
    }

    [Fact]
    public void PlaintextReader_ReleasesPackageHandlesOnDispose()
    {
        var package = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (package == null)
        {
            return;
        }

        using (var reader = PackageReader.Open(package, Passcode, _fixture.Root, CancellationToken.None))
        {
            // Nhiều worker → nhiều kênh giải nén, mỗi kênh một FileStream lên tệp .pkg.
            reader.ExtractAll(TempFolder("handles"), null, CancellationToken.None, parallelism: 4);
        }

        // FileShare.None (LOCK_EX trên Unix, sharing violation trên Windows) chỉ mở được khi reader đã đóng hết handle của nó.
        using var exclusive = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public void Extract_KeepsUnrelatedFilesInTheOutputFolder()
    {
        var package = _fixture.GetPackage(OuterImageMode.PlaintextNoAuth);
        if (package == null)
        {
            return;
        }

        using var reader = PackageReader.Open(package, Passcode, _fixture.Root, CancellationToken.None);
        var folder = TempFolder("keep");
        var keep = Path.Combine(folder, "data", "small", "keep-me.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        File.WriteAllText(keep, "user file");

        var entry = reader.Entries.Single(e => e.Path == "data/small/file_03.txt");
        var result = reader.Extract([entry], folder, null, CancellationToken.None);
        Assert.Equal(1, result.FileCount);
        Assert.Equal("user file", File.ReadAllText(keep));
        Assert.Equal(_fixture.SourceFiles()["data/small/file_03.txt"], File.ReadAllBytes(Path.Combine(folder, "data", "small", "file_03.txt")));
    }

    private sealed class SyncProgress : IProgress<ExtractProgress>
    {
        private readonly Action<ExtractProgress> _handler;

        public SyncProgress(Action<ExtractProgress> handler)
        {
            _handler = handler;
        }

        public void Report(ExtractProgress value) => _handler(value);
    }
}

/// <summary>vi.json, en.json và ko.json phải có đúng cùng một tập khoá.</summary>
public class ExtractionLocalizationTests
{
    [Fact]
    public void LocalizationTables_HaveIdenticalKeys()
    {
        var vi = LoadKeys("vi");
        var en = LoadKeys("en");
        var ko = LoadKeys("ko");
        Assert.NotEmpty(vi);
        Assert.Empty(vi.Except(en));
        Assert.Empty(en.Except(vi));
        Assert.Empty(ko.Except(en));
        Assert.Empty(en.Except(ko));
    }

    private static HashSet<string> LoadKeys(string code)
    {
        using var stream = typeof(Core.Localization.Loc).Assembly.GetManifestResourceStream($"PsViethoa.FpkgBuilder.Core.Localization.{code}.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }
}
