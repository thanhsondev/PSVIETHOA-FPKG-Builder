using LibProsperoPkg.Content;
using PsViethoa.FpkgBuilder.Core.ExFat;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Trạng thái một tệp UCP của nguồn.</summary>
public enum UcpState
{
    /// <summary>Cấu trúc hợp lệ, digest SHA-1 khớp.</summary>
    Valid,

    /// <summary>Cấu trúc hợp lệ nhưng digest SHA-1 ở header không khớp nội dung — sửa được bằng cách tính lại digest.</summary>
    StaleDigest,

    /// <summary>Không đọc được như một UCP (bản niêm phong/mã hoá của gói gốc, tệp cụt…) — không sửa được.</summary>
    Invalid,
}

/// <summary>
/// Tệp UCP trong <c>sce_sys/trophy2</c> (cúp) và <c>sce_sys/uds</c> (hoạt động, Universal Data System) của bản dump. Engine LibProsperoPkg
/// có hàm tính lại digest SHA-1 (<c>EnsureUcpArchives</c>) nhưng nó không chạy trong luồng publisher mà fpkg-gui và công cụ dùng (đo với
/// 0.6.12: gói vẫn mang nguyên UCP lệch), còn Publishing Tools từ chối cả lượt tạo gói ("ucp_header.ucp_digest is invalid") — nên công cụ tự
/// sửa ở cả hai đường. UCP lệch trong gói không làm game dừng lúc mở mà lỗi ở lúc game mở cúp hay gửi sự kiện hoạt động — tức là chơi được
/// một đoạn rồi mới lỗi.
/// </summary>
public static class UcpInspector
{
    /// <summary>Thư mục con của sce_sys chứa UCP (như engine).</summary>
    public static readonly IReadOnlyList<string> Folders = ["trophy2", "uds"];

    /// <summary>UCP lớn hơn mức này không được đọc vào bộ nhớ để kiểm tra (engine cũng giới hạn 128 MiB).</summary>
    public const long MaxSize = 128L * 1024 * 1024;

    /// <summary>Đường dẫn trong gói (a/b/c) có phải UCP engine kiểm tra không: sce_sys/trophy2/*.ucp hoặc sce_sys/uds/*.ucp.</summary>
    public static bool IsCandidate(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        return parts.Length == 3 &&
               parts[0].Equals("sce_sys", StringComparison.OrdinalIgnoreCase) &&
               Folders.Contains(parts[1], StringComparer.OrdinalIgnoreCase) &&
               parts[2].EndsWith(".ucp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Kiểm tra nội dung một UCP; <paramref name="error"/> là lý do khi <see cref="UcpState.Invalid"/>.</summary>
    public static UcpState Inspect(ReadOnlySpan<byte> data, out string? error)
    {
        if (!ProsperoUcp.IsUcp(data))
        {
            error = "not a UCP archive (bad magic or truncated header)";
            return UcpState.Invalid;
        }

        if (!ProsperoUcp.Validate(data, out error))
        {
            return UcpState.Invalid;
        }

        error = null;
        return ProsperoUcp.VerifyDigest(data) ? UcpState.Valid : UcpState.StaleDigest;
    }

    /// <summary>Bản sao với digest đã tính lại (nội dung các mục giữ nguyên).</summary>
    public static byte[] Repair(ReadOnlySpan<byte> data) => ProsperoUcp.WithRepairedDigest(data);

    /// <summary>
    /// Các UCP trong sce_sys của một thư mục ứng dụng kèm trạng thái (bỏ qua UCP hợp lệ). Đọc lỗi hoặc quá lớn thì bỏ qua — kiểm tra
    /// này chỉ để ghi nhật ký, không chặn việc tạo gói.
    /// </summary>
    public static IReadOnlyList<(string Relative, UcpState State, string? Error)> ScanFolder(string appFolder)
    {
        var result = new List<(string, UcpState, string?)>();
        foreach (var folder in Folders)
        {
            var directory = Path.Combine(appFolder, "sce_sys", folder);
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    if (!file.Name.EndsWith(".ucp", StringComparison.OrdinalIgnoreCase) || file.Length > MaxSize || JunkFileFinder.IsJunkFileName(file.Name))
                    {
                        continue;
                    }

                    var state = Inspect(File.ReadAllBytes(file.FullName), out var error);
                    if (state != UcpState.Valid)
                    {
                        result.Add(("sce_sys/" + new DirectoryInfo(directory).Name + "/" + file.Name, state, error));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    /// <summary>Như <see cref="ScanFolder"/> nhưng đọc thẳng trong ảnh exFAT (không gắn, không giải nén).</summary>
    public static IReadOnlyList<(string Relative, UcpState State, string? Error)> ScanImage(ExFatImage image, ExFatEntry appRoot)
    {
        var result = new List<(string, UcpState, string?)>();
        try
        {
            if (image.Find("sce_sys", appRoot) is not { IsDirectory: true } sceSys)
            {
                return result;
            }

            foreach (var folder in image.Enumerate(sceSys).Where(entry => entry.IsDirectory && Folders.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)))
            {
                foreach (var file in image.Enumerate(folder).Where(entry => !entry.IsDirectory && entry.Name.EndsWith(".ucp", StringComparison.OrdinalIgnoreCase) &&
                                                                             entry.Length <= MaxSize && !JunkFileFinder.IsJunkFileName(entry.Name)))
                {
                    using var stream = image.OpenRead(file);
                    var data = new byte[file.Length];
                    stream.ReadExactly(data);
                    var state = Inspect(data, out var error);
                    if (state != UcpState.Valid)
                    {
                        result.Add(("sce_sys/" + folder.Name + "/" + file.Name, state, error));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }

        return result;
    }
}
