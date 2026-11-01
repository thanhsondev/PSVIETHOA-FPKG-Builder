using System.Security.Cryptography;
using System.Xml.Linq;
using PsViethoa.FpkgBuilder.Core.Ps4;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// PS4 PKG (cơ chế PSVIETHOA STUDIO): param.sfo, phiên bản, đường dẫn an toàn, nhận nguồn, GP4 base / DLC / gộp (chế độ chỉ tạo GP4,
/// không cần engine) và — khi máy có orbis-pub-cmd chạy được (Windows hoặc Wine có i386) — đóng thật base → xả lại → update → gộp.
/// Dữ liệu game đều tổng hợp; nguồn không bao giờ bị sửa.
/// </summary>
public sealed class Ps4Tests : IDisposable
{
    private const string ContentId = "UP0000-CUSA00001_00-PSVIETHOATEST000";
    private const string Passcode = Ps4Defaults.Passcode;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-ps4-" + Guid.NewGuid().ToString("N"));

    public Ps4Tests() => Directory.CreateDirectory(_root);

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

    // ── Dữ liệu tổng hợp ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>param.sfo PS4 thật (độ dài tối đa đúng chuẩn, FORMAT=obs) dựng bằng LibOrbisPkg — engine kiểm tra kỹ định dạng.</summary>
    private static byte[] Sfo(string category, string appVer, string version, string title = "PSVIETHOA Test", string contentId = ContentId)
    {
        var values = new List<LibOrbisPkg.SFO.Value>
        {
            new LibOrbisPkg.SFO.IntegerValue("APP_TYPE", 1),
            new LibOrbisPkg.SFO.Utf8Value("APP_VER", appVer, 8),
            new LibOrbisPkg.SFO.IntegerValue("ATTRIBUTE", 0),
            new LibOrbisPkg.SFO.Utf8Value("CATEGORY", category, 4),
            new LibOrbisPkg.SFO.Utf8Value("CONTENT_ID", contentId, 48),
            new LibOrbisPkg.SFO.IntegerValue("DOWNLOAD_DATA_SIZE", 0),
            new LibOrbisPkg.SFO.Utf8Value("FORMAT", "obs", 4),
            new LibOrbisPkg.SFO.IntegerValue("PARENTAL_LEVEL", 1),
            new LibOrbisPkg.SFO.IntegerValue("SYSTEM_VER", 0x05050000),
            new LibOrbisPkg.SFO.Utf8Value("TITLE", title, 128),
            new LibOrbisPkg.SFO.Utf8Value("TITLE_ID", contentId.Substring(7, 9), 12),
            new LibOrbisPkg.SFO.Utf8Value("VERSION", version, 8),
        };
        if (category == "ac")
        {
            values.RemoveAll(v => v.Name is "APP_TYPE" or "APP_VER" or "DOWNLOAD_DATA_SIZE" or "PARENTAL_LEVEL" or "SYSTEM_VER");
        }

        return new LibOrbisPkg.SFO.ParamSfo { Values = values }.Serialize();
    }

    /// <summary>nptitle.dat (magic NPTD, Title ID) và npbind.dat (magic D294A018) có header đúng như gói thật.</summary>
    private static byte[] NpTitle(string titleId = "CUSA00001_00")
    {
        var data = new byte[160];
        "NPTD"u8.CopyTo(data);
        data[7] = 0x80;
        System.Text.Encoding.ASCII.GetBytes(titleId).CopyTo(data, 0x10);
        new Random(40).NextBytes(data.AsSpan(0x20));
        return data;
    }

