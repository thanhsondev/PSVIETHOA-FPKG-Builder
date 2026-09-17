using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DokanNet;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;
using DokanFileAccess = DokanNet.FileAccess;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Kiểm thử lớp hệ thống tệp ảo (Dokan) trên fixture exFAT thật — chạy ở mọi hệ điều hành vì không cần driver:
/// gọi thẳng các callback IDokanOperations như driver sẽ gọi.
/// </summary>
public sealed class ExFatVirtualFileSystemTests : IDisposable
{
    private const string Wrapper = "PPSA-TEST";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-vfs-" + Guid.NewGuid().ToString("N"));

    public ExFatVirtualFileSystemTests()
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

    /// <summary>Ảnh mẫu nén gzip trong Fixtures/ — giải nén ra thư mục tạm như ExFatTests.</summary>
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

    private (ExFatImage Image, ExFatVirtualFileSystem FileSystem) Open(
        bool hideJunk = true,
        IReadOnlyDictionary<string, byte[]>? overlays = null,
        string? wrapper = Wrapper)
    {
        var image = ExFatImage.Open(Fixture("small-bare.exfat"));
        var fileSystem = new ExFatVirtualFileSystem(image, image.Root, wrapper, hideJunk, overlays, "TESTVOL");
        return (image, fileSystem);
    }

    private static IList<FileInformation> List(ExFatVirtualFileSystem fs, string path, string pattern = "*")
    {
        Assert.Equal(NtStatus.Success, fs.FindFilesWithPattern(path, pattern, out var files, new FakeFileInfo()));
        return files;
    }

    private static byte[] ReadAll(ExFatVirtualFileSystem fs, string path, int chunk)
    {
        var info = new FakeFileInfo();
        Assert.Equal(NtStatus.Success, fs.CreateFile(path, DokanFileAccess.GenericRead, FileShare.Read, FileMode.Open, FileOptions.None, FileAttributes.Normal, info));
        Assert.NotNull(info.Context);
        Assert.Equal(NtStatus.Success, fs.GetFileInformation(path, out var meta, info));

        using var output = new MemoryStream();
        var buffer = new byte[chunk];
        long offset = 0;
        while (true)
        {
            Assert.Equal(NtStatus.Success, fs.ReadFile(path, buffer, out var read, offset, info));
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            offset += read;
        }

        fs.Cleanup(path, info);
        fs.CloseFile(path, info);
        Assert.Null(info.Context);
        Assert.Equal(meta.Length, output.Length);
        return output.ToArray();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("\\", "")]
    [InlineData("\\a\\b", "a/b")]
    [InlineData("a/b/", "a/b")]
    [InlineData("\\\\a//b\\", "a/b")]
    public void NormalizePath_HandlesSeparators(string input, string expected) =>
        Assert.Equal(expected, ExFatVirtualFileSystem.NormalizePath(input));

