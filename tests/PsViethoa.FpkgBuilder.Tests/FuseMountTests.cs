using System.Security.Cryptography;
using System.Text;
using PsViethoa.FpkgBuilder.Core.ExFat;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Kiểm thử gắn ảnh thật bằng FUSE trên Linux. Máy không có libfuse3/fusermount3 (hoặc không phải Linux) thì các
/// phép kiểm tra tự bỏ qua chứ không báo hỏng — đúng như CI không có FUSE.
/// </summary>
public sealed class FuseMountTests : IDisposable
{
    private const string Wrapper = "PPSA-FUSE";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-fuse-" + Guid.NewGuid().ToString("N"));

    public FuseMountTests() => Directory.CreateDirectory(_root);

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

    private static bool Available => FuseImageMounter.IsAvailable;

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

    private FuseMount MountFixture(
        ExFatImage image,
        IReadOnlyDictionary<string, byte[]>? overlays = null,
        bool hideJunk = true,
        IReadOnlyCollection<string>? hiddenPaths = null) =>
        FuseImageMounter.Mount(image, image.Root, Wrapper, hideJunk, overlays, hiddenPaths, "FUSETEST", CancellationToken.None);

    [Fact]
    public void Availability_IsLinuxOnly()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.False(FuseImageMounter.IsAvailable);
            Assert.Null(FuseImageMounter.FindFusermount());
        }
    }

    [Fact]
    public void Mount_ExposesTheAppFolderAndHidesJunk()
    {
        if (!Available)
        {
            return;
        }

        using var image = ExFatImage.Open(Fixture("small-bare.exfat"));
        using var mount = MountFixture(image);

        Assert.True(Directory.Exists(mount.MountPoint));
        Assert.True(Directory.Exists(mount.SourceFolder));
        Assert.Equal(Path.Combine(mount.MountPoint, Wrapper), mount.SourceFolder);

        // Thư mục ứng dụng phải thấy được và không chứa tệp rác.
        Assert.True(Directory.Exists(Path.Combine(mount.SourceFolder, "sce_sys")));
        Assert.True(File.Exists(Path.Combine(mount.SourceFolder, "eboot.bin")));

        var all = Directory.EnumerateFileSystemEntries(mount.SourceFolder, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToList();
        Assert.DoesNotContain(all, n => n!.StartsWith("._", StringComparison.Ordinal));
        Assert.DoesNotContain(all, n => string.Equals(n, ".DS_Store", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, n => string.Equals(n, "__MACOSX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mount_ReadsBytesIdenticalToTheImage()
    {
        if (!Available)
        {
            return;
        }

        using var image = ExFatImage.Open(Fixture("small-bare.exfat"));
        using var mount = MountFixture(image);

        foreach (var relative in new[] { "eboot.bin", "data/random.bin", "data/frag_c.bin", "data/small/file_05.txt" })
        {
            var expected = image.ReadAllBytes(image.Find(relative)!);
            var actual = File.ReadAllBytes(Path.Combine(mount.SourceFolder, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(actual)));
        }
    }

    [Fact]
    public void Mount_AppliesOverlaysWithoutTouchingTheImage()
    {
        if (!Available)
        {
            return;
        }

        var replaced = Encoding.UTF8.GetBytes("{\"applicationDrmType\":\"standard\"}");
        var overlays = new Dictionary<string, byte[]> { ["sce_sys/param.json"] = replaced };

        var path = Fixture("small-bare.exfat");
        var before = File.GetLastWriteTimeUtc(path);
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        using (var image = ExFatImage.Open(path))
        using (var mount = MountFixture(image, overlays))
        {
            var seen = File.ReadAllBytes(Path.Combine(mount.SourceFolder, "sce_sys", "param.json"));
            Assert.Equal(replaced, seen);
        }

        // Ảnh gốc không được thay đổi.
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Mount_IsReadOnly()
    {
        if (!Available)
        {
            return;
        }

        using var image = ExFatImage.Open(Fixture("small-bare.exfat"));
        using var mount = MountFixture(image);

        Assert.ThrowsAny<Exception>(() => File.WriteAllText(Path.Combine(mount.SourceFolder, "new.txt"), "nope"));
        Assert.ThrowsAny<Exception>(() => File.AppendAllText(Path.Combine(mount.SourceFolder, "eboot.bin"), "nope"));
        Assert.ThrowsAny<Exception>(() => File.Delete(Path.Combine(mount.SourceFolder, "eboot.bin")));
    }

    [Fact]
    public void Dispose_UnmountsAndRemovesTheMountPoint()
    {
        if (!Available)
        {
            return;
        }

        using var image = ExFatImage.Open(Fixture("small-bare.exfat"));
        string mountPoint;
        using (var mount = MountFixture(image))
        {
            mountPoint = mount.MountPoint;
            Assert.True(Directory.Exists(mountPoint));
        }

        Assert.False(IsMounted(mountPoint));
        Assert.False(Directory.Exists(mountPoint));
    }

    /// <summary>
    /// Đường dẫn bị ẩn hẳn (tàn dư AMPR emu) phải biến mất khỏi ổ FUSE y như trên ổ ảo Dokan — hai backend dùng
    /// chung mô hình đọc nên khả năng phải khớp nhau.
    /// </summary>
    [Fact]
    public void Mount_HidesExplicitlyHiddenPaths()
    {
        if (!Available)
        {
            return;
        }

        using var image = ExFatImage.Open(Fixture("small-bare.exfat"));
        using var mount = MountFixture(image, hiddenPaths: new[] { "eboot.bin", "data/random.bin" });

        Assert.False(File.Exists(Path.Combine(mount.SourceFolder, "eboot.bin")));
        Assert.False(File.Exists(Path.Combine(mount.SourceFolder, "data", "random.bin")));

        // Những tệp khác không bị ảnh hưởng.
        Assert.True(Directory.Exists(Path.Combine(mount.SourceFolder, "sce_sys")));
        Assert.True(File.Exists(Path.Combine(mount.SourceFolder, "data", "text.bin")));
    }

    private static bool IsMounted(string mountPoint)
    {
        try
        {
            return File.ReadLines("/proc/self/mounts")
                .Select(line => line.Split(' '))
                .Any(fields => fields.Length > 1 && fields[1] == mountPoint);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
