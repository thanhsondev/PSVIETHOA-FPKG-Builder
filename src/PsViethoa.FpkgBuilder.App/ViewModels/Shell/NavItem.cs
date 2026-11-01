using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.App.ViewModels.Shell;

/// <summary>Các trang của ứng dụng, theo thứ tự trên thanh bên.</summary>
public enum AppPage
{
    Ps5Build,
    Ps5Update,
    Ps5Extract,
    Ps5Queue,
    Ps4Extract,
    Ps4Base,
    Ps4Patch,
    Ps4Dlc,
    Ps4Merge,
    Ftp,
    SaveResign,
}

/// <summary>Hệ máy / nhóm chức năng: quyết định huy hiệu, màu nhấn và nhãn nhóm trên thanh bên.</summary>
public enum Platform
{
    Ps5,
    Ps4,
    Ftp,
    Save,
}

/// <summary>Một mục trên thanh bên. Tiêu đề/mô tả là khoá bản dịch để đổi ngôn ngữ ngay khi chạy.</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(AppPage page, Platform platform, int index, string titleKey, string subtitleKey, string tipKey, string iconKey)
    {
        Page = page;
        Platform = platform;
        Index = index.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        TitleKey = titleKey;
        SubtitleKey = subtitleKey;
        TipKey = tipKey;
        IconKey = iconKey;
    }

    public AppPage Page { get; }

    public Platform Platform { get; }

    /// <summary>Số thứ tự hiện ở bên phải (01, 02…).</summary>
    public string Index { get; }

    public string TitleKey { get; }

    public string SubtitleKey { get; }

    public string TipKey { get; }

    /// <summary>Khoá hình trong Styles/Icons.axaml.</summary>
    public string IconKey { get; }

    public string Title => Loc.T(TitleKey);

    public string Subtitle => Loc.T(SubtitleKey);

    public string Tip => Loc.T(TipKey);

    public Geometry? Icon =>
        Avalonia.Application.Current?.TryGetResource(IconKey, null, out var value) == true ? value as Geometry : null;

    /// <summary>Màu nhấn của hệ máy (thanh sáng, số thứ tự khi được chọn).</summary>
    public IBrush AccentBrush => ShellBrushes.Accent(Platform);

    /// <summary>Nền mục đang chọn: dải màu nhấn nhạt dần sang phải.</summary>
    public IBrush SelectedBrush => ShellBrushes.Selected(Platform);

    /// <summary>Viền mục đang chọn.</summary>
    public IBrush SelectedBorderBrush => ShellBrushes.SelectedBorder(Platform);

    /// <summary>Ô biểu tượng khi được chọn: dải màu của huy hiệu hệ máy.</summary>
    public IBrush TileBrush => ShellBrushes.Tile(Platform);

    /// <summary>Màu biểu tượng trên ô đã tô.</summary>
    public IBrush TileInkBrush => ShellBrushes.TileInk(Platform);

    /// <summary>Quầng sáng của thanh bên trái.</summary>
    public BoxShadows RailGlow => ShellBrushes.Glow(Platform);

    [ObservableProperty] private bool _isSelected;

    /// <summary>Trang này đang chạy việc nền (tạo gói, truyền tệp…): chấm nhấp nháy bên cạnh tiêu đề.</summary>
    [ObservableProperty] private bool _isBusy;

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Tip));
    }
}

/// <summary>Bút vẽ theo hệ máy cho thanh bên và đầu trang (dùng chung bảng màu với <see cref="Controls.PlatformBadge"/>).</summary>
public static class ShellBrushes
{
    private static readonly Dictionary<(Platform, string), object> Cache = new();

    private static T Get<T>(Platform platform, string kind, Func<T> create)
        where T : class
    {
        if (!Cache.TryGetValue((platform, kind), out var value))
        {
            value = create();
            Cache[(platform, kind)] = value;
        }

        return (T)value;
    }

    public static IBrush Accent(Platform platform) =>
        Get<IBrush>(platform, "accent", () => new SolidColorBrush(Controls.PlatformBadge.Palette(platform).Accent).ToImmutable());

    public static IBrush Selected(Platform platform) => Get<IBrush>(platform, "selected", () =>
    {
        var accent = Controls.PlatformBadge.Palette(platform).Accent;
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 0, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(58, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(18, accent.R, accent.G, accent.B), 0.65),
                new GradientStop(Color.FromArgb(6, accent.R, accent.G, accent.B), 1),
            },
        }.ToImmutable();
    });

    /// <summary>Màu nhấn đậm hơn cho chữ trên nền sáng (theme sáng).</summary>
    public static IBrush AccentStrong(Platform platform) => Get<IBrush>(platform, "strong", () => new SolidColorBrush(Color.Parse(platform switch
    {
        Platform.Ps5 => "#2563EB",
        Platform.Ps4 => "#4F46E5",
        Platform.Ftp => "#0F766E",
        Platform.Save => "#B45309",
        _ => "#475569",
    })).ToImmutable());

    public static IBrush SelectedBorder(Platform platform) => Get<IBrush>(platform, "border", () =>
    {
        var accent = Controls.PlatformBadge.Palette(platform).Accent;
        return new SolidColorBrush(Color.FromArgb(56, accent.R, accent.G, accent.B)).ToImmutable();
    });

    public static IBrush Tile(Platform platform) => Get<IBrush>(platform, "tile", () =>
    {
        var (top, bottom, _, _) = Controls.PlatformBadge.Palette(platform);
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(0.4, 1, Avalonia.RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        }.ToImmutable();
    });

    public static IBrush TileInk(Platform platform) =>
        Get<IBrush>(platform, "ink", () => new SolidColorBrush(Controls.PlatformBadge.Palette(platform).Ink).ToImmutable());

    public static BoxShadows Glow(Platform platform)
    {
        var accent = Controls.PlatformBadge.Palette(platform).Accent;
        return new BoxShadows(new BoxShadow { Blur = 10, Spread = 0, Color = Color.FromArgb(220, accent.R, accent.G, accent.B) });
    }
}

/// <summary>Một nhóm trên thanh bên: huy hiệu hệ máy, tên nhóm và các mục.</summary>
public sealed partial class NavGroup : ObservableObject
{
    public NavGroup(Platform platform, string titleKey, IReadOnlyList<NavItem> items)
    {
        Platform = platform;
        TitleKey = titleKey;
        Items = items;
    }

    public Platform Platform { get; }

    public string TitleKey { get; }

    public string Title => Loc.T(TitleKey);

    public IBrush AccentBrush => ShellBrushes.Accent(Platform);

    public IReadOnlyList<NavItem> Items { get; }

    /// <summary>Một trang trong nhóm đang chạy việc nền: huy hiệu nhóm phát sáng nhịp chậm.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Trang đang mở thuộc nhóm này.</summary>
    [ObservableProperty] private bool _isActive;

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Title));
        foreach (var item in Items)
        {
            item.RefreshTexts();
        }
    }
}
