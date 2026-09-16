using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Core.Models;

/// <summary>Loại container của tệp PKG (theo ProsperoPkgReader.DetectType).</summary>
public enum PackageContainerKind
{
    Unknown,

    /// <summary>Chỉ có CNT (metadata) — không cài được, không có ảnh trong.</summary>
    Meta,

    /// <summary>Ảnh FIH đã ký retail — chỉ xem thông tin, không giải nén được.</summary>
    FullRetail,

    /// <summary>Ảnh FIH debug (chữ ký 0x00) — giải nén được.</summary>
    FullDebug,
}

/// <summary>Cách biểu diễn lớp PFS ngoài của gói đã hoàn thiện.</summary>
public enum PackageImageMode
{
    Unknown,

    /// <summary>Có dấu "PPRPLAIN-NOAUTH!" — lớp ngoài không mã hoá, đọc trực tiếp theo khoảng.</summary>
    PlaintextNoAuth,

    /// <summary>Lớp ngoài mã hoá AES-XTS — phải giải mã ra tệp tạm trước khi đọc.</summary>
    Native,
}

/// <summary>Các trường đọc được từ header FIH (64 KiB đầu tệp) của gói đã hoàn thiện.</summary>
public sealed record PackageFihInfo(
    byte SignedByte,
    bool IsOfficial,
    ulong PfsImageOffset,
    ulong PfsImageSize,
    ulong EmbeddedCntOffset,
    uint InnerImageBlockCount,
    uint MetadataBlockCount,
    ulong NapsLayoutSize,
    uint DataRegionBlockCount,
    ulong InnerImageSize,
    uint OuterFileCount,
    uint SparseAfidCount,
    uint EmptyFileCount,
    long OuterSuperblockOffset,
    ushort OuterPfsMode,
    string? Marker);

/// <summary>Bản đồ các vùng trong tệp PKG (ProsperoPackageArchive.Inspect).</summary>
public sealed record PackageMapInfo(
    long FihOffset,
    long FihSize,
    long OuterPfsOffset,
    long OuterPfsSize,
    long CntOffset,
    long CntSize,
    long SupplementOffset,
    long SupplementSize,
    int OuterSuperblockIndex);

/// <summary>Một mục trong bảng entry của container CNT.</summary>
public sealed record PackageCntEntry(
    string Id,
    uint RawId,
    string Name,
    uint DataOffset,
    uint DataSize,
    bool Encrypted,
    uint KeyIndex,
    uint Flags1,
    uint Flags2)
{
    /// <summary>Tên hiển thị: tên tệp nếu có, nếu không là tên định danh (Digests, EntryKeys…).</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;
}

/// <summary>Header và bảng entry của container CNT (nhúng trong ảnh FIH hoặc là cả tệp với gói Meta).</summary>
public sealed record PackageCntInfo(
    string ContentId,
    uint DrmType,
    uint ContentType,
    uint Flags,
    uint EntryCount,
    ushort ScEntryCount,
    ulong BodyOffset,
    ulong BodySize,
    IReadOnlyList<PackageCntEntry> Entries,
    bool? MetadataWrapValid);

/// <summary>Một cặp khoá/giá trị của param.json (giữ nguyên thứ tự trong tệp).</summary>
public sealed record ParamField(string Key, string Value);

/// <summary>param.json đã phân tích: mọi trường vô hướng theo thứ tự + các giá trị suy ra.</summary>
public sealed class PackageParams
{
    public IReadOnlyList<ParamField> Fields { get; init; } = Array.Empty<ParamField>();

    public string? ContentId { get; init; }

    public string? TitleId { get; init; }

    /// <summary>Tên hiển thị theo defaultLanguage (hoặc ngôn ngữ đầu tiên có titleName).</summary>
    public string? Title { get; init; }

    public string? DefaultLanguage { get; init; }

    public string? ContentVersion { get; init; }

    public string? MasterVersion { get; init; }

    public string? SdkVersion { get; init; }

    public int? SdkMajor { get; init; }

    public string? RequiredSystemSoftwareVersion { get; init; }

    public int? ApplicationCategoryType { get; init; }

    public string? ApplicationDrmType { get; init; }

    public int? ParentalLevel { get; init; }

    public string? ContentBadgeType { get; init; }

    /// <summary>Mô tả loại ứng dụng suy ra từ applicationCategoryType.</summary>
    public string CategoryLabel => PackageInspector.DescribeCategory(ApplicationCategoryType);
}

