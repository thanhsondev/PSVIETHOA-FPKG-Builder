using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Những thay đổi công cụ áp lên nguồn khi tạo GP5 cho SDK Sony — thay cho thư mục gương của engine tích hợp, vì GP5 phẳng
/// trỏ thẳng vào từng tệp nguồn nên chỉ cần bỏ tệp khỏi danh sách hoặc trỏ sang bản thay thế.
/// </summary>
/// <param name="Skip">Đường dẫn tương đối (a/b/c, không phân biệt hoa thường) không đưa vào gói: bộ giả lập DLC khi bỏ…</param>
/// <param name="Replace">Đường dẫn tương đối → tệp thay thế đã có sẵn ở nơi khác.</param>
/// <param name="SkipJunk">Bỏ tệp/thư mục rác hệ điều hành (.DS_Store, ._*, Thumbs.db…). Script gốc không bỏ; công cụ luôn bỏ.</param>
/// <param name="ParamPatch">
/// Sửa đổi thêm trên param.json chuẩn hoá (versionFileUri, attribute3, hạ firmware). applicationDrmType = "standard" thì
/// script gốc luôn đặt, không cần bật ở đây.
/// </param>
/// <param name="PlayGo">
/// Cấu trúc PlayGo của gói gốc (chunk, ngôn ngữ, kịch bản, tệp thuộc chunk nào) để ghi vào chunk_info thay cho 1 chunk / 1 kịch
/// bản cố định của script — game nhiều ngôn ngữ thoại cần đúng các chunk đó (xem <see cref="PlayGoStructure"/>). Null = như script.
/// </param>
public sealed record SonySdkSourcePlan(IReadOnlySet<string> Skip, IReadOnlyDictionary<string, string> Replace, bool SkipJunk, ParamJsonPatchOptions ParamPatch = default, PlayGoStructure? PlayGo = null)
{
    /// <summary>
    /// Thư mục update của bản vá: tệp ở đây đè lên (hoặc thêm vào) cây của thư mục ứng dụng gốc khi liệt kê GP5 — so tên không phân
    /// biệt hoa thường, tệp bị đè giữ đúng tên/đường dẫn của bản gốc. Null = một thư mục nguồn như script gốc.
    /// </summary>
    public string? OverlayRoot { get; init; }

    /// <summary>
    /// Mọi đường dẫn tệp của gói gốc (a/b/c) khi thư mục gốc trên đĩa chỉ chứa những tệp nguồn KHÔNG có (giải nén từng phần): dùng để
    /// biết tệp của thư mục update là "thay" hay "thêm", và để giữ đúng tên/hoa-thường của gói gốc cho tệp bị thay. Null = xem trên đĩa.
    /// </summary>
    public IReadOnlyCollection<string>? OverlayBasePaths { get; init; }

    /// <summary>
    /// <c>--auto-size-profile</c> của script fix12 (<see cref="SonySdkPackageSize.Sdk279"/>): tự chọn attributePub (và hạ
    /// kernel.addcontMountLevel khi cần) theo kích thước chưa nén của các tệp trong GP5. build-from-folder.ps1 bật cho gói đầy đủ, tắt
    /// cho bản vá (giữ attributePub của nguồn). Null = không chọn (như script chạy không có cờ này).
    /// </summary>
    public string? AutoSizeProfile { get; init; }

    /// <summary>
    /// Thư mục (a/b, tên đúng trên đĩa) phải có trong gói dù rỗng — thư mục gắn DLC của dlc_emu.ini khi giữ bộ giả lập DLC. Script gốc
    /// chỉ liệt kê tệp nên thư mục rỗng mất khỏi gói; thư mục nào ở đây mà không chứa tệp nào của GP5 được khai báo bằng
    /// <c>&lt;empty_dir dst_path="…"/&gt;</c> (đúng phần tử Publishing Tools tự ghi qua <c>gp5_dir_add --virtual true</c>). Rỗng = như script.
    /// </summary>
    public IReadOnlyList<string> KeepDirectories { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Khai báo mọi thư mục rỗng của nguồn (kể cả thư mục chỉ chứa tệp rác) bằng <c>&lt;empty_dir&gt;</c>, như engine tích hợp giữ cây
    /// thư mục nguyên vẹn. Game có thể mở hay liệt kê một thư mục rỗng giữa chừng (thư mục gắn DLC, thư mục nội dung tuỳ chọn); thiếu nó
    /// thì game lỗi ở đúng đoạn đó. Bỏ qua thư mục trong sce_sys. False = như script (chỉ <see cref="KeepDirectories"/>).
    /// </summary>
    public bool KeepEmptyDirectories { get; init; }

    /// <summary>
    /// Thư mục sce_sys thay thế (metadata khôi phục online, trong thư mục tạm): tệp ở đây đè lên hoặc thêm vào sce_sys của nguồn khi liệt
    /// kê GP5, như thư mục update của bản vá nhưng chỉ cho cây sce_sys. Null = sce_sys của nguồn như script.
    /// </summary>
    public string? SceSysOverlay { get; init; }

    /// <summary>
    /// Cho mỗi chunk PlayGo không có tệp nào một tệp giữ chỗ 1 MiB không nén (như chunk ngôn ngữ của script). PS5 coi chunk rỗng là chưa
    /// cài: game chạy tới phần cần chunk đó thì hiện "Đang cài đặt trò chơi 0GB/0" (đo trên Control PPSA34547: bảng gốc 2 chunk, bản dump
    /// mất bảng ánh xạ tệp nên mọi tệp vào chunk 0, chunk 1 rỗng). False = như script (chunk thừa để rỗng).
    /// </summary>
    public bool FillEmptyChunks { get; init; }

    /// <summary>Đúng như script gốc: không bỏ, không thay, không sửa gì ngoài applicationDrmType.</summary>
    public static readonly SonySdkSourcePlan Pure = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), false);
}

/// <summary>Ảnh PNG chuyển từ DDS: đích trong gói (sce_sys/pic0.png), tên tệp DDS nguồn và tệp PNG đã ghi.</summary>
/// <param name="ReplacedInvalid">PNG có trong nguồn nhưng sai chế độ màu SDK yêu cầu (hoặc không phải PNG) nên bị thay bằng bản chuyển từ DDS.</param>
/// <param name="Mode">"RGB" (icon0/pic0/pic1) hoặc "RGBA" (pic2 và ảnh khác, giữ kênh alpha).</param>
public sealed record SonySdkRecoveredPng(string Destination, string DdsName, string PngPath, bool ReplacedInvalid = false, string Mode = "RGB");