    /// <summary>PNG 512×512 hợp lệ (icon0.png của PS4), một màu theo <paramref name="seed"/>.</summary>
    private static byte[] Png(int seed)
    {
        const int size = 512;
        var raw = new byte[size * (1 + size * 3)];
        var color = new[] { (byte)(seed * 40), (byte)(120 + seed), (byte)200 };
        for (var y = 0; y < size; y++)
        {
            var row = y * (1 + size * 3);
            for (var x = 0; x < size; x++)
            {
                raw[row + 1 + x * 3] = color[0];
                raw[row + 2 + x * 3] = color[1];
                raw[row + 3 + x * 3] = color[2];
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] body)
        {
            var header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
            png.Write(header);
            var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
            png.Write(typed);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
            png.Write(crc);
        }

        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), size);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return png.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] NpBind()
    {
        var data = new byte[532];
        new byte[] { 0xD2, 0x94, 0xA0, 0x18, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x02, 0x14, 0, 0, 0, 0, 0, 0, 0x01, 0x80, 0, 0, 0, 0, 0, 0, 0, 1 }.CopyTo(data, 0);
        return data;
    }

    private static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Thư mục game kiểu đã xả: &lt;root&gt;/image0 (+ metadata). Trả về thư mục cha (chứa image0).</summary>
    private string Game(string name, string category, string appVer, string version, bool keystone = true, Action<string>? extra = null)
    {
        var parent = Path.Combine(_root, name);
        var image = Path.Combine(parent, "image0");
        Write(Path.Combine(image, "eboot.bin"), Bytes(4096, 1));
        Write(Path.Combine(image, "data", "a.bin"), Bytes(70_000, 2));
        Write(Path.Combine(image, "data", "b.bin"), Bytes(1_000, 3));
        Write(Path.Combine(image, "sce_sys", "param.sfo"), Sfo(category, appVer, version));
        Write(Path.Combine(image, "sce_sys", "icon0.png"), Png(4));
        Write(Path.Combine(image, "sce_sys", "nptitle.dat"), NpTitle());
        Write(Path.Combine(image, "sce_sys", "npbind.dat"), NpBind());
        if (keystone)
        {
            Write(Path.Combine(image, "sce_sys", "keystone"), Bytes(96, 7));
        }

        // Metadata engine tự tạo / từ chối: phải bị loại khỏi GP4.
        Write(Path.Combine(image, "sce_sys", "playgo-chunk.dat"), Bytes(64, 8));
        Write(Path.Combine(image, "sce_sys", "license.dat"), Bytes(64, 9));
        Write(Path.Combine(image, "sce_sys", "about", "right.sprx"), Bytes(64, 10));
        Write(Path.Combine(image, "old.gp4"), "<psproject/>"u8.ToArray());
        extra?.Invoke(image);
        return parent;
    }

    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) + File.GetLastWriteTimeUtc(f).Ticks);

    private static async Task<Ps4JobResult> Run(Ps4JobRequest request) =>
        await Ps4JobRunner.RunAsync(request, null, null, new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);

    /// <summary>Chạy engine thật; lỗi thì kèm các dòng [Error]/[Warn] của job.log để biết engine từ chối gì.</summary>
    private static async Task<Ps4JobResult> RunEngine(Ps4JobRequest request)
    {
        try
        {
            return await Run(request);
        }
        catch (Ps4JobFailureException ex)
        {
            var log = Path.Combine(ex.Folder, "job.log");
            var lines = File.Exists(log) ? File.ReadLines(log).Where(l => l.Contains("[ERROR]") || l.Contains("[WARN]")).TakeLast(15) : [];
            throw new Xunit.Sdk.XunitException(ex.Message + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }
    }

    private static Dictionary<string, XElement> Gp4Files(string project) =>
        XDocument.Load(project).Root!.Element("files")!.Elements("file").ToDictionary(f => (string)f.Attribute("targ_path")!, f => f);

    private static Dictionary<string, string> SfoValues(string path)
    {
        var document = Ps4SfoDocument.Parse(File.ReadAllBytes(path));
        return document.Entries.ToDictionary(e => e.Key, e => e.Display);
    }

    // ── param.sfo ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sfo_RoundTrip_GrowsStringsAndKeepsOthers()
    {
        var bytes = Sfo("gd", "01.00", "01.00");
        var document = Ps4SfoDocument.Parse(bytes);
        Assert.Equal("gd", document["CATEGORY"]!.Display);
        Assert.Equal("0x00000000", document["ATTRIBUTE"]!.Display);
        Assert.Equal("1", document["APP_TYPE"]!.Display);

        var longTitle = new string('Đ', 100);
        document.SetText("TITLE", longTitle);
        document.SetText("ATTRIBUTE", "0x0000000C");
        Assert.True(document.Remove("APP_TYPE"));
        document.Add("TITLE_15", Ps4SfoDocument.Utf8, "Tên tiếng Việt");
        var again = Ps4SfoDocument.Parse(document.ToBytes());
        Assert.Equal(longTitle, again["TITLE"]!.Display);
        Assert.Equal(0, again["TITLE"]!.MaxLength % 4);
        Assert.Equal("0x0000000C", again["ATTRIBUTE"]!.Display);
        Assert.Null(again["APP_TYPE"]);
        Assert.Equal("Tên tiếng Việt", again["TITLE_15"]!.Display);
        Assert.Equal(again.Entries.Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal), again.Entries.Select(e => e.Key));
    }

    [Fact]
    public void Sfo_ValidatesValuesAndKeys()
    {
        Assert.Null(Ps4SfoDocument.Validate(Ps4SfoDocument.Integer, "0x10", "ATTRIBUTE"));
        Assert.NotNull(Ps4SfoDocument.Validate(Ps4SfoDocument.Integer, "abc", "ATTRIBUTE"));
        Assert.NotNull(Ps4SfoDocument.Validate(Ps4SfoDocument.Utf8, "a\0b", "TITLE"));
        Assert.NotNull(Ps4SfoDocument.Validate(Ps4SfoDocument.Binary, "ABC", "BIN"));
        var document = Ps4SfoDocument.Parse(Sfo("gd", "01.00", "01.00"));
        Assert.Throws<ArgumentException>(() => document.Add("bad key", Ps4SfoDocument.Utf8, "x"));
        Assert.Throws<ArgumentException>(() => document.Add("TITLE", Ps4SfoDocument.Utf8, "x"));
        Assert.Throws<InvalidDataException>(() => Ps4SfoDocument.Parse(new byte[40]));
    }

    // ── Phiên bản / đường dẫn ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.03", "01.03")]
    [InlineData("01.03", "01.03")]
    [InlineData(" 9.99 ", "09.99")]
    public void AppVersion_Normalizes(string input, string expected) => Assert.Equal(expected, Ps4AppVersion.Normalize(input));

    [Fact]
    public void AppVersion_StepsAndRejects()
    {
        Assert.Equal("01.10", Ps4AppVersion.Step("01.09", 1));
        Assert.Equal("00.99", Ps4AppVersion.Step("01.00", -1));
        Assert.Equal("00.00", Ps4AppVersion.Step("00.00", -1));
        Assert.Equal("99.99", Ps4AppVersion.Step("99.99", 1));
        Assert.Throws<ArgumentException>(() => Ps4AppVersion.Normalize("1.3"));
        Assert.Throws<ArgumentException>(() => Ps4AppVersion.Normalize("v1.00"));
        Assert.False(Ps4AppVersion.TryParse("100.00", out _));
    }

    [Theory]
    [InlineData("../evil.bin")]
    [InlineData("/abs/path")]
    [InlineData("a/./b")]
    [InlineData("CON.txt")]
    [InlineData("dir/lpt1")]
    [InlineData("name.")]
    [InlineData("C:/x")]
    [InlineData("a?b")]
    public void SafeChild_RejectsDangerousPaths(string relative) =>
        Assert.Throws<InvalidDataException>(() => Ps4Files.SafeChild(Path.GetTempPath(), relative));

    [Fact]
    public void SafeChild_AcceptsNormalPaths()
    {
        var root = Path.GetTempPath();
        var full = Ps4Files.SafeChild(root, "sce_sys/param.sfo");
        Assert.StartsWith(Path.GetFullPath(root), full);
        Assert.EndsWith(Path.Combine("sce_sys", "param.sfo"), full);
    }

    // ── Nhận nguồn ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveImage_AcceptsParentImage0OrDirectFolder()
    {
        var parent = Game("CUSA00001-base", "gd", "01.00", "01.00");
        var image = Path.Combine(parent, "image0");
        Assert.Equal(image, Ps4ProjectBuilder.ResolveImage(parent));
        Assert.Equal(image, Ps4ProjectBuilder.ResolveImage(image));
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Throws<InvalidDataException>(() => Ps4ProjectBuilder.ResolveImage(empty));
        Assert.Throws<DirectoryNotFoundException>(() => Ps4ProjectBuilder.ResolveImage(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void DetectVersion_PatchPrefersOuterUpdateSfo()
    {
        // Update đã xả: SFO bên trong PFS là của base (gd 01.00), Sc0 là SFO ngoài của update (gp 01.05).
        var parent = Game("CUSA00001-update", "gd", "01.00", "01.00");
        Write(Path.Combine(parent, "Sc0", "param.sfo"), Sfo("gp", "01.05", "01.00"));
        var detected = Ps4ProjectBuilder.DetectVersion(parent, Ps4JobKind.Patch);
        Assert.Equal("01.05", detected.Version);
        Assert.EndsWith(Path.Combine("Sc0", "param.sfo"), detected.SfoPath);
        Assert.Equal("01.00", Ps4ProjectBuilder.DetectVersion(parent, Ps4JobKind.Base).Version);
        var info = Ps4ProjectBuilder.InspectFolder(parent, Ps4JobKind.Patch);
        Assert.Equal("CUSA00001", info["TITLE_ID"]);
        Assert.Equal("01.05", info["APP_VER"]);
    }

    // ── GP4 (không cần engine) ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gp4_Base_MatchesStudioRulesAndLeavesSourceUntouched()
    {
        var parent = Game("CUSA00001-base", "gd", "01.00", "01.00");
        var before = Snapshot(parent);
        var output = Path.Combine(_root, "out");
        var result = await Run(new Ps4JobRequest(Ps4JobKind.Base, parent, output, Version: "01.02", ProjectOnly: true));

        Assert.Equal("project_ready", result.Status);
        Assert.Equal("Base.gp4", Path.GetFileName(result.Project));
        var document = XDocument.Load(result.Project!);
        var volume = document.Root!.Element("volume")!;
        Assert.Equal("pkg_ps4_app", (string?)volume.Element("volume_type"));
        var package = volume.Element("package")!;
        Assert.Equal(ContentId, (string?)package.Attribute("content_id"));
        Assert.Equal(Passcode, (string?)package.Attribute("passcode"));
        Assert.Equal("digital50", (string?)package.Attribute("storage_type"));
        Assert.Equal("1", (string?)volume.Element("chunk_info")!.Attribute("chunk_count"));

        var files = Gp4Files(result.Project!);
        foreach (var expected in new[] { "eboot.bin", "data/a.bin", "data/b.bin", "sce_sys/param.sfo", "sce_sys/icon0.png", "sce_sys/keystone", "PSVIETHOA.COM.txt" })
        {
            Assert.True(files.ContainsKey(expected), expected);
        }

        foreach (var excluded in new[] { "sce_sys/playgo-chunk.dat", "sce_sys/license.dat", "sce_sys/about/right.sprx", "old.gp4" })
        {
            Assert.False(files.ContainsKey(excluded), excluded);
        }

        Assert.All(files.Values, f => Assert.Equal("enable", (string?)f.Attribute("pfs_compression")));

        // param.sfo là bản sao trong thư mục tác vụ: CATEGORY gd, APP_VER theo ô phiên bản; bản nguồn giữ nguyên.
        var staged = (string)files["sce_sys/param.sfo"].Attribute("orig_path")!;
        Assert.StartsWith(Path.GetFullPath(output), staged);
        var values = SfoValues(staged);
        Assert.Equal("gd", values["CATEGORY"]);
        Assert.Equal("01.02", values["APP_VER"]);
        Assert.Equal("01.00", SfoValues(Path.Combine(parent, "image0", "sce_sys", "param.sfo"))["APP_VER"]);
        Assert.Equal(before, Snapshot(parent));
    }

    [Fact]
    public async Task Gp4_Base_WithoutKeystone_Fails()
    {
        var parent = Game("CUSA00001-nokey", "gd", "01.00", "01.00", keystone: false);
        var error = await Assert.ThrowsAsync<Ps4JobFailureException>(() => Run(new Ps4JobRequest(Ps4JobKind.Base, parent, Path.Combine(_root, "out"), ProjectOnly: true)));
        Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.True(Directory.Exists(error.Folder));
    }

    [Fact]
    public async Task Gp4_RejectsOutputInsideSourceAndBadPasscode()
    {
        var parent = Game("CUSA00001-base", "gd", "01.00", "01.00");
        await Assert.ThrowsAsync<Ps4JobFailureException>(() => Run(new Ps4JobRequest(Ps4JobKind.Base, parent, Path.Combine(parent, "image0", "out"), ProjectOnly: true)));
        await Assert.ThrowsAsync<Ps4JobFailureException>(() => Run(new Ps4JobRequest(Ps4JobKind.Base, parent, Path.Combine(_root, "out"), Passcode: "short", ProjectOnly: true)));
    }

    [Fact]
    public async Task Gp4_Dlc_UsesEntitlementKeyAndAcVolume()
    {
        var parent = Game("CUSA00001-dlc", "ac", "01.00", "01.00", keystone: false);
        var key = "00112233445566778899aabbccddeeff";
        var result = await Run(new Ps4JobRequest(Ps4JobKind.Dlc, parent, Path.Combine(_root, "out"), ProjectOnly: true, EntitlementKey: key.ToUpperInvariant()));
        var volume = XDocument.Load(result.Project!).Root!.Element("volume")!;
        Assert.Equal("pkg_ps4_ac_data", (string?)volume.Element("volume_type"));
        Assert.Equal(key, (string?)volume.Element("package")!.Attribute("entitlement_key"));
        Assert.Null(volume.Element("chunk_info"));
        Assert.Equal("ac", SfoValues((string)Gp4Files(result.Project!)["sce_sys/param.sfo"].Attribute("orig_path")!)["CATEGORY"]);
    }

    [Fact]
    public async Task Gp4_Dlc_RejectsNonDlcSourceAndBadKey()
    {
        var game = Game("CUSA00001-base", "gd", "01.00", "01.00");
        await Assert.ThrowsAsync<Ps4JobFailureException>(() => Run(new Ps4JobRequest(Ps4JobKind.Dlc, game, Path.Combine(_root, "out"), ProjectOnly: true, EntitlementKey: "00112233445566778899aabbccddeeff")));
        var dlc = Game("CUSA00001-dlc", "ac", "01.00", "01.00", keystone: false);
        await Assert.ThrowsAsync<Ps4JobFailureException>(() => Run(new Ps4JobRequest(Ps4JobKind.Dlc, dlc, Path.Combine(_root, "out"), ProjectOnly: true, EntitlementKey: "nothex")));
        await Assert.ThrowsAsync<Ps4JobFailureException>(() => Run(new Ps4JobRequest(Ps4JobKind.Dlc, dlc, Path.Combine(_root, "out"), ProjectOnly: true)));
    }

    [Fact]
    public async Task Gp4_MergeFolders_UpdateWinsKeystoneFromBase()
    {
        var baseFolder = Game("CUSA00001-base", "gd", "01.00", "01.00", extra: image => Write(Path.Combine(image, "data", "base-only.bin"), Bytes(300, 23)));
        var update = Game("CUSA00001-update", "gp", "01.07", "01.00", keystone: false, extra: image =>
        {
            Write(Path.Combine(image, "data", "a.bin"), Bytes(1234, 20));
            Write(Path.Combine(image, "data", "c.bin"), Bytes(100, 21));
            Write(Path.Combine(image, "sce_sys", "keystone"), Bytes(96, 22));
        });
        var baseBefore = Snapshot(baseFolder);
        var updateBefore = Snapshot(update);
        var result = await Run(new Ps4JobRequest(Ps4JobKind.Merge, update, Path.Combine(_root, "out"), baseFolder, ProjectOnly: true));

        Assert.Equal("Merged.gp4", Path.GetFileName(result.Project));
        Assert.Equal("pkg_ps4_app", (string?)XDocument.Load(result.Project!).Root!.Element("volume")!.Element("volume_type"));
        var files = Gp4Files(result.Project!);
        string Source(string target) => (string)files[target].Attribute("orig_path")!;
        Assert.StartsWith(Path.Combine(update, "image0"), Source("data/a.bin"));
        Assert.StartsWith(Path.Combine(update, "image0"), Source("data/c.bin"));
        Assert.StartsWith(Path.Combine(update, "image0"), Source("data/b.bin"));
        Assert.StartsWith(Path.Combine(baseFolder, "image0"), Source("data/base-only.bin"));
        Assert.StartsWith(Path.Combine(baseFolder, "image0"), Source("sce_sys/keystone"));
        var values = SfoValues(Source("sce_sys/param.sfo"));
        Assert.Equal("gd", values["CATEGORY"]);
        Assert.Equal("01.00", values["APP_VER"]);
        Assert.Equal("01.07", values["VERSION"]);
        Assert.Equal(baseBefore, Snapshot(baseFolder));
        Assert.Equal(updateBefore, Snapshot(update));
    }

    [Fact]
    public async Task Gp4_Merge_RejectsOtherGameAndLowerUpdate()
    {
        var baseFolder = Game("base", "gd", "01.00", "01.00");
        var other = Path.Combine(_root, "other");
        Write(Path.Combine(other, "image0", "eboot.bin"), Bytes(100, 1));
        Write(Path.Combine(other, "image0", "sce_sys", "param.sfo"), Sfo("gp", "01.05", "01.00", contentId: "UP0000-CUSA00002_00-PSVIETHOATEST000"));
        await Assert.ThrowsAsync<Ps4JobFailureException>(() => Run(new Ps4JobRequest(Ps4JobKind.Merge, other, Path.Combine(_root, "out"), baseFolder, ProjectOnly: true)));
        var notUpdate = Game("notupdate", "gd", "01.05", "01.00");
        await Assert.ThrowsAsync<Ps4JobFailureException>(() => Run(new Ps4JobRequest(Ps4JobKind.Merge, notUpdate, Path.Combine(_root, "out"), baseFolder, ProjectOnly: true)));
    }

    [Fact]
    public void Extract_RejectsNonPkg()
    {
        var file = Path.Combine(_root, "fake.pkg");
        Write(file, Bytes(8192, 3));
        Assert.Throws<InvalidDataException>(() => Ps4PackageReader.Inspect(file));
        Assert.Null(Ps4PackageReader.ExtractFolderName(file));
    }

    // ── Engine ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Engine_BundledBinariesMatchKnownHashes()
    {
        var tools = Ps4Engine.FindTools();
        Assert.NotNull(tools);
        string Hash(string name) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(tools!, name)))).ToLowerInvariant();
        Assert.Equal(Ps4Engine.OriginalHash, Hash(Ps4Engine.ToolFileName));
        Assert.Equal(Ps4Engine.FastHash, Hash(Ps4Engine.FastToolFileName));
        Assert.True(File.Exists(Path.Combine(tools!, "orbis-pub-prx.dll")));
        Assert.True(File.Exists(Path.Combine(tools!, "ext", "sc.exe")));
    }

    /// <summary>
    /// Đóng thật bằng orbis-pub-cmd: base từ thư mục tổng hợp → xả lại khớp SHA-256 → update tham chiếu base đó → gộp base + update.
    /// Bỏ qua khi máy không chạy được engine (thiếu Wine có thư viện 32-bit / Rosetta).
    /// </summary>
    [Fact]
    public async Task Engine_RealBuild_BaseExtractUpdateMerge()
    {
        if (Ps4Engine.Resolve(out _) == null)
        {
            return;
        }

        var parent = Game("CUSA00001-base", "gd", "01.00", "01.00");
        var output = Path.Combine(_root, "built");
        var baseResult = await RunEngine(new Ps4JobRequest(Ps4JobKind.Base, parent, output));
        Assert.Equal("completed", baseResult.Status);
        Assert.True(File.Exists(baseResult.Package));
        Assert.Equal(output, baseResult.Directory);
        var info = Ps4PackageReader.Inspect(baseResult.Package!);
        Assert.Equal(ContentId, info.ContentId);
        Assert.Equal("gd", info.Category);
        Assert.True(info.PasscodeMatches);

        var listed = Ps4PackageReader.ListFiles(baseResult.Package!).Select(f => f.Path).ToHashSet();
        Assert.Contains("eboot.bin", listed);
        Assert.Contains("data/a.bin", listed);
        Assert.Contains("PSVIETHOA.COM.txt", listed);

        var extracted = await Run(new Ps4JobRequest(Ps4JobKind.Extract, baseResult.Package!, Path.Combine(_root, "extracted"), Verify: true));
        var image = Path.Combine(extracted.Directory, "image0");
        foreach (var relative in new[] { "eboot.bin", "data/a.bin", "data/b.bin", "sce_sys/keystone" })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(parent, "image0", relative)), File.ReadAllBytes(Path.Combine(image, relative)));
        }

        var update = Game("CUSA00001-update", "gp", "01.01", "01.00", keystone: false, extra: folder => Write(Path.Combine(folder, "data", "a.bin"), Bytes(5000, 30)));
        var patch = await RunEngine(new Ps4JobRequest(Ps4JobKind.Patch, update, Path.Combine(_root, "patch"), baseResult.Package!));
        Assert.Equal("gp", Ps4PackageReader.Inspect(patch.Package!).Category);
        Assert.Equal("01.01", Ps4PackageReader.Inspect(patch.Package!).Version);

        var merged = await RunEngine(new Ps4JobRequest(Ps4JobKind.Merge, patch.Package!, Path.Combine(_root, "merged"), baseResult.Package!));
        var mergedInfo = Ps4PackageReader.Inspect(merged.Package!);
        Assert.Equal("gd", mergedInfo.Category);
        Assert.Equal("01.00", mergedInfo.Version);
        Assert.Equal("01.01", mergedInfo.MasterVersion);
        var mergedExtract = await Run(new Ps4JobRequest(Ps4JobKind.Extract, merged.Package!, Path.Combine(_root, "merged-x")));
        Assert.Equal(Bytes(5000, 30), File.ReadAllBytes(Path.Combine(mergedExtract.Directory, "image0", "data", "a.bin")));
        Assert.Equal(Bytes(1_000, 3), File.ReadAllBytes(Path.Combine(mergedExtract.Directory, "image0", "data", "b.bin")));
    }
}
