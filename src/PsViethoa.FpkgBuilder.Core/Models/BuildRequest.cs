namespace PsViethoa.FpkgBuilder.Core.Models;

/// <summary>Toàn bộ tham số của một lần tạo gói.</summary>
public sealed class BuildRequest
{
    public const int PasscodeLength = 32;
    public const int MinKrakenLevel = -4;
    public const int MaxKrakenLevel = 9;
    public const int DefaultKrakenLevel = 4;
    public const int MaxThreads = 256;
    public const int MinPlayGoChunks = 1;
    public const int MaxPlayGoChunks = 255;

    /// <summary>Số khối PlayGo dự phòng mặc định của engine 0.6.8 (dùng khi nguồn không có playgo-chunk.dat).</summary>
    public const int DefaultPlayGoChunks = 100;
    public const int MinSdkMajor = 1;
    public const int MaxSdkMajor = 11;
    public const int MinKrakenBlockKiB = 128;
    public const int MaxKrakenBlockKiB = 256;
    public const int DefaultKrakenBlockKiB = 256;

    /// <summary>Thư mục ứng dụng (chứa sce_sys) hoặc tệp ảnh đĩa exFAT (.exfat).</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Cách xử lý khi nguồn là ảnh exFAT.</summary>
    public ExFatStrategy ExFat { get; set; } = ExFatStrategy.Auto;

    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Thư mục tạm cho ảnh trung gian. Để trống sẽ tự chọn cùng ổ đĩa với thư mục xuất.</summary>
    public string TemporaryFolder { get; set; } = string.Empty;

    public string ContentId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Version { get; set; } = "01.000.000";

    public string Passcode { get; set; } = new('0', PasscodeLength);

    public PackageKind Kind { get; set; } = PackageKind.Application;

    public OuterImageMode ImageMode { get; set; } = OuterImageMode.PlaintextNoAuth;

    public KrakenBackendKind KrakenBackend { get; set; } = KrakenBackendKind.Auto;

    /// <summary>Mức nén Kraken -4..9 (7 = mặc định SDK).</summary>
    public int KrakenLevel { get; set; } = DefaultKrakenLevel;

    /// <summary>Số luồng nén; 0 = số nhân CPU logic.</summary>
    public int Threads { get; set; }

    /// <summary>Định dạng container nén PFS (v2 mặc định, v3 mở các tuỳ chọn shuffle).</summary>
    public PfsFormat PfsFormat { get; set; } = PfsFormat.V2;

    /// <summary>Kích thước khối nén Kraken, KiB (128..256; 256 = mặc định SDK).</summary>
    public int KrakenBlockKiB { get; set; } = DefaultKrakenBlockKiB;

    /// <summary>Mẫu shuffle trước nén (chỉ có tác dụng với PFS v3).</summary>
    public ShufflePatternKind ShufflePattern { get; set; } = ShufflePatternKind.None;

    /// <summary>Tự đánh giá mọi mẫu shuffle trong lúc nén và chọn mẫu tốt nhất (PFS v3).</summary>
    public bool ShuffleAnalysis { get; set; }

    /// <summary>Mức Kraken dùng để phân giải bí danh shuffle dự đoán; null = tự động theo SDK (PFS v3).</summary>
    public int? ShufflePredictionLevel { get; set; }

    /// <summary>Bỏ kiểm tra tương thích input-header PFS v3 (chuyên gia).</summary>
    public bool SkipPfsInputCheck { get; set; }

    /// <summary>Gộp khối và điều chỉnh canh lề bố cục vật lý như Publishing Tools (khuyên bật).</summary>
    public bool LayoutOptimization { get; set; } = true;

    /// <summary>
    /// Ép applicationDrmType = "standard" trong gói (tránh game bị khoá trên PS5). Từ engine 0.6.8 việc này do chính thư viện làm
    /// trong bộ nhớ (ForceStandardApplicationDrm), nên không cần sửa hay sao chép param.json.
    /// </summary>
    public bool ForceStandardDrm { get; set; } = true;

