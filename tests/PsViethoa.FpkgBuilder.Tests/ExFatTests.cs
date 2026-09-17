using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Kiểm thử bộ đọc exFAT với ảnh mẫu (8 MB, nén gzip trong Fixtures/).</summary>
public sealed class ExFatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-exfat-" + Guid.NewGuid().ToString("N"));

    public ExFatTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private string Fixture(string name)
    {
        var target = Path.Combine(_root, name);
        if (!File.Exists(target))
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".gz");
            using var input = new GZipStream(File.OpenRead(source), CompressionMode.Decompress);
            using var output = File.Create(target);
            input.CopyTo(output);
        }

        return target;
    }

    [Theory]
    [InlineData("small-bare.exfat", 0L)]
    [InlineData("small-mbr.exfat", -1L)]
    [InlineData("small-gpt.exfat", -1L)]
    public void Open_DetectsLayouts(string name, long expectedOffset)
    {
        var path = Fixture(name);
        Assert.True(ExFatImage.IsExFatFile(path));
        using var image = ExFatImage.Open(path);
        Assert.Equal(512, image.BytesPerSector);
        Assert.True(image.ClusterSize >= 512);
        if (expectedOffset >= 0)
        {
            Assert.Equal(expectedOffset, image.VolumeOffset);
        }
        else
        {
            Assert.True(image.VolumeOffset > 0, "ảnh có bảng phân vùng phải có offset > 0");
        }

        Assert.False(string.IsNullOrEmpty(image.VolumeLabel));
        Assert.Equal(SourceKind.ExFatImage, SourceLocator.Detect(path));
    }

    [Fact]
    public void IsExFatFile_RejectsOtherFiles()
    {
        var text = Path.Combine(_root, "not-an-image.exfat");
        File.WriteAllText(text, "hello");
        Assert.False(ExFatImage.IsExFatFile(text));
        Assert.Equal(SourceKind.None, SourceLocator.Detect(Path.Combine(_root, "missing.exfat")));
    }

    [Fact]
    public void Enumerate_ReadsNamesSizesAndUnicode()
    {
        using var image = ExFatImage.Open(Fixture("small-bare.exfat"));
        var root = image.Enumerate(image.Root).ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        Assert.True(root["sce_sys"].IsDirectory);
        Assert.True(root["data"].IsDirectory);
        Assert.Equal(64 * 1024, root["eboot.bin"].Length);

        var data = image.Enumerate(root["data"]).ToDictionary(e => e.Name, StringComparer.Ordinal);
        Assert.Equal(1024 * 1024 + 123, data["random.bin"].Length);
        Assert.Equal(1024 * 1024, data["text.bin"].Length);
        Assert.Contains(data.Keys, k => k.StartsWith("Tệp tên rất dài", StringComparison.Ordinal) && k.EndsWith("0123456789.txt", StringComparison.Ordinal));

        var small = image.Enumerate(data["small"]).ToDictionary(e => e.Name, StringComparer.Ordinal);
        Assert.Equal(0, small["empty.txt"].Length);
        Assert.True(small["empty.txt"].IsEmpty);
        Assert.Equal(31, small.Keys.Count(k => !k.StartsWith("._", StringComparison.Ordinal)));
    }

    [Fact]
    public void OpenRead_ReturnsExactBytes()
    {
        using var image = ExFatImage.Open(Fixture("small-bare.exfat"));
        var data = image.Find("data")!;

        var text = image.ReadAllBytes(image.Find("data/text.bin")!);
        Assert.Equal(1024 * 1024, text.Length);
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal((byte)(i % 2 == 0 ? 'A' : 'B'), text[i]));
        Assert.Equal((byte)'B', text[^1]);

        var fragmented = image.ReadAllBytes(image.Find("data/frag_c.bin")!);
        Assert.Equal(900 * 1024, fragmented.Length);
        Assert.True(fragmented.All(b => b == (byte)'c'));

        var small = image.ReadAllBytes(image.Find("data/small/file_05.txt")!);
        Assert.Equal(string.Concat(Enumerable.Repeat("small 5\n", 6)), Encoding.UTF8.GetString(small));

        var unicode = image.Walk(data).First(e => e.Name.StartsWith("Tệp tên", StringComparison.Ordinal));
        Assert.Equal(string.Concat(Enumerable.Repeat("xin chào\n", 100)), Encoding.UTF8.GetString(image.ReadAllBytes(unicode)));

        // Đọc từng mảnh nhỏ và seek phải cho cùng kết quả với đọc một lần.
        var random = image.Find("data/random.bin")!;
        var whole = image.ReadAllBytes(random);
        using var stream = image.OpenRead(random);
        var buffer = new byte[7777];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        Assert.Equal(Convert.ToHexString(SHA256.HashData(whole)), Convert.ToHexString(hash.GetHashAndReset()));
        stream.Seek(whole.Length - 10, SeekOrigin.Begin);
        var tail = new byte[10];
        stream.ReadExactly(tail);
        Assert.Equal(whole[^10..], tail);
    }

    [Fact]
    public void SourceLocator_FindsNestedAppRoot()
    {
        var bare = SourceLocator.Resolve(Fixture("small-bare.exfat"));
        Assert.True(bare.IsExFat);
        Assert.Equal(string.Empty, bare.AppRootInImage);

        var nested = SourceLocator.Resolve(Fixture("small-mbr.exfat"));
        Assert.Equal("PPSA00002", nested.AppRootInImage);

        Assert.Throws<InvalidDataException>(() => SourceLocator.Resolve(Fixture("small-gpt.exfat")));
    }

    [Fact]
    public void MetadataAndStats_ReadFromImage()
    {
        var path = Fixture("small-mbr.exfat");
        var metadata = MetadataReader.Read(path, CancellationToken.None);
        Assert.True(metadata.IsExFat);
        Assert.True(metadata.HasSceSys);
        Assert.True(metadata.HasParamJson);
        Assert.True(metadata.HasEboot);
        Assert.Equal("UP9000-PPSA00002_00-PSVIETHOAEXFAT01", metadata.ContentId);
        Assert.Equal("exFAT Test App", metadata.Title);
        Assert.Equal("PPSA00002", metadata.AppRootInImage);
        Assert.NotNull(metadata.IconBytes);
        Assert.Equal(0x89, metadata.IconBytes![0]);

        var stats = FolderScanner.Scan(path, CancellationToken.None);
        Assert.True(stats.FileCount >= 38, $"files={stats.FileCount}");
        Assert.True(stats.TotalBytes > 3_000_000);

        var junk = JunkFileFinder.Find(path, CancellationToken.None);
        Assert.True(junk.Count >= 4, $"junk={junk.Count}");
        Assert.Contains(junk, j => j.Path.EndsWith("/.DS_Store", StringComparison.Ordinal));
        Assert.Contains(junk, j => j.Path.EndsWith("/Thumbs.db", StringComparison.Ordinal));
        Assert.Contains(junk, j => j.Path.EndsWith("/._random.bin", StringComparison.Ordinal));
        Assert.Contains(junk, j => j.IsDirectory && j.Path.EndsWith("/__MACOSX", StringComparison.Ordinal));
        Assert.All(junk, j => Assert.True(j.IsReadOnly));
        var (deleted, errors) = JunkFileFinder.Delete(junk);
        Assert.Equal(0, deleted);
        Assert.Equal(junk.Count, errors.Count);
    }

    [Fact]
    public void Extractor_CopiesFilesAndSkipsJunk()
    {
        using var image = ExFatImage.Open(Fixture("small-mbr.exfat"));
        var source = SourceLocator.Resolve(image.Path);
        var root = SourceLocator.ResolveAppRoot(image, source);
        var plan = ExFatExtractor.CreatePlan(image, root, skipJunk: true, CancellationToken.None);
        var junk = JunkFileFinder.Find(image.Path, CancellationToken.None);
        Assert.Equal(junk.Count, plan.SkippedJunk);
        Assert.DoesNotContain(plan.Files, f => f.Name == ".DS_Store" || f.Name == "Thumbs.db" || f.Name.StartsWith("._", StringComparison.Ordinal));

        var destination = Path.Combine(_root, "extract");
        long lastDone = -1;
        ExFatExtractor.Extract(image, plan, destination, (done, total) => { Assert.True(done >= lastDone); lastDone = done; Assert.True(done <= total); }, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "sce_sys", "param.json")));
        Assert.True(File.Exists(Path.Combine(destination, "eboot.bin")));
        Assert.False(File.Exists(Path.Combine(destination, ".DS_Store")));
        Assert.False(Directory.Exists(Path.Combine(destination, "__MACOSX")));
        Assert.Empty(Directory.EnumerateFiles(destination, "._*", SearchOption.AllDirectories));
        Assert.Equal(0, new FileInfo(Path.Combine(destination, "data", "small", "empty.txt")).Length);

        var extracted = File.ReadAllBytes(Path.Combine(destination, "data", "random.bin"));
        var original = image.ReadAllBytes(image.Find("PPSA00002/data/random.bin")!);
        Assert.Equal(original, extracted);
        Assert.Equal(plan.TotalBytes, lastDone);
    }

    [Fact]
    public async Task Engine_BuildsPackageFromExFatImageByExtraction()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var request = new BuildRequest
        {
            SourcePath = Fixture("small-bare.exfat"),
            OutputFolder = Path.Combine(_root, "out"),
            TemporaryFolder = Path.Combine(_root, "tmp"),
            ContentId = "UP9000-PPSA00002_00-PSVIETHOAEXFAT01",
            Title = "exFAT Test App",
            ExFat = ExFatStrategy.Extract,
            KrakenBackend = KrakenBackendKind.BuiltIn,
            KrakenLevel = 2,
            PreventSleep = false,
        };

        Assert.Empty(BuildPreparer.Validate(request));

        var log = new List<LogEntry>();
        var progress = new List<BuildProgress>();
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, new SynchronousProgress(progress.Add), CancellationToken.None);

        Assert.True(File.Exists(outcome.OutputPath));
        Assert.Equal("FullDebug", outcome.Verification.ContainerType);
        Assert.Equal("UP9000-PPSA00002_00-PSVIETHOAEXFAT01", outcome.Verification.ContentId);
        Assert.Contains(progress, p => p.Phase == PhaseCatalog.Extract.Name);
        // Thư mục trích tạm phải được dọn — và nếu thư mục tạm do lần tạo gói này sinh ra thì nó cũng bị xoá luôn.
        AssertNoStagingLeftBehind(Path.Combine(_root, "tmp"));
        Assert.Contains(log, e => e.Message.Contains("skipped", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("bỏ qua", StringComparison.OrdinalIgnoreCase));

        // Bản giải nén là của công cụ: sửa thẳng trên đó, không dựng gương bằng liên kết (chạy được cả khi thư mục tạm ở ổ
        // exFAT/FAT32 không tạo được liên kết). Trước đây mọi lượt tạo gói từ bản giải nén đều dựng gương.
        Assert.DoesNotContain(log, e => e.Message.Contains("mirror-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Engine_BuildsPfsV3PackageWithShuffleAnalysis()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var request = new BuildRequest
        {
            SourcePath = Fixture("small-bare.exfat"),
            OutputFolder = Path.Combine(_root, "out-v3"),
            TemporaryFolder = Path.Combine(_root, "tmp-v3"),
            ContentId = "UP9000-PPSA00002_00-PSVIETHOAEXFAT01",
            ExFat = ExFatStrategy.Extract,
            KrakenBackend = KrakenBackendKind.BuiltIn,
            PreventSleep = false,
        };
        BuildPresets.Apply(BuildPresets.Maximum, request);

        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);
        Assert.True(File.Exists(outcome.OutputPath));
        Assert.Equal("FullDebug", outcome.Verification.ContainerType);
        Assert.Contains(log, e => e.Message.Contains("PFS compression format: v3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log, e => e.Message.Contains("shuffle analysis=enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Engine_BuildsPackageFromExFatImageByMounting()
    {
        if (!BuildEngine.KeysAvailable || !ExFatMounter.IsAvailable)
        {
            return;
        }

        var request = new BuildRequest
        {
            SourcePath = Fixture("small-mbr.exfat"),
            OutputFolder = Path.Combine(_root, "out-mount"),
            TemporaryFolder = Path.Combine(_root, "tmp-mount"),
            ContentId = "UP9000-PPSA00002_00-PSVIETHOAEXFAT01",
            ExFat = ExFatStrategy.Mount,
            KrakenBackend = KrakenBackendKind.BuiltIn,
            KrakenLevel = 2,
            PreventSleep = false,
        };

        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);
        Assert.True(File.Exists(outcome.OutputPath));
        Assert.Equal("FullDebug", outcome.Verification.ContainerType);
        Assert.Contains(log, e => e.Message.Contains("/Volumes/", StringComparison.Ordinal));
    }

    /// <summary>
    /// macOS gắn ảnh .exfat bằng hdiutil, ổ đó chỉ đọc nên không ẩn được tệp. Thư mục gương chỉ ĐỌC từ ổ rồi ghi thay đổi
    /// vào thư mục tạm, nên vẫn bỏ được tệp và sửa được param.json mà KHÔNG phải giải nén cả ảnh.
    /// </summary>
    [Fact]
    public async Task Engine_MountsAndStillPatchesWithoutExtracting()
    {
        if (!BuildEngine.KeysAvailable || !ExFatMounter.IsAvailable)
        {
            return;
        }

        var request = new BuildRequest
        {
            SourcePath = Fixture("small-mbr.exfat"),
            OutputFolder = Path.Combine(_root, "out-mount-patch"),
            TemporaryFolder = Path.Combine(_root, "tmp-mount-patch"),
            ContentId = "UP9000-PPSA00002_00-PSVIETHOAEXFAT02",
            ExFat = ExFatStrategy.Auto,
            KrakenBackend = KrakenBackendKind.BuiltIn,
            KrakenLevel = 2,
            PreventSleep = false,

            // Đúng những tuỳ chọn trước đây ép phải giải nén trên macOS.
            ForceStandardDrm = true,
            ClearVersionFileUri = true,
            ClearPlayGoAttributes = true,
            RemovePlayGoFiles = true,
        };

        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        Assert.True(File.Exists(outcome.OutputPath));
        Assert.Contains(log, e => e.Message.Contains("/Volumes/", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(request.TemporaryFolder, "exfat-*"));
    }

    [Fact]
    public void RealSample_WhenPresent_IsRecognised()
    {
        var sample = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PPSA27625.exfat");
        if (!File.Exists(sample))
        {
            return;
        }

        var source = SourceLocator.Resolve(sample);
        Assert.Equal(string.Empty, source.AppRootInImage);
        var metadata = MetadataReader.Read(sample, CancellationToken.None);
        Assert.True(metadata.HasParamJson);
        Assert.True(ContentIdHelper.IsValid(metadata.ContentId), metadata.ContentId);
        Assert.Contains("PPSA27625", metadata.ContentId);
    }

    [Fact]
    public void RealSample_PPSA06438_WhenPresent_IsRecognised()
    {
        var sample = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PPSA06438.exfat");
        if (!File.Exists(sample))
        {
            return;
        }

        var source = SourceLocator.Resolve(sample);
        Assert.Equal(string.Empty, source.AppRootInImage);
        Assert.Equal("06438LetsBu", source.VolumeLabel);

        var metadata = MetadataReader.Read(sample, CancellationToken.None);
        Assert.Equal("EP3999-PPSA06438_00-0000000000000000", metadata.ContentId);
        Assert.Equal("Let's Build A Zoo", metadata.Title);
        Assert.Equal("01.001.140", metadata.Version);
        Assert.Equal(4, metadata.SdkMajor);
        Assert.True(metadata.HasEboot);
        Assert.NotNull(metadata.IconBytes);

        var stats = FolderScanner.Scan(sample, CancellationToken.None);
        Assert.Equal(64, stats.FileCount);
        Assert.Equal(852_932_949, stats.TotalBytes);
    }

    private sealed class SynchronousProgress : IProgress<BuildProgress>
    {
        private readonly Action<BuildProgress> _handler;

        public SynchronousProgress(Action<BuildProgress> handler)
        {
            _handler = handler;
        }

        public void Report(BuildProgress value) => _handler(value);
    }

    /// <summary>
    /// Sau khi tạo gói xong không được còn thư mục trích tạm "exfat-*". Thư mục tạm do lần tạo gói sinh ra và cuối
    /// cùng vẫn rỗng thì bị xoá hẳn, nên "thư mục tạm không tồn tại" cũng là đạt (còn chặt hơn).
    /// </summary>
    private static void AssertNoStagingLeftBehind(string temporaryFolder)
    {
        if (!Directory.Exists(temporaryFolder))
        {
            return;
        }

        Assert.Empty(Directory.GetDirectories(temporaryFolder, "exfat-*"));
    }
}