/// <summary>Kết quả tạo dự án GP5 cho SDK Sony.</summary>
/// <param name="ProjectPath">Tệp .gp5 vừa ghi.</param>
/// <param name="ScenarioPath">Tệp playgo-scenario.json mặc định đi kèm.</param>
/// <param name="ParamJsonPath">param.json chuẩn hoá trong .gp5-assets (applicationDrmType = standard + sửa đổi của công cụ).</param>
/// <param name="ParamChanges">Các thay đổi (đã dịch) so với param.json của nguồn.</param>
/// <param name="RecoveredPngs">Ảnh pic*.png khôi phục từ pic*.dds vì nguồn thiếu PNG.</param>
/// <param name="ContentId">Content ID đọc từ sce_sys/param.json.</param>
/// <param name="FileCount">Số ánh xạ tệp trong dự án (kể cả playgo-scenario.json và PNG khôi phục).</param>
/// <param name="TotalBytes">Tổng kích thước các tệp nguồn.</param>
/// <param name="Excluded">Tệp script gốc cũng loại, kèm lý do ("path (reason)").</param>
/// <param name="SkippedByApp">Tệp công cụ loại thêm theo tuỳ chọn/tệp rác, kèm lý do.</param>
/// <param name="Replaced">Đường dẫn tương đối đã trỏ sang bản thay thế.</param>
/// <param name="PlayGoMappedFiles">Số tệp được gán vào chunk khác 0 theo bảng PlayGo của gói gốc (0 khi dùng 1 chunk như script).</param>
/// <param name="ScenarioFromSource">playgo-scenario.json là tệp của gói gốc (cấu trúc nhiều chunk), không phải bản tạo mới.</param>
/// <param name="UnsupportedPaths">Đường dẫn trong gói mà Publishing Tools 2.79 sẽ từ chối (<see cref="SonySdkProject.IsSdkPackagePath"/>).</param>
/// <param name="SourceFiles">Đường dẫn máy của mọi tệp nguồn trong GP5 (để quét trước, <see cref="SonySdkPrescan"/>).</param>
public sealed record SonySdkProjectResult(
    string ProjectPath,
    string ScenarioPath,
    string ParamJsonPath,
    IReadOnlyList<string> ParamChanges,
    IReadOnlyList<SonySdkRecoveredPng> RecoveredPngs,
    string ContentId,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string> SkippedByApp,
    IReadOnlyList<string> Replaced,
    int PlayGoMappedFiles = 0,
    bool ScenarioFromSource = false,
    IReadOnlyList<string>? UnsupportedPaths = null,
    IReadOnlyList<string>? SourceFiles = null)
{    /// <summary>Số tệp giữ chỗ ngôn ngữ PlayGo (bố cục script fix6) đã thêm vào GP5.</summary>
    public int PlayGoLanguagePayloads { get; init; }

    /// <summary>Chunk PlayGo không có tệp nào đã nhận tệp giữ chỗ (<see cref="SonySdkSourcePlan.FillEmptyChunks"/>).</summary>
    public IReadOnlyList<int> FilledChunks { get; init; } = Array.Empty<int>();

    /// <summary>Thư mục update: tệp của gói gốc bị thay bằng bản trong thư mục update (đường dẫn trong gói).</summary>
    public IReadOnlyList<string> OverlayReplaced { get; init; } = Array.Empty<string>();

    /// <summary>Thư mục update: tệp mới chỉ có trong thư mục update.</summary>
    public IReadOnlyList<string> OverlayAdded { get; init; } = Array.Empty<string>();

    /// <summary>Tệp SELF đã sửa header (đường dẫn trong gói, mô tả cách sửa) — bản sửa nằm trong .gp5-assets/&lt;tên&gt;/normalized-self.</summary>
    public IReadOnlyList<(string Relative, string Repairs)> RepairedSelfs { get; init; } = Array.Empty<(string, string)>();

    /// <summary>UCP (sce_sys/trophy2, sce_sys/uds) có digest lệch đã được tính lại — bản sửa nằm trong .gp5-assets/&lt;tên&gt;/repaired-ucp.</summary>
    public IReadOnlyList<string> RepairedUcps { get; init; } = Array.Empty<string>();

    /// <summary>UCP không đọc được (bản niêm phong của gói gốc, tệp cụt…) — đưa nguyên vào gói, kèm lý do.</summary>
    public IReadOnlyList<(string Relative, string Error)> InvalidUcps { get; init; } = Array.Empty<(string, string)>();

    /// <summary>Cỡ gói đã chọn (attributePub…) khi bật <see cref="SonySdkSourcePlan.AutoSizeProfile"/>; null khi không chọn.</summary>
    public SonySdkSizeSelection? SizeSelection { get; init; }

    /// <summary>Kích thước chưa nén và số tệp dùng để chọn cỡ gói (<see cref="SizeSelection"/>).</summary>
    public (long Bytes, int Files) SizeInputs { get; init; }

    /// <summary>addcontMountLevel của param.json nguồn (trước khi chọn cỡ gói).</summary>
    public int OriginalMountLevel { get; init; }

    /// <summary>Các dòng script in ra trước dòng "Created …" (chuyển DDS, sửa SELF, chọn cỡ gói), đúng thứ tự.</summary>
    public IReadOnlyList<string> ScriptLines { get; init; } = Array.Empty<string>();

    /// <summary>Thư mục rỗng đã khai báo bằng &lt;empty_dir&gt; (<see cref="SonySdkSourcePlan.KeepDirectories"/> không chứa tệp nào).</summary>
    public IReadOnlyList<string> KeptEmptyDirectories { get; init; } = Array.Empty<string>();

    /// <summary>Đường dẫn trong <see cref="SkippedByApp"/> là tệp/thư mục rác hệ điều hành (.DS_Store, ._tên, Thumbs.db…), bỏ phần lý do.</summary>
    public IReadOnlyList<string> SkippedJunk => SkippedByApp.Where(SonySdkProject.IsJunkSkip).Select(SonySdkProject.SkipPath).ToList();

    /// <summary>Đường dẫn trong <see cref="SkippedByApp"/> bị bỏ theo tuỳ chọn (tàn dư AMPR emu, bản dump, bộ giả lập DLC khi tắt giữ…).</summary>
    public IReadOnlyList<string> SkippedByOption => SkippedByApp.Where(item => !SonySdkProject.IsJunkSkip(item)).Select(SonySdkProject.SkipPath).ToList();

    /// <summary>Đầu ra chuẩn của create-gp5-from-folder.py cho lượt này (ghi vào 01-create-gp5.log như build-from-folder.ps1).</summary>
    public string Report
    {
        get
        {
            var builder = new StringBuilder();
            foreach (var line in ScriptLines)
            {
                builder.Append(line).Append('\n');
            }

            builder.Append("Created ").Append(ProjectPath).Append(" with ").Append(FileCount).Append(" explicit file mapping(s).\n");
            if (Excluded.Count > 0)
            {
                builder.Append("Excluded ").Append(Excluded.Count).Append(" service/project artifact(s):\n");
                foreach (var item in Excluded)
                {
                    builder.Append("  ").Append(item).Append('\n');
                }
            }

            if (SkippedByApp.Count > 0)
            {
                builder.Append("Skipped ").Append(SkippedByApp.Count).Append(" file(s) by PSVIETHOA FPKG Builder options:\n");
                foreach (var item in SkippedByApp)
                {
                    builder.Append("  ").Append(item).Append('\n');
                }
            }

            foreach (var item in Replaced)
            {
                builder.Append("Replaced ").Append(item).Append(" with the substitute file.\n");
            }

            if (FilledChunks.Count > 0)
            {
                builder.Append("Filled ").Append(FilledChunks.Count).Append(" empty PlayGo chunk(s) with uncompressed 1 MiB placeholders: ").Append(string.Join(", ", FilledChunks)).Append('\n');
            }

            if (KeptEmptyDirectories.Count > 0)
            {
                builder.Append("Kept ").Append(KeptEmptyDirectories.Count).Append(" empty folder(s) as <empty_dir> entries (DLC mount folders from dlc_emu.ini, empty source folders):\n");
                foreach (var item in KeptEmptyDirectories)
                {
                    builder.Append("  ").Append(item).Append("/\n");
                }
            }

            if (OverlayReplaced.Count + OverlayAdded.Count > 0)
            {
                builder.Append("Update folder: ").Append(OverlayReplaced.Count).Append(" file(s) replaced, ").Append(OverlayAdded.Count).Append(" file(s) added:\n");
                foreach (var item in OverlayReplaced)
                {
                    builder.Append("  * ").Append(item).Append('\n');
                }

                foreach (var item in OverlayAdded)
                {
                    builder.Append("  + ").Append(item).Append('\n');
                }
            }

            return builder.ToString();
        }
    }
}

/// <summary>
/// Tạo dự án GP5 "phẳng" từ một thư mục ứng dụng, giống từng byte với <c>scripts/create-gp5-from-folder.py</c> của bộ
/// sdk-fpkg279-fix12 (chế độ <c>--keep-keystone --absolute-paths --chunk-count N [--auto-size-profile sdk279]</c> mà
/// build-from-folder.ps1 dùng): mọi tệp được liệt kê tường
/// minh (không dùng rootdir) để tàn dư của lần giải nén/đóng gói trước không lọt vào gói; các tệp sce_sys do SDK tự sinh (PlayGo,
/// pfs-version, license giả, ảnh .dds, about/…) bị loại để SDK tạo lại; <c>ampr_emu.index</c> và hai module giả lập
/// <c>fakelib/libSceAmpr.sprx</c>, <c>fakelib/libScePlayGo.sprx</c> bị loại; keystone 96 byte của nguồn luôn được giữ (SDK đã vá
/// nhận keystone này thay vì tự tạo từ passcode); thư mục <c>sce_suppl</c>/<c>sce_sc</c> ở gốc bị loại (SDK giữ riêng). param.json được
/// chuẩn hoá với applicationDrmType = "standard" (và attributePub tự chọn), ảnh <c>sce_sys/*.png</c> thiếu hoặc sai chế độ màu được
/// chuyển từ <c>.dds</c>, tệp SELF lệch header được sửa — tất cả vào thư mục <c>.gp5-assets/&lt;tên&gt;</c> cạnh GP5; nguồn không
/// bao giờ bị sửa. Thứ tự tệp = <c>sorted(name.casefold())</c> của Python vì Publishing Tools xếp dữ liệu vào ảnh theo đúng thứ tự
/// trong GP5 (đảo thứ tự là gói khác hẳn).
/// </summary>
public static partial class SonySdkProject
{
    public const int KeystoneLength = 96;

    public const string KeystonePath = "sce_sys/keystone";

    /// <summary>Thư mục cạnh GP5 chứa param.json chuẩn hoá và PNG khôi phục (<c>GENERATED_ASSET_DIRECTORY</c>).</summary>
    public const string AssetsFolderName = ".gp5-assets";

