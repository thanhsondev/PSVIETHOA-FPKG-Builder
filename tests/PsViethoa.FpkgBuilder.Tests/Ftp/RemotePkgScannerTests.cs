using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.Ftp;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests.Ftp;

/// <summary>
/// <see cref="RemotePkgScanner"/> trên máy chủ giả có cây thư mục giống PS5: /user/app, /mnt/ext1/user/app, /mnt/usb0/user/app,
/// /user/appmeta. Issue #16: với ftpsrv v0.21–v0.22 (MLSD bỏ qua đối số) bản 3.2.0 quét ra "0 PS5 FPKG trong 0.0 s".
/// </summary>
public sealed class RemotePkgScannerTests
{
    private const string FtpsrvGreeting =
        "220-Welcome to ftpsrv.elf running on pid 82\r\n220-Version: v0.22 (built Nov 10 2026 10:00:00)\r\n220 Service is ready";

    private static byte[] Package(byte b1, byte b2, byte b3, int size = 80 * 1024)
    {
        var data = FtpTestData.Bytes(size, b1);
        data[0] = 0x7F;
        data[1] = b1;
        data[2] = b2;
        data[3] = b3;
        return data;
    }

    private static byte[] Ps5Package() => Package((byte)'F', (byte)'I', (byte)'H');

    private static byte[] Param(string title, string version, string contentId) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["contentId"] = contentId,
        ["contentVersion"] = version,
        ["localizedParameters"] = new Dictionary<string, object>
        {
            ["defaultLanguage"] = "en-US",
            ["en-US"] = new Dictionary<string, string> { ["titleName"] = title },
        },
    });

    private static void BuildConsole(FtpSandbox box)
    {
        // Gốc PS5 có rác ngang hàng như máy thật; một thư mục gốc trông như mã game để bắt lỗi "liệt kê nhầm /".
        box.ServerFile("/adm/readme.txt", FtpTestData.Bytes(5, 1));
        box.ServerFile("/app_temp0/x.bin", FtpTestData.Bytes(5, 1));
        box.ServerFile("/PPSA99999/app.pkg", Ps5Package());

        // Bộ nhớ trong: FPKG PS5, có app.json và appmeta.
        box.ServerFile("/user/app/PPSA01234/app.pkg", Ps5Package());
        box.ServerFile("/user/app/PPSA01234/app.json", """{"fake":true,"pieces":[{"url":"D:\\pkgs\\Game-A.pkg"}]}"""u8.ToArray());
        box.ServerFile("/user/appmeta/PPSA01234/param.json", Param("Game A", "01.002.000", "UP0001-PPSA01234_00-GAMEA0000000000"));
        box.ServerFile("/user/appmeta/PPSA01234/icon0.png", FtpTestData.Bytes(100, 4));

        // Bị bỏ qua: gói PS4, game chạy từ dump (không có app.pkg), app hệ thống, app.pkg quá nhỏ.
        box.ServerFile("/user/app/CUSA00001/app.pkg", Package((byte)'C', (byte)'N', (byte)'T'));
        box.ServerFile("/user/app/PPSA02222/sce_sys/param.json", "{}"u8.ToArray());
        box.ServerFile("/user/app/NPXS40000/app.pkg", Ps5Package());
        box.ServerFile("/user/app/PPSA03333/app.pkg", FtpTestData.Bytes(1000, 5));

        // Ổ mở rộng: không có appmeta, tên lấy từ sce_sys/param.json cạnh gói.
        box.ServerFile("/mnt/ext1/user/app/PPSA05678/app.pkg", Ps5Package());
        box.ServerFile("/mnt/ext1/user/app/PPSA05678/sce_sys/param.json", Param("Game B", "01.000.000", "EP0002-PPSA05678_00-GAMEB0000000000"));
        box.ServerFile("/mnt/ext1/user/app/PPSA05678/icon0.png", FtpTestData.Bytes(100, 6));
        box.ServerFile("/mnt/usb0/user/app/readme.txt", FtpTestData.Bytes(5, 7));
        box.ServerFile("/mnt/sandbox/PPSA07777/app.pkg", Ps5Package());
    }

    [Theory]
    [InlineData(FakeFeat.FtpsrvMlsd)]
    [InlineData(FakeFeat.Empty)]
    public async Task Scan_FindsInstalledPs5Fpkgs_OnInternalAndExtendedStorage(FakeFeat feat)
    {
        await using var box = new FtpSandbox();
        box.Server.Feat = feat;
        box.Server.Greeting = FtpsrvGreeting;
        BuildConsole(box);
        var log = new List<string>();

        var games = await RemotePkgScanner.ScanAsync(box.Connection, new SyncProgress(log), box.Token, workers: 2).WaitAsync(FtpSandbox.Limit, box.Token);

        Assert.Equal(new[] { "Game A", "Game B" }, games.Select(g => g.DisplayTitle));

        var a = games[0];
        Assert.Equal(("PPSA01234", RemoteGame.InternalLocation, "/user/app/PPSA01234"), (a.TitleId, a.Location, a.Folder));
        Assert.Equal("/user/app/PPSA01234/app.pkg", a.PkgPath);
        Assert.Equal(80 * 1024, a.PkgSize);
        Assert.Equal(("01.002", "UP0001-PPSA01234_00-GAMEA0000000000"), (a.Version, a.ContentId));
        Assert.Equal(("Game-A.pkg", true), (a.OriginalName, a.IsFake));
        Assert.Equal("/user/appmeta/PPSA01234/icon0.png", a.IconPath);

        var b = games[1];
        Assert.Equal(("PPSA05678", "ext1", "/mnt/ext1/user/app/PPSA05678/app.pkg"), (b.TitleId, b.Location, b.PkgPath));
        Assert.Equal("01.000", b.Version);
        Assert.Equal("/mnt/ext1/user/app/PPSA05678/icon0.png", b.IconPath);

        // ftpsrv có REST (dù FEAT không ghi): không được cảnh báo nhầm.
        Assert.DoesNotContain(log, l => l.Contains("REST", StringComparison.Ordinal));
    }

    /// <summary>Báo tiến độ ngay trên luồng gọi (Progress&lt;T&gt; gửi qua SynchronizationContext, tới muộn).</summary>
    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