/// <summary>Toàn bộ thông tin đọc được từ một tệp PKG (chưa gồm danh sách tệp bên trong).</summary>
public sealed class PackageInfo
{
    public required string Path { get; init; }

    public long FileSize { get; init; }

    public PackageContainerKind Kind { get; init; }

    public PackageImageMode ImageMode { get; init; }

    public PackageFihInfo? Fih { get; init; }

    public PackageMapInfo? Map { get; init; }

    public PackageCntInfo? Cnt { get; init; }

    public PackageParams? Params { get; init; }

    /// <summary>Lỗi khi phân tích param.json (nếu có).</summary>
    public string? ParamJsonError { get; init; }

    public byte[]? ParamJsonBytes { get; init; }

    public byte[]? IconBytes { get; init; }

    /// <summary>Tên các tệp sce_sys có trong CNT (param.json, icon0.png, playgo-chunk.dat, nptitle.dat…).</summary>
    public IReadOnlyList<string> SceSysFiles { get; init; } = Array.Empty<string>();

    public string? ContentId => Cnt?.ContentId ?? Params?.ContentId;

    /// <summary>Gói DLC "chỉ quyền sở hữu" (content type 0x22): không có ảnh trong là đúng thiết kế, vẫn cài được.</summary>
    public bool IsDlcWithoutData => Cnt?.ContentType == 0x22;

    /// <summary>Gói DLC có dữ liệu (content type 0x21) — xuất được mẫu DLC (sce_sys + dự án GP5) để đóng gói lại.</summary>
    public bool IsDlcWithData => Cnt?.ContentType == 0x21;

    public string? Title => Params?.Title;

    /// <summary>Chỉ ảnh FIH debug mới giải nén được.</summary>
    public bool CanExtract => Kind == PackageContainerKind.FullDebug;

    public bool HasSupplement => Map is { SupplementSize: > 0 };

    public string KindLabel => Kind switch
    {
        PackageContainerKind.FullDebug => Loc.T("Container.FullDebug"),
        PackageContainerKind.FullRetail => Loc.T("Container.FullRetail"),
        PackageContainerKind.Meta => IsDlcWithoutData ? Loc.T("Container.DlcNoData") : Loc.T("Container.Meta"),
        _ => Loc.T("Extract.KindUnknown"),
    };

    public string ImageModeLabel => ImageMode switch
    {
        PackageImageMode.PlaintextNoAuth => Loc.T("Extract.ModePlain"),
        PackageImageMode.Native => Loc.T("Extract.ModeNative"),
        _ => "—",
    };

    /// <summary>Lý do không giải nén được (đã dịch), null nếu giải nén được.</summary>
    public string? ExtractBlockedReason => Kind switch
    {
        PackageContainerKind.FullDebug => null,
        PackageContainerKind.FullRetail => Loc.T("Extract.RetailBlocked"),
        PackageContainerKind.Meta => Loc.T("Extract.MetaBlocked"),
        _ => Loc.T("Extract.UnknownBlocked"),
    };
}

/// <summary>Loại mục trong ảnh PFS trong.</summary>
public enum PackageEntryKind
{
    File,
    Directory,
}

/// <summary>Một tệp hoặc thư mục trong ảnh PPR-PFS trong (đường dẫn tương đối, không có tiền tố uroot).</summary>
public sealed record PackageEntry(
    string Path,
    string Name,
    PackageEntryKind Kind,
    long Size,
    long CompressedSize,
    bool IsPfsCompressed,
    uint Inode,
    long Offset,
    string Flags)
{
    public bool IsDirectory => Kind == PackageEntryKind.Directory;

    /// <summary>Cách lưu trong ảnh trong: "PFSC" (nén khối PFS) hay "Kraken/NAPS" (nén ở lớp NAPS chung của ảnh).</summary>
    public string CompressionLabel => IsDirectory ? "—" : IsPfsCompressed ? "PFSC" : Loc.T("Extract.StoredNaps");
}

/// <summary>Ảnh chụp tiến độ giải nén.</summary>
public readonly record struct ExtractProgress(long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles, string? CurrentFile);

/// <summary>Kết quả một lần giải nén.</summary>
public sealed record ExtractResult(string OutputFolder, int FileCount, long TotalBytes, TimeSpan Elapsed, IReadOnlyList<string> Warnings);
