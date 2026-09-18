using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.Services;

/// <summary>Cấu hình được lưu giữa các phiên làm việc.</summary>
public sealed class AppSettings
{
    /// <summary>Thư mục ứng dụng, tệp ảnh .exfat hoặc tệp dự án .gp5.</summary>
    public string SourcePath { get; set; } = string.Empty;

    public ExFatStrategy ExFat { get; set; } = ExFatStrategy.Auto;

    /// <summary>Cách đọc thư mục nguồn: tự dùng .gp5 ở cấp trên cùng (Auto) hay chỉ thư mục (Folder). Nguồn .gp5 luôn dùng Gp5Project.</summary>
    public SourceMode SourceMode { get; set; } = SourceMode.Auto;

    /// <summary>Ngôn ngữ giao diện: "vi" hoặc "en".</summary>
    public string Language { get; set; } = "vi";

    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Bật = thư mục xuất luôn tự đặt theo nguồn ("&lt;nguồn&gt;-pkg" cạnh nguồn); tắt = người dùng tự chọn.</summary>
    public bool OutputFolderAuto { get; set; } = true;

    public string TemporaryFolder { get; set; } = string.Empty;

    public string ContentId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Version { get; set; } = VersionHelper.Default;

    public PackageKind Kind { get; set; } = PackageKind.Application;

    public OuterImageMode ImageMode { get; set; } = OuterImageMode.PlaintextNoAuth;

    public KrakenBackendKind KrakenBackend { get; set; } = KrakenBackendKind.Auto;

    public int KrakenLevel { get; set; } = BuildRequest.DefaultKrakenLevel;

    public int Threads { get; set; }

    public PfsFormat PfsFormat { get; set; } = PfsFormat.V2;

    public int KrakenBlockKiB { get; set; } = BuildRequest.DefaultKrakenBlockKiB;

    public ShufflePatternKind ShufflePattern { get; set; } = ShufflePatternKind.None;

    public bool ShuffleAnalysis { get; set; }

    public bool SkipPfsInputCheck { get; set; }

    public bool LayoutOptimization { get; set; } = true;

    /// <summary>Ép applicationDrmType = "standard" khi tạo gói (mặc định bật; engine 0.6.8 làm trong bộ nhớ).</summary>
    public bool ForceStandardDrm { get; set; } = true;

    /// <summary>Tạo gói bằng SDK Sony (Publishing Tools 2.79 kèm theo) thay vì engine tích hợp — mặc định bật từ 2.2.0.</summary>
    public bool UseSonySdk { get; set; } = true;

    /// <summary>SDK Sony trên Windows: quét trước song song tệp nguồn (Windows Defender) trước Publishing Tools — mặc định bật.</summary>
    public bool SdkPrescan { get; set; } = true;

    /// <summary>SDK Sony: PlayGo dự phòng cho nguồn không còn playgo-chunk.dat — mặc định tắt (như bộ công cụ gốc).</summary>
    public bool SdkPlayGoFallback { get; set; } = true;

    /// <summary>SDK Sony: mức --compression_level; null = mặc định của Publishing Tools (7), như bộ công cụ gốc.</summary>
    public int? SdkCompressionLevel { get; set; }

    public int PlayGoChunks { get; set; } = BuildRequest.DefaultPlayGoChunks;

    /// <summary>
    /// Đánh dấu đã chuyển số khối PlayGo sang mặc định của engine 0.6.8. Bản cũ lưu 64 (vừa là mặc định vừa là tối đa cũ); cấu
    /// hình chưa có dấu này mà vẫn giữ 64 thì được đưa về 100.
    /// </summary>
    public int PlayGoDefaultsRevision { get; set; }

    /// <summary>Hạ requiredSystemSoftwareVersion về SDK của game khi tạo gói (mặc định bật, theo fpkg-gui 0.6.8).</summary>
    public bool LowerRequiredFirmware { get; set; } = true;

    /// <summary>Kiểm tra đầy đủ gói sau khi tạo (giải nén thử toàn bộ trong bộ nhớ).</summary>
    public bool FullVerify { get; set; }

    public bool Deterministic { get; set; } = true;

    public bool ComputeSha256 { get; set; }

    public bool PreventSleep { get; set; } = true;

    public bool OverrideSdk { get; set; }

    public int SdkMajor { get; set; } = 1;

    public string PublishingToolsPath { get; set; } = string.Empty;

    public bool AdvancedExpanded { get; set; }

    public bool AutoScrollLog { get; set; } = true;

    public string Theme { get; set; } = "Dark";

    public double WindowWidth { get; set; } = 1320;

    public double WindowHeight { get; set; } = 880;

    /// <summary>Chế độ cuối cùng: true = giải nén gói, false = tạo gói.</summary>
    public bool ExtractMode { get; set; }

    /// <summary>Tệp .pkg mở gần nhất trong chế độ giải nén.</summary>
    public string ExtractPackagePath { get; set; } = string.Empty;

    /// <summary>Thư mục giải nén gần nhất.</summary>
    public string ExtractOutputFolder { get; set; } = string.Empty;

    /// <summary>Bật = thư mục xuất của chế độ giải nén luôn tự đặt theo gói ("&lt;gói&gt;-extract" cạnh tệp .pkg).</summary>
    public bool ExtractOutputAuto { get; set; } = true;

    public List<string> RecentSources { get; set; } = new();

    /// <summary>Tự kiểm tra bản mới trên GitHub Releases khi khởi động (tối đa một lần mỗi 6 giờ).</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>Bỏ sce_sys/playgo* của bản dump khỏi gói để thư viện tạo bộ PlayGo mới (mặc định bật).</summary>
    public bool RemovePlayGoFiles { get; set; } = true;

    /// <summary>Giữ bộ giả lập DLC (dlc_emu.ini + module fakelib) trong gói (mặc định bật); tắt = dọn khỏi gói.</summary>
    public bool KeepDlcEmu { get; set; } = true;

    /// <summary>Xoá versionFileUri trong param.json khi tạo gói (mặc định bật).</summary>
    public bool ClearVersionFileUri { get; set; } = true;

    /// <summary>Đặt attribute3 trong param.json về 0 khi tạo gói (mặc định tắt từ 2.2.1: giữ cờ PS5 Pro / 120 Hz / VRR).</summary>
    public bool ClearPlayGoAttributes { get; set; }


    /// <summary>Dọn tàn dư AMPR emu (ampr_emu.index) khỏi gói.</summary>
    public bool RemoveAmprLeftovers { get; set; } = true;

    /// <summary>Đã hỏi "cài Dokan để gắn ảnh trực tiếp?" một lần rồi (Windows) — không hỏi lại.</summary>
    public bool DokanPromptShown { get; set; }

    public DateTime? LastUpdateCheckUtc { get; set; }

}

public static class SettingsService
{
    public static string Directory => JsonFileStore.AppDataDirectory(AppInfo.DataFolderName);

    public static string Path => System.IO.Path.Combine(Directory, "settings.json");

    public static AppSettings Load() => JsonFileStore.Load<AppSettings>(Path);

    public static bool Save(AppSettings settings) => JsonFileStore.Save(Path, settings);
}
