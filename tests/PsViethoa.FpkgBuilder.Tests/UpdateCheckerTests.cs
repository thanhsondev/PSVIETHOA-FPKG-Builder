using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

public sealed class UpdateCheckerTests
{
    private const string Sample = """
        {"tag_name":"v2.1.5","html_url":"https://github.com/thanhsondev/PSVIETHOA-FPKG-Builder/releases/tag/v2.1.5","published_at":"2026-09-14T10:00:00Z",
         "assets":[{"name":"PSVIETHOA-FPKG-Builder-2.1.5-macOS-AppleSilicon.zip","browser_download_url":"https://example/arm.zip","size":100},
                   {"name":"PSVIETHOA-FPKG-Builder-2.1.5-macOS-Intel.zip","browser_download_url":"https://example/intel.zip","size":200},
                   {"name":"PSVIETHOA-FPKG-Builder-2.1.5-Windows-x64.zip","browser_download_url":"https://example/win.zip","size":300}]}
        """;

    [Theory]
    [InlineData("2.1.4", true)]
    [InlineData("v2.1.4", true)]
    [InlineData("2.1.5", false)]
    [InlineData("2.2.0", false)]
    [InlineData("2.1.5+abc123", false)]
    public void Parse_ComparesVersions(string current, bool expectedNewer)
    {
        var info = UpdateChecker.Parse(Sample, current, "macOS-AppleSilicon");
        Assert.Equal("2.1.5", info.LatestVersion);
        Assert.Equal("v2.1.5", info.TagName);
        Assert.Equal(expectedNewer, info.IsNewer);
        Assert.Equal("https://example/arm.zip", info.AssetUrl);
        Assert.Equal(100, info.AssetSize);
        Assert.NotNull(info.PublishedAt);
    }

    [Theory]
    [InlineData("macOS-Intel", "https://example/intel.zip")]
    [InlineData("Windows-x64", "https://example/win.zip")]
    [InlineData("", null)]
    public void Parse_PicksThePlatformAsset(string hint, string? expectedUrl)
    {
        var info = UpdateChecker.Parse(Sample, "2.1.4", hint);
        Assert.Equal(expectedUrl, info.AssetUrl);
        Assert.Equal("https://github.com/thanhsondev/PSVIETHOA-FPKG-Builder/releases/tag/v2.1.5", info.ReleaseUrl);
    }

    [Fact]
    public void Parse_RejectsUnexpectedJson()
    {
        Assert.Throws<InvalidDataException>(() => UpdateChecker.Parse("{\"message\":\"Not Found\"}", "2.1.4", ""));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => UpdateChecker.Parse("not json", "2.1.4", ""));
    }

    [Fact]
    public void Parse_DoesNotOfferTheRunningTestBuildAsAnUpdate()
    {
        const string json = """{"tag_name":"v2.2.0-test3","html_url":"https://example/r","published_at":"2026-09-17T12:00:00Z","assets":[{"name":"PSVIETHOA-FPKG-Builder-2.2.0-test3-Windows-x64.zip","browser_download_url":"https://example/win.zip","size":10}]}""";
        Assert.False(UpdateChecker.Parse(json, "2.2.0-test3", "Windows-x64").IsNewer);
        Assert.True(UpdateChecker.Parse(json, "2.2.0-test2", "Windows-x64").IsNewer);
        Assert.True(UpdateChecker.Parse(json, "2.1.9", "Windows-x64").IsNewer);
        Assert.Equal("2.2.0-test3", UpdateChecker.Parse(json, "2.1.9", "Windows-x64").LatestVersion);
    }

    [Theory]
    [InlineData("v2.2.0", "2.2.0-test3", true)]
    [InlineData("v2.2.0-test3", "2.2.0-test2", true)]
    [InlineData("v2.2.0-test10", "2.2.0-test9", true)]
    [InlineData("v2.2.0-test3", "2.2.0-test3", false)]
    [InlineData("v2.2.0-test2", "2.2.0-test3", false)]
    [InlineData("v2.2.0-test3", "2.2.0", false)]
    [InlineData("v2.2.1", "2.2.0-test3", true)]
    [InlineData("v2.1.9", "2.2.0-test3", false)]
    public void IsNewer_TreatsTestBuildsAsOlderThanTheFinalVersion(string latest, string current, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsNewer(latest, current));

    [Fact]
    public void IsNewer_HandlesPrefixesAndBuildMetadata()
    {
        Assert.True(UpdateChecker.IsNewer("v2.10.0", "2.9.9"));
        Assert.False(UpdateChecker.IsNewer("2.1.4", "2.1.4+deadbeef"));
        Assert.False(UpdateChecker.IsNewer("garbage", "2.1.4"));
        Assert.Equal("2.1.4", UpdateChecker.NormalizeVersion("v2.1.4+deadbeef"));
    }
}
