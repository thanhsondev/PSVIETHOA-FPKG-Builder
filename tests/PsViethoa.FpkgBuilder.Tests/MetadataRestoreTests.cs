using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageMagick;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// "Khôi phục metadata online" (engine fpkg-gui 0.6.12) qua một máy chủ giả: XML phiên bản → manifest → mảnh *_sc.pkg (CNT) có
/// param.json, icon0.png, changeinfo.xml và một UCP bị niêm phong. Thư viện ghi vào thư mục nó được trỏ tới nên công cụ phải cho nó
/// làm việc trên bản sao trong thư mục tạm — thư mục game chỉ được đọc.
/// </summary>
public sealed class MetadataRestoreTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP9999-PPSA99993_00-PSVIETHOAMETA000";
    private const string VersionUri = "https://meta.test/PPSA99993-version.xml";
    private const string ManifestUri = "https://meta.test/manifest.json";
    private const string PackageUri = "https://meta.test/" + ContentId + "-A0100-V0100_sc.pkg";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-meta-" + Guid.NewGuid().ToString("N"));

    public MetadataRestoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        MetadataRestore.ClientFactory = () => null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private sealed class FakeServer(IReadOnlyDictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            lock (Requests)
            {
                Requests.Add(uri);
            }

            return Task.FromResult(files.TryGetValue(uri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static JsonObject Param(string title, string sdk) => new()
    {
        ["contentId"] = ContentId,
        ["contentVersion"] = "01.000.000",
        ["masterVersion"] = "01.00",
        ["titleId"] = "PPSA99993",
        ["sdkVersion"] = sdk,
        ["requiredSystemSoftwareVersion"] = sdk,
        ["applicationCategoryType"] = 0,
        ["applicationDrmType"] = "standard",
        ["versionFileUri"] = VersionUri,
        ["localizedParameters"] = new JsonObject { ["defaultLanguage"] = "en-US", ["en-US"] = new JsonObject { ["titleName"] = title } },
    };

    private string MakeSource(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), Param("Dump title", "0x0450000000000000").ToJsonString());
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "keystone"), new byte[96]);
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllBytes(Path.Combine(folder, "data.bin"), Enumerable.Range(0, 4096).Select(i => (byte)(i * 3)).ToArray());
        return folder;
    }

    private static readonly string ChangeInfo = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<changeinfo><changes app_ver=\"01.00\">Restored from the server</changes></changeinfo>\n";

    private static byte[] Icon()
    {
        using var image = new MagickImage(new MagickColor("#2060a0"), 512, 512);
        image.Strip();
        return image.ToByteArray(MagickFormat.Png24);
    }

    /// <summary>Mảnh metadata dạng CNT như _sc.pkg của Sony: header "\x7FCNT", Content ID @64, bảng mục 32 byte (id, cờ, offset, cỡ).</summary>
    private static byte[] MetadataPiece()
    {
        var records = new List<(uint Id, uint Flags, byte[] Data)>
        {
            (8192, 0, Encoding.UTF8.GetBytes(Param("Restored title", "0x0500000000000000").ToJsonString(new JsonSerializerOptions { WriteIndented = true }))),
            (4608, 0, Icon()),
            (4704, 0, Encoding.UTF8.GetBytes(ChangeInfo)),
            // trophy2/trophy00.ucp bị niêm phong trên máy chủ: phải bị bỏ qua (và báo lại), không được thay tệp nào.
            (5248, 0x1C000000, new byte[256]),
        };
        const int table = 0x100;
        var offset = table + records.Count * 32;
        var data = new MemoryStream();
        var buffer = new byte[offset];
        "\u007fCNT"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(16), (uint)records.Count);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(24), table);
        Encoding.ASCII.GetBytes(ContentId).CopyTo(buffer, 64);
        for (var index = 0; index < records.Count; index++)
        {
            var entry = buffer.AsSpan(table + index * 32, 32);
            BinaryPrimitives.WriteUInt32BigEndian(entry, records[index].Id);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], records[index].Flags);
            BinaryPrimitives.WriteUInt32BigEndian(entry[16..], (uint)(offset + data.Length));
            BinaryPrimitives.WriteUInt32BigEndian(entry[20..], (uint)records[index].Data.Length);
            data.Write(records[index].Data);
        }

        return buffer.Concat(data.ToArray()).ToArray();
    }

    /// <summary>Máy chủ giả đủ ba tầng: XML phiên bản (versionFileUri) → manifest JSON (SHA-256 + cỡ) → *_sc.pkg.</summary>
    private static FakeServer Server()
    {
        var piece = MetadataPiece();
        var manifest = new JsonObject
        {
            ["pieces"] = new JsonArray(new JsonObject
            {
                ["url"] = PackageUri,
                ["hashValue"] = Convert.ToHexString(SHA256.HashData(piece)),
                ["fileSize"] = piece.Length,
            }),
        };
        var version = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<titlepatch titleid=\"PPSA99993\"><tag name=\"PPSA99993_00\"><app_tag content_id=\"{ContentId}\">" +
                      $"<package content_ver=\"01.000.000\" manifest_url=\"{ManifestUri}\" /></app_tag></tag></titlepatch>\n";
        return new FakeServer(new Dictionary<string, byte[]>
        {
            [VersionUri] = Encoding.UTF8.GetBytes(version),
            [ManifestUri] = Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            [PackageUri] = piece,
        });
    }

    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(folder, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    [Fact]
    public async Task Prepare_RestoresIntoATemporaryCopyAndLeavesTheGameFolderAlone()
    {
        var source = MakeSource("unit");
        var before = Snapshot(source);
        var server = Server();
        MetadataRestore.ClientFactory = () => new HttpClient(server);
        var temp = Path.Combine(_root, "tmp");
        Directory.CreateDirectory(temp);
        var log = new List<LogEntry>();

        var overlay = await MetadataRestore.PrepareAsync(source, temp, null, log.Add, CancellationToken.None);

        Assert.NotNull(overlay);
        Assert.Equal(["sce_sys/changeinfo/changeinfo.xml", "sce_sys/icon0.png", "sce_sys/param.json"], overlay.Files.Order(StringComparer.Ordinal));
        Assert.Equal([VersionUri, ManifestUri, PackageUri], server.Requests);
        var param = JsonNode.Parse(File.ReadAllBytes(overlay.FullPath("sce_sys/param.json")))!;
        Assert.Equal("Restored title", param["localizedParameters"]!["en-US"]!["titleName"]!.GetValue<string>());
        // Mặc định giữ SDK / firmware / DRM của bản dump.
        Assert.Equal("0x0450000000000000", param["sdkVersion"]!.GetValue<string>());
        Assert.Equal(ChangeInfo, File.ReadAllText(overlay.FullPath("sce_sys/changeinfo/changeinfo.xml")));
        Assert.Contains(log, entry => entry.Message.Contains("trophy2/trophy00.ucp", StringComparison.Ordinal));
        Assert.Contains(log, entry => entry.Level == LogLevel.Success && entry.Message.Contains("icon0.png", StringComparison.Ordinal));
        Assert.Equal(before, Snapshot(source));

        overlay.Dispose();
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
    }

    [Fact]
    public async Task Prepare_OnlyWarnsWhenTheServerHasNothing()
    {
        var source = MakeSource("missing");
        MetadataRestore.ClientFactory = () => new HttpClient(new FakeServer(new Dictionary<string, byte[]>()));
        var temp = Path.Combine(_root, "tmp-missing");
        Directory.CreateDirectory(temp);
        var log = new List<LogEntry>();

        Assert.Null(await MetadataRestore.PrepareAsync(source, temp, null, log.Add, CancellationToken.None));
        Assert.Contains(log, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("404", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
    }

    private BuildRequest Request(string source, string output, bool useSdk) => new()
    {
        UseSonySdk = useSdk,
        SourcePath = source,
        OutputFolder = Path.Combine(_root, output),
        TemporaryFolder = Path.Combine(_root, "tmp-" + output),
        ContentId = ContentId,
        Title = "Metadata test",
        KrakenBackend = KrakenBackendKind.BuiltIn,
        KrakenLevel = -4,
        PreventSleep = false,
        RestoreMetadataOnline = true,
    };

    private void AssertRestored(string package)
    {
        var info = PackageInspector.Inspect(package, Passcode, CancellationToken.None);
        Assert.Equal("Restored title", info.Params?.Title);
        var exported = Path.Combine(_root, "export-" + Guid.NewGuid().ToString("N"));
        var written = PackageReader.ExportSceSys(package, exported, Passcode, CancellationToken.None);
        Assert.Contains("sce_sys/icon0.png", written);

        // changeinfo.xml: engine tích hợp ghi thành mục CNT, Publishing Tools để nó trong ảnh PFS — cả hai đều là tệp đã khôi phục.
        if (!written.Contains("sce_sys/changeinfo/changeinfo.xml"))
        {
            using var reader = PackageReader.Open(package, Passcode, Path.Combine(_root, "read-tmp"), CancellationToken.None);
            var entry = reader.Entries.Single(item => item.Path.Replace('\\', '/').Trim('/') == "sce_sys/changeinfo/changeinfo.xml");
            reader.Extract([entry], exported, null, CancellationToken.None);
        }

        Assert.Contains("Restored from the server", File.ReadAllText(Path.Combine(exported, "sce_sys", "changeinfo", "changeinfo.xml")));
    }

    [Fact]
    public async Task EngineBuild_PacksTheRestoredMetadata()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("engine");
        var before = Snapshot(source);
        MetadataRestore.ClientFactory = () => new HttpClient(Server());

        var outcome = await new BuildEngine().BuildAsync(Request(source, "engine-out", useSdk: false), _ => { }, null, CancellationToken.None);

        AssertRestored(outcome.OutputPath);
        Assert.Equal(before, Snapshot(source));
    }

    [Fact]
    public async Task SdkBuild_PacksTheRestoredMetadata()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("sdk");
        var before = Snapshot(source);
        MetadataRestore.ClientFactory = () => new HttpClient(Server());

        var outcome = await new BuildEngine().BuildAsync(Request(source, "sdk-out", useSdk: true), _ => { }, null, CancellationToken.None);

        AssertRestored(outcome.OutputPath);
        Assert.Equal(before, Snapshot(source));
        var temp = Path.Combine(_root, "tmp-sdk-out");
        Assert.True(!Directory.Exists(temp) || !Directory.EnumerateDirectories(temp, "metadata-*").Any(), "the metadata workspace must be deleted after the build");
    }
}