    /// <summary>
    /// Bỏ tàn dư AMPR emu (ampr_emu.index, fakelib/libSceAmpr.sprx) khỏi gói. Engine luôn bỏ module giả; tệp chỉ mục hiện còn
    /// sót lại và tác giả thư viện cho biết sẽ bỏ nốt — bỏ sẵn ở đây để gói sạch như gói thật.
    /// </summary>
    public bool RemoveAmprLeftovers { get; set; } = true;

    /// <summary>
    /// Bỏ các tệp sce_sys/playgo* của bản dump khỏi gói để thư viện tự tạo bộ PlayGo khớp với ảnh mới (Drakmor: game không
    /// khởi chạy kèm lỗi "playgo" trong log là do bộ playgo cũ). Mặc định bật.
    /// </summary>
    public bool RemovePlayGoFiles { get; set; } = true;

    /// <summary>
    /// Giữ bộ giả lập DLC của Drakmor (dlc_emu.ini + module libSceAppContent / libSceNpEntitlementAccess / libSceGameUpdate trong
    /// fakelib) trong gói. Mặc định bật. Tắt để loại chúng khỏi gói (fakelib trống sau khi dọn cũng bị bỏ) và cài DLC bằng gói riêng.
    /// </summary>
    public bool KeepDlcEmu { get; set; } = true;

    /// <summary>
    /// Đặt <c>versionFileUri</c> trong param.json thành chuỗi rỗng lúc tạo gói: gói tự tạo không cập nhật qua URL phiên bản của
    /// gói gốc. Mặc định bật; tệp nguồn được khôi phục nguyên vẹn sau khi tạo gói.
    /// </summary>
    public bool ClearVersionFileUri { get; set; } = true;

    /// <summary>
    /// Đặt <c>attribute3</c> trong param.json về 0 lúc tạo gói — bước "xoá cờ PlayGo trong attribute3" của hướng dẫn sửa lỗi
    /// màn hình đen cũ. <b>Mặc định tắt từ 2.2.1</b>: attribute3 chứa các cờ tính năng (bit 7 = 120 Hz, bit 19 = VRR, bit 23 =
    /// PS5 Pro Enhanced…), đặt về 0 làm PS5 Pro không nhận "PS5 Pro Enhanced" và mất 120 Hz/VRR; tài liệu công khai không mô tả
    /// bit PlayGo nào ở đây, còn lỗi PlayGo thật đã được xử lý ở bảng playgo. Bộ công cụ Sony gốc cũng giữ nguyên trường này.
    /// </summary>
    public bool ClearPlayGoAttributes { get; set; }

    /// <summary>
    /// Hạ requiredSystemSoftwareVersion về đúng SDK của game khi nó đang cao hơn (fpkg-gui 0.6.8: "Automatic downgrading of the
    /// required software version to the SDK-specified version"). Khi chọn SDK riêng, engine tự đặt cả hai trường theo SDK đó
    /// nên tuỳ chọn này chỉ có tác dụng khi giữ SDK của game. Mặc định bật.
    /// </summary>
    public bool LowerRequiredFirmware { get; set; } = true;

    /// <summary>
    /// Các sửa đổi param.json công cụ tự làm trong lúc tạo gói. DRM không nằm ở đây vì engine đã làm; hạ phiên bản hệ thống chỉ
    /// cần khi không chọn SDK riêng.
    /// </summary>
    public Services.ParamJsonPatchOptions ParamPatch =>
        new(
            SonySdkActive,
            ClearVersionFileUri,
            ClearPlayGoAttributes,
            LowerRequiredFirmware && SdkMajorOverride is null,
            SonySdkActive && SdkApplyPackageDetails ? Version : null,
            SonySdkActive && SdkApplyPackageDetails ? Title : null);

