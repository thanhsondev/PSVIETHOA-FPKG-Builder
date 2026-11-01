using Avalonia.Data.Converters;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.Converters;

/// <summary>Bộ chuyển đổi dùng trong XAML.</summary>
public static class AppConverters
{
    public static readonly IValueConverter Size = new FuncValueConverter<long, string>(Formatters.Size);

    public static readonly IValueConverter Percent = new FuncValueConverter<double, string>(value => $"{value:0}%");

    public static readonly IValueConverter Clock = new FuncValueConverter<TimeSpan, string>(Formatters.Clock);

    public static readonly IValueConverter NotEmpty = new FuncValueConverter<string?, bool>(value => !string.IsNullOrWhiteSpace(value));

    public static readonly IValueConverter Empty = new FuncValueConverter<string?, bool>(string.IsNullOrWhiteSpace);

    /// <summary>Bề rộng logo đầu thanh bên: nhỏ lại khi thanh bên thu gọn.</summary>
    public static readonly IValueConverter LogoWidth = new FuncValueConverter<bool, double>(compact => compact ? 46 : 84);

    /// <summary>Số đếm bằng 0 → true (hiện trạng thái trống của danh sách).</summary>
    /// <summary>Ô bị khoá (do ứng dụng quản lý) hiện mờ hơn.</summary>
    public static readonly IValueConverter LockedOpacity = new FuncValueConverter<bool, double>(locked => locked ? 0.6 : 1);

    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(value => value == 0);

    public static readonly IValueConverter FolderName = new FuncValueConverter<string?, string>(value =>
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(value);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    });
}
