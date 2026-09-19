using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Các phép thử "chạy êm trên mọi nền tảng": chạy được trên macOS, Windows và Linux mà không cần SDK/Wine — chuỗi dịch đủ và khớp
/// tham số, bộ công cụ kèm theo đủ tệp, các dịch vụ phụ thuộc hệ điều hành không ném lỗi ở nơi chúng không áp dụng.
/// </summary>
public sealed class CrossPlatformTests
{
    private static Dictionary<string, string> LoadLanguage(string code)
    {
        var assembly = typeof(Loc).Assembly;
        using var stream = assembly.GetManifestResourceStream("PsViethoa.FpkgBuilder.Core.Localization." + code + ".json")
                           ?? throw new InvalidOperationException("missing localization resource " + code);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    [Fact]
    public void Localization_BothLanguagesHaveTheSameKeys()
    {
        var vi = LoadLanguage("vi");
        var en = LoadLanguage("en");
        Assert.Empty(vi.Keys.Except(en.Keys).Order());
        Assert.Empty(en.Keys.Except(vi.Keys).Order());
        Assert.All(vi, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
        Assert.All(en, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
    }

    [Fact]
    public void Localization_PlaceholdersMatchBetweenLanguages()
    {
        var vi = LoadLanguage("vi");
        var en = LoadLanguage("en");
        var placeholder = new Regex(@"\{(\d+)(?:[:,][^}]*)?\}");
        var mismatched = new List<string>();
        foreach (var (key, english) in en)
        {
            if (!vi.TryGetValue(key, out var vietnamese))
            {
                continue;
            }

            var a = placeholder.Matches(english).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
            var b = placeholder.Matches(vietnamese).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
            if (!a.SequenceEqual(b))
            {
                mismatched.Add(key);
            }
        }

        Assert.Empty(mismatched);
    }

    /// <summary>Chuỗi có tham số phải định dạng được (không thừa/thiếu ngoặc nhọn) ở cả hai ngôn ngữ.</summary>
    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void Localization_EveryStringIsAValidFormatString(string code)
    {
        var broken = new List<string>();
        var arguments = Enumerable.Range(0, 12).Select(i => (object)("arg" + i)).ToArray();
        foreach (var (key, value) in LoadLanguage(code))
        {
            if (!value.Contains('{'))
            {
                continue;
            }

            try
            {
                _ = string.Format(value, arguments);
            }
            catch (FormatException)
            {
                // Chuỗi chứa JSON/ngoặc nhọn thật (không phải chuỗi định dạng) chỉ hợp lệ khi không có {số}.
                if (Regex.IsMatch(value, @"\{\d+\}"))
                {
                    broken.Add(key);
                }
            }
        }

        Assert.Empty(broken);
    }

    /// <summary>Bộ công cụ kèm trong repo (libs/sony-sdk) phải đủ tệp sau mỗi lần cập nhật — thiếu một tệp là cả chế độ SDK hỏng trên mọi máy.</summary>
    [Fact]
    public void BundledToolkit_HasEveryFileTheAppNeeds()
    {
        var directory = SonySdkToolchain.FindToolchain();
        if (directory == null)
        {
            return;
        }

        var toolkit = SonySdkToolchain.ToolchainIn(Path.GetDirectoryName(directory)!) == directory ? Path.GetDirectoryName(directory)! : directory;
        foreach (var relative in new[]
                 {
                     "toolchain/prospero-pub-cmd.exe", "toolchain/libScePubTools.dll", "toolchain/ext/ric.exe", "toolchain/ext/sc2.exe", "toolchain/ext/ispc_texcomp.dll",
                     "prospero-dds2png.exe", "scripts/create-gp5-from-folder.py", "build-from-folder.ps1",
                 })
        {
            var path = Path.Combine(toolkit, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "missing toolkit file: " + relative);
            Assert.True(new FileInfo(path).Length > 0, "empty toolkit file: " + relative);
        }

        // fix8: build-from-folder.ps1 phải biết tạo bản vá.
        Assert.Contains("--ref_pkg_path", File.ReadAllText(Path.Combine(toolkit, "build-from-folder.ps1")));
    }

    [Fact]
    public void OsDependentServices_NeverThrowWhereTheyDoNotApply()
    {
        // Không ném lỗi trên hệ điều hành nào: trả null/false/"không áp dụng" khi không dùng được.
        using (var inhibitor = SleepInhibitor.TryAcquire())
        {
            Assert.True(inhibitor == null || !string.IsNullOrEmpty(inhibitor.Mechanism));
        }

        var rows = ComponentProbe.Run(null);
        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.Id == "sony-sdk");
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Detail), row.Id));