    [Fact]
    public void Root_ContainsOnlyTheWrapperFolder()
    {
        var (image, fs) = Open();
        using (image)
        {
            Assert.Equal(Wrapper, fs.SourceRelativePath);
            var root = List(fs, "\\");
            var only = Assert.Single(root);
            Assert.Equal(Wrapper, only.FileName);
            Assert.True(only.Attributes.HasFlag(FileAttributes.Directory));

            var app = List(fs, "\\" + Wrapper).Select(f => f.FileName).ToList();
            Assert.Contains("sce_sys", app, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("data", app, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("eboot.bin", app, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(NtStatus.Success, fs.GetFileInformation("\\" + Wrapper + "\\eboot.bin", out var eboot, new FakeFileInfo()));
            Assert.Equal(64 * 1024, eboot.Length);
            Assert.True(eboot.Attributes.HasFlag(FileAttributes.ReadOnly));
            Assert.False(eboot.Attributes.HasFlag(FileAttributes.Directory));
            Assert.NotNull(eboot.LastWriteTime);
        }
    }

    [Fact]
    public void WithoutWrapper_AppRootIsTheDriveRoot()
    {
        var (image, fs) = Open(wrapper: null);
        using (image)
        {
            Assert.Equal(string.Empty, fs.SourceRelativePath);
            var names = List(fs, "\\").Select(f => f.FileName).ToList();
            Assert.Contains("eboot.bin", names, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Wrapper, names);
        }
    }

    [Fact]
    public void JunkFiles_AreHiddenByDefault()
    {
        var (image, fs) = Open(hideJunk: true);
        using (image)
        {
            var small = List(fs, "\\" + Wrapper + "\\data\\small").Select(f => f.FileName).ToList();
            Assert.Equal(31, small.Count);
            Assert.DoesNotContain(small, n => n.StartsWith("._", StringComparison.Ordinal));
        }

        var (image2, visible) = Open(hideJunk: false);
        using (image2)
        {
            var small = List(visible, "\\" + Wrapper + "\\data\\small").Select(f => f.FileName).ToList();
            Assert.True(small.Count > 31);
            Assert.Contains(small, n => n.StartsWith("._", StringComparison.Ordinal));
            Assert.True(small.Where(n => n.StartsWith("._", StringComparison.Ordinal)).All(n => JunkFileFinder.IsJunkFileName(n)));
        }
    }

    [Fact]
    public void ReadFile_MatchesImageBytesForContiguousAndFragmentedFiles()
    {
        var (image, fs) = Open();
        using (image)
        {
            foreach (var (relative, chunk) in new[] { ("data/random.bin", 7777), ("data/frag_c.bin", 65536), ("data/small/file_05.txt", 4096), ("eboot.bin", 1 << 20) })
            {
                var expected = image.ReadAllBytes(image.Find(relative)!);
                var actual = ReadAll(fs, "\\" + Wrapper + "\\" + relative.Replace('/', '\\'), chunk);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(actual)));
            }
        }
    }

    [Fact]
    public void ReadFile_AtArbitraryOffsetsAndPastEnd()
    {
        var (image, fs) = Open();
        using (image)
        {
            var path = "\\" + Wrapper + "\\data\\random.bin";
            var whole = image.ReadAllBytes(image.Find("data/random.bin")!);
            var info = new FakeFileInfo();
            Assert.Equal(NtStatus.Success, fs.CreateFile(path, DokanFileAccess.ReadData | DokanFileAccess.ReadAttributes, FileShare.Read, FileMode.Open, FileOptions.None, FileAttributes.Normal, info));

            var buffer = new byte[4096];
            Assert.Equal(NtStatus.Success, fs.ReadFile(path, buffer, out var read, 1000, info));
            Assert.Equal(4096, read);
            Assert.Equal(whole.AsSpan(1000, 4096).ToArray(), buffer);

            Assert.Equal(NtStatus.Success, fs.ReadFile(path, buffer, out read, whole.Length - 100, info));
            Assert.Equal(100, read);
            Assert.Equal(whole[^100..], buffer[..100]);

            Assert.Equal(NtStatus.Success, fs.ReadFile(path, buffer, out read, whole.Length, info));
            Assert.Equal(0, read);

            Assert.Equal(NtStatus.Success, fs.ReadFile(path, buffer, out read, whole.Length + 5000, info));
            Assert.Equal(0, read);

            fs.CloseFile(path, info);

            // Đọc không qua CreateFile (driver đôi khi làm vậy với paging I/O) vẫn phải đúng.
            Assert.Equal(NtStatus.Success, fs.ReadFile(path, buffer, out read, 0, new FakeFileInfo()));
            Assert.Equal(4096, read);
            Assert.Equal(whole.AsSpan(0, 4096).ToArray(), buffer);
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

        var (image, fs) = Open(overlays: overlays);
        using (image)
        {
            var names = List(fs, "\\" + Wrapper + "\\sce_sys").Select(f => f.FileName).ToList();
            Assert.Contains("param.json", names, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("extra.json", names, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(NtStatus.Success, fs.GetFileInformation("\\" + Wrapper + "\\sce_sys\\param.json", out var paramInfo, new FakeFileInfo()));
            Assert.Equal(replaced.Length, paramInfo.Length);
            Assert.Equal(replaced, ReadAll(fs, "\\" + Wrapper + "\\SCE_SYS\\PARAM.JSON", 8));
            Assert.Equal(added, ReadAll(fs, "\\" + Wrapper + "\\sce_sys\\extra.json", 3));

            // Tệp không bị đè vẫn đọc từ ảnh.
            var eboot = image.ReadAllBytes(image.Find("eboot.bin")!);
            Assert.Equal(eboot, ReadAll(fs, "\\" + Wrapper + "\\eboot.bin", 10000));
        }
    }

    [Fact]
    public void WriteAndCreateRequests_AreDenied()
    {
        var (image, fs) = Open();
        using (image)
        {
            var existing = "\\" + Wrapper + "\\eboot.bin";
            Assert.Equal(NtStatus.AccessDenied, fs.CreateFile(existing, DokanFileAccess.GenericWrite, FileShare.None, FileMode.Open, FileOptions.None, FileAttributes.Normal, new FakeFileInfo()));
            Assert.Equal(NtStatus.AccessDenied, fs.CreateFile(existing, DokanFileAccess.WriteData, FileShare.None, FileMode.Open, FileOptions.None, FileAttributes.Normal, new FakeFileInfo()));
            Assert.Equal(NtStatus.AccessDenied, fs.CreateFile(existing, DokanFileAccess.GenericRead, FileShare.None, FileMode.Create, FileOptions.None, FileAttributes.Normal, new FakeFileInfo()));
            Assert.Equal(NtStatus.ObjectNameCollision, fs.CreateFile(existing, DokanFileAccess.GenericRead, FileShare.None, FileMode.CreateNew, FileOptions.None, FileAttributes.Normal, new FakeFileInfo()));

            var missing = "\\" + Wrapper + "\\nope.bin";
            Assert.Equal(NtStatus.ObjectNameNotFound, fs.CreateFile(missing, DokanFileAccess.GenericRead, FileShare.Read, FileMode.Open, FileOptions.None, FileAttributes.Normal, new FakeFileInfo()));
            Assert.Equal(NtStatus.AccessDenied, fs.CreateFile(missing, DokanFileAccess.GenericRead, FileShare.Read, FileMode.OpenOrCreate, FileOptions.None, FileAttributes.Normal, new FakeFileInfo()));
            Assert.Equal(NtStatus.ObjectNameNotFound, fs.GetFileInformation(missing, out _, new FakeFileInfo()));
            Assert.Equal(NtStatus.ObjectPathNotFound, fs.FindFiles("\\" + Wrapper + "\\missing-dir", out _, new FakeFileInfo()));

            Assert.Equal(NtStatus.AccessDenied, fs.WriteFile(existing, new byte[4], out var written, 0, new FakeFileInfo()));
            Assert.Equal(0, written);
            Assert.Equal(NtStatus.AccessDenied, fs.DeleteFile(existing, new FakeFileInfo()));
            Assert.Equal(NtStatus.AccessDenied, fs.MoveFile(existing, "\\" + Wrapper + "\\x.bin", false, new FakeFileInfo()));
            Assert.Equal(NtStatus.AccessDenied, fs.SetEndOfFile(existing, 1, new FakeFileInfo()));
        }
    }

    [Fact]
    public void Directories_OpenAsDirectories()
    {
        var (image, fs) = Open();
        using (image)
        {
            var info = new FakeFileInfo();
            Assert.Equal(NtStatus.Success, fs.CreateFile("\\" + Wrapper + "\\data", DokanFileAccess.GenericRead, FileShare.Read, FileMode.Open, FileOptions.None, FileAttributes.Normal, info));
            Assert.True(info.IsDirectory);
            Assert.Null(info.Context);

            var asDirectory = new FakeFileInfo { IsDirectory = true };
            Assert.Equal(NtStatus.NotADirectory, fs.CreateFile("\\" + Wrapper + "\\eboot.bin", DokanFileAccess.GenericRead, FileShare.Read, FileMode.Open, FileOptions.None, FileAttributes.Normal, asDirectory));

            var rootInfo = new FakeFileInfo();
            Assert.Equal(NtStatus.Success, fs.CreateFile("\\", DokanFileAccess.GenericRead, FileShare.Read, FileMode.Open, FileOptions.None, FileAttributes.Normal, rootInfo));
            Assert.True(rootInfo.IsDirectory);
            Assert.Equal(NtStatus.NotADirectory, fs.FindFiles("\\" + Wrapper + "\\eboot.bin", out _, new FakeFileInfo()));
        }
    }

    [Fact]
    public void Pattern_FiltersListing()
    {
        var (image, fs) = Open();
        using (image)
        {
            var bins = List(fs, "\\" + Wrapper + "\\data", "*.bin").Select(f => f.FileName).ToList();
            Assert.NotEmpty(bins);
            Assert.All(bins, n => Assert.EndsWith(".bin", n, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("random.bin", bins);

            var exact = List(fs, "\\" + Wrapper + "\\data", "TEXT.BIN");
            Assert.Equal("text.bin", Assert.Single(exact).FileName);
        }
    }

    [Fact]
    public void VolumeInformation_IsReadOnlyExFat()
    {
        var (image, fs) = Open();
        using (image)
        {
            Assert.Equal(NtStatus.Success, fs.GetVolumeInformation(out var label, out var features, out var name, out var maxComponent, new FakeFileInfo()));
            Assert.Equal("TESTVOL", label);
            Assert.Equal("exFAT", name);
            Assert.Equal(255u, maxComponent);
            Assert.True(features.HasFlag(FileSystemFeatures.ReadOnlyVolume));
            Assert.True(features.HasFlag(FileSystemFeatures.UnicodeOnDisk));

            Assert.Equal(NtStatus.Success, fs.GetDiskFreeSpace(out var free, out var total, out var totalFree, new FakeFileInfo()));
            Assert.Equal(0, free);
            Assert.Equal(0, totalFree);
            Assert.Equal(image.VolumeLengthBytes, total);

            Assert.False(fs.IsMounted);
            Assert.Equal(NtStatus.Success, fs.Mounted("Z:\\", new FakeFileInfo()));
            Assert.True(fs.IsMounted);
            Assert.True(fs.WaitForMount(TimeSpan.Zero, CancellationToken.None));
        }
    }

    [Fact]
    public void PickDriveLetter_PrefersHighestFreeLetter()
    {
        Assert.Equal('Z', DokanImageMounter.PickDriveLetter(new[] { 'C', 'D', 'E' }));
        Assert.Equal('Y', DokanImageMounter.PickDriveLetter(new[] { 'c', 'z' }));
        Assert.Null(DokanImageMounter.PickDriveLetter(Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c)));
    }

    [Fact]
    public void WrapperName_ComesFromTheImageFileName()
    {
        var plain = new SourceInfo(SourceKind.ExFatImage, Path.Combine("H:", "GamePS5", "PPSA21564_Astrobot.exfat"), "/", "21564ASTROB");
        Assert.Equal("PPSA21564_Astrobot", ImageMounter.WrapperNameFor(plain));

        var container = new SourceInfo(SourceKind.ExFatImage, "/tmp/PPSA02849_格雷克：阿祖爾的回憶.ffpfsc", "/", "OSFIMG", IsPfsContainer: true);
        var name = ImageMounter.WrapperNameFor(container);
        Assert.StartsWith("PPSA02849_", name, StringComparison.Ordinal);
        Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c));

        var odd = new SourceInfo(SourceKind.ExFatImage, "/tmp/...exfat", "/", null);
        Assert.Equal("image", ImageMounter.WrapperNameFor(odd));
    }

    [Fact]
    public void MountBackend_IsConsistentOnThisPlatform()
    {
        var capable = ImageMounter.Backend is MountBackend.Dokan or MountBackend.Fuse;
        Assert.Equal(ImageMounter.Backend != MountBackend.None, ImageMounter.IsAvailable);
        Assert.Equal(capable, ImageMounter.CanMountContainers);
        Assert.Equal(capable, ImageMounter.CanHideJunk);
        Assert.Equal(capable, ImageMounter.CanOverlayFiles);
        Assert.Equal(OperatingSystem.IsWindows(), DokanImageMounter.IsSupportedPlatform);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(DokanImageMounter.IsDriverInstalled);
            Assert.False(ImageMounter.DokanMissingOnWindows);
            Assert.Null(DokanImageMounter.DriverVersion);
        }

        // FUSE chỉ có trên Linux; nền tảng khác không bao giờ chọn backend này.
        Assert.False(FuseImageMounter.IsAvailable && !OperatingSystem.IsLinux());
        if (!OperatingSystem.IsLinux())
        {
            Assert.NotEqual(MountBackend.Fuse, ImageMounter.Backend);
        }

        var container = new SourceInfo(SourceKind.ExFatImage, "x.ffpfsc", "/", null, IsPfsContainer: true);
        Assert.Equal(ImageMounter.CanMountContainers, ImageMounter.CanMount(container));
    }

    private sealed class FakeFileInfo : IDokanFileInfo
    {
        public object? Context { get; set; }

        public bool DeletePending { get; set; }

        public bool IsDirectory { get; set; }

        public bool NoCache => false;

        public bool PagingIo => false;

        public int ProcessId => 0;

        public bool SynchronousIo => true;

        public bool WriteToEndOfFile => false;

        public WindowsIdentity GetRequestor() => throw new NotSupportedException();

        public bool TryResetTimeout(int milliseconds) => true;
    }
}
