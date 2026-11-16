using System.Reflection;

namespace PsViethoa.FpkgBuilder.App;

/// <summary>Thông tin tĩnh của ứng dụng.</summary>
public static class AppInfo
{
    public const string Name = "PSVIETHOA FPKG Builder";

    public const string DataFolderName = "PSVIETHOA FPKG Builder";

    public const string Team = "PSVIETHOA";

    public const string Authors = "Nguyễn Thanh Sơn & Ngô Phi Phương";

    public const string MainProjectAuthor = "Drakmor";

    public const string AuthorUrl = "https://github.com/SvenGDK";

    /// <summary>Trang ủng hộ nhóm (nút ở chân thanh bên — chỉ có ở bản GitHub): giao diện tiếng Việt mở psviethoa.com, tiếng Anh mở Ko-fi.</summary>
    public const string DonateUrlVi = "https://psviethoa.com/donate?tab=donate";

    public const string KofiUrl = "https://ko-fi.com/psviethoacom";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "2.0.0";

    public static string PlatformLabel =>
        OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Khác";
}
