using System.Diagnostics;
using System.Text;
using PsViethoa.FpkgBuilder.Core.Ftp;
using PsViethoa.FpkgBuilder.Core.Localization;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests.Ftp;

/// <summary><see cref="Ps5Ftp"/> nói chuyện với máy chủ giả: đăng nhập, FEAT, đọc LIST kiểu ftpsrv, lệnh tệp, truyền thẳng, hạn chờ.</summary>
public sealed class Ps5FtpClientTests
{
    [Theory]
    [InlineData(FakeFeat.NotImplemented)]
    [InlineData(FakeFeat.Empty)]
    public async Task Connect_WithoutFeatures_AssumesWhatFtpsrvImplements(FakeFeat feat)
    {
        await using var box = new FtpSandbox();
        box.Server.Feat = feat;
        using var client = await box.ConnectAsync();

        Assert.True(client.Connected);
        Assert.StartsWith("220", client.Welcome, StringComparison.Ordinal);
        Assert.Equal("127.0.0.1", client.Host);
        Assert.Equal(box.Server.Port, client.Port);
        Assert.Equal(new[] { "SIZE", "REST STREAM" }, client.Features);
        Assert.True(client.SupportsSize);
        Assert.True(client.SupportsRest);
        Assert.False(client.SupportsMfmt);
        Assert.False(client.SupportsMlsd);
        Assert.False(client.SupportsEpsv);
        Assert.False(client.Utf8);
        Assert.Equal("/", await client.GetWorkingDirectoryAsync(box.Token));
        await client.NoopAsync(box.Token);
        Assert.Equal(1, box.Server.CountCommands("TYPE", "I"));
    }

    [Fact]
    public async Task Connect_WithFullFeatures_TurnsOnUtf8AndMfmt()
    {
        await using var box = new FtpSandbox();
        box.Server.Feat = FakeFeat.Full;
        using var client = await box.ConnectAsync();

        Assert.Equal(new[] { "SIZE", "REST STREAM", "MDTM", "MFMT", "UTF8" }, client.Features);
        Assert.True(client.Utf8);
        Assert.True(client.SupportsMfmt);
        Assert.Equal(1, box.Server.CountCommands("OPTS", "UTF8 ON"));
    }

