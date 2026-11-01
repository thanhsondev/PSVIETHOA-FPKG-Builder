using PsViethoa.FpkgBuilder.Core.Ftp;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests.Ftp;

/// <summary>Các hàm đường dẫn của <see cref="Ps5Ftp"/> và các kiểu dữ liệu nhỏ của lõi FTP (không cần mạng).</summary>
public sealed class FtpPathAndModelTests
{
    [Theory]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("//", "/")]
    [InlineData("data", "/data")]
    [InlineData("/data/", "/data")]
    [InlineData("/data///", "/data")]
    [InlineData(@"\mnt\usb0\game", "/mnt/usb0/game")]
    [InlineData("  /user/app  ", "/user/app")]
    [InlineData("/Thư mục/Trò chơi", "/Thư mục/Trò chơi")]
    public void NormalizeDir_ProducesARootedPathWithoutTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, Ps5Ftp.NormalizeDir(input));
    }

    [Theory]
    [InlineData("/", "a.bin", "/a.bin")]
    [InlineData("", "a.bin", "/a.bin")]
    [InlineData("/data", "a.bin", "/data/a.bin")]
    [InlineData("/data/", "/a.bin", "/data/a.bin")]
    [InlineData("data", "sub/a b.bin", "/data/sub/a b.bin")]
    [InlineData(@"\mnt\usb0", "x", "/mnt/usb0/x")]
    public void Combine_JoinsWithExactlyOneSlash(string dir, string name, string expected)
    {
        Assert.Equal(expected, Ps5Ftp.Combine(dir, name));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/data", "/")]
    [InlineData("/data/", "/")]
    [InlineData("/data/homebrew/game", "/data/homebrew")]
    [InlineData("data/x", "/data")]
    [InlineData(@"\mnt\usb0\a.bin", "/mnt/usb0")]
    public void Parent_GoesUpOneLevelAndStopsAtRoot(string path, string expected)
    {
        Assert.Equal(expected, Ps5Ftp.Parent(path));
    }

    [Theory]
    [InlineData("/data/a.bin", "a.bin")]
    [InlineData("/data/game/", "game")]
    [InlineData("a.bin", "a.bin")]
    [InlineData(@"\mnt\usb0\Trò chơi.pkg", "Trò chơi.pkg")]
    [InlineData("/", "")]
    [InlineData("", "")]
    public void FileName_ReturnsTheLastSegment(string path, string expected)
    {
        Assert.Equal(expected, Ps5Ftp.FileName(path));
    }

    [Theory]
    [InlineData("drwxr-xr-x", 0x1ED)]
    [InlineData("-rw-r--r--", 0x1A4)]
    [InlineData("lrwxrwxrwx", 0x1FF)]
    [InlineData("----------", 0)]
    [InlineData("-rwsr-sr-t", 0x1ED)]
    [InlineData("-rw-------x", 0x180)]
    public void PermissionsToOctal_ReadsTheNineModeCharacters(string permissions, int expected)
    {
        Assert.Equal(expected, Ps5Ftp.PermissionsToOctal(permissions));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rwxr-xr-x")]
    [InlineData("755")]
    public void PermissionsToOctal_ReturnsNullForAnythingElse(string? permissions)
    {
        Assert.Null(Ps5Ftp.PermissionsToOctal(permissions));
    }

    [Fact]
    public void PermissionsToOctal_MatchesTheOctalDigits()
    {
        // 0x1ED == 0755, 0x1A4 == 0644: kiểm tra lại bằng chuỗi bát phân mà SITE CHMOD gửi đi.
        Assert.Equal("755", Convert.ToString(Ps5Ftp.PermissionsToOctal("drwxr-xr-x")!.Value, 8));
        Assert.Equal("644", Convert.ToString(Ps5Ftp.PermissionsToOctal("-rw-r--r--")!.Value, 8));
        Assert.Equal("777", Convert.ToString(new FtpEntry("x", "/x", FtpEntryKind.Link, 1, null, "lrwxrwxrwx", "/y").OctalMode!.Value, 8));
    }

    [Theory]
    [InlineData(150, false, true)]
    [InlineData(200, true, false)]
    [InlineData(226, true, false)]
    [InlineData(331, false, true)]
    [InlineData(350, false, true)]
    [InlineData(421, false, false)]
    [InlineData(550, false, false)]
    public void FtpReply_ClassifiesTheCode(int code, bool ok, bool intermediate)
    {
        var reply = new FtpReply(code, code + " text");
        Assert.Equal(ok, reply.Ok);
        Assert.Equal(intermediate, reply.Intermediate);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(421, false)]
    [InlineData(450, false)]
    [InlineData(500, true)]
    [InlineData(550, true)]
    [InlineData(599, true)]
    public void FtpException_OnlyFiveHundredsArePermanent(int code, bool permanent)
    {
        var ex = new FtpException(code, "x");
        Assert.Equal(code, ex.Code);
        Assert.Equal(permanent, ex.Permanent);
    }

    [Fact]
    public void SmallTypes_FormatAndDeriveAsExpected()
    {
        var when = new DateTime(2026, 11, 1, 9, 5, 7);
        // Dấu ngăn giờ theo văn hoá của máy, nên so với cùng định dạng thay vì chuỗi cố định.
        Assert.Equal($"{when:HH:mm:ss}  →  LIST /", new FtpLogLine(true, "LIST /", when).ToString());
        Assert.Equal($"{when:HH:mm:ss}  ←  226 Done", new FtpLogLine(false, "226 Done", when).ToString());
        Assert.Equal("anonymous@192.168.1.2:2121", new FtpConnectionInfo("192.168.1.2", Ps5Ftp.DefaultPort, "anonymous", "pw").ToString());

        var directory = new FtpEntry("a", "/a", FtpEntryKind.Directory, 0, null, "drwxr-xr-x", null);
        var link = new FtpEntry("l", "/l", FtpEntryKind.Link, 0, null, "lrwxrwxrwx", "/a");
        Assert.True(directory.IsDirectory);
        Assert.False(link.IsDirectory);
        Assert.Null(new FtpEntry("f", "/f", FtpEntryKind.File, 0, null, "", null).OctalMode);
    }

    [Fact]
    public void TransferItem_DerivesNameAndPercent()
    {
        var upload = new FtpTransferItem(FtpDirection.Upload, Path.Combine("local", "Trò chơi.bin"), "/data/other.bin", 200);
        var download = new FtpTransferItem(FtpDirection.Download, Path.Combine("local", "ignored.bin"), "/data/Tệp tải.bin", -1);
        Assert.Equal("Trò chơi.bin", upload.Name);
        Assert.Equal("Tệp tải.bin", download.Name);
        Assert.Equal(FtpItemState.Queued, upload.State);
        Assert.Equal(1, upload.Segments);
        Assert.Equal(-1, upload.Estimate);
        Assert.Equal("", upload.Error);

        Assert.Equal(0, upload.Percent);
        upload.Transferred = 50;
        Assert.Equal(25, upload.Percent);
        upload.Transferred = 500;
        Assert.Equal(100, upload.Percent);

        // Tệp rỗng (hoặc chưa rõ cỡ) chỉ đạt 100 % khi đã xong.
        Assert.Equal(0, download.Percent);
        download.State = FtpItemState.Done;
        Assert.Equal(100, download.Percent);
        download.Size = 0;
        download.Speed = 1234.5;
        Assert.Equal(100, download.Percent);
        Assert.Equal(1234.5, download.Speed);
    }

    [Fact]
    public void TransferJob_IsAPlainDescription()
    {
        var before = DateTime.Now;
        var job = new FtpTransferJob(FtpDirection.Download, "Game", "/data/Game", Path.Combine("out", "Game")) { IsFolder = true };
        Assert.Equal(FtpDirection.Download, job.Direction);
        Assert.Equal("Game", job.Name);
        Assert.Equal("/data/Game", job.Source);
        Assert.Equal(Path.Combine("out", "Game"), job.Target);
        Assert.True(job.IsFolder);
        Assert.InRange(job.Created, before.AddSeconds(-1), DateTime.Now.AddSeconds(1));
        Assert.False(new FtpTransferJob(FtpDirection.Upload, "a", "b", "c").IsFolder);
    }
}

/// <summary>Mô hình ước lượng thời gian còn lại (<see cref="FtpEtaModel"/>): khớp đúng hệ số từ mẫu sạch và ổn định với tải lệch.</summary>
public sealed class FtpEtaModelTests
{
    private const double PriorPerFile = 0.05;
    private const double PriorPerMb = 0.01;

    [Fact]
    public void StartsAtThePriorsAndBecomesReadyAfterThreeSamples()
    {
        var model = new FtpEtaModel();
        Assert.Equal(PriorPerFile, model.SecondsPerFile, 1e-9);
        Assert.Equal(PriorPerMb, model.SecondsPerMb, 1e-9);
        Assert.False(model.Ready);

        model.Add(1, 10, 5 * 1048576, 2);
        model.Add(1, 10, 5 * 1048576, 2);
        Assert.False(model.Ready);
        model.Add(1, 10, 5 * 1048576, 2);
        Assert.True(model.Ready);
        Assert.Equal(3, model.Samples);

        model.Reset();
        Assert.Equal(0, model.Samples);
        Assert.False(model.Ready);
        Assert.Equal(PriorPerFile, model.SecondsPerFile, 1e-9);
        Assert.Equal(PriorPerMb, model.SecondsPerMb, 1e-9);
    }

    [Fact]
    public void IgnoresSamplesWithoutTimeOrWork()
    {
        var model = new FtpEtaModel();
        model.Add(0, 10, 1000);
        model.Add(-1, 10, 1000);
        model.Add(1, 0, 0);
        model.Add(1, -5, -10);
        Assert.Equal(0, model.Samples);
        Assert.Equal(PriorPerFile, model.SecondsPerFile, 1e-9);
    }

    [Fact]
    public void LearnsPerFileAndPerMegabyteCostsFromAMixedWorkload()
    {
        const double perFile = 0.03;
        const double perMb = 0.02;
        var model = new FtpEtaModel();
        for (var i = 0; i < 40; i++)
        {
            var files = (i * 37) % 120 + 5;
            var mb = (i * 53) % 40 + 1;
            model.Add(files / 4.0 * perFile + mb * perMb, files, mb * 1048576L, 4);
        }

        Assert.Equal(perFile, model.SecondsPerFile, 1e-4);
        Assert.Equal(perMb, model.SecondsPerMb, 1e-4);
        // 1 000 tệp nhỏ trên 16 luồng + 2 GB qua đường chung.
        var expected = 1000 / 16.0 * perFile + 2048 * perMb;
        Assert.Equal(expected, model.Cost(1000, 2048L * 1048576, 16), 1e-6);
    }

    [Fact]
    public void OneSidedWorkloadsKeepThePriorForTheUnseenCost()
    {
        var smallFiles = new FtpEtaModel();
        for (var i = 0; i < 30; i++)
        {
            var files = 50 + i % 7;
            smallFiles.Add(files / 8.0 * 0.04, files, 0, 8);
        }

        Assert.Equal(0.04, smallFiles.SecondsPerFile, 1e-4);
        Assert.Equal(PriorPerMb, smallFiles.SecondsPerMb, 1e-4);

        var oneBigFile = new FtpEtaModel();
        for (var i = 0; i < 30; i++)
        {
            var mb = 100 + i % 5;
            oneBigFile.Add(mb * 0.008, 0, mb * 1048576L, 2);
        }

        Assert.Equal(PriorPerFile, oneBigFile.SecondsPerFile, 1e-4);
        Assert.Equal(0.008, oneBigFile.SecondsPerMb, 1e-4);
    }

    [Fact]
    public void ClampsAbsurdCostsAndTreatsFewerThanOneWorkerAsOne()
    {
        var model = new FtpEtaModel();
        for (var i = 0; i < 10; i++)
        {
            model.Add(1000, 10, 0, 1);
        }

        Assert.Equal(10, model.SecondsPerFile, 1e-9);
        Assert.Equal(model.Cost(20, 0, 1), model.Cost(20, 0, 0.25), 1e-9);
        Assert.Equal(model.Cost(20, 0, 1) / 4, model.Cost(20, 0, 4), 1e-9);
        Assert.Equal(model.Cost(5, 0), model.Cost(5, -100), 1e-9);
    }
}
