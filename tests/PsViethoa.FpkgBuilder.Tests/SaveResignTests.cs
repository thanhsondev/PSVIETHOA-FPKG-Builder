using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PsViethoa.FpkgBuilder.Core.Saves;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Resign save (cơ chế PSVIETHOA STUDIO): đọc / sửa param.sfo của save PS4 (ACCOUNT_ID uint64) và PS5 (ACCOUNT_ID nhị phân, PARAMS),
/// nhận loại tệp, resign save đã giải mã ngay trên máy tính (thư mục, param.sfo, .zip), giải nén zip an toàn, ping garlic-savemgr và
/// gửi ELF. Mọi dữ liệu save đều tổng hợp.
/// </summary>
public sealed class SaveResignTests : IDisposable
{
    private const ulong OldAccount = 0x1111111111111111;
    private const ulong NewAccount = 0x1ab6ffd98ef91465;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-save-" + Guid.NewGuid().ToString("N"));

    public SaveResignTests()
    {
        Directory.CreateDirectory(_root);
        SaveResigner.BackupRoot = Path.Combine(_root, "backups");
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

    private sealed record Field(string Key, ushort Format, byte[] Data, int Max);

    /// <summary>param.sfo của save: ACCOUNT_ID (PS4 0x0404 / PS5 0x0004), PARAMS, SAVEDATA_DIRECTORY, TITLE_ID.</summary>
    private static byte[] SaveSfoBytes(bool ps5, ulong account = OldAccount, string title = "CUSA00001", string dir = "SAVEDATA00")
    {
        var accountBytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(accountBytes, account);
        var paramsData = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(paramsData.AsSpan(4), 0x10000001);
        var fields = new List<Field>
        {
            new("ACCOUNT_ID", (ushort)(ps5 ? 0x0004 : 0x0404), accountBytes, 8),
            new("PARAMS", 0x0004, paramsData, 1024),
            new("SAVEDATA_DIRECTORY", 0x0204, Encoding.UTF8.GetBytes(dir + "\0"), 32),
            new("TITLE", 0x0204, Encoding.UTF8.GetBytes("Save tổng hợp\0"), 128),
            new("TITLE_ID", 0x0204, Encoding.UTF8.GetBytes(title + "\0"), 16),
        };
        var keyTable = 20 + fields.Count * 16;
        var keyBytes = fields.Sum(f => f.Key.Length + 1);
        var dataTable = (keyTable + keyBytes + 3) & ~3;
        var buffer = new byte[dataTable + fields.Sum(f => f.Max)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0x46535000);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 0x0101);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), (uint)keyTable);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), (uint)dataTable);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), (uint)fields.Count);
        int keyOffset = 0, dataOffset = 0;
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var index = buffer.AsSpan(20 + 16 * i);
            BinaryPrimitives.WriteUInt16LittleEndian(index, (ushort)keyOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(index[2..], f.Format);
            BinaryPrimitives.WriteUInt32LittleEndian(index[4..], (uint)f.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(index[8..], (uint)f.Max);
            BinaryPrimitives.WriteUInt32LittleEndian(index[12..], (uint)dataOffset);
            Encoding.ASCII.GetBytes(f.Key).CopyTo(buffer, keyTable + keyOffset);
            f.Data.CopyTo(buffer, dataTable + dataOffset);
            keyOffset += f.Key.Length + 1;
            dataOffset += f.Max;
        }

        return buffer;
    }

    private string DecryptedSave(string name, bool ps5 = false)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "param.sfo"), SaveSfoBytes(ps5));
        File.WriteAllBytes(Path.Combine(folder, "game.dat"), Encoding.ASCII.GetBytes("payload"));
        return folder;
    }

    private static ulong? Account(string sfo)
    {
        var bytes = File.ReadAllBytes(sfo);
        return SaveSfo.ReadAccountId(bytes, SaveSfo.Parse(bytes));
    }

    private static ResignOptions Offline(string? title = null) => new(NewAccount, 0x179a0cd8, "127.0.0.1", 8082, false, title);

    // ── param.sfo ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1ab6ffd98ef91465", 8, "1ab6ffd98ef91465")]
    [InlineData("0x1AB6FFD98EF91465", 8, "1ab6ffd98ef91465")]
    [InlineData(" 179A0CD8 ", 4, "179a0cd8")]
    [InlineData("179a0cd", 4, null)]
    [InlineData("zz9a0cd8", 4, null)]
    [InlineData("", 8, null)]
    public void NormalizeHex_AcceptsOnlyExactHex(string input, int bytes, string? expected) => Assert.Equal(expected, SaveResigner.NormalizeHex(input, bytes));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sfo_PatchesAccountIdForPs4AndPs5(bool ps5)
    {
        var buffer = SaveSfoBytes(ps5);
        var entries = SaveSfo.Parse(buffer);
        Assert.Equal(ps5, SaveSfo.IsPs5Format(buffer, entries));
        Assert.Equal(OldAccount, SaveSfo.ReadAccountId(buffer, entries));
        var length = buffer.Length;
        SaveSfo.PatchAccountId(buffer, entries, NewAccount);
        SaveSfo.PatchString(buffer, entries, "TITLE_ID", "PPSA12345");
        SaveSfo.PatchPs5UserId(buffer, entries, 0x179a0cd8);
        var again = SaveSfo.Parse(buffer);
        Assert.Equal(length, buffer.Length);
        Assert.Equal(NewAccount, SaveSfo.ReadAccountId(buffer, again));
        Assert.Equal("PPSA12345", SaveSfo.ReadString(buffer, again, "TITLE_ID"));
        Assert.Equal("SAVEDATA00", SaveSfo.ReadString(buffer, again, "SAVEDATA_DIRECTORY"));
        var param = SaveSfo.Find(again, "PARAMS")!;
        Assert.Equal(0x179a0cd8u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(param.DataOffset + 4)));
    }

    [Fact]
    public void Sfo_RejectsDamagedFilesAndTooLongStrings()
    {
        Assert.Throws<SfoException>(() => SaveSfo.Parse(new byte[10]));
        var wrongMagic = SaveSfoBytes(false);
        wrongMagic[0] = 0x7F;
        Assert.Throws<SfoException>(() => SaveSfo.Parse(wrongMagic));
        var buffer = SaveSfoBytes(false);
        Assert.Throws<SfoException>(() => SaveSfo.PatchString(buffer, SaveSfo.Parse(buffer), "TITLE_ID", new string('A', 40)));
        Assert.Throws<SfoException>(() => SaveSfo.PatchString(buffer, SaveSfo.Parse(buffer), "MISSING", "x"));
    }

    [Fact]
    public void ProcessPath_KeepsFirstBackupAndPatchesInPlace()
    {
        var folder = DecryptedSave("SAVEDATA00");
        var sfo = Path.Combine(folder, "sce_sys", "param.sfo");
        var original = File.ReadAllBytes(sfo);
        var log = new List<string>();
        Assert.True(SaveSfo.ProcessPath(folder, NewAccount, "CUSA99999", backup: true, log.Add));
        Assert.Equal(NewAccount, Account(sfo));
        Assert.Equal(original, File.ReadAllBytes(sfo + ".bak"));
        Assert.True(SaveSfo.ProcessPath(folder, OldAccount, null, backup: true, log.Add));
        Assert.Equal(original, File.ReadAllBytes(sfo + ".bak"));
        Assert.Contains(log, line => line.Contains("[OK]", StringComparison.Ordinal));
        Assert.False(SaveSfo.ProcessPath(Path.Combine(_root, "nothing"), NewAccount, null, true, log.Add));
    }

    // ── Nhận loại ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_RecognizesEveryKind()
    {
        var folder = DecryptedSave("SAVEDATA00");
        var ps5 = Path.Combine(_root, "sdimg_SAVEDATA00");
        var image = new byte[0x2000];
        "pfsSKKey"u8.CopyTo(image.AsSpan(0x800));
        File.WriteAllBytes(ps5, image);
        var ps4Image = Path.Combine(_root, "sdimg_GAME00");
        var ps4Data = new byte[0x8000];
        ps4Data[0] = 1;
        File.WriteAllBytes(ps4Image, ps4Data);
        var key = Path.Combine(_root, "GAME00.bin");
        var keyData = new byte[0x60];
        "pfsSKKey"u8.CopyTo(keyData);
        File.WriteAllBytes(key, keyData);
        var zip = Path.Combine(_root, "save.zip");
        ZipFile.CreateFromDirectory(folder, zip);
        var other = Path.Combine(_root, "notes.txt");
        File.WriteAllText(other, "hi");

        Assert.Equal(SaveKind.Folder, SaveResigner.Classify(folder));
        Assert.Equal(SaveKind.ParamSfo, SaveResigner.Classify(Path.Combine(folder, "sce_sys", "param.sfo")));
        Assert.Equal(SaveKind.Ps5Encrypted, SaveResigner.Classify(ps5));
        Assert.Equal(SaveKind.Ps4Encrypted, SaveResigner.Classify(ps4Image));
        Assert.Equal(SaveKind.Ps4Key, SaveResigner.Classify(key));
        Assert.Equal(SaveKind.Zip, SaveResigner.Classify(zip));
        Assert.Equal(SaveKind.Other, SaveResigner.Classify(other));
        Assert.Equal(key, SaveResigner.FindPs4KeyForImage(ps4Image));

        Assert.True(SaveResigner.NeedsConsole(ps5, install: false));
        Assert.True(SaveResigner.NeedsConsole(ps4Image, install: false));
        Assert.False(SaveResigner.NeedsConsole(folder, install: false));
        Assert.False(SaveResigner.NeedsConsole(zip, install: false));
        Assert.True(SaveResigner.NeedsConsole(zip, install: true));
    }

    // ── Resign trên máy tính (không cần PS5) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProcessAny_DecryptedFolder_ResignsOffline()
    {
        var folder = DecryptedSave("SAVEDATA00");
        var log = new List<string>();
        var result = SaveResigner.ProcessAny(folder, Offline("CUSA12345"), log.Add);
        Assert.Equal(new ResignCount(1, 0), result);
        var sfo = Path.Combine(folder, "sce_sys", "param.sfo");
        Assert.Equal(NewAccount, Account(sfo));
        var bytes = File.ReadAllBytes(sfo);
        Assert.Equal("CUSA12345", SaveSfo.ReadString(bytes, SaveSfo.Parse(bytes), "TITLE_ID"));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(folder, "game.dat")));
    }

    [Fact]
    public void ProcessAny_Zip_WritesResignedCopyAndKeepsSource()
    {
        var folder = DecryptedSave("SAVEDATA00");
        var zip = Path.Combine(_root, "save.zip");
        ZipFile.CreateFromDirectory(folder, zip);
        var before = File.ReadAllBytes(zip);
        var result = SaveResigner.ProcessAny(zip, Offline(), _ => { });
        Assert.Equal(new ResignCount(1, 0), result);
        Assert.Equal(before, File.ReadAllBytes(zip));
        var resigned = Path.Combine(_root, "save_resigned.zip");
        Assert.True(File.Exists(resigned));
        using var archive = ZipFile.OpenRead(resigned);
        var entry = archive.Entries.Single(e => e.FullName.EndsWith("param.sfo", StringComparison.Ordinal));
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var sfo = memory.ToArray();
        Assert.Equal(NewAccount, SaveSfo.ReadAccountId(sfo, SaveSfo.Parse(sfo)));
    }

    [Fact]
    public void ProcessAny_ReportsUnknownAndMissingAndSkipsKeys()
    {
        var other = Path.Combine(_root, "notes.txt");
        File.WriteAllText(other, "hi");
        Assert.Equal(new ResignCount(0, 1), SaveResigner.ProcessAny(other, Offline(), _ => { }));
        Assert.Equal(new ResignCount(0, 1), SaveResigner.ProcessAny(Path.Combine(_root, "missing"), Offline(), _ => { }));
        var key = Path.Combine(_root, "GAME00.bin");
        var keyData = new byte[0x60];
        "pfsSKKey"u8.CopyTo(keyData);
        File.WriteAllBytes(key, keyData);
        Assert.Equal(new ResignCount(0, 0), SaveResigner.ProcessAny(key, Offline(), _ => { }));
    }

    [Fact]
    public void SafeExtractZip_RefusesPathsOutsideTarget()
    {
        var zip = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open()))
            {
                writer.Write("x");
            }

            using (var writer = new StreamWriter(archive.CreateEntry("save/ok.txt").Open()))
            {
                writer.Write("ok");
            }
        }

        var target = Path.Combine(_root, "out");
        Directory.CreateDirectory(target);
        Assert.True(SaveResigner.SafeExtractZip(zip, target));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.Equal("ok", File.ReadAllText(Path.Combine(target, "save", "ok.txt")));
        File.WriteAllText(Path.Combine(_root, "broken.zip"), "not a zip");
        Assert.False(SaveResigner.SafeExtractZip(Path.Combine(_root, "broken.zip"), target));
    }

    // ── Mạng (máy chủ giả trên 127.0.0.1) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PingGarlic_DetectsServiceAndRejectsOthers()
    {
        using var listener = new HttpListenerStub("{\"mounted\":false,\"ps5param_version\":1}");
        Assert.True(SaveResigner.PingGarlic("127.0.0.1", listener.Port, 5).Ok);
        using var other = new HttpListenerStub("{\"hello\":1}");
        Assert.False(SaveResigner.PingGarlic("127.0.0.1", other.Port, 5).Ok);
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Assert.False(SaveResigner.PingGarlic("127.0.0.1", port, 2).Ok);
    }

    [Fact]
    public async Task SendElf_DeliversBundledPayload()
    {
        var elf = SaveResigner.BundledElf();
        Assert.NotNull(elf);
        Assert.True(elf!.Length > 100_000);
        Assert.Equal(0x7F, elf[0]);
        Assert.Equal((byte)'E', elf[1]);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receive = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var memory = new MemoryStream();
            await client.GetStream().CopyToAsync(memory);
            return memory.ToArray();
        });
        long reported = 0;
        SaveResigner.SendElf("127.0.0.1", elf, null, (sent, _) => reported = sent, port);
        var received = await receive.WaitAsync(TimeSpan.FromSeconds(20));
        listener.Stop();
        Assert.Equal(elf, received);
        Assert.Equal(elf.Length, reported);
    }

    /// <summary>Máy chủ HTTP tối giản trả về một JSON cho mọi yêu cầu.</summary>
    private sealed class HttpListenerStub : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public HttpListenerStub(string json)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var body = Encoding.UTF8.GetBytes(json);
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        var read = 0;
                        while (read < buffer.Length)
                        {
                            var n = await stream.ReadAsync(buffer.AsMemory(read));
                            if (n == 0)
                            {
                                break;
                            }

                            read += n;
                            if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                            {
                                break;
                            }
                        }

                        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header);
                        await stream.WriteAsync(body);
                    }
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