    [Fact]
    public async Task Connect_WithAWrongPassword_ThrowsAPermanentFtpException()
    {
        await using var box = new FtpSandbox();
        box.Server.Password = "secret";

        using var wrong = new Ps5Ftp { TimeoutSeconds = 10 };
        var ex = await Assert.ThrowsAsync<FtpException>(() => wrong.ConnectAsync("127.0.0.1", box.Server.Port, "anonymous", "nope", box.Token));
        Assert.Equal(530, ex.Code);
        Assert.True(ex.Permanent);
        // SendAsync đã ném với nguyên văn phản hồi của máy chủ trước khi tới nhánh "Đăng nhập bị từ chối" (giữ như bản gốc).
        Assert.Equal("530 Login incorrect.", ex.Message);

        // Mật khẩu đúng thì vào được, và mật khẩu không bao giờ hiện trong nhật ký.
        var log = new List<FtpLogLine>();
        using var right = new Ps5Ftp { TimeoutSeconds = 10 };
        right.Log += log.Add;
        await right.ConnectAsync("127.0.0.1", box.Server.Port, "anonymous", "secret", box.Token);
        Assert.True(right.Connected);
        Assert.DoesNotContain(log, l => l.Text.Contains("secret", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Outgoing && l.Text == "PASS  ********");
        Assert.Contains(log, l => !l.Outgoing && l.Text.StartsWith("230", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SilentServer_FailsAfterTheTimeoutInsteadOfHanging()
    {
        await using var box = new FtpSandbox();
        box.Server.CommandDelay = TimeSpan.FromSeconds(4);
        using var client = new Ps5Ftp { TimeoutSeconds = 1 };
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<FtpException>(() => client.ConnectAsync("127.0.0.1", box.Server.Port, "anonymous", "", box.Token));

        Assert.Equal(0, ex.Code);
        Assert.Equal(Loc.F("Ftp.Core.ReplyTimeout", 1), ex.Message);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 3.5);
    }

    [Fact]
    public async Task List_ParsesUnixRowsLikeFtpsrv()
    {
        await using var box = new FtpSandbox();
        Directory.CreateDirectory(box.Server.RealPath("/Folder A"));
        Directory.CreateDirectory(box.Server.RealPath("/Thư mục con"));
        box.ServerFile("/file one.bin", FtpTestData.Bytes(1234, 1));
        box.ServerFile("/Trò chơi Việt Nam.txt", "xin chào"u8.ToArray());
        box.ServerFile("/empty.dat", []);
        box.ServerFile("/Folder A/inner.txt", FtpTestData.Bytes(77, 2));
        box.Server.ExtraListLines["/"] =
        [
            "total 12",
            "lrwxrwxrwx 1 0 0 11 Jan 05 12:00 my link -> /mnt/usb0/target dir",
            "-rw-r--r-- 1 0 0 42 Mar 15  2021 old file.txt",
            "drwxrwxrwx 1 0 0 0 Dec 31 23:59 .hidden",
        ];
        using var client = await box.ConnectAsync();

        var entries = await client.ListAsync("/", box.Token);

        Assert.DoesNotContain(entries, e => e.Name is "." or "..");
        Assert.Equal(8, entries.Count);
        FtpEntry Find(string name) => Assert.Single(entries, e => e.Name.Normalize(NormalizationForm.FormC) == name.Normalize(NormalizationForm.FormC));

        var folder = Find("Folder A");
        Assert.Equal(FtpEntryKind.Directory, folder.Kind);
        Assert.True(folder.IsDirectory);
        Assert.Equal("/Folder A", folder.FullPath);
        Assert.Equal("drwxr-xr-x", folder.Permissions);
        Assert.Equal(0x1ED, folder.OctalMode);
        Assert.True(Find("Thư mục con").IsDirectory);

        var file = Find("file one.bin");
        Assert.Equal(FtpEntryKind.File, file.Kind);
        Assert.Equal(1234, file.Size);
        Assert.Equal("/file one.bin", file.FullPath);
        Assert.Equal(0x1A4, file.OctalMode);
        Assert.Null(file.LinkTarget);
        Assert.NotNull(file.Modified);
        Assert.InRange(file.Modified!.Value, DateTime.Now.AddMinutes(-3), DateTime.Now.AddMinutes(3));

        var unicode = Find("Trò chơi Việt Nam.txt");
        Assert.Equal("xin chào"u8.Length, unicode.Size);
        Assert.Equal(0, Find("empty.dat").Size);

        var link = Find("my link");
        Assert.Equal(FtpEntryKind.Link, link.Kind);
        Assert.False(link.IsDirectory);
        Assert.Equal("/mnt/usb0/target dir", link.LinkTarget);
        Assert.Equal("/my link", link.FullPath);
        Assert.Equal(11, link.Size);
        Assert.Equal(0x1FF, link.OctalMode);

        var old = Find("old file.txt");
        Assert.Equal(new DateTime(2021, 3, 15), old.Modified);
        Assert.Equal(42, old.Size);

        var hidden = Find(".hidden");
        Assert.True(hidden.IsDirectory);
        Assert.Equal(12, hidden.Modified!.Value.Month);
        Assert.True(hidden.Modified.Value <= DateTime.Now.AddDays(1));

        // Đường dẫn có '/' ở cuối vẫn liệt kê đúng, FullPath ghép với thư mục đã chuẩn hoá.
        var inner = Assert.Single(await client.ListAsync("Folder A/", box.Token));
        Assert.Equal("inner.txt", inner.Name);
        Assert.Equal("/Folder A/inner.txt", inner.FullPath);
        Assert.Equal(77, inner.Size);
        // CWD vào thư mục rồi LIST không đối số (issue #16: không tin máy chủ đọc đúng đối số của lệnh liệt kê).
        Assert.Equal(1, box.Server.CountCommands("CWD", "/Folder A"));
        Assert.Equal(0, box.Server.CountCommands("LIST", "/Folder A"));
    }

    /// <summary>Lời chào thật của ftpsrv ps5-payload-dev v0.22.</summary>
    private const string FtpsrvGreeting =
        "220-Welcome to ftpsrv.elf running on pid 82\r\n220-Version: v0.22 (built Nov 10 2026 10:00:00)\r\n220 Service is ready";

    [Fact]
    public async Task List_OnFtpsrvWhoseMlsdIgnoresItsArgument_ListsTheRequestedFolder()
    {
        // Issue #16: ftpsrv v0.21–v0.22 quảng bá MLSD nhưng "MLSD <đường dẫn>" luôn trả thư mục hiện hành, nên bấm vào thư mục nào
        // cũng hiện lại "/" và đường dẫn cứ dài ra (/app_temp0/app_tmp/app_tmp/...).
        await using var box = new FtpSandbox();
        box.Server.Feat = FakeFeat.FtpsrvMlsd;
        box.Server.Greeting = FtpsrvGreeting;
        box.ServerFile("/adm/readme.txt", FtpTestData.Bytes(5, 1));
        box.ServerFile("/user/app/PPSA01234/app.pkg", FtpTestData.Bytes(300, 2));
        box.ServerFile("/user/app/PPSA01234/sce_sys/param.json", "{}"u8.ToArray());
        box.ServerFile("/data/my folder/-dash.bin", FtpTestData.Bytes(9, 3));
        using var client = await box.ConnectAsync();
        Assert.True(client.SupportsMlsd);

        var root = await client.ListAsync("/", box.Token);
        Assert.Equal(new[] { "adm", "data", "user" }, root.Select(e => e.Name).Order(StringComparer.Ordinal));

        // Đi xuống từng cấp như người dùng bấm đúp: mỗi cấp phải là nội dung của chính nó, không phải "/".
        var user = Assert.Single(await client.ListAsync("/user", box.Token));
        Assert.Equal(("app", "/user/app", FtpEntryKind.Directory), (user.Name, user.FullPath, user.Kind));
        var game = Assert.Single(await client.ListAsync(user.FullPath + "/", box.Token));
        Assert.Equal("/user/app/PPSA01234", game.FullPath);
        var files = await client.ListAsync(game.FullPath, box.Token);
        var pkg = Assert.Single(files, e => e.Name == "app.pkg");
        Assert.Equal((FtpEntryKind.File, 300L, "/user/app/PPSA01234/app.pkg"), (pkg.Kind, pkg.Size, pkg.FullPath));
        Assert.True(Assert.Single(files, e => e.Name == "sce_sys").IsDirectory);
        Assert.NotNull(pkg.Modified);

        // Tên có dấu cách / bắt đầu bằng '-' vẫn đúng; đi ngược lên cấp trên cũng đúng.
        var odd = Assert.Single(await client.ListAsync("/data/my folder", box.Token));
        Assert.Equal(("-dash.bin", 9L), (odd.Name, odd.Size));
        Assert.Equal("my folder", Assert.Single(await client.ListAsync(Ps5Ftp.Parent("/data/my folder"), box.Token)).Name);
        Assert.Equal(3, (await client.ListAsync(Ps5Ftp.Parent("/data"), box.Token)).Count);

        // Mọi lệnh liệt kê là MLSD trần: không gửi đường dẫn để máy chủ bỏ qua.
        Assert.Equal(0, box.Server.Commands.Count(c => c.StartsWith("MLSD ", StringComparison.Ordinal)));
        Assert.Equal(0, box.Server.CountCommands("LIST"));
    }

    [Fact]
    public async Task List_OfAMissingFolderOnFtpsrv_ThrowsInsteadOfListingTheCurrentFolder()
    {
        await using var box = new FtpSandbox();
        box.Server.Feat = FakeFeat.FtpsrvMlsd;
        box.Server.Greeting = FtpsrvGreeting;
        box.ServerFile("/adm/readme.txt", FtpTestData.Bytes(5, 1));
        using var client = await box.ConnectAsync();

        var ex = await Assert.ThrowsAsync<FtpException>(() => client.ListAsync("/user/app", box.Token));
        Assert.Equal(550, ex.Code);
        Assert.Equal("readme.txt", Assert.Single(await client.ListAsync("/adm", box.Token)).Name);
    }

    [Fact]
    public async Task Connect_ToFtpsrvWithItsShortFeatList_StillAssumesSizeAndRest()
    {
        await using var box = new FtpSandbox();
        box.Server.Feat = FakeFeat.FtpsrvMlsd;
        box.Server.Greeting = FtpsrvGreeting;
        using var client = await box.ConnectAsync();

        Assert.StartsWith("220-Welcome to ftpsrv.elf", client.Welcome, StringComparison.Ordinal);
        Assert.Equal(new[] { "MLSD", "KILL", "MTRW", "SELF", "SIZE", "REST STREAM" }, client.Features);
        Assert.True(client.SupportsSize);
        Assert.True(client.SupportsRest);
    }

    [Fact]
    public async Task Connect_ToAnotherServerWithoutRest_DoesNotInventRest()
    {
        await using var box = new FtpSandbox();
        box.Server.Feat = FakeFeat.FtpsrvMlsd;
        box.Server.Greeting = "220 Some other FTP server";
        using var client = await box.ConnectAsync();

        Assert.Equal(new[] { "MLSD", "KILL", "MTRW", "SELF" }, client.Features);
        Assert.False(client.SupportsRest);
        Assert.False(client.SupportsSize);
    }

    [Fact]
    public async Task List_OfAMissingFolder_ThrowsWithTheServerCode()
    {
        await using var box = new FtpSandbox();
        using var client = await box.ConnectAsync();
        var ex = await Assert.ThrowsAsync<FtpException>(() => client.ListAsync("/nope", box.Token));
        Assert.Equal(550, ex.Code);

        // Kênh điều khiển vẫn dùng tiếp được sau lỗi đó.
        Assert.Empty(await client.ListAsync("/", box.Token));
    }

    [Fact]
    public async Task FileCommands_WorkThroughTheClient()
    {
        await using var box = new FtpSandbox();
        box.Server.Feat = FakeFeat.Full;
        using var client = await box.ConnectAsync();

        await client.CreateDirectoryAsync("/work", box.Token);
        await client.CreateDirectoryAsync("/work/Thư mục", box.Token);
        Assert.True(await client.DirectoryExistsAsync("/work/Thư mục", box.Token));
        Assert.False(await client.DirectoryExistsAsync("/work/missing", box.Token));
        Assert.Equal("/", await client.GetWorkingDirectoryAsync(box.Token));
        var mkdirAgain = await Assert.ThrowsAsync<FtpException>(() => client.CreateDirectoryAsync("/work", box.Token));
        Assert.Equal(550, mkdirAgain.Code);

        var content = FtpTestData.Bytes(50_000, 3);
        await client.WriteAllBytesAsync("/work/Thư mục/tệp một.bin", content, box.Token);
        Assert.Equal(content, File.ReadAllBytes(box.Server.RealPath("/work/Thư mục/tệp một.bin")));
        Assert.Equal(content, await client.ReadAllBytesAsync("/work/Thư mục/tệp một.bin", box.Token));
        Assert.Equal(content.Length, await client.GetSizeAsync("/work/Thư mục/tệp một.bin", box.Token));
        Assert.Equal(-1, await client.GetSizeAsync("/work/missing.bin", box.Token));

        var stamp = new DateTime(2023, 4, 5, 6, 7, 8, DateTimeKind.Local);
        Assert.True(await client.SetModifiedAsync("/work/Thư mục/tệp một.bin", stamp, box.Token));
        Assert.Equal(stamp, await client.GetModifiedAsync("/work/Thư mục/tệp một.bin", box.Token));
        Assert.Null(await client.GetModifiedAsync("/work/missing.bin", box.Token));
        Assert.True(await client.ChmodAsync("/work/Thư mục/tệp một.bin", 0x1ED, box.Token));
        Assert.Equal(1, box.Server.CountCommands("SITE", "CHMOD 755 /work/Thư mục/tệp một.bin"));

        // RNTO trên ftpsrv đè im lặng tệp cùng tên (vì vậy giao diện phải hỏi trước).
        await client.WriteAllBytesAsync("/work/a.txt", "AAAA"u8.ToArray(), box.Token);
        await client.WriteAllBytesAsync("/work/b.txt", "BBBBBBBB"u8.ToArray(), box.Token);
        await client.RenameAsync("/work/a.txt", "/work/b.txt", box.Token);
        Assert.Equal(-1, await client.GetSizeAsync("/work/a.txt", box.Token));
        Assert.Equal(4, await client.GetSizeAsync("/work/b.txt", box.Token));

        await client.ChangeDirectoryAsync("/work", box.Token);
        Assert.Equal("/work", await client.GetWorkingDirectoryAsync(box.Token));
        await client.DeleteFileAsync("b.txt", box.Token);
        await client.DeleteFileAsync("/work/Thư mục/tệp một.bin", box.Token);
        var missing = await Assert.ThrowsAsync<FtpException>(() => client.DeleteFileAsync("/work/b.txt", box.Token));
        Assert.Equal(550, missing.Code);
        Assert.True(missing.Permanent);
        var notEmpty = await Assert.ThrowsAsync<FtpException>(() => client.RemoveDirectoryAsync("/work", box.Token));
        Assert.Equal(550, notEmpty.Code);
        await client.RemoveDirectoryAsync("/work/Thư mục", box.Token);
        await client.ChangeDirectoryAsync("/", box.Token);
        await client.RemoveDirectoryAsync("/work", box.Token);
        Assert.Empty(Directory.EnumerateFileSystemEntries(box.Server.Root));

        var raw = await client.SendRawAsync("XYZZY", box.Token);
        Assert.Equal(502, raw.Code);
        await Assert.ThrowsAsync<FtpException>(() => client.SendAsync("XYZZY", box.Token));
    }

    [Fact]
    public async Task DownloadAsync_WhenRestIsRefused_RestartsASeekableTarget()
    {
        await using var box = new FtpSandbox();
        box.Server.RefuseRest = true;
        var content = FtpTestData.Bytes(300_000, 4);
        box.ServerFile("/file.bin", content);
        using var client = await box.ConnectAsync();
        using var target = new MemoryStream();
        target.Write(content, 0, 1000);

        await client.DownloadAsync("/file.bin", target, 1000, null, box.Token);

        Assert.Equal(content, target.ToArray());
        Assert.Equal(1, box.Server.CountCommands("REST", "1000"));
    }

    [Fact]
    public async Task DownloadRangeAsync_ReadsExactlyTheRequestedBytes()
    {
        await using var box = new FtpSandbox();
        var content = FtpTestData.Bytes(400_000, 5);
        box.ServerFile("/file.bin", content);
        using var target = new MemoryStream();
        long lastProgress = 0;

        // Đoạn giữa tệp: kênh dữ liệu bị cắt sớm, nên client này phải bỏ đi sau đó.
        using (var client = await box.ConnectAsync())
        {
            await client.DownloadRangeAsync("/file.bin", target, 10_000, 150_000, p => lastProgress = p, box.Token);
        }

        Assert.Equal(content.AsSpan(10_000, 150_000).ToArray(), target.ToArray());
        Assert.Equal(150_000, lastProgress);

        // Đoạn cuối tệp: máy chủ tự tới cuối nên trả 226.
        using var tail = new MemoryStream();
        using (var client = await box.ConnectAsync())
        {
            Assert.True(await client.DownloadRangeAsync("/file.bin", tail, 300_000, 100_000, null, box.Token));
        }

        Assert.Equal(content.AsSpan(300_000).ToArray(), tail.ToArray());
    }
}