    /// <summary><c>GENERATED_SCE_SYS_FILES</c> của script (kể cả license.info/license.dat từ bản fixdss3).</summary>
    private static readonly HashSet<string> GeneratedSceSysFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "keystone",
        "pfs-version.dat",
        "disc_info.dat",
        "ext_info.dat",
        "imagedigs.dat",
        "playgo-chunk.dat",
        "playgo-hash-table.dat",
        "playgo-ficm.dat",
        "playgo-scenario.json",
        "playgo-manifest.xml",
        "playgo-chunk.crc",
        "pfsimage.xml",
        "param.sfo",
        "param_cp_values.json",
        "pronunciation.sig",
        "origin-deltainfo.dat",
        "target-deltainfo.dat",
        "license.info",
        "license.dat",
        // Bản gốc/đích của param.json mà Publishing Tools cất trong CNT của gói (bản vá/backport); chế độ Giải nén chép chúng ra
        // sce_sys, đưa lại vào GP5 thì SDK từ chối "reserved node found" (đo 2026-09-18; script fix12 đã thêm hai tên này).
        "origin-param.json",
        "target-param.json",
        // fix12: gợi ý vùng nén PFS và bảng relocation của bản vá cũng là đầu ra của SDK.
        "pfs-region-hints.json",
        "origin-relocinfo.dat",
        "target-relocinfo.dat",
    };

    /// <summary>Lý do ghi sau đường dẫn trong <see cref="SonySdkProjectResult.SkippedByApp"/> (01-create-gp5.log, tiếng Anh như log của script).</summary>
    public const string JunkFileReason = " (OS junk file)";

    public const string JunkFolderReason = "/ (OS junk folder)";

    public const string OptionFileReason = " (excluded by option)";

    public const string OptionFolderReason = "/ (excluded by option)";

    /// <summary>Mục bỏ là tệp/thư mục rác hệ điều hành.</summary>
    public static bool IsJunkSkip(string item) =>
        item.EndsWith(JunkFileReason, StringComparison.Ordinal) || item.EndsWith(JunkFolderReason, StringComparison.Ordinal);

    /// <summary>Đường dẫn của một mục bỏ, không có phần lý do (thư mục giữ dấu "/").</summary>
    public static string SkipPath(string item)
    {
        foreach (var (reason, keep) in new[] { (JunkFolderReason, "/"), (OptionFolderReason, "/"), (JunkFileReason, ""), (OptionFileReason, "") })
        {
            if (item.EndsWith(reason, StringComparison.Ordinal))
            {
                return item[..^reason.Length] + keep;
            }
        }

        return item;
    }

    /// <summary><c>GENERATED_ROOT_DIRECTORIES</c> (fix12): thư mục ở gốc mà Publishing Tools giữ riêng, bản giải nén có thể mang theo.</summary>
    private static readonly HashSet<string> GeneratedRootDirectories = new(StringComparer.Ordinal) { "sce_suppl", "sce_sc" };

    private static readonly HashSet<string> GeneratedSceSysDirectories = new(StringComparer.OrdinalIgnoreCase) { "about" };

    private static readonly HashSet<string> ReservedSceSysSuffixes = new(StringComparer.OrdinalIgnoreCase) { ".dds", ".auth_info" };

    private static readonly HashSet<string> ProjectSuffixes = new(StringComparer.OrdinalIgnoreCase) { ".gp4", ".gp5", ".esbak" };

    /// <summary><c>EXCLUDED_ROOT_FILES</c>: tệp ở gốc thư mục ứng dụng bị loại (chỉ mục AMPR emu của máy chủ).</summary>
    private static readonly HashSet<string> ExcludedRootFiles = new(StringComparer.Ordinal) { "ampr_emu.index" };

    /// <summary><c>EXCLUDED_FAKE_LIBRARIES</c>: module giả lập AMPR/PlayGo trong fakelib (khoá đã casefold).</summary>
    private static readonly HashSet<string> ExcludedFakeLibraries = new(StringComparer.Ordinal) { "libsceampr.sprx", "libsceplaygo.sprx" };

    [GeneratedRegex("^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_[0-9]{2}-[A-Z0-9]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContentIdPattern();

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Thư mục có sce_sys/keystone đúng 96 byte không (bộ công cụ bắt buộc có).</summary>
    public static bool HasValidKeystone(string appFolder, out long? length)
    {
        var path = Path.Combine(appFolder, "sce_sys", "keystone");
        length = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            length = new FileInfo(path).Length;
            return length == KeystoneLength;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Đường dẫn trong gói (a/b/c) Publishing Tools 2.79 có nhận không. Đo trực tiếp bằng <c>img_create</c>: mọi ký tự ASCII in được
    /// đều nhận (kể cả khoảng trắng đầu tên, <c>! # $ &amp; ' ( ) + , - = @ [ ] ^ _ ` { } ~</c>, tên bắt đầu bằng dấu chấm) trừ
    /// <c>%</c> và <c>;</c>; từ chối mọi ký tự ngoài ASCII (tiếng Việt, tiếng Nhật, emoji…) và thành phần kết thúc bằng dấu chấm.
    /// </summary>
    public static bool IsSdkPackagePath(string relative)
    {
        foreach (var c in relative)
        {
            if (c is < ' ' or > '~' or '%' or ';')
            {
                return false;
            }
        }

        foreach (var part in relative.Split('/'))
        {
            if (part.Length == 0 || part.EndsWith('.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Tệp có phải sản phẩm của SDK/bản dump trong sce_sys (không được đưa vào GP5) không — <c>is_service_artifact</c>.</summary>
    public static bool IsServiceArtifact(string relative)
    {
        var parts = relative.Split('/');
        if (!PythonCaseFold.Fold(parts[0]).Equals("sce_sys", StringComparison.Ordinal))
        {
            return false;
        }

        if (parts.Length >= 2 && GeneratedSceSysDirectories.Contains(parts[1]))
        {
            return true;
        }

        if (parts.Length == 2 && GeneratedSceSysFiles.Contains(parts[1]))
        {
            return true;
        }

        // Publishing Tools tự chuyển PNG trình bày thành nút DDS; DDS và tệp auth_info của bản giải nén là đầu ra, không phải đầu vào.
        return ReservedSceSysSuffixes.Contains(Path.GetExtension(parts[^1]));
    }

    /// <summary>
    /// Lý do script gốc bỏ một tệp (null = đưa vào GP5), đúng thứ tự kiểm tra của <c>collect_files</c>. <paramref name="keep"/> là
    /// các đường dẫn sce_sys vẫn giữ dù SDK tự sinh.
    /// </summary>
    public static string? SkipReason(string relative, ISet<string> keep)
    {
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        if (ProjectSuffixes.Contains(Path.GetExtension(name)))
        {
            return "project artifact";
        }

        if (name.EndsWith(".playgo-scenario.json", StringComparison.OrdinalIgnoreCase))
        {
            return "generated GP5 scenario sidecar";
        }

        var normalized = PythonCaseFold.Fold(relative);
        if (ExcludedRootFiles.Contains(normalized))
        {
            return "host emulator index";
        }

        if (normalized.StartsWith("fakelib/", StringComparison.Ordinal) && ExcludedFakeLibraries.Contains(normalized["fakelib/".Length..]))
        {
            return "SDK/runtime fakelib module";
        }

        if (IsServiceArtifact(relative) && !keep.Contains(relative))
        {
            return "reserved/SDK-generated sce_sys artifact";
        }

        return null;
    }

    /// <summary>
    /// Ghi <paramref name="projectPath"/> (.gp5), &lt;tên&gt;.playgo-scenario.json cạnh nó và thư mục .gp5-assets/&lt;tên&gt;/sce_sys
    /// (param.json chuẩn hoá, PNG khôi phục). <paramref name="toolPath"/> đổi đường dẫn máy thành đường dẫn SDK mở được (bí danh
    /// ASCII, ổ Z: của Wine). <paramref name="listed"/> nhận số tệp đã liệt kê trong lúc duyệt. <paramref name="ddsConverter"/>
    /// (dds, png, preserveAlpha) chạy prospero-dds2png; null thì thiếu PNG là lỗi như script gốc thiếu bộ chuyển đổi.
    /// </summary>
    public static SonySdkProjectResult Create(
        string appFolder,
        string projectPath,
        string passcode,
        Func<string, string> toolPath,
        SonySdkSourcePlan? plan = null,
        CancellationToken cancellationToken = default,
        Action<int>? listed = null,
        Action<string, string, bool>? ddsConverter = null)
    {
        plan ??= SonySdkSourcePlan.Pure;
        if (passcode.Length != 32)
        {
            throw new InvalidDataException(Loc.T("Sdk.PasscodeLength"));
        }

        if (!Directory.Exists(appFolder))
        {
            throw new DirectoryNotFoundException(appFolder);
        }

        // Bản vá ghép thư mục update: keystone có thể nằm ở thư mục update (khi thư mục gốc chỉ chứa các tệp nguồn không có).
        var keystoneFolder = !string.IsNullOrWhiteSpace(plan.OverlayRoot) && File.Exists(Path.Combine(plan.OverlayRoot, "sce_sys", "keystone")) ? plan.OverlayRoot : appFolder;
        if (!HasValidKeystone(keystoneFolder, out var keystoneLength))
        {
            throw new InvalidDataException(keystoneLength == null
                ? Loc.F("Sdk.KeystoneMissing", Path.Combine(keystoneFolder, "sce_sys", "keystone"))
                : Loc.F("Sdk.KeystoneLength", keystoneLength, KeystoneLength));
        }

        // Script gốc dùng Path.resolve(): đường dẫn thật, không qua liên kết (/var → /private/var trên macOS, junction trên Windows).
        projectPath = RealPath(projectPath);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { KeystonePath };
        var overlayRoot = string.IsNullOrWhiteSpace(plan.OverlayRoot) ? null : RealPath(plan.OverlayRoot);
        if (overlayRoot != null && !Directory.Exists(overlayRoot))
        {
            throw new DirectoryNotFoundException(overlayRoot);
        }

        var walker = new Walker(RealPath(appFolder), projectPath, keep, plan, listed, cancellationToken, overlayRoot);
        walker.Walk(walker.Root);
        listed?.Invoke(walker.Files.Count);

        var files = walker.Files;
        var paramSource = files.Find(file => file.Relative.Equals("sce_sys/param.json", StringComparison.OrdinalIgnoreCase)).FullPath
                          ?? throw new InvalidDataException(Loc.T("Sdk.ParamMissing"));
        var (contentId, language) = ReadParam(paramSource);

        // Ảnh trình bày (trước write_scenario như script): DDS nào cần chuyển vì PNG thiếu/sai chế độ màu; PNG sai bị bỏ khỏi GP5.
        var conversions = PlanPresentationImages(walker.Root, files, walker.Excluded, plan.SkipJunk);

        var scenarioPath = Path.ChangeExtension(projectPath, ".playgo-scenario.json");
        var projectFolder = Path.GetDirectoryName(projectPath)!;
        Directory.CreateDirectory(projectFolder);
        var playGo = plan.PlayGo is { IsTrivial: false } structure ? structure : null;
        // Bố cục script (không có bảng gốc): scenario JSON do write_scenario() tạo. Bảng gốc nhiều chunk: Publishing Tools chép
        // nguyên playgo-scenario.json vào gói, nên dùng lại đúng tệp của gói gốc (tiêu đề, mô tả, danh sách ngôn ngữ) khi nó khớp cấu
        // trúc; không có/không khớp thì tạo từ nhãn kịch bản. 1 chunk (tuỳ chọn dự phòng tắt): kịch bản mặc định.
        var originalScenario = playGo is null or { ScriptLayout: true } ? null : OriginalScenarioJson(Path.Combine(walker.Root, "sce_sys", SonySdkPlayGo.ScenarioFileName), playGo);
        if (playGo is { ScriptLayout: true, ScenarioJson: { } scenarioJson })
        {
            WriteScriptText(scenarioPath, scenarioJson);
        }
        else if (originalScenario != null)
        {
            File.WriteAllBytes(scenarioPath, originalScenario);
        }
        else
        {
            WriteScriptText(scenarioPath, playGo == null ? DefaultScenario(language) : ScenarioJson(playGo, language));
        }

        // .gp5-assets/<tên>: sce_sys/param.json chuẩn hoá, sce_sys/*.png chuyển từ DDS, normalized-self/, playgo-languages/.
        var generatedSystem = Path.Combine(projectFolder, AssetsFolderName, Path.GetFileNameWithoutExtension(projectPath), "sce_sys");
        var generatedRoot = Path.GetDirectoryName(generatedSystem)!;
        Directory.CreateDirectory(generatedSystem);
        var generatedParam = Path.Combine(generatedSystem, "param.json");
        var scriptLines = new List<string>();
        var recovered = ConvertDdsImages(conversions, generatedSystem, ddsConverter, scriptLines, cancellationToken);
        var repairs = PrepareExecutableInputs(files, generatedRoot, scriptLines, cancellationToken);
        var ucpRepairs = PrepareUcpInputs(files, generatedRoot, scriptLines, out var invalidUcps, cancellationToken);

        // write_language_payloads(): tệp giữ chỗ 1 MiB (không nén) cho từng ngôn ngữ trong .gp5-assets/<tên>/playgo-languages/.
        // Rồi tới chunk không có tệp nào (FillEmptyChunks): cũng một tệp giữ chỗ để PS5 thấy chunk đó đã cài.
        var languagePayloads = playGo?.LanguagePayloads ?? Array.Empty<PlayGoLanguagePayload>();
        var fillers = EmptyChunkFillers(plan, playGo, files, languagePayloads);
        IReadOnlyList<PlayGoLanguagePayload> payloads = fillers.Count == 0 ? languagePayloads : languagePayloads.Concat(fillers).ToList();
        var payloadPaths = new List<string>(payloads.Count);
        foreach (var payload in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payloadPath = Path.Combine(generatedRoot, payload.Destination.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            using (var stream = new FileStream(payloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(SonySdkPlayGo.LanguagePayloadSize);
            }

            payloadPaths.Add(payloadPath);
        }

        // --auto-size-profile: cỡ gói từ kích thước chưa nén của mọi tệp GP5 trỏ tới (param.json chuẩn hoá tính vào phần dự phòng).
        SonySdkSizeSelection? selection = null;
        var sizeInputs = (Bytes: 0L, Files: 0);
        var originalMountLevel = 0;
        if (plan.AutoSizeProfile is { } profile)
        {
            long unpacked = 0;
            var mappedCount = 0;
            for (var index = 0; index < files.Count; index++)
            {
                if (string.Equals(files[index].FullPath, paramSource, StringComparison.Ordinal))
                {
                    continue;
                }

                unpacked += repairs.TryGetValue(index, out var repaired) ? new FileInfo(repaired.Path).Length
                    : ucpRepairs.TryGetValue(index, out var ucp) ? new FileInfo(ucp).Length
                    : files[index].Length;
                mappedCount++;
            }

            unpacked += recovered.Sum(png => new FileInfo(png.PngPath).Length) + payloadPaths.Sum(path => new FileInfo(path).Length) + new FileInfo(scenarioPath).Length;
            mappedCount += recovered.Count + payloadPaths.Count + 1;
            sizeInputs = (unpacked, mappedCount + 1);
            originalMountLevel = SonySdkPackageSize.ReadMountLevel(ParseParamObject(paramSource), paramSource);
            selection = SonySdkPackageSize.Choose(unpacked, mappedCount + 1, profile, originalMountLevel);
            if (selection.MountLevel != originalMountLevel)
            {
                scriptLines.Add($"[Warn] Adjusting kernel.addcontMountLevel from {originalMountLevel} to {selection.MountLevel} in the generated param.json; the source file is unchanged.");
            }

            var limit = SonySdkPackageSize.ProfileLimit(profile);
            if (selection.EstimatedBytes > limit)
            {
                var maximum = profile == SonySdkPackageSize.Sdk279 ? "attributePub=2 (lv2)" : "attributePub=4 (lv3), appSizeInGib=320";
                scriptLines.Add($"[Warn] Conservative estimate {selection.EstimatedBytes} bytes exceeds the maximum declared size for {profile}: {limit} bytes. Continuing with {maximum}; img_create will apply the final limit after compression.");
            }

            var detail = selection.AppSizeInGib is { } gib ? $", appSizeInGib={gib}" : string.Empty;
            scriptLines.Add($"Unpacked GP5 inputs: {unpacked} bytes, {mappedCount + 1} files; conservative estimate: {selection.EstimatedBytes} bytes; addcontMountLevel={selection.MountLevel}; attributePub={selection.AttributePub}{detail}");
        }

        var paramChanges = WriteStandardParam(paramSource, generatedParam, plan.ParamPatch, selection);

        // Cùng bố cục với ElementTree của Python (khai báo nháy đơn, thụt 2 khoảng trắng, " />" cho phần tử rỗng, không xuống dòng cuối).
        var xml = new StringBuilder(512 + files.Count * 160);
        xml.Append("<?xml version='1.0' encoding='utf-8'?>\n");
        xml.Append("<psproject fmt=\"gp5\">\n");
        xml.Append("  <volume>\n");
        xml.Append("    <volume_type>prospero_app</volume_type>\n");
        xml.Append("    <package passcode=\"").Append(EscapeAttribute(passcode)).Append("\" content_id=\"").Append(EscapeAttribute(contentId)).Append("\" />\n");
        if (playGo == null)
        {
            xml.Append("    <chunk_info chunk_count=\"1\" scenario_count=\"1\">\n");
            xml.Append("      <chunks>\n");
            xml.Append("        <chunk id=\"0\" label=\"Chunk #0\" />\n");
            xml.Append("      </chunks>\n");
            xml.Append("      <scenarios default_id=\"0\">\n");
            xml.Append("        <scenario id=\"0\" type=\"playmode\" initial_chunk_count=\"1\" label=\"Scenario #0\">0</scenario>\n");
            xml.Append("      </scenarios>\n");
            xml.Append("    </chunk_info>\n");
        }
        else
        {
            // Cấu trúc chunk/kịch bản/ngôn ngữ (bố cục script hoặc của gói gốc — Publishing Tools tự ghi đúng cú pháp này qua gp5_chunk_add/gp5_scenario_add).
            xml.Append(SonySdkPlayGo.ChunkInfoXml(playGo));
        }

        xml.Append("  </volume>\n");
        xml.Append("  <files>\n");
        // Bố cục script ghi chunk="0" tường minh trên mọi tệp; bố cục bảng gốc chỉ ghi chunk khác 0 (chunk 0 là mặc định của SDK).
        var explicitChunk = playGo is { ScriptLayout: true };
        AppendFile(xml, "sce_sys/playgo-scenario.json", toolPath(scenarioPath), explicitChunk ? 0 : -1);
        foreach (var png in recovered)
        {
            AppendFile(xml, png.Destination, toolPath(png.PngPath), explicitChunk ? 0 : -1);
        }

        for (var index = 0; index < payloads.Count; index++)
        {
            AppendFile(xml, payloads[index].Destination, toolPath(payloadPaths[index]), payloads[index].ChunkId, noCompression: true);
        }

        var mapped = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            // Tệp thuộc chunk khác 0 trong gói gốc: giữ nguyên (gp5_file_add --chunk).
            var chunk = playGo?.ChunkOf(file.Relative) ?? 0;
            if (chunk > 0)
            {
                mapped++;
            }

            var actual = string.Equals(file.FullPath, paramSource, StringComparison.Ordinal)
                ? generatedParam
                : repairs.TryGetValue(index, out var repaired) ? repaired.Path
                : ucpRepairs.TryGetValue(index, out var ucp) ? ucp
                : file.FullPath;
            AppendFile(xml, file.Relative, toolPath(actual), explicitChunk || chunk > 0 ? chunk : -1);
            if ((index + 1) % 2000 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // Thư mục phải có dù rỗng (thư mục gắn DLC, thư mục rỗng của nguồn): script chỉ liệt kê tệp nên chúng mất; khai báo như Publishing
        // Tools tự ghi (gp5_dir_add --virtual true → <empty_dir dst_path="…"/>, sau các tệp). Không có thư mục nào thì GP5 giống hệt script.
        var emptyDirectories = EmptyDirectoriesToKeep(plan, files, plan.KeepEmptyDirectories ? walker.EmptyDirectories : Array.Empty<string>());
        foreach (var directory in emptyDirectories)
        {
            xml.Append("    <empty_dir dst_path=\"").Append(EscapeAttribute(directory)).Append("\" />\n");
        }

        xml.Append("  </files>\n");
        xml.Append("</psproject>");
        WriteScriptText(projectPath, xml.ToString());

        return new SonySdkProjectResult(
            projectPath,
            scenarioPath,
            generatedParam,
            paramChanges,
            recovered,
            contentId,
            files.Count + recovered.Count + payloads.Count + 1,
            files.Sum(file => file.Length),
            walker.Excluded,
            walker.SkippedByApp,
            walker.Replaced,
            mapped,
            originalScenario != null,
            files.Where(file => !IsSdkPackagePath(file.Relative)).Select(file => file.Relative).ToList(),
            files.Select(file => file.FullPath).ToList())
        {
            PlayGoLanguagePayloads = languagePayloads.Count,
            FilledChunks = fillers.Select(filler => filler.ChunkId).ToList(),
            OverlayReplaced = walker.OverlayReplaced,
            OverlayAdded = walker.OverlayAdded,
            RepairedSelfs = repairs.OrderBy(pair => pair.Key).Select(pair => (files[pair.Key].Relative, pair.Value.Repairs)).ToList(),
            RepairedUcps = ucpRepairs.OrderBy(pair => pair.Key).Select(pair => files[pair.Key].Relative).ToList(),
            InvalidUcps = invalidUcps,
            SizeSelection = selection,
            SizeInputs = sizeInputs,
            OriginalMountLevel = originalMountLevel,
            ScriptLines = scriptLines,
            KeptEmptyDirectories = emptyDirectories,
        };
    }

    /// <summary>
    /// Thư mục cần khai báo &lt;empty_dir&gt;: thư mục của <see cref="SonySdkSourcePlan.KeepDirectories"/> không chứa tệp nào của GP5
    /// (rỗng, hoặc chỉ còn tệp rác/tệp bị bỏ) rồi tới <paramref name="sourceEmpty"/> (thư mục rỗng của nguồn). Bỏ qua thư mục (hoặc thư
    /// mục cha) bị loại theo tuỳ chọn, thư mục rác, thư mục trong sce_sys và đường dẫn Publishing Tools không nhận.
    /// </summary>
    private static IReadOnlyList<string> EmptyDirectoriesToKeep(SonySdkSourcePlan plan, List<(string Relative, string FullPath, long Length)> files, IReadOnlyList<string> sourceEmpty)
    {
        if (plan.KeepDirectories.Count == 0 && sourceEmpty.Count == 0)
        {
            return Array.Empty<string>();
        }

        // Mọi thư mục cha của các tệp trong GP5 (không phân biệt hoa thường): thư mục có ở đây thì không rỗng.
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            for (var slash = file.Relative.LastIndexOf('/'); slash > 0 && occupied.Add(file.Relative[..slash]); slash = file.Relative.LastIndexOf('/', slash - 1))
            {
            }
        }

        var result = new List<string>();
        foreach (var raw in plan.KeepDirectories.Concat(sourceEmpty))
        {
            var directory = raw.Replace('\\', '/').Trim('/');
            var parts = directory.Split('/');
            if (directory.Length == 0 || !IsSdkPackagePath(directory) || parts.Any(part => JunkFileFinder.IsJunkDirectoryName(part)) ||
                Enumerable.Range(1, parts.Length).Any(depth => plan.Skip.Contains(string.Join('/', parts.Take(depth)))) ||
                PythonCaseFold.Fold(parts[0]) is var root && (root == AssetsFolderName || root == "sce_sys" || GeneratedRootDirectories.Contains(root)) ||
                result.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!occupied.Contains(directory))
            {
                result.Add(directory);
            }
        }

        return result;
    }

    /// <summary>
    /// Nội dung playgo-scenario.json của gói gốc nếu dùng lại được cho cấu trúc <paramref name="structure"/>: JSON object, cùng số
    /// kịch bản, cùng kịch bản mặc định, mỗi kịch bản đúng id theo thứ tự và kiểu "playmode". Null khi thiếu, hỏng hoặc không khớp.
    /// </summary>
    public static byte[]? OriginalScenarioJson(string path, PlayGoStructure structure)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > 1024 * 1024)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            var json = bytes.AsSpan().StartsWith(Utf8Bom) ? bytes.AsSpan(Utf8Bom.Length) : bytes.AsSpan();
            if (JsonNode.Parse(json) is not JsonObject root ||
                root["scenarioCount"] is not JsonValue count || !count.TryGetValue<int>(out var scenarioCount) || scenarioCount != structure.Scenarios.Count ||
                root["scenarioDefaultId"] is not JsonValue defaultId || !defaultId.TryGetValue<int>(out var defaultScenario) || defaultScenario != structure.DefaultScenarioId ||
                root["scenarios"] is not JsonArray scenarios || scenarios.Count != structure.Scenarios.Count)
            {
                return null;
            }

            for (var index = 0; index < scenarios.Count; index++)
            {
                if (scenarios[index] is not JsonObject scenario ||
                    scenario["id"] is not JsonValue id || !id.TryGetValue<int>(out var scenarioId) || scenarioId != structure.Scenarios[index].Id ||
                    scenario["type"] is not JsonValue type || !type.TryGetValue<string>(out var typeText) || typeText != "playmode")
                {
                    return null;
                }
            }

            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>playgo-scenario.json cho cấu trúc nhiều kịch bản của gói gốc: mỗi kịch bản một mục, tiêu đề = nhãn.</summary>
    public static string ScenarioJson(PlayGoStructure structure, string language)
    {
        var defaultLanguage = structure.DefaultLanguageId < SonySdkPlayGo.LanguageCodes.Count ? SonySdkPlayGo.LanguageCodes[structure.DefaultLanguageId] : language;
        var scenarios = new JsonArray();
        foreach (var scenario in structure.Scenarios)
        {
            scenarios.Add(new JsonObject
            {
                ["id"] = scenario.Id,
                ["type"] = "playmode",
                [defaultLanguage] = new JsonObject
                {
                    ["title"] = scenario.Label,
                    ["description"] = scenario.Id == structure.DefaultScenarioId ? "Default play scenario" : scenario.Label,
                },
            });
        }

        var json = new JsonObject
        {
            ["scenarioCount"] = structure.Scenarios.Count,
            ["scenarioDefaultId"] = structure.DefaultScenarioId,
            ["scenarioDefaultLanguage"] = defaultLanguage,
            ["scenarios"] = scenarios,
        };
        return PythonJson.Serialize(json) + "\n";
    }

    /// <summary>
    /// <c>write_standard_param</c>: param.json của nguồn với applicationDrmType = "standard", ghi lại theo json.dumps(indent=2,
    /// ensure_ascii=False) + "\n"; các sửa đổi tuỳ chọn của công cụ (versionFileUri, attribute3, hạ firmware) áp thêm. Trả về
    /// danh sách thay đổi (đã dịch) so với nguồn.
    /// </summary>
    public static IReadOnlyList<string> WriteStandardParam(string source, string destination, ParamJsonPatchOptions extra, SonySdkSizeSelection? selection = null)
    {
        var node = ParseParamObject(source);
        var options = extra with { ForceStandardDrm = true };
        var changes = ParamJsonPatch.ApplyTo(node, options);
        if (node[ParamJsonPatch.DrmField] is not JsonValue drm || !drm.TryGetValue<string>(out var text) || text != ParamJsonPatch.StandardDrm)
        {
            // Trường thiếu hoặc không phải chuỗi: script gốc gán thẳng value["applicationDrmType"] = "standard".
            node[ParamJsonPatch.DrmField] = ParamJsonPatch.StandardDrm;
        }

        if (selection != null)
        {
            SonySdkPackageSize.Apply(node, selection, source);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        WriteScriptText(destination, PythonJson.Serialize(node) + "\n");
        return changes;
    }

    /// <summary>
    /// Ghi tệp văn bản như script gốc chạy trên Windows (nền tảng của bộ công cụ: build.bat, build-from-folder.ps1): Python mở tệp ở
    /// chế độ văn bản (<c>ElementTree.write</c>, <c>Path.write_text</c>) nên mỗi "\n" thành "\r\n". Publishing Tools chép nguyên
    /// playgo-scenario.json vào gói, vì vậy phải xuống dòng giống hệt thì gói mới giống từng byte gói của bộ công cụ; macOS dùng cùng
    /// quy tắc để cùng một nguồn cho ra cùng một gói trên mọi máy.
    /// </summary>
    public static void WriteScriptText(string path, string text) => File.WriteAllText(path, text.Replace("\n", "\r\n"), new UTF8Encoding(false));

    /// <summary>Một ảnh DDS trong sce_sys cần chuyển sang PNG: tệp DDS, tên PNG đích và PNG của nguồn bị thay (nếu có).</summary>
    public sealed record DdsConversion(string DdsPath, string PngName, bool ReplacesSourcePng);

    [GeneratedRegex("^(?:icon0|pic0|pic1)(?:_[0-9]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex RgbPresentationPattern();

    [GeneratedRegex("^pic2(?:_[0-9]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex RgbaPresentationPattern();

    /// <summary><c>required_png_color_type</c>: 2 (RGB 8 bit) cho icon0/pic0/pic1[_NN], 6 (RGBA 8 bit) cho pic2[_NN], null cho ảnh khác.</summary>
    public static int? RequiredPngColorType(string name)
    {
        var stem = PythonCaseFold.Fold(PythonStem(name));
        return RgbPresentationPattern().IsMatch(stem) ? 2 : RgbaPresentationPattern().IsMatch(stem) ? 6 : null;
    }

    /// <summary><c>png_color_type</c>: kiểu màu IHDR của PNG 8 bit; null khi không phải PNG hợp lệ hoặc không phải 8 bit.</summary>
    public static int? PngColorType(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[26];
            if (stream.ReadAtLeast(header, 26, throwOnEndOfStream: false) < 26 || !header[..8].SequenceEqual(PngSignature) ||
                !header[8..12].SequenceEqual(new byte[] { 0, 0, 0, 0x0D }) || !header[12..16].SequenceEqual("IHDR"u8) || header[24] != 8)
            {
                return null;
            }

            return header[25];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>Path(name).stem</c>: bỏ phần đuôi cuối cùng (tên bắt đầu bằng dấu chấm và không có đuôi khác thì giữ nguyên).</summary>
    private static string PythonStem(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[..dot] : name;
    }

    /// <summary><c>Path(name).with_suffix(".png").name</c>.</summary>
    private static string PythonWithPngSuffix(string name) => PythonStem(name) + ".png";

    /// <summary>
    /// <c>sce_sys_dds_images</c>: mọi tệp *.dds (không phân biệt hoa thường) ngay trong sce_sys, sắp theo tên đã casefold, kèm tên PNG
    /// tương ứng; hai DDS cùng ra một PNG là lỗi.
    /// </summary>
    public static IReadOnlyList<(string DdsPath, string PngName)> SceSysDdsImages(string appFolder, bool skipJunk = false)
    {
        var system = Path.Combine(appFolder, "sce_sys");
        if (!Directory.Exists(system))
        {
            return Array.Empty<(string, string)>();
        }

        var images = new List<(string, string)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(system).EnumerateFiles()
                     .Where(file => !skipJunk || !JunkFileFinder.IsJunkFileName(file.Name))
                     .Select(file => (File: file, Key: PythonCaseFold.Fold(file.Name)))
                     .OrderBy(pair => pair.Key, Comparer<string>.Create(PythonCaseFold.CompareCodePoints))
                     .Select(pair => pair.File))
        {
            if (!PythonCaseFold.Fold(Path.GetExtension(file.Name)).Equals(".dds", StringComparison.Ordinal) || PythonStem(file.Name) == file.Name)
            {
                continue;
            }

            var pngName = PythonWithPngSuffix(file.Name);
            if (!names.Add(PythonCaseFold.Fold(pngName)))
            {
                throw new InvalidDataException(Loc.F("Sdk.DdsDuplicate", pngName));
            }

            images.Add((file.FullName, pngName));
        }

        return images;
    }

    /// <summary>
    /// Phần ảnh của <c>build_gp5</c> (fix12): DDS dùng khi PNG cùng tên thiếu hoặc sai chế độ màu SDK yêu cầu; PNG sai bị bỏ khỏi
    /// <paramref name="files"/> và ghi vào <paramref name="excluded"/> sau các mục của bộ duyệt. PNG trình bày sai màu mà không có DDS để
    /// sửa thì dừng như script.
    /// </summary>
    private static IReadOnlyList<DdsConversion> PlanPresentationImages(string appFolder, List<(string Relative, string FullPath, long Length)> files, List<string> excluded, bool skipJunk)
    {
        var sourcePngs = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < files.Count; index++)
        {
            sourcePngs.TryAdd(PythonCaseFold.Fold(files[index].Relative), index);
        }

        var conversions = new List<DdsConversion>();
        var replaced = new HashSet<int>();
        foreach (var (dds, pngName) in SceSysDdsImages(appFolder, skipJunk))
        {
            var required = RequiredPngColorType(pngName);
            if (sourcePngs.TryGetValue(PythonCaseFold.Fold("sce_sys/" + pngName), out var original))
            {
                if (required == null || PngColorType(files[original].FullPath) == required)
                {
                    continue;
                }

                replaced.Add(original);
                excluded.Add(files[original].Relative + " (invalid PNG color mode; replaced from DDS)");
            }

            conversions.Add(new DdsConversion(dds, pngName, sourcePngs.ContainsKey(PythonCaseFold.Fold("sce_sys/" + pngName))));
        }

        if (replaced.Count > 0)
        {
            var kept = files.Where((_, index) => !replaced.Contains(index)).ToList();
            files.Clear();
            files.AddRange(kept);
        }

        foreach (var file in files)
        {
            var folded = PythonCaseFold.Fold(file.Relative);
            if (folded.StartsWith("sce_sys/", StringComparison.Ordinal) && folded.Count(c => c == '/') == 1 &&
                PythonCaseFold.Fold(Path.GetExtension(file.Relative)) == ".png" &&
                RequiredPngColorType(file.Relative[(file.Relative.IndexOf('/') + 1)..]) is { } required &&
                PngColorType(file.FullPath) != required)
            {
                throw new InvalidDataException(Loc.F("Sdk.PngWrongMode", file.Relative, required == 2 ? "RGB" : "RGBA"));
            }
        }

        return conversions;
    }

    /// <summary>
    /// <c>convert_dds_images</c>: chạy prospero-dds2png cho từng ảnh (song song, kết quả giữ đúng thứ tự), <c>--preserve-alpha</c> trừ ảnh
    /// cần RGB, và kiểm tra PNG ra đúng chế độ màu. Dòng "Converting …" vào <paramref name="lines"/> theo thứ tự.
    /// </summary>
    private static IReadOnlyList<SonySdkRecoveredPng> ConvertDdsImages(IReadOnlyList<DdsConversion> conversions, string generatedSystem, Action<string, string, bool>? converter, List<string> lines, CancellationToken cancellationToken)
    {
        if (conversions.Count == 0)
        {
            return Array.Empty<SonySdkRecoveredPng>();
        }

        if (converter == null)
        {
            throw new InvalidOperationException(Loc.F("Sdk.DdsConverterMissing", string.Join(", ", conversions.Select(item => "sce_sys/" + Path.GetFileName(item.DdsPath)))));
        }

        Directory.CreateDirectory(generatedSystem);
        foreach (var conversion in conversions)
        {
            var destination = Path.Combine(generatedSystem, conversion.PngName);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new IOException($"generated PNG already exists: {destination}");
            }

            lines.Add($"Converting sce_sys/{Path.GetFileName(conversion.DdsPath)} to {conversion.PngName} ({Mode(conversion)})...");
        }

        // Mỗi ảnh 4K BC7 mất vài giây (qua Wine trên macOS/Linux) và game có thể có cả chục pic2_XX: chạy song song, mỗi lần một tiến trình.
        var converted = new SonySdkRecoveredPng?[conversions.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8), CancellationToken = cancellationToken };
        try
        {
            Parallel.For(0, conversions.Count, options, index =>
            {
                var conversion = conversions[index];
                var destination = Path.Combine(generatedSystem, conversion.PngName);
                var alpha = PreserveAlpha(conversion);
                converter(conversion.DdsPath, destination, alpha);
                if (PngColorType(destination) != (alpha ? 6 : 2))
                {
                    throw new InvalidDataException(Loc.F("Sdk.DdsConvertInvalid", destination) + " (8-bit " + Mode(conversion) + ")");
                }

                converted[index] = new SonySdkRecoveredPng("sce_sys/" + conversion.PngName, Path.GetFileName(conversion.DdsPath), destination, conversion.ReplacesSourcePng, Mode(conversion));
            });
        }
        catch (AggregateException ex)
        {
            // Parallel.For gói lỗi lại: trả về lỗi đầu tiên (huỷ giữ nguyên là OperationCanceledException).
            throw ex.InnerExceptions.FirstOrDefault(inner => inner is OperationCanceledException) ?? ex.InnerExceptions[0];
        }

        return converted.Select(item => item!).ToList();

        static bool PreserveAlpha(DdsConversion conversion) => RequiredPngColorType(conversion.PngName) is null or 6;
        static string Mode(DdsConversion conversion) => PreserveAlpha(conversion) ? "RGBA" : "RGB";
    }

    /// <summary>
    /// <c>prepare_executable_inputs</c>: đọc header mọi tệp của GP5 (song song), SELF cần sửa thì ghi bản sửa vào
    /// .gp5-assets/&lt;tên&gt;/normalized-self/&lt;đường dẫn&gt; theo thứ tự tệp. Trả về chỉ số tệp → (bản sửa, mô tả).
    /// </summary>
    private static Dictionary<int, (string Path, string Repairs)> PrepareExecutableInputs(List<(string Relative, string FullPath, long Length)> files, string generatedRoot, List<string> lines, CancellationToken cancellationToken)
    {
        var plans = new SelfRepairPlan?[files.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = cancellationToken };
        try
        {
            Parallel.For(0, files.Count, options, index => plans[index] = files[index].Length < 0x20 ? null : SonySdkSelfRepair.Plan(files[index].FullPath));
        }
        catch (AggregateException ex)
        {
            throw ex.InnerExceptions.FirstOrDefault(inner => inner is OperationCanceledException) ?? ex.InnerExceptions[0];
        }

        var repairs = new Dictionary<int, (string, string)>();
        for (var index = 0; index < files.Count; index++)
        {
            if (plans[index] is not { } plan)
            {
                continue;
            }

            var relative = files[index].Relative;
            var destination = Path.Combine(generatedRoot, SonySdkSelfRepair.FolderName, relative.Replace('/', Path.DirectorySeparatorChar));
            lines.Add($"Repairing SELF {relative} ({plan.Description})...");
            SonySdkSelfRepair.Write(files[index].FullPath, destination, plan, cancellationToken);
            repairs[index] = (destination, plan.Description);
        }

        return repairs;
    }

    /// <summary>
    /// Chunk của <paramref name="playGo"/> không có dữ liệu sau khi ánh xạ → mỗi chunk một tệp giữ chỗ trong
    /// <see cref="SonySdkPlayGo.ChunkPayloadFolder"/>. Chunk 0 luôn có dữ liệu (tệp không có trong bảng, param.json, scenario, PNG khôi phục
    /// đều vào đó); tệp 0 byte không tạo extent nào nên không tính. Bố cục script ghi chunk="0" trên mọi tệp game nên chỉ chunk ngôn ngữ có dữ liệu.
    /// </summary>
    private static List<PlayGoLanguagePayload> EmptyChunkFillers(SonySdkSourcePlan plan, PlayGoStructure? playGo, List<(string Relative, string FullPath, long Length)> files, IReadOnlyList<PlayGoLanguagePayload> languagePayloads)
    {
        var fillers = new List<PlayGoLanguagePayload>();
        if (!plan.FillEmptyChunks || playGo == null || playGo.Chunks.Count < 2)
        {
            return fillers;
        }

        var occupied = new HashSet<int> { 0 };
        occupied.UnionWith(languagePayloads.Select(payload => payload.ChunkId));
        if (!playGo.ScriptLayout)
        {
            foreach (var file in files)
            {
                if (file.Length > 0 && playGo.ChunkOf(file.Relative) is { } chunk)
                {
                    occupied.Add(chunk);
                }
            }
        }

        // Nguồn là gói đã giải nén từng tạo bằng công cụ (còn playgo-chunks/) thì tránh trùng dst_path — Publishing Tools từ chối GP5 có hai tệp cùng đường dẫn.
        var taken = new HashSet<string>(files.Select(file => file.Relative), StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(languagePayloads.Select(payload => payload.Destination));
        foreach (var chunk in playGo.Chunks.Where(chunk => !occupied.Contains(chunk.Id)))
        {
            var destination = $"{SonySdkPlayGo.ChunkPayloadFolder}/{chunk.Id:000}.bin";
            for (var suffix = 2; taken.Contains(destination); suffix++)
            {
                destination = $"{SonySdkPlayGo.ChunkPayloadFolder}/{chunk.Id:000}-{suffix}.bin";
            }

            taken.Add(destination);
            fillers.Add(new PlayGoLanguagePayload(destination, chunk.Id, string.Empty));
        }

        return fillers;
    }

    /// <summary>Thư mục con trong .gp5-assets/&lt;tên&gt; chứa UCP đã tính lại digest.</summary>
    public const string RepairedUcpFolderName = "repaired-ucp";

    /// <summary>
    /// UCP của sce_sys/trophy2 và sce_sys/uds có digest SHA-1 lệch: ghi bản đã tính lại digest vào .gp5-assets/&lt;tên&gt;/repaired-ucp/ và
    /// cho GP5 trỏ tới đó. Script gốc đưa nguyên tệp vào GP5 và Publishing Tools từ chối cả lượt ("ucp_header.ucp_digest is invalid"); đường
    /// engine tích hợp cũng được sửa trong gương. UCP không đọc được thì giữ nguyên và báo
    /// trong <paramref name="invalid"/>. Không có UCP lệch thì GP5 giống hệt script. Trả về chỉ số tệp → bản sửa.
    /// </summary>
    private static Dictionary<int, string> PrepareUcpInputs(List<(string Relative, string FullPath, long Length)> files, string generatedRoot, List<string> lines, out IReadOnlyList<(string Relative, string Error)> invalid, CancellationToken cancellationToken)
    {
        var repairs = new Dictionary<int, string>();
        var broken = new List<(string, string)>();
        for (var index = 0; index < files.Count; index++)
        {
            var (relative, fullPath, length) = files[index];
            if (!UcpInspector.IsCandidate(relative) || length > UcpInspector.MaxSize)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var data = File.ReadAllBytes(fullPath);
            switch (UcpInspector.Inspect(data, out var error))
            {
                case UcpState.StaleDigest:
                    var destination = Path.Combine(generatedRoot, RepairedUcpFolderName, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    lines.Add($"Repairing UCP digest {relative}...");
                    File.WriteAllBytes(destination, UcpInspector.Repair(data));
                    repairs[index] = destination;
                    break;
                case UcpState.Invalid:
                    broken.Add((relative, error ?? "invalid"));
                    break;
            }
        }

        invalid = broken;
        return repairs;
    }

    /// <summary>param.json (utf-8-sig) → JsonObject; lỗi phân tích báo như script.</summary>
    private static JsonObject ParseParamObject(string source)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(source), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                   ?? throw new InvalidDataException(Loc.F("Sdk.ParamInvalid", source, "JSON object expected"));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Loc.F("Sdk.ParamInvalid", source, ex.Message), ex);
        }
    }

    /// <param name="chunk">Thuộc tính <c>chunk</c>; âm = không ghi.</param>
    /// <param name="noCompression"><c>pfs_compression="disable"</c> (tệp giữ chỗ ngôn ngữ của script fix6).</param>
    private static void AppendFile(StringBuilder xml, string destination, string source, int chunk = -1, bool noCompression = false)
    {
        xml.Append("    <file dst_path=\"").Append(EscapeAttribute(destination)).Append("\" src_path=\"").Append(EscapeAttribute(source)).Append('"');
        if (chunk >= 0)
        {
            xml.Append(" chunk=\"").Append(chunk).Append('"');
        }

        if (noCompression)
        {
            xml.Append(" pfs_compression=\"disable\"");
        }

        xml.Append(" />\n");
    }

    /// <summary><c>xml.etree.ElementTree._escape_attrib</c> (Python 3.9–3.12).</summary>
    public static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")
            .Replace("\r", "&#13;").Replace("\n", "&#10;").Replace("\t", "&#09;");

    /// <summary>Đường dẫn tuyệt đối đã giải mọi liên kết tượng trưng/junction trên từng thành phần (<c>pathlib.Path.resolve()</c>); phần chưa tồn tại giữ nguyên.</summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return current;
    }

    /// <summary>
    /// Duyệt thư mục như <c>collect_files</c>: từng thư mục sắp theo <c>name.casefold()</c>, thư mục con nằm đúng vị trí của nó
    /// trong thứ tự đó. Các thư mục con được đọc song song (ổ USB/exFAT với ~100 000 tệp mất hàng chục giây khi đọc tuần tự) rồi
    /// ghép lại theo đúng thứ tự, nên kết quả không phụ thuộc vào việc chạy song song.
    /// </summary>
    private sealed class Walker(string root, string projectPath, ISet<string> keep, SonySdkSourcePlan plan, Action<int>? listed, CancellationToken cancellationToken, string? overlayRoot = null)
    {
        private static readonly int Parallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);

        private int _listed;

        // Đường dẫn của gói gốc (không phân biệt hoa thường → đúng hoa thường của gói gốc), cho tệp và cho thư mục cha.
        private readonly Dictionary<string, string>? _baseFiles = BuildBaseFiles(plan.OverlayBasePaths);
        private readonly Dictionary<string, string>? _baseDirectories = BuildBaseDirectories(plan.OverlayBasePaths);

        private static Dictionary<string, string>? BuildBaseFiles(IReadOnlyCollection<string>? paths)
        {
            if (paths == null)
            {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                map.TryAdd(path, path);
            }

            return map;
        }

        private static Dictionary<string, string>? BuildBaseDirectories(IReadOnlyCollection<string>? paths)
        {
            if (paths == null)
            {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                {
                    var directory = path[..slash];
                    if (!map.TryAdd(directory, directory))
                    {
                        break;
                    }
                }
            }

            return map;
        }

        /// <summary>Đường dẫn trong gói của một tệp thư mục update: đúng tên của gói gốc nếu tệp (hoặc thư mục cha) đã có ở đó.</summary>
        private string CanonicalOverlayPath(string relative, out bool replacesBase)
        {
            replacesBase = false;
            if (_baseFiles == null || _baseDirectories == null)
            {
                return relative;
            }

            if (_baseFiles.TryGetValue(relative, out var file))
            {
                replacesBase = true;
                return file;
            }

            for (var slash = relative.LastIndexOf('/'); slash > 0; slash = relative.LastIndexOf('/', slash - 1))
            {
                if (_baseDirectories.TryGetValue(relative[..slash], out var directory))
                {
                    return directory + relative[slash..];
                }
            }

            return relative;
        }

        public string Root { get; } = root;

        public List<(string Relative, string FullPath, long Length)> Files { get; } = new();

        public List<string> Excluded { get; } = new();

        public List<string> SkippedByApp { get; } = new();

        public List<string> Replaced { get; } = new();

        public List<string> OverlayReplaced { get; } = new();

        public List<string> OverlayAdded { get; } = new();

        /// <summary>Thư mục rỗng trong nguồn (không có mục nào ngoài tệp/thư mục rác), theo thứ tự duyệt — xem <see cref="SonySdkSourcePlan.KeepEmptyDirectories"/>.</summary>
        public List<string> EmptyDirectories { get; } = new();

        public void Walk(string directory)
        {
            DirectoryResult result;
            try
            {
                result = WalkDirectory(directory, overlayRoot, string.Empty);
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
                throw;
            }

            Files.AddRange(result.Files);
            Excluded.AddRange(result.Excluded);
            SkippedByApp.AddRange(result.SkippedByApp);
            Replaced.AddRange(result.Replaced);
            OverlayReplaced.AddRange(result.OverlayReplaced);
            OverlayAdded.AddRange(result.OverlayAdded);
            EmptyDirectories.AddRange(result.EmptyDirectories);
        }

        private sealed class DirectoryResult
        {
            public List<(string Relative, string FullPath, long Length)> Files { get; } = new();

            public List<string> Excluded { get; } = new();

            public List<string> SkippedByApp { get; } = new();

            public List<string> Replaced { get; } = new();

            public List<string> OverlayReplaced { get; } = new();

            public List<string> OverlayAdded { get; } = new();

            public List<string> EmptyDirectories { get; } = new();

            public void Append(DirectoryResult other)
            {
                Files.AddRange(other.Files);
                Excluded.AddRange(other.Excluded);
                SkippedByApp.AddRange(other.SkippedByApp);
                Replaced.AddRange(other.Replaced);
                OverlayReplaced.AddRange(other.OverlayReplaced);
                OverlayAdded.AddRange(other.OverlayAdded);
                EmptyDirectories.AddRange(other.EmptyDirectories);
            }
        }

        /// <summary>Một mục của thư mục đã ghép: tên trong gói, mục trên đĩa, thư mục gốc/update tương ứng (để đi tiếp) và nguồn gốc.</summary>
        private sealed record Merged(string Name, FileSystemInfo Entry, string? BaseDirectory, string? OverlayDirectory, bool FromOverlay, bool ReplacesBase);

        /// <summary>
        /// Các mục của một thư mục: chỉ thư mục gốc (như script), hoặc ghép thêm thư mục update — mục update cùng tên (không phân biệt hoa
        /// thường) thay mục gốc nhưng giữ TÊN của gốc (game mở tệp theo đúng tên cũ); thư mục có ở cả hai thì đi tiếp cả hai.
        /// </summary>
        private static List<Merged> MergedEntries(string? directory, string? overlayDirectory)
        {
            var merged = new List<Merged>();
            if (directory != null)
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                    merged.Add(new Merged(entry.Name, entry, isDirectory ? entry.FullName : null, null, false, false));
                }
            }

            if (overlayDirectory != null)
            {
                var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < merged.Count; i++)
                {
                    index.TryAdd(merged[i].Name, i);
                }

                foreach (var entry in new DirectoryInfo(overlayDirectory).EnumerateFileSystemInfos())
                {
                    var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                    if (index.TryGetValue(entry.Name, out var position))
                    {
                        var existing = merged[position];
                        var baseIsDirectory = existing.BaseDirectory != null;
                        merged[position] = isDirectory
                            ? new Merged(existing.Name, baseIsDirectory ? existing.Entry : entry, existing.BaseDirectory, entry.FullName, !baseIsDirectory, false)
                            : new Merged(existing.Name, entry, null, null, true, !baseIsDirectory);
                    }
                    else
                    {
                        merged.Add(new Merged(entry.Name, entry, null, isDirectory ? entry.FullName : null, true, false));
                    }
                }
            }

            return merged;
        }

        private DirectoryResult WalkDirectory(string? directory, string? overlayDirectory, string prefix)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Khoá sắp xếp tính một lần cho mỗi mục (casefold cấp phát chuỗi); OrderBy ổn định nên hoà thì giữ thứ tự liệt kê như sorted() của Python.
            var entries = MergedEntries(directory, overlayDirectory)
                .Select(entry => (Entry: entry, Key: PythonCaseFold.Fold(entry.Name)))
                .OrderBy(pair => pair.Key, Comparer<string>.Create(PythonCaseFold.CompareCodePoints))
                .Select(pair => pair.Entry)
                .ToList();

            // Mỗi mục cho ra một mảnh kết quả; thư mục con được duyệt song song rồi ghép đúng vị trí.
            var pieces = new DirectoryResult?[entries.Count];
            var directories = new List<int>();
            for (var index = 0; index < entries.Count; index++)
            {
                var item = entries[index];
                var entry = item.Entry;
                var relative = prefix.Length == 0 ? item.Name : prefix + "/" + item.Name;
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Script gốc: "symbolic link/junction is not a GP5 input".
                    throw new InvalidDataException(Loc.F("Sdk.SymlinkNotAllowed", entry.FullName));
                }

                var piece = new DirectoryResult();
                pieces[index] = piece;
                if (string.Equals(entry.FullName, projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    piece.Excluded.Add(relative + " (output GP5)");
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory && prefix.Length == 0 && PythonCaseFold.Fold(item.Name) == AssetsFolderName)
                {
                    // Thư mục .gp5-assets của một lần chạy trước ngay trong nguồn (khi thư mục xuất từng nằm trong nguồn).
                    piece.Excluded.Add(relative + "/ (generated GP5 assets)");
                    continue;
                }

                if (isDirectory && prefix.Length == 0 && GeneratedRootDirectories.Contains(PythonCaseFold.Fold(item.Name)))
                {
                    // fix12: sce_suppl/sce_sc của bản giải nén — Publishing Tools giữ riêng hai thư mục này.
                    piece.Excluded.Add(relative + "/ (reserved/SDK-generated directory)");
                    continue;
                }

                if (plan.SkipJunk && (isDirectory ? JunkFileFinder.IsJunkDirectoryName(entry.Name) : JunkFileFinder.IsJunkFileName(entry.Name)))
                {
                    piece.SkippedByApp.Add(relative + (isDirectory ? JunkFolderReason : JunkFileReason));
                    continue;
                }

                if (isDirectory && plan.Skip.Contains(relative))
                {
                    // Cả thư mục bị bỏ theo tuỳ chọn (tàn dư của bản dump: _DUBLEX_, Saved của Unreal…).
                    piece.SkippedByApp.Add(relative + OptionFolderReason);
                    continue;
                }

                if (isDirectory)
                {
                    pieces[index] = null;
                    directories.Add(index);
                    continue;
                }

                if (entry is not FileInfo file)
                {
                    piece.Excluded.Add(relative + " (not a regular file)");
                    continue;
                }

                if (SkipReason(relative, keep) is { } reason)
                {
                    piece.Excluded.Add(relative + " (" + reason + ")");
                    continue;
                }

                if (plan.Skip.Contains(relative))
                {
                    piece.SkippedByApp.Add(relative + OptionFileReason);
                    continue;
                }

                var fullPath = file.FullName;
                var length = file.Length;
                if (plan.Replace.TryGetValue(relative, out var replacement))
                {
                    fullPath = replacement;
                    length = new FileInfo(replacement).Length;
                    piece.Replaced.Add(relative);
                }

                if (item.FromOverlay)
                {
                    relative = CanonicalOverlayPath(relative, out var replacesListedBase);
                    (item.ReplacesBase || replacesListedBase ? piece.OverlayReplaced : piece.OverlayAdded).Add(relative);
                }

                piece.Files.Add((relative, fullPath, length));

                var count = Interlocked.Increment(ref _listed);
                if (listed != null && count % 500 == 0)
                {
                    listed(count);
                }
            }

            DirectoryResult Descend(int index)
            {
                var overlay = entries[index].OverlayDirectory;
                if (prefix.Length == 0 && plan.SceSysOverlay != null && PythonCaseFold.Fold(entries[index].Name) == "sce_sys")
                {
                    // Metadata khôi phục online: sce_sys trong thư mục tạm đè lên sce_sys của nguồn.
                    overlay = plan.SceSysOverlay;
                }

                return WalkDirectory(entries[index].BaseDirectory, overlay, prefix.Length == 0 ? entries[index].Name : prefix + "/" + entries[index].Name);
            }

            if (directories.Count == 1)
            {
                pieces[directories[0]] = Descend(directories[0]);
            }
            else if (directories.Count > 1)
            {
                Parallel.ForEach(
                    directories,
                    new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                    index => pieces[index] = Descend(index));
            }

            var result = new DirectoryResult();

            // Thư mục rỗng của nguồn (hoặc chỉ có tệp rác như .DS_Store mà máy chép thêm): script chỉ liệt kê tệp nên nó mất khỏi gói.
            if (prefix.Length > 0 && entries.All(entry => plan.SkipJunk && ((entry.Entry.Attributes & FileAttributes.Directory) != 0
                    ? JunkFileFinder.IsJunkDirectoryName(entry.Name)
                    : JunkFileFinder.IsJunkFileName(entry.Name))))
            {
                result.EmptyDirectories.Add(prefix);
            }

            foreach (var piece in pieces)
            {
                result.Append(piece!);
            }

            return result;
        }
    }

    private static (string ContentId, string Language) ReadParam(string path)
    {
        JsonObject? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Loc.F("Sdk.ParamInvalid", path, ex.Message), ex);
        }

        var contentId = node?["contentId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (string.IsNullOrEmpty(contentId))
        {
            throw new InvalidDataException(Loc.F("Sdk.ParamNoContentId", path));
        }

        if (!ContentIdPattern().IsMatch(contentId))
        {
            throw new InvalidDataException(Loc.F("Sdk.ParamBadContentId", path, contentId));
        }

        var language = node?["localizedParameters"] is JsonObject localized &&
                       localized["defaultLanguage"] is JsonValue languageValue &&
                       languageValue.TryGetValue<string>(out var languageText) &&
                       !string.IsNullOrEmpty(languageText)
            ? languageText
            : "en-US";
        return (contentId, language);
    }

    /// <summary>playgo-scenario.json mặc định (<c>write_default_scenario</c>): một kịch bản "playmode" duy nhất, JSON thụt 2, xuống dòng cuối.</summary>
    public static string DefaultScenario(string language) => PythonJson.Serialize(SonySdkPlayGo.DefaultScenarioObject(language)) + "\n";

    /// <summary>default_language() của script: localizedParameters.defaultLanguage của param.json, thiếu/lỗi → "en-US".</summary>
    public static string DefaultLanguageOf(string paramPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(paramPath)) is JsonObject node &&
                   node["localizedParameters"] is JsonObject localized &&
                   localized["defaultLanguage"] is JsonValue value && value.TryGetValue<string>(out var language) && !string.IsNullOrEmpty(language)
                ? language
                : "en-US";
        }
        catch (Exception)
        {
            return "en-US";
        }
    }
}
