using System.Buffers.Binary;
using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.ExFat;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Bộ playgo* của bản dump (sce_sys/playgo-chunk.dat, playgo-hash-table.dat, playgo-ficm.dat, playgo-scenario.json…) mô tả bố cục
/// khối của gói gốc. Công cụ tạo ra một ảnh liền khối và thư viện tự sinh bộ PlayGo riêng, nên bộ cũ còn trong gói làm PS5 đi tìm
/// những khối không tồn tại: mở game là bật lỗi ứng dụng ("application error"), log báo lỗi "playgo". Drakmor (2026-09-14): "if the
/// games don't even launch and the log shows a playgo error, you need to delete the /sce_sys/playgo* files before packaging" và
/// "delete playgo solves the application error when starting the game, no black screen".
/// <para>
/// KHÁC với bệnh đứng ở màn hình splash mà không báo lỗi (Stellar Blade, Ghost of Yōtei): bệnh đó nằm ở kernel
/// (prevent_flat_search/APR), không sửa được từ khâu đóng gói, và bản dump của chúng thường không có tệp playgo* nào.
/// </para>
/// <para>
/// Lớp này CHỈ đụng vào dữ liệu trong <c>sce_sys</c>, không bao giờ đụng vào module trong <c>fakelib</c>. Riêng
/// <c>fakelib/libScePlayGo.sprx</c> (và <c>fakelib/libSceAmpr.sprx</c>) thì chính engine LibProsperoPkg tự loại khỏi gói —
/// đã kiểm chứng bằng cách tạo gói thật: hai tệp đó biến mất khỏi <c>fakelib</c> trong khi mọi module khác, và cả bản sao
/// trong <c>fakelib2</c>, vẫn còn. Công cụ không thêm cũng không ngăn được việc đó.
/// </para>
/// <para>
/// Từ engine fpkg-gui 0.6.8, thư viện KHÔNG còn chép bộ playgo* của nguồn vào gói nữa: nó chỉ lấy số khối và số kịch bản từ
/// playgo-chunk.dat (hoặc scenarioCount trong playgo-scenario.json khi thiếu chunk.dat) rồi tạo lại toàn bộ bảng cho ảnh mới.
/// Đổi lại, tệp đầu vào hỏng làm cả lượt tạo gói dừng ("Invalid PS5 playgo-chunk.dat header."), nên khi giữ bộ playgo* công cụ
/// kiểm tra trước bằng đúng quy tắc của engine (<see cref="ReadChunkCounts"/>, <see cref="ReadScenarioCount"/>) và bỏ tệp hỏng.
/// </para>
/// Lớp này chỉ liệt kê; việc đưa tệp ra ngoài lúc tạo gói do BuildEngine làm (không xoá gì trong thư mục nguồn).
/// </summary>
public static class PlayGoCleanup
{
    public const string Folder = "sce_sys";

    public const string Prefix = "playgo";

    /// <summary>
    /// Module stub PlayGo trong fakelib. Công cụ KHÔNG bao giờ đụng vào tệp này; engine LibProsperoPkg tự loại nó khỏi gói
    /// (giống fakelib/libSceAmpr.sprx). Hằng số này chỉ để đối chiếu và ghi chú.
    /// </summary>
    public const string EmuModule = "fakelib/libScePlayGo.sprx";

    public const string ChunkFile = "playgo-chunk.dat";

    public const string ScenarioFile = "playgo-scenario.json";

    /// <summary>Giới hạn của engine 0.6.8 (ProsperoPlayGo.ReadSourceCounts).</summary>
    public const int MaxChunks = 255;

    public const int MaxScenarios = 64;

    /// <summary>Tệp chunk/scenario lớn hơn mức này chắc chắn không phải dữ liệu PlayGo thật — không đọc hết vào bộ nhớ.</summary>
    private const long MaxScenarioBytes = 4L * 1024 * 1024;

    private const string RelativePrefix = Folder + "/" + Prefix;

    /// <summary>Số khối và số kịch bản PlayGo engine sẽ dùng.</summary>
    public readonly record struct Counts(int Chunks, int Scenarios);

    /// <summary>
    /// Kết quả kiểm tra bộ playgo* đầu vào: <see cref="Counts"/> là số engine sẽ lấy (null = dùng số khối dự phòng), còn
    /// <see cref="Invalid"/> là các tệp hỏng (đường dẫn "sce_sys/…") phải bỏ khỏi gói để engine không dừng giữa chừng.
    /// </summary>
    public sealed record InputCheck(Counts? Counts, IReadOnlyList<string> Invalid)
    {
        public static readonly InputCheck None = new(null, Array.Empty<string>());
    }

    /// <summary>
    /// Đọc số khối/kịch bản từ header playgo-chunk.dat theo đúng quy tắc engine: magic "plgx", version 0x1000, trường 6 = 0,
    /// dài tối thiểu 256 byte, kích thước ghi ở offset 16 bằng độ dài tệp, số khối 1..255 và số kịch bản 1..64. Null = engine từ chối.
    /// </summary>
    public static Counts? ReadChunkCounts(Stream stream)
    {
        try
        {
            var length = stream.Length;
            if (length < 256)
            {
                return null;
            }

            Span<byte> header = stackalloc byte[24];
            stream.Position = 0;
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("plgx"u8) ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 0x1000 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) != length)
            {
                return null;
            }

