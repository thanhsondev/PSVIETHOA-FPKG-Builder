using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Dọn thư mục tạm: lần tạo gói không được để lại thư mục "fpkg-temp" rỗng cạnh thư mục xuất, nhưng cũng không
/// được xoá thư mục người dùng đã có sẵn hay thư mục còn tệp bên trong.
/// </summary>
public sealed class TemporaryFolderCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-cleanup-" + Guid.NewGuid().ToString("N"));

    public TemporaryFolderCleanupTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void TryDeleteEmptyDirectory_RemovesAnEmptyDirectory()
    {
        var path = Path.Combine(_root, "empty");
        Directory.CreateDirectory(path);

        BuildEngine.TryDeleteEmptyDirectory(path);

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void TryDeleteEmptyDirectory_KeepsADirectoryHoldingFiles()
    {
        var path = Path.Combine(_root, "with-file");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "keep.bin"), "data");

        BuildEngine.TryDeleteEmptyDirectory(path);

        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(path, "keep.bin")));
    }

    [Fact]
    public void TryDeleteEmptyDirectory_KeepsADirectoryHoldingSubdirectories()
    {
        var path = Path.Combine(_root, "with-subdir");
        Directory.CreateDirectory(Path.Combine(path, "child"));

        BuildEngine.TryDeleteEmptyDirectory(path);

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void TryDeleteEmptyDirectory_IgnoresMissingOrBlankPaths()
    {
        BuildEngine.TryDeleteEmptyDirectory(Path.Combine(_root, "never-created"));
        BuildEngine.TryDeleteEmptyDirectory(string.Empty);
        BuildEngine.TryDeleteEmptyDirectory("   ");
    }
}
