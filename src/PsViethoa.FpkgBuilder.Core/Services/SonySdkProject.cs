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
    /// <summary>Đúng như script gốc: không bỏ, không thay, không sửa gì ngoài applicationDrmType.</summary>
    public static readonly SonySdkSourcePlan Pure = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), false);
}

/// <summary>Ảnh PNG khôi phục từ DDS: đích trong gói (sce_sys/pic0.png), tên tệp DDS nguồn và tệp PNG đã ghi.</summary>
public sealed record SonySdkRecoveredPng(string Destination, string DdsName, string PngPath);

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
{
    /// <summary>Đầu ra chuẩn của create-gp5-from-folder.py cho lượt này (ghi vào 01-create-gp5.log như build-from-folder.ps1).</summary>
    public string Report
    {
        get
        {
            var builder = new StringBuilder();
            foreach (var png in RecoveredPngs)
            {
                builder.Append("Recovering ").Append(png.Destination).Append(" from ").Append(png.DdsName).Append("...\n");
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

            return builder.ToString();
        }
    }
}

/// <summary>
/// Tạo dự án GP5 "phẳng" từ một thư mục ứng dụng, giống từng byte với <c>scripts/create-gp5-from-folder.py</c> của bộ
/// sdk-fpkg279-fixdss3 (chế độ <c>--keep-keystone --absolute-paths</c> mà build-from-folder.ps1 dùng): mọi tệp được liệt kê tường
/// minh (không dùng rootdir) để tàn dư của lần giải nén/đóng gói trước không lọt vào gói; các tệp sce_sys do SDK tự sinh (PlayGo,
/// pfs-version, license giả, ảnh .dds, about/…) bị loại để SDK tạo lại; <c>ampr_emu.index</c> và hai module giả lập
/// <c>fakelib/libSceAmpr.sprx</c>, <c>fakelib/libScePlayGo.sprx</c> bị loại; keystone 96 byte của nguồn luôn được giữ (SDK đã vá
/// nhận keystone này thay vì tự tạo từ passcode). param.json được chuẩn hoá với applicationDrmType = "standard" và ảnh
/// <c>sce_sys/pic*.png</c> thiếu được khôi phục từ <c>.dds</c> vào thư mục <c>.gp5-assets/&lt;tên&gt;/sce_sys</c> cạnh GP5; nguồn không
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
    };

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

        if (!HasValidKeystone(appFolder, out var keystoneLength))
        {
            throw new InvalidDataException(keystoneLength == null
                ? Loc.F("Sdk.KeystoneMissing", Path.Combine(appFolder, "sce_sys", "keystone"))
                : Loc.F("Sdk.KeystoneLength", keystoneLength, KeystoneLength));
        }

        // Script gốc dùng Path.resolve(): đường dẫn thật, không qua liên kết (/var → /private/var trên macOS, junction trên Windows).
        projectPath = RealPath(projectPath);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { KeystonePath };
        var walker = new Walker(RealPath(appFolder), projectPath, keep, plan, listed, cancellationToken);
        walker.Walk(walker.Root);
        listed?.Invoke(walker.Files.Count);

        var paramIndex = walker.Files.FindIndex(file => file.Relative.Equals("sce_sys/param.json", StringComparison.OrdinalIgnoreCase));
        if (paramIndex < 0)
        {
            throw new InvalidDataException(Loc.T("Sdk.ParamMissing"));
        }

        var paramSource = walker.Files[paramIndex].FullPath;
        var (contentId, language) = ReadParam(paramSource);
        var scenarioPath = Path.ChangeExtension(projectPath, ".playgo-scenario.json");
        var projectFolder = Path.GetDirectoryName(projectPath)!;
        Directory.CreateDirectory(projectFolder);
        var playGo = plan.PlayGo is { IsTrivial: false } structure ? structure : null;
        // Nhiều chunk: Publishing Tools chép nguyên playgo-scenario.json vào gói, nên dùng lại đúng tệp của gói gốc (tiêu đề, mô tả,
        // danh sách ngôn ngữ) khi nó khớp cấu trúc; không có/không khớp thì tạo từ nhãn kịch bản. 1 chunk: như script gốc.
        var originalScenario = playGo == null ? null : OriginalScenarioJson(Path.Combine(walker.Root, "sce_sys", SonySdkPlayGo.ScenarioFileName), playGo);
        if (originalScenario != null)
        {
            File.WriteAllBytes(scenarioPath, originalScenario);
        }
        else
        {
            WriteScriptText(scenarioPath, playGo == null ? DefaultScenario(language) : ScenarioJson(playGo, language));
        }

        // .gp5-assets/<tên>/sce_sys: param.json chuẩn hoá (write_standard_param) + PNG khôi phục (recover_presentation_pngs).
        var generatedSystem = Path.Combine(projectFolder, AssetsFolderName, Path.GetFileNameWithoutExtension(projectPath), "sce_sys");
        Directory.CreateDirectory(generatedSystem);
        var generatedParam = Path.Combine(generatedSystem, "param.json");
        var paramChanges = WriteStandardParam(paramSource, generatedParam, plan.ParamPatch);
        var recovered = RecoverPresentationPngs(walker.Root, generatedSystem, ddsConverter, cancellationToken);

        // Cùng bố cục với ElementTree của Python (khai báo nháy đơn, thụt 2 khoảng trắng, " />" cho phần tử rỗng, không xuống dòng cuối).
        var xml = new StringBuilder(512 + walker.Files.Count * 160);
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
            // Cấu trúc chunk/kịch bản/ngôn ngữ của gói gốc (Publishing Tools tự ghi đúng cú pháp này qua gp5_chunk_add/gp5_scenario_add).
            xml.Append(SonySdkPlayGo.ChunkInfoXml(playGo));
        }

        xml.Append("  </volume>\n");
        xml.Append("  <files>\n");
        AppendFile(xml, "sce_sys/playgo-scenario.json", toolPath(scenarioPath));
        foreach (var png in recovered)
        {
            AppendFile(xml, png.Destination, toolPath(png.PngPath));
        }

        var written = 0;
        var mapped = 0;
        for (var index = 0; index < walker.Files.Count; index++)
        {
            var file = walker.Files[index];
            // Tệp thuộc chunk khác 0 trong gói gốc: giữ nguyên (gp5_file_add --chunk); chunk 0 là mặc định nên không ghi.
            var chunk = playGo?.ChunkOf(file.Relative) ?? 0;
            if (chunk > 0)
            {
                mapped++;
            }

            AppendFile(xml, file.Relative, toolPath(index == paramIndex ? generatedParam : file.FullPath), chunk);
            if (++written % 2000 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
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
            walker.Files.Count + recovered.Count + 1,
            walker.Files.Sum(file => file.Length),
            walker.Excluded,
            walker.SkippedByApp,
            walker.Replaced,
            mapped,
            originalScenario != null,
            walker.Files.Where(file => !IsSdkPackagePath(file.Relative)).Select(file => file.Relative).ToList(),
            walker.Files.Select(file => file.FullPath).ToList());
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
    public static IReadOnlyList<string> WriteStandardParam(string source, string destination, ParamJsonPatchOptions extra)
    {
        JsonObject node;
        try
        {
            node = JsonNode.Parse(File.ReadAllBytes(source), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                   ?? throw new InvalidDataException(Loc.F("Sdk.ParamInvalid", source, "JSON object expected"));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Loc.F("Sdk.ParamInvalid", source, ex.Message), ex);
        }

        var options = extra with { ForceStandardDrm = true };
        var changes = ParamJsonPatch.ApplyTo(node, options);
        if (node[ParamJsonPatch.DrmField] is not JsonValue drm || !drm.TryGetValue<string>(out var text) || text != ParamJsonPatch.StandardDrm)
        {
            // Trường thiếu hoặc không phải chuỗi: script gốc gán thẳng value["applicationDrmType"] = "standard".
            node[ParamJsonPatch.DrmField] = ParamJsonPatch.StandardDrm;
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

    /// <summary><c>missing_presentation_pngs</c>: pic*.dds trong sce_sys không có PNG cùng tên, theo thứ tự tên đã casefold.</summary>
    public static IReadOnlyList<(string DdsPath, string PngName)> MissingPresentationPngs(string appFolder)
    {
        var system = Path.Combine(appFolder, "sce_sys");
        if (!Directory.Exists(system))
        {
            return Array.Empty<(string, string)>();
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(system).EnumerateFiles())
        {
            files[PythonCaseFold.Fold(file.Name)] = file.FullName;
        }

        var missing = new List<(string, string)>();
        foreach (var name in files.Keys.OrderBy(key => key, Comparer<string>.Create(PythonCaseFold.CompareCodePoints)))
        {
            if (!name.StartsWith("pic", StringComparison.Ordinal) || !name.EndsWith(".dds", StringComparison.Ordinal))
            {
                continue;
            }

            var dds = files[name];
            var pngName = Path.GetFileNameWithoutExtension(dds) + ".png";
            if (!files.ContainsKey(PythonCaseFold.Fold(pngName)))
            {
                missing.Add((dds, pngName));
            }
        }

        return missing;
    }

    private static IReadOnlyList<SonySdkRecoveredPng> RecoverPresentationPngs(string appFolder, string generatedSystem, Action<string, string, bool>? converter, CancellationToken cancellationToken)
    {
        var missing = MissingPresentationPngs(appFolder);
        if (missing.Count == 0)
        {
            return Array.Empty<SonySdkRecoveredPng>();
        }

        if (converter == null)
        {
            throw new InvalidOperationException(Loc.F("Sdk.DdsConverterMissing", string.Join(", ", missing.Select(item => "sce_sys/" + Path.GetFileName(item.DdsPath)))));
        }

        var recovered = new List<SonySdkRecoveredPng>();
        foreach (var (dds, pngName) in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(generatedSystem, pngName);
            Directory.CreateDirectory(generatedSystem);
            converter(dds, destination, PythonCaseFold.Fold(Path.GetFileNameWithoutExtension(dds)) == "pic2");
            if (!File.Exists(destination) || !HasPngSignature(destination))
            {
                throw new InvalidDataException(Loc.F("Sdk.DdsConvertInvalid", destination));
            }

            recovered.Add(new SonySdkRecoveredPng("sce_sys/" + pngName, Path.GetFileName(dds), destination));
        }

        return recovered;
    }

    private static bool HasPngSignature(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8];
        return stream.Read(head) == 8 && head.SequenceEqual(PngSignature);
    }

    private static void AppendFile(StringBuilder xml, string destination, string source, int chunk = 0)
    {
        xml.Append("    <file dst_path=\"").Append(EscapeAttribute(destination)).Append("\" src_path=\"").Append(EscapeAttribute(source)).Append('"');
        if (chunk > 0)
        {
            xml.Append(" chunk=\"").Append(chunk).Append('"');
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
    private sealed class Walker(string root, string projectPath, ISet<string> keep, SonySdkSourcePlan plan, Action<int>? listed, CancellationToken cancellationToken)
    {
        private static readonly int Parallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);

        private int _listed;

        public string Root { get; } = root;

        public List<(string Relative, string FullPath, long Length)> Files { get; } = new();

        public List<string> Excluded { get; } = new();

        public List<string> SkippedByApp { get; } = new();

        public List<string> Replaced { get; } = new();

        public void Walk(string directory)
        {
            DirectoryResult result;
            try
            {
                result = WalkDirectory(directory, string.Empty);
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
        }

        private sealed class DirectoryResult
        {
            public List<(string Relative, string FullPath, long Length)> Files { get; } = new();

            public List<string> Excluded { get; } = new();

            public List<string> SkippedByApp { get; } = new();

            public List<string> Replaced { get; } = new();

            public void Append(DirectoryResult other)
            {
                Files.AddRange(other.Files);
                Excluded.AddRange(other.Excluded);
                SkippedByApp.AddRange(other.SkippedByApp);
                Replaced.AddRange(other.Replaced);
            }
        }

        private DirectoryResult WalkDirectory(string directory, string prefix)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Khoá sắp xếp tính một lần cho mỗi mục (casefold cấp phát chuỗi); OrderBy ổn định nên hoà thì giữ thứ tự liệt kê như sorted() của Python.
            var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos()
                .Select(entry => (Entry: entry, Key: PythonCaseFold.Fold(entry.Name)))
                .OrderBy(pair => pair.Key, Comparer<string>.Create(PythonCaseFold.CompareCodePoints))
                .Select(pair => pair.Entry)
                .ToList();

            // Mỗi mục cho ra một mảnh kết quả; thư mục con được duyệt song song rồi ghép đúng vị trí.
            var pieces = new DirectoryResult?[entries.Count];
            var directories = new List<int>();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
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
                if (isDirectory && prefix.Length == 0 && PythonCaseFold.Fold(entry.Name) == AssetsFolderName)
                {
                    // Thư mục .gp5-assets của một lần chạy trước ngay trong nguồn (khi thư mục xuất từng nằm trong nguồn).
                    piece.Excluded.Add(relative + "/ (generated GP5 assets)");
                    continue;
                }

                if (plan.SkipJunk && (isDirectory ? JunkFileFinder.IsJunkDirectoryName(entry.Name) : JunkFileFinder.IsJunkFileName(entry.Name)))
                {
                    piece.SkippedByApp.Add(relative + (isDirectory ? "/ (OS junk folder)" : " (OS junk file)"));
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
                    piece.SkippedByApp.Add(relative + " (excluded by option)");
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

                piece.Files.Add((relative, fullPath, length));
                var count = Interlocked.Increment(ref _listed);
                if (listed != null && count % 500 == 0)
                {
                    listed(count);
                }
            }

            if (directories.Count == 1)
            {
                var index = directories[0];
                pieces[index] = WalkDirectory(entries[index].FullName, prefix.Length == 0 ? entries[index].Name : prefix + "/" + entries[index].Name);
            }
            else if (directories.Count > 1)
            {
                Parallel.ForEach(
                    directories,
                    new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                    index => pieces[index] = WalkDirectory(entries[index].FullName, prefix.Length == 0 ? entries[index].Name : prefix + "/" + entries[index].Name));
            }

            var result = new DirectoryResult();
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
    public static string DefaultScenario(string language)
    {
        var scenario = new JsonObject
        {
            ["scenarioCount"] = 1,
            ["scenarioDefaultId"] = 0,
            ["scenarioDefaultLanguage"] = language,
            ["scenarios"] = new JsonArray(new JsonObject
            {
                ["id"] = 0,
                ["type"] = "playmode",
                [language] = new JsonObject
                {
                    ["title"] = "Scenario #0",
                    ["description"] = "Default play scenario",
                },
            }),
        };
        return PythonJson.Serialize(scenario) + "\n";
    }
}