            var chunks = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            var scenarios = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
            return chunks is >= 1 and <= MaxChunks && scenarios is >= 1 and <= MaxScenarios ? new Counts(chunks, scenarios) : null;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Đọc scenarioCount (số nguyên 1..64) trong playgo-scenario.json; null = engine từ chối tệp này.</summary>
    public static int? ReadScenarioCount(Stream stream)
    {
        try
        {
            if (stream.CanSeek && stream.Length > MaxScenarioBytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("scenarioCount", out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out var count) &&
                   count is >= 1 and <= MaxScenarios
                ? count
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Kiểm tra playgo-chunk.dat và playgo-scenario.json như engine sẽ đọc. <paramref name="open"/> mở một tệp trong sce_sys theo tên
    /// (null khi không có). Thiếu playgo-chunk.dat (hoặc nó hỏng và bị bỏ) thì engine chuyển sang scenarioCount và dùng số khối dự phòng.
    /// </summary>
    public static InputCheck CheckInputs(Func<string, Stream?> open)
    {
        var invalid = new List<string>();
        Counts? counts = null;
        using (var chunk = TryOpen(open, ChunkFile))
        {
            if (chunk != null)
            {
                counts = ReadChunkCounts(chunk);
                if (counts == null)
                {
                    invalid.Add(Folder + "/" + ChunkFile);
                }
            }
        }

        using (var scenario = TryOpen(open, ScenarioFile))
        {
            if (scenario != null)
            {
                var scenarios = ReadScenarioCount(scenario);
                if (scenarios == null)
                {
                    // Tệp này không bao giờ được chép vào gói (engine tạo lại), nhưng hỏng thì engine dừng khi phải đọc nó.
                    invalid.Add(Folder + "/" + ScenarioFile);
                }
                else if (counts == null)
                {
                    counts = new Counts(0, scenarios.Value);
                }
            }
        }

        return invalid.Count == 0 && counts == null ? InputCheck.None : new InputCheck(counts, invalid);
    }

    /// <summary>Kiểm tra bộ playgo* trong thư mục ứng dụng.</summary>
    public static InputCheck CheckFolder(string appFolder) =>
        CheckInputs(name =>
        {
            var path = Path.Combine(appFolder, Folder, name);
            return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
        });

    /// <summary>Kiểm tra bộ playgo* trong ảnh exFAT.</summary>
    public static InputCheck CheckImage(ExFatImage image, ExFatEntry appRoot) =>
        CheckInputs(name => image.Find(Folder + "/" + name, appRoot) is { IsDirectory: false } entry ? image.OpenRead(entry) : null);

    /// <summary>Kiểm tra bộ playgo* trong ảnh UFS2.</summary>
    public static InputCheck CheckImage(UfsImage image, UfsEntry appRoot) =>
        CheckInputs(name => image.Find(Folder + "/" + name, appRoot) is { IsDirectory: false } entry ? image.OpenRead(entry) : null);

    private static Stream? TryOpen(Func<string, Stream?> open, string name)
    {
        try
        {
            return open(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Tên tệp thuộc bộ dữ liệu playgo (không phân biệt hoa thường).</summary>
    public static bool IsPlayGoFile(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Đường dẫn tương đối dạng "sce_sys/playgo-…" (module fakelib/libScePlayGo.sprx không tính).</summary>
    public static bool IsCandidate(string relativePath) =>
        relativePath.Replace('\\', '/').StartsWith(RelativePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Các tệp sce_sys/playgo* trong thư mục nguồn (đường dẫn tương đối, phân cách '/', đã sắp xếp).</summary>
    public static IReadOnlyList<string> ListFolder(string sourceFolder)
    {
        var sceSys = Path.Combine(sourceFolder, Folder);
        if (!Directory.Exists(sceSys))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(sceSys)
                .Select(Path.GetFileName)
                .Where(name => name != null && IsPlayGoFile(name))
                .Select(name => Folder + "/" + name)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Các tệp sce_sys/playgo* trong ảnh exFAT (tính từ thư mục ứng dụng).</summary>
    public static IReadOnlyList<string> ListImage(ExFatImage image, ExFatEntry appRoot)
    {
        try
        {
            var sceSys = image.Find(Folder, appRoot);
            if (sceSys is not { IsDirectory: true })
            {
                return Array.Empty<string>();
            }

            return image.Enumerate(sceSys)
                .Where(entry => !entry.IsDirectory && IsPlayGoFile(entry.Name))
                .Select(entry => Folder + "/" + entry.Name)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Tên tệp gọn (bỏ tiền tố "sce_sys/") để ghi nhật ký.</summary>
    public static string Describe(IEnumerable<string> paths) =>
        string.Join(", ", paths.Select(path =>
        {
            var normalized = path.Replace('\\', '/');
            return normalized.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase) ? normalized[(Folder.Length + 1)..] : normalized;
        }));
}
