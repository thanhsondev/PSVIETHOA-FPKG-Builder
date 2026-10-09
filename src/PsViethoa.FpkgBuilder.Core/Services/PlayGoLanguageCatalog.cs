namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Danh sách 31 ngôn ngữ PlayGo của PS5 theo engine (<c>ProsperoPlayGoLanguages.Supported</c>), cho giao diện chọn ngôn ngữ.</summary>
public static class PlayGoLanguageCatalog
{
    /// <summary>(mã, tên tiếng Anh) theo thứ tự id.</summary>
    public static IReadOnlyList<(string Code, string Name)> Supported { get; } =
        LibProsperoPkg.PlayGo.ProsperoPlayGoLanguages.Supported.Select(language => (language.Code, language.EnglishName)).ToList();
}
