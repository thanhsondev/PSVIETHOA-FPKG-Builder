using System.Diagnostics;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Kết quả quét trước: số tệp đã mở, số tệp không mở được (SDK sẽ tự báo lỗi nếu thật sự thiếu) và thời gian.</summary>
public sealed record SonySdkPrescanResult(int Files, int Failed, TimeSpan Elapsed)
{
    public double FilesPerSecond => Elapsed.TotalSeconds > 0 ? Files / Elapsed.TotalSeconds : Files;
}

/// <summary>
/// Mở trước song song mọi tệp nguồn của GP5 (chỉ đọc 1 byte) trước khi Publishing Tools chạy. Đo trên Windows 11 bật Windows
/// Defender: tệp mở LẦN ĐẦU bị quét ~11 ms dù tiến trình nào mở, còn pha "Checking files to see if compression/conversion is
/// needed" của <c>img_create</c> mở tuần tự từng tệp — 2 011 tệp mới mất 22,5 s, lượt thứ hai (đã có kết quả quét trong bộ nhớ
/// đệm của Defender) 0,37 s; Ghost of Yōtei (95 386 tệp) mất hàng chục phút chỉ ở pha này. Mở song song 16–32 luồng đạt ~700
/// tệp/giây (so với ~90 khi tuần tự) nên pha kiểm tra của SDK gần như tức thì. Chỉ mở để đọc: không ghi, không đổi thời gian
/// truy cập (đã kiểm tra trên exFAT), GP5 và gói không đổi một byte.
/// </summary>
public static class SonySdkPrescan
{
    /// <summary>Chỉ có ích trên Windows (Defender quét khi mở tệp); Wine trên macOS không có bước quét này.</summary>
    public static bool Applicable => OperatingSystem.IsWindows();

    /// <summary>Số luồng: Defender không nhanh thêm quá ~32 yêu cầu đồng thời (đo 4 → 424, 16 → 698, 32 → 740, 64 → 683 tệp/giây).</summary>
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount * 2, 8, 32);

    public static SonySdkPrescanResult Run(IReadOnlyList<string> paths, int parallelism, Action<int, int>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var done = 0;
        var failed = 0;
        Parallel.ForEach(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            () => new byte[1],
            (path, state, buffer) =>
            {
                try
                {
                    // bufferSize 0: không có bộ đệm của FileStream, chỉ một lần đọc 1 byte — đủ để bộ lọc chống mã độc quét tệp.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, FileOptions.None);
                    _ = stream.Read(buffer, 0, 1);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref failed);
                }

                var count = Interlocked.Increment(ref done);
                if (count % 1000 == 0)
                {
                    progress?.Invoke(count, paths.Count);
                }

                return buffer;
            },
            _ => { });
        progress?.Invoke(done, paths.Count);
        return new SonySdkPrescanResult(done, failed, stopwatch.Elapsed);
    }
}