    /// <summary>
    /// SDK Sony: ghi <see cref="Version"/> (contentVersion) và <see cref="Title"/> (titleName của ngôn ngữ mặc định) vào bản
    /// param.json chuẩn hoá khi chúng khác nguồn — để đổi phiên bản/tên ngay trong công cụ (cần cho bản vá). Giao diện luôn bật
    /// (hai ô tự điền từ nguồn nên không sửa thì không đổi gì); mã gọi trực tiếp mặc định tắt vì Version có giá trị mặc định.
    /// </summary>
    public bool SdkApplyPackageDetails { get; set; }

    /// <summary>
    /// Tạo gói bằng SDK Sony (Publishing Tools 2.79 đã vá, bộ công cụ sdk-fpkg729-fix) thay vì engine tích hợp. Mặc định bật.
    /// Chạy trực tiếp trên Windows, qua Wine kèm theo trên macOS. Chỉ áp dụng cho gói ứng dụng dạng lớp ngoài không mã hoá; nguồn
    /// phải có sce_sys/keystone 96 byte. Không dùng được (thiếu bộ công cụ / Wine) thì tạo bằng engine tích hợp.
    /// </summary>
    public bool UseSonySdk { get; set; } = true;

    /// <summary>
    /// SDK Sony trên Windows: mở trước song song mọi tệp nguồn (chỉ đọc) để Windows Defender quét song song trước khi Publishing
    /// Tools kiểm tra tuần tự từng tệp (<see cref="Services.SonySdkPrescan"/>). Không đổi GP5 hay gói. Mặc định bật.
    /// </summary>
    public bool SdkPrescan { get; set; } = true;

    /// <summary>
    /// SDK Sony: mức nén truyền cho <c>img_create --compression_level</c> (-4..9). Null (mặc định) = không truyền, Publishing Tools
    /// dùng mức 7 — đúng như bộ công cụ gốc. Mức thấp nén nhanh hơn nhưng gói lớn hơn (tuỳ chọn opt-in, xem docs/research).
    /// </summary>
    public int? SdkCompressionLevel { get; set; }

    /// <summary>
    /// SDK Sony: giữ .gp5, .playgo-scenario.json và .gp5-assets cạnh gói sau khi tạo xong (mặc định xoá như build-from-folder.ps1
    /// của bộ fix6; thư mục -build-logs luôn được giữ).
    /// </summary>
    public bool SdkKeepIntermediate { get; set; }

    /// <summary>
    /// SDK Sony (bộ fix8): gói .pkg gốc đã cài trên máy để tạo BẢN VÁ thay vì gói đầy đủ (<c>img_create --ref_pkg_path</c>). Kết quả là
    /// &lt;tên&gt;.pkg (delta, cài đè lên gói gốc) và &lt;tên&gt;.pkg.remastered.pkg (gói đầy đủ đi kèm để kiểm tra/giải nén).
    /// contentVersion của nguồn phải cao hơn gói gốc. Null = gói đầy đủ.
    /// </summary>
    public string? SdkReferencePackage { get; set; }

    /// <summary>
    /// Bản vá (có <see cref="SdkReferencePackage"/>) từ nguồn là thư mục: công cụ so đường dẫn tệp của nguồn với gói gốc; tệp gói gốc
    /// có mà nguồn KHÔNG có được lấy lại từ gói gốc (chỉ giải nén đúng những tệp thiếu vào thư mục tạm, hoặc đọc từ
    /// <see cref="SdkPatchBaseFolder"/>) — nên nguồn có thể chỉ là "thư mục update" gồm tệp thay đổi/thêm như Patch Builder của PS4.
    /// Bật cờ này để coi nguồn là bản ĐẦY ĐỦ đúng như nó có: tệp thiếu = đã xoá khỏi phiên bản mới (hành vi của bộ công cụ gốc).
    /// </summary>
    public bool SdkPatchExactSource { get; set; }

