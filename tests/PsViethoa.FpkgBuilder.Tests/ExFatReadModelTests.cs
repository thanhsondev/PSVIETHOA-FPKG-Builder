using System.Security.Cryptography;
using System.Text;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Kiểm thử mô hình đọc dùng chung (nền của cả ổ ảo Dokan lẫn FUSE) trên fixture exFAT thật — chạy ở mọi hệ điều
/// hành vì không cần driver. Các phép kiểm tra ở đây phải khớp với ExFatVirtualFileSystemTests: cùng một ngữ nghĩa,
/// chỉ khác lối vào.
/// </summary>
public sealed class ExFatReadModelTests : IDisposable
{
    private const string Wrapper = "PPSA-TEST";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-model-" + Guid.NewGuid().ToString("N"));

    public ExFatReadModelTests() => Directory.CreateDirectory(_root);

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
            using var input = new System.IO.Compression.GZipStream(File.OpenRead(source), System.IO.Compression.CompressionMode.Decompress);
            using var output = File.Create(target);
            input.CopyTo(output);
        }

        return target;
    }

    private (ExFatImage Image, ExFatReadModel Model) Open(
        bool hideJunk = true,
        IReadOnlyDictionary<string, byte[]>? overlays = null,
        string? wrapper = Wrapper)
    {
        var image = ExFatImage.Open(Fixture("small-bare.exfat"));
        return (image, new ExFatReadModel(image, image.Root, wrapper, hideJunk, overlays, "TESTVOL"));
    }

    private static byte[] ReadAll(ExFatReadModel model, ExFatNode node, int chunk)
    {
        using var handle = model.OpenHandle(node);
        using var output = new MemoryStream();
        var buffer = new byte[chunk];
        long offset = 0;
        while (true)
        {
            Assert.Equal(ExFatReadStatus.Success, model.Read(handle, buffer, 0, buffer.Length, offset, out var read));
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            offset += read;
        }

        return output.ToArray();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("\\a\\b", "a/b")]
    [InlineData("a/b/", "a/b")]
    [InlineData("//a//b/", "a/b")]
    public void NormalizePath_IsSeparatorAgnostic(string input, string expected) =>
        Assert.Equal(expected, ExFatReadModel.NormalizePath(input));

    [Fact]
    public void Root_ExposesOnlyTheWrapperFolder()
    {
        var (image, model) = Open();
        using (image)
        {
            Assert.Equal(Wrapper, model.SourceRelativePath);
            var only = Assert.Single(model.ListChildren(model.Root));
            Assert.Equal(Wrapper, only.Name);
            Assert.True(only.IsDirectory);

            var app = model.ListChildren(model.LookupPath("/" + Wrapper)!).Select(n => n.Name).ToList();
            Assert.Contains("sce_sys", app, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("eboot.bin", app, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WithoutWrapper_AppRootIsTheMountRoot()
    {
        var (image, model) = Open(wrapper: null);
        using (image)
        {
            Assert.Equal(string.Empty, model.SourceRelativePath);
            var names = model.ListChildren(model.Root).Select(n => n.Name).ToList();
            Assert.Contains("eboot.bin", names, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Wrapper, names);
        }
    }

    [Fact]
    public void JunkFiles_AreHiddenByDefault()
    {
        var (image, model) = Open(hideJunk: true);
        using (image)
        {
            var small = model.ListChildren(model.LookupPath($"/{Wrapper}/data/small")!).Select(n => n.Name).ToList();
            Assert.Equal(31, small.Count);
            Assert.DoesNotContain(small, n => n.StartsWith("._", StringComparison.Ordinal));
        }

        var (image2, visible) = Open(hideJunk: false);
        using (image2)
        {
            var small = visible.ListChildren(visible.LookupPath($"/{Wrapper}/data/small")!).Select(n => n.Name).ToList();
            Assert.True(small.Count > 31);
            Assert.Contains(small, n => JunkFileFinder.IsJunkFileName(n));
        }
    }

    [Fact]
    public void Read_MatchesImageBytesForContiguousAndFragmentedFiles()
    {
        var (image, model) = Open();
        using (image)
        {
            foreach (var (relative, chunk) in new[] { ("data/random.bin", 7777), ("data/frag_c.bin", 65536), ("eboot.bin", 1 << 20) })
            {
                var expected = image.ReadAllBytes(image.Find(relative)!);
                var node = model.LookupPath($"/{Wrapper}/{relative}")!;
                var actual = ReadAll(model, node, chunk);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(actual)));
            }
        }
    }

    [Fact]
    public void Read_HandlesOffsetsPastEndAndInvalidArguments()
    {
        var (image, model) = Open();
        using (image)
        {
            var node = model.LookupPath($"/{Wrapper}/data/random.bin")!;
            var whole = image.ReadAllBytes(image.Find("data/random.bin")!);
            using var handle = model.OpenHandle(node);
            var buffer = new byte[4096];

            Assert.Equal(ExFatReadStatus.Success, model.Read(handle, buffer, 0, buffer.Length, 1000, out var read));
            Assert.Equal(4096, read);
            Assert.Equal(whole.AsSpan(1000, 4096).ToArray(), buffer);

            Assert.Equal(ExFatReadStatus.Success, model.Read(handle, buffer, 0, buffer.Length, whole.Length - 100, out read));
            Assert.Equal(100, read);

            Assert.Equal(ExFatReadStatus.Success, model.Read(handle, buffer, 0, buffer.Length, whole.Length + 5000, out read));
            Assert.Equal(0, read);

            Assert.Equal(ExFatReadStatus.InvalidParameter, model.Read(handle, buffer, 0, buffer.Length, -1, out _));
            Assert.Equal(ExFatReadStatus.InvalidParameter, model.Read(handle, buffer, 4000, 500, 0, out _));
        }
    }

    [Fact]
    public void Read_AtBufferOffset_WritesOnlyTheRequestedWindow()
    {
        var (image, model) = Open();
        using (image)
        {
            var node = model.LookupPath($"/{Wrapper}/data/random.bin")!;
            var whole = image.ReadAllBytes(image.Find("data/random.bin")!);
            using var handle = model.OpenHandle(node);

            var buffer = new byte[256];
            Assert.Equal(ExFatReadStatus.Success, model.Read(handle, buffer, 64, 128, 0, out var read));
            Assert.Equal(128, read);
            Assert.Equal(whole.AsSpan(0, 128).ToArray(), buffer.AsSpan(64, 128).ToArray());
            Assert.All(buffer[..64], b => Assert.Equal(0, b));
            Assert.All(buffer[192..], b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void ReadingADirectory_ReportsIsDirectory()
    {
        var (image, model) = Open();
        using (image)
        {
            var directory = model.LookupPath($"/{Wrapper}/data")!;
            using var handle = model.OpenHandle(directory);
            Assert.Equal(ExFatReadStatus.IsDirectory, model.Read(handle, new byte[16], 0, 16, 0, out _));
        }
    }

    [Fact]
    public void Overlay_ReplacesExistingFileAndAddsMissingOne()
    {
        var replaced = Encoding.UTF8.GetBytes("{\"applicationDrmType\":\"standard\"}");
        var added = Encoding.UTF8.GetBytes("extra");
        var overlays = new Dictionary<string, byte[]>
        {
            ["sce_sys\\param.json"] = replaced,
            ["sce_sys/extra.json"] = added,
        };

        var (image, model) = Open(overlays: overlays);
        using (image)
        {
            var names = model.ListChildren(model.LookupPath($"/{Wrapper}/sce_sys")!).Select(n => n.Name).ToList();
            Assert.Contains("param.json", names, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("extra.json", names, StringComparer.OrdinalIgnoreCase);

            var paramNode = model.LookupPath($"/{Wrapper}/SCE_SYS/PARAM.JSON")!;
            Assert.Equal(replaced.Length, paramNode.Length);
            Assert.Equal(replaced, ReadAll(model, paramNode, 8));
            Assert.Equal(added, ReadAll(model, model.LookupPath($"/{Wrapper}/sce_sys/extra.json")!, 3));

            // Tệp không bị đè vẫn đọc từ ảnh.
            var eboot = image.ReadAllBytes(image.Find("eboot.bin")!);
            Assert.Equal(eboot, ReadAll(model, model.LookupPath($"/{Wrapper}/eboot.bin")!, 10000));
        }
    }

    [Fact]
    public void Lookup_ReturnsNullForMissingPaths()
    {
        var (image, model) = Open();
        using (image)
        {
            Assert.Null(model.LookupPath($"/{Wrapper}/nope.bin"));
            Assert.Null(model.LookupPath($"/{Wrapper}/missing-dir/x"));
            Assert.Null(model.LookupPath($"/{Wrapper}/eboot.bin/child"));
            Assert.NotNull(model.LookupPath(string.Empty));
        }
    }

    [Fact]
    public void VolumeMetadata_IsReadOnlyExFat()
    {
        var (image, model) = Open();
        using (image)
        {
            Assert.Equal("TESTVOL", model.VolumeLabel);
            Assert.Equal(image.VolumeLengthBytes, model.VolumeLengthBytes);
            Assert.Equal("exFAT", ExFatReadModel.FileSystemName);
        }
    }

    /// <summary>Lớp vỏ Dokan phải phơi đúng mô hình bên dưới (không nhân bản trạng thái).</summary>
    [Fact]
    public void DokanShim_SharesTheSameModel()
    {
        var (image, model) = Open();
        using (image)
        {
            var fileSystem = new ExFatVirtualFileSystem(model);
            Assert.Same(model, fileSystem.Model);
            Assert.Equal(model.SourceRelativePath, fileSystem.SourceRelativePath);
            Assert.Equal(model.WrapperName, fileSystem.WrapperName);
        }
    }
}