        _ = SonySdkToolchain.RosettaInstalled;
        _ = SonySdkToolchain.RosettaMissing;
        _ = SonySdkToolchain.VcRuntimeInstalled;
        Assert.False(string.IsNullOrEmpty(SonySdkToolchain.WinePrefixPath()));
        Assert.False(string.IsNullOrEmpty(UpdateChecker.PreferredAssetToken()));
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(SonySdkToolchain.FindWine());
            Assert.Equal("Windows-x64", UpdateChecker.PlatformAssetHint());
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.StartsWith("Linux-", UpdateChecker.PlatformAssetHint());
            Assert.Equal(".tar.gz", UpdateChecker.PreferredAssetToken());
            Assert.False(SonySdkToolchain.RosettaMissing);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.StartsWith("macOS-", UpdateChecker.PlatformAssetHint());
        }
    }

    [Fact]
    public void Aliases_AreNeverPlacedOnTheSourceDrive()
    {
        var bases = SonySdkPathAliases.DefaultBases(Path.Combine(Path.GetTempPath(), "tmp-x"), Path.Combine(Path.GetTempPath(), "out-x"));
        Assert.NotEmpty(bases);
        Assert.All(bases, folder => Assert.True(Path.IsPathRooted(folder), folder));
        Assert.Equal(2, typeof(SonySdkPathAliases).GetMethod(nameof(SonySdkPathAliases.DefaultBases), BindingFlags.Public | BindingFlags.Static)!.GetParameters().Length);
        Assert.True(SonySdkPathAliases.IsAsciiSafe(@"C:\Games\PPSA01234-app"));
        Assert.False(SonySdkPathAliases.IsAsciiSafe("/Volumes/SSD/Ghost of Yōtei"));
        Assert.False(SonySdkPathAliases.IsAsciiSafe("D:\\Việt hoá\\game"));
    }

    [Theory]
    [InlineData("/Users/a/b c/game.gp5", "Z:\\Users\\a\\b c\\game.gp5")]
    [InlineData("/tmp/x", "Z:\\tmp\\x")]
    public void WinePaths_MapUnixPathsToTheZDrive(string unix, string expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, SonySdkToolchain.ToWinePath(unix));
    }

    /// <summary>Tên gói và tên tệp đi kèm không chứa ký tự mà Windows cấm — cùng một tên dùng được trên cả ba hệ điều hành.</summary>
    [Theory]
    [InlineData("UP9000-PPSA26344_00-GHOST2SHIP000000", "01.512.000")]
    [InlineData("EP4295-PPSA21607_00-SMURFSDREAMPS5EU", "1.2")]
    public void OutputNames_AreValidOnEveryFileSystem(string contentId, string version)
    {
        var name = SonySdkBuilder.PackageFileName(contentId, version);
        Assert.EndsWith(".pkg", name);
        Assert.DoesNotContain(name, c => "<>:\"/\\|?*".Contains(c) || c < 0x20);
        Assert.All(SonySdkBuilder.Artifacts(Path.GetTempPath(), contentId, version), path => Assert.DoesNotContain(Path.GetFileName(path), c => "<>:\"|?*".Contains(c)));
        Assert.True((name + SonySdkPatchReference.RemasteredSuffix).Length < 120);
    }
}
