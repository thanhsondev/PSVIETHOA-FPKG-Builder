using System.Security.Cryptography;
using PsViethoa.FpkgBuilder.Core.Ftp;

namespace PsViethoa.FpkgBuilder.Tests.Ftp;

/// <summary>
/// Một lượt thử FTP: thư mục tạm riêng (máy tính + gốc máy chủ), máy chủ giả, và hạn chờ chung ~30 giây để lỗi không bao giờ làm
/// treo cả lượt chạy thử.
/// </summary>
internal sealed class FtpSandbox : IAsyncDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource timeout = new(Limit);

    public FtpSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "psviethoa-ftp-tests", Guid.NewGuid().ToString("N"));
        Local = Path.Combine(Root, "local");
        Directory.CreateDirectory(Local);
        Server = new FakeFtpServer(Path.Combine(Root, "server"));
    }

    public string Root { get; }

    /// <summary>Thư mục "máy tính" của lượt thử.</summary>
    public string Local { get; }

    public FakeFtpServer Server { get; }

    public CancellationToken Token => timeout.Token;

    public FtpConnectionInfo Connection => Server.Connection;

    /// <summary>Đường dẫn trong thư mục máy tính của lượt thử.</summary>
    public string LocalPath(params string[] parts) => Path.Combine([Local, .. parts]);

    /// <summary>Client đã đăng nhập vào máy chủ giả.</summary>
    public async Task<Ps5Ftp> ConnectAsync(int timeoutSeconds = 10)
    {
        var client = new Ps5Ftp { TimeoutSeconds = timeoutSeconds };
        await client.ConnectAsync("127.0.0.1", Server.Port, "anonymous", Server.Password ?? "", Token).WaitAsync(Limit, Token);
        return client;
    }

    /// <summary>Hàng đợi trỏ tới máy chủ giả, thời gian chờ ngắn để lỗi lộ ra nhanh.</summary>
    public FtpTransferQueue NewQueue(Action<FtpTransferQueue>? configure = null)
    {
        var queue = new FtpTransferQueue(Connection) { TimeoutSeconds = 10 };
        configure?.Invoke(queue);
        return queue;
    }

    /// <summary>Chạy hàng đợi tới khi rảnh (sự kiện Idle); quá hạn thì dừng nó và báo lỗi.</summary>
    public async Task RunAsync(FtpTransferQueue queue)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Idle += () => idle.TrySetResult();
        queue.Start();
        try
        {
            await idle.Task.WaitAsync(Limit, Token);
        }
        catch (Exception)
        {
            queue.Stop();
            throw;
        }
    }

    /// <summary>Tạo tệp trong gốc máy chủ (đường dẫn kiểu "/a/b.bin").</summary>
    public string ServerFile(string remote, byte[] content)
    {
        var real = Server.RealPath(remote);
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllBytes(real, content);
        return real;
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        timeout.Dispose();
        for (var attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows có thể giữ tệp thêm một chút sau khi socket/tệp đóng.
                await Task.Delay(100);
            }
        }
    }
}

internal static class FtpTestData
{
    /// <summary>Dữ liệu giả ngẫu nhiên xác định (cùng hạt giống thì cùng nội dung).</summary>
    public static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