    /// <summary>Bản vá: giữ lại tệp nào sau khi Publishing Tools tạo xong (nó luôn ghi cả hai; tệp không chọn bị xoá sau khi kiểm tra cấu trúc).</summary>
    public SdkPatchOutput SdkPatchOutput { get; set; } = SdkPatchOutput.Both;

    /// <summary>Thư mục game gốc đầy đủ (bản dump đã dùng để tạo gói gốc) để lấy tệp thiếu mà không phải giải nén gói gốc; chỉ đọc. Null = lấy từ gói gốc.</summary>
    public string? SdkPatchBaseFolder { get; set; }

    /// <summary>
    /// SDK Sony: khi nguồn KHÔNG còn <c>sce_sys/playgo-chunk.dat</c> (đa số bản dump) thì tạo <see cref="PlayGoChunks"/> chunk và số kịch
    /// bản của playgo-scenario.json gốc như engine tích hợp, thay vì 1 chunk / 1 kịch bản của script gốc — để game hỏi chunk ngôn ngữ
    /// thoại hay chế độ chơi (The Last of Us Part I/II có 2 kịch bản) vẫn thấy đã cài. Nguồn còn bảng gốc thì luôn dùng bảng gốc.
    /// Tuỳ chọn opt-in, mặc định tắt (như bộ công cụ).
    /// </summary>
    public bool SdkPlayGoFallback { get; set; } = true;

    /// <summary>
    /// Do BuildEngine đặt khi lượt này thật sự chạy SDK Sony: bộ công cụ luôn chuẩn hoá param.json với applicationDrmType =
    /// "standard" (không có tuỳ chọn tắt), nên <see cref="ForceStandardDrm"/> không có tác dụng ở chế độ này.
    /// </summary>
    internal bool SonySdkActive { get; set; }

    /// <summary>
    /// Sau khi tạo gói, giải mã và giải nén thử toàn bộ nội dung trong bộ nhớ để đối chiếu (VerifyPackageFull của engine). Kiểm tra
    /// nhanh (cấu trúc, chữ ký CNT, bố cục PlayGo, NAPS, inode) luôn chạy; kiểm tra đầy đủ chậm ngang một lượt đọc hết gói.
    /// </summary>
    public bool FullVerify { get; set; }

    /// <summary>Cách đọc nguồn (thư mục rời / GP5).</summary>
    public SourceMode SourceMode { get; set; } = SourceMode.Auto;

    /// <summary>Tệp dự án GP5 khi SourceMode = Gp5Project.</summary>
    public string? ProjectFilePath { get; set; }

    /// <summary>Số khối PlayGo dự phòng (1..255). Nguồn còn playgo-chunk.dat thì engine lấy số khối/kịch bản từ tệp đó.</summary>
    public int PlayGoChunks { get; set; } = DefaultPlayGoChunks;

    public bool Deterministic { get; set; } = true;

    public bool ComputeSha256 { get; set; }

    /// <summary>Ghi đè SDK major (1..11); null = giữ nguyên metadata gốc.</summary>
    public int? SdkMajorOverride { get; set; }

    /// <summary>Đường dẫn libScePubTools.dll (chỉ dùng khi backend là PublishingTools/Auto trên Windows).</summary>
    public string? PublishingToolsPath { get; set; }

    /// <summary>Ngăn máy ngủ trong lúc tạo gói.</summary>
    public bool PreventSleep { get; set; } = true;

    public BuildRequest Clone() => (BuildRequest)MemberwiseClone();
}

/// <summary>Tệp xuất ra của một lượt tạo gói Update.</summary>
public enum SdkPatchOutput
{
    /// <summary>UPDATE_….pkg (delta, cài đè lên gói base) và ….remastered.pkg (cả game bản mới).</summary>
    Both,

    /// <summary>Chỉ UPDATE_….pkg.</summary>
    UpdateOnly,

    /// <summary>Chỉ ….remastered.pkg — gói game đầy đủ đã kèm update.</summary>
    FullOnly,
}
