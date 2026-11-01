using PsViethoa.FpkgBuilder.Core.Ps4;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Môi trường chạy bộ công cụ PS4: Wine 32-bit, runtime VC++ x86 và dòng "PS4" của bảng thành phần.</summary>
public sealed class Ps4RuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-ps4rt-" + Guid.NewGuid().ToString("N"));

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

    private string FakeWine(params string[] libraries)
    {
        var bin = Path.Combine(_root, "wine", "bin");
        Directory.CreateDirectory(bin);
        var wine = Path.Combine(bin, "wine");
        File.WriteAllText(wine, "#!/bin/sh\n");
        foreach (var library in libraries)
        {
            Directory.CreateDirectory(Path.Combine(_root, "wine", "lib", "wine", library));
        }

        return wine;
    }

    [Fact]
    public void Wine32Missing_OnlyWhenLayoutShows64BitWithout32Bit()
    {
        Assert.True(Ps4Engine.Wine32Missing(FakeWine("x86_64-windows", "x86_64-unix")));
        Directory.CreateDirectory(Path.Combine(_root, "wine", "lib", "wine", "i386-windows"));
        Assert.False(Ps4Engine.Wine32Missing(Path.Combine(_root, "wine", "bin", "wine")));
    }

    [Fact]
    public void Wine32Missing_UnknownLayoutIsNotReported()
    {
        Assert.False(Ps4Engine.Wine32Missing(FakeWine()));
        Assert.False(Ps4Engine.Wine32Missing(Path.Combine(_root, "nowhere", "wine")));
    }

    [Fact]
    public void WineVersionFromFiles_ReadsBundleWithoutRunningWine()
    {
        var wine = FakeWine("x86_64-windows", "i386-windows");
        Assert.Null(SonySdkToolchain.WineVersionFromFiles(wine));
        File.WriteAllText(Path.Combine(_root, "wine", "README-PSVIETHOA.txt"), "Wine 11.17 for macOS (x86-64), unmodified binaries\r\nsecond line");
        Assert.Equal("wine-11.17", SonySdkToolchain.WineVersionFromFiles(wine));

        // Wine*.app cài riêng: …/Wine Devel.app/Contents/Resources/wine/bin/wine
        var app = Path.Combine(_root, "Wine Devel.app", "Contents");
        var bin = Path.Combine(app, "Resources", "wine", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "wine"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(app, "Info.plist"), "<plist><dict><key>CFBundleShortVersionString</key>\n\t<string>10.4</string></dict></plist>");
        Assert.Equal("wine-10.4", SonySdkToolchain.WineVersionFromFiles(Path.Combine(bin, "wine")));

        if (OperatingSystem.IsMacOS())
        {
            // macOS không chạy Wine chỉ để hỏi phiên bản (tệp "wine" giả này không chạy được nếu bị gọi).
            Assert.Equal("wine-11.17", SonySdkToolchain.WineVersion(wine));
        }
    }

    [Fact]
    public void ResolveImage_RejectsPackageFileWithClearMessage()
    {
        Directory.CreateDirectory(_root);
        var package = Path.Combine(_root, "UP0000-CUSA00000_00-TEST000000000000-A0101-V0100.pkg");
        File.WriteAllBytes(package, new byte[64]);
        var error = Assert.Throws<InvalidDataException>(() => Ps4ProjectBuilder.ResolveImage(package));
        Assert.Contains(package, error.Message);
        Assert.DoesNotContain("Ps4.Project", error.Message);
        Assert.Throws<DirectoryNotFoundException>(() => Ps4ProjectBuilder.ResolveImage(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void VcRuntimeCheck_IsNotApplicableOffWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(Ps4Engine.MissingVcRuntimes());
        }
    }

    [Fact]
    public void ComponentRow_MatchesResolve()
    {
        var row = ComponentProbe.Run().Single(r => r.Id == "ps4");
        var runtime = Ps4Engine.Resolve(out _);
        if (runtime == null)
        {
            Assert.Equal(ComponentState.Missing, row.State);
        }
        else
        {
            Assert.NotEqual(ComponentState.Missing, row.State);
            Assert.Contains(runtime.ToolDirectory, row.Detail);
        }
    }
}
