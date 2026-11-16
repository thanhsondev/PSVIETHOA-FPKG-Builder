using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.App.Xmas;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Cỡ nút / chữ thanh bên (cửa sổ Giao diện).</summary>
public enum SidebarSizeMode
{
    Small,
    Medium,
    Large,
    Auto,
}

/// <summary>Kiểu danh sách công cụ trên thanh bên (cửa sổ Giao diện).</summary>
public enum SidebarLayoutMode
{
    /// <summary>Hai cột khi thanh bên đủ rộng, một cột khi kéo hẹp.</summary>
    Auto,

    /// <summary>Luôn lưới hai cột (như PSVIETHOA STUDIO).</summary>
    Grid,

    /// <summary>Luôn một cột, ô gọn.</summary>
    Single,

    /// <summary>Danh sách chi tiết như 3.0: biểu tượng, tên, dòng phụ, số thứ tự.</summary>
    List,
}

/// <summary>
/// Tuỳ chỉnh giao diện (cửa sổ Giao diện ở chân thanh bên): theme, cỡ và kiểu thanh bên, khung Hệ thống, hiệu ứng chuyển động và từng
/// hiệu ứng Giáng sinh. Mọi thay đổi áp dụng ngay và được lưu ngay; MainWindow.Xmas theo dõi các thuộc tính Giáng sinh để bật / tắt lớp hình.
/// </summary>
public partial class MainViewModel
{
    private bool _loadingAppearance;

    [ObservableProperty] private SidebarSizeMode _sidebarSizeMode = SidebarSizeMode.Medium;
    [ObservableProperty] private SidebarLayoutMode _sidebarLayoutMode = SidebarLayoutMode.List;
    [ObservableProperty] private bool _showSystemPanel = true;
    [ObservableProperty] private bool _animationsEnabled = true;

    [ObservableProperty] private bool _xmasTheme = true;
    [ObservableProperty] private bool _xmasSnow = true;
    [ObservableProperty] private bool _xmasLights = true;
    [ObservableProperty] private bool _xmasSleigh = true;
    [ObservableProperty] private bool _xmasScenes = true;
    [ObservableProperty] private bool _xmasSnowCaps = true;
    [ObservableProperty] private bool _xmasMusicOn;
    [ObservableProperty] private double _xmasVolume = 0.35;

    /// <summary>Khung Hệ thống: chỉ Windows, thanh bên mở rộng và người dùng không tắt.</summary>
    public bool ShowSystemHost => OperatingSystem.IsWindows() && ShowSystemPanel && !IsSidebarCompact;

    /// <summary>Tuỳ chọn khung Hệ thống chỉ có nghĩa trên Windows.</summary>
    public bool SystemPanelSupported => OperatingSystem.IsWindows();

    /// <summary>Nhạc nền chỉ có trên Windows (Media Foundation) và macOS (afplay).</summary>
    public bool MusicSupported => XmasMusic.Supported;

    /// <summary>Đã kéo đổi bề rộng thanh bên (nút "Bề rộng tự động" trong cửa sổ Giao diện).</summary>
    public bool HasCustomSidebarWidth => SidebarUserWidth != null;

    // ───── RadioButton hai chiều (giống LanguageVi / LanguageEn) ─────

    public bool ThemeDark
    {
        get => IsDarkTheme;
        set
        {
            if (value) IsDarkTheme = true;
        }
    }

    public bool ThemeLight
    {
        get => !IsDarkTheme;
        set
        {
            if (value) IsDarkTheme = false;
        }
    }

    public bool SizeSmall { get => SidebarSizeMode == SidebarSizeMode.Small; set { if (value) SidebarSizeMode = SidebarSizeMode.Small; } }

    public bool SizeMedium { get => SidebarSizeMode == SidebarSizeMode.Medium; set { if (value) SidebarSizeMode = SidebarSizeMode.Medium; } }

    public bool SizeLarge { get => SidebarSizeMode == SidebarSizeMode.Large; set { if (value) SidebarSizeMode = SidebarSizeMode.Large; } }

    public bool SizeAuto { get => SidebarSizeMode == SidebarSizeMode.Auto; set { if (value) SidebarSizeMode = SidebarSizeMode.Auto; } }

    public bool LayoutAuto { get => SidebarLayoutMode == SidebarLayoutMode.Auto; set { if (value) SidebarLayoutMode = SidebarLayoutMode.Auto; } }

    public bool LayoutGrid { get => SidebarLayoutMode == SidebarLayoutMode.Grid; set { if (value) SidebarLayoutMode = SidebarLayoutMode.Grid; } }

    public bool LayoutSingle { get => SidebarLayoutMode == SidebarLayoutMode.Single; set { if (value) SidebarLayoutMode = SidebarLayoutMode.Single; } }

    public bool LayoutList { get => SidebarLayoutMode == SidebarLayoutMode.List; set { if (value) SidebarLayoutMode = SidebarLayoutMode.List; } }

    /// <summary>Đọc lựa chọn đã lưu (gọi từ RestorePage, sau khi nạp cài đặt).</summary>
    private void LoadAppearance()
    {
        _loadingAppearance = true;
        try
        {
            SidebarSizeMode = Enum.TryParse<SidebarSizeMode>(_settings.SidebarSize, true, out var size) && Enum.IsDefined(size) ? size : SidebarSizeMode.Medium;
            SidebarLayoutMode = Enum.TryParse<SidebarLayoutMode>(_settings.SidebarLayout, true, out var layout) && Enum.IsDefined(layout) ? layout : SidebarLayoutMode.List;
            ShowSystemPanel = _settings.ShowSystemPanel;
            AnimationsEnabled = _settings.Animations;
            XmasTheme = _settings.XmasTheme;
            XmasSnow = _settings.XmasSnow;
            XmasLights = _settings.XmasLights;
            XmasSleigh = _settings.XmasSleigh;
            XmasScenes = _settings.XmasScenes;
            XmasSnowCaps = _settings.XmasSnowCaps;
            XmasMusicOn = _settings.XmasMusic;
            XmasVolume = Math.Clamp(_settings.XmasVolume, 0, 1);
        }
        finally
        {
            _loadingAppearance = false;
        }

        UpdateSidebarScale();
        ApplyMotion();
    }

    /// <summary>Lưu ngay (trừ lúc đang nạp) để tắt app đột ngột vẫn giữ lựa chọn.</summary>
    private void SaveAppearance()
    {
        if (!_loadingAppearance)
        {
            SettingsService.Save(_settings);
        }
    }

    /// <summary>Chế độ chụp màn hình tự động luôn tắt chuyển động (ảnh không bắt khung hình dở dang).</summary>
    private void ApplyMotion() =>
        Motion.Suppressed = !AnimationsEnabled || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PSVIETHOA_SCREENSHOT"));

    partial void OnSidebarSizeModeChanged(SidebarSizeMode value)
    {
        _settings.SidebarSize = value.ToString();
        UpdateSidebarScale();
        OnPropertyChanged(nameof(SizeSmall));
        OnPropertyChanged(nameof(SizeMedium));
        OnPropertyChanged(nameof(SizeLarge));
        OnPropertyChanged(nameof(SizeAuto));
        SaveAppearance();
    }

    partial void OnSidebarLayoutModeChanged(SidebarLayoutMode value)
    {
        _settings.SidebarLayout = value.ToString();
        UpdateNavColumns();
        OnPropertyChanged(nameof(LayoutAuto));
        OnPropertyChanged(nameof(LayoutGrid));
        OnPropertyChanged(nameof(LayoutSingle));
        OnPropertyChanged(nameof(LayoutList));
        SaveAppearance();
    }

    partial void OnShowSystemPanelChanged(bool value)
    {
        _settings.ShowSystemPanel = value;
        OnPropertyChanged(nameof(ShowSystemHost));
        SaveAppearance();
    }

    partial void OnAnimationsEnabledChanged(bool value)
    {
        _settings.Animations = value;
        ApplyMotion();
        SaveAppearance();
    }

    partial void OnXmasThemeChanged(bool value)
    {
        _settings.XmasTheme = value;
        SaveAppearance();
    }

    partial void OnXmasSnowChanged(bool value)
    {
        _settings.XmasSnow = value;
        SaveAppearance();
    }

    partial void OnXmasLightsChanged(bool value)
    {
        _settings.XmasLights = value;
        SaveAppearance();
    }

    partial void OnXmasSleighChanged(bool value)
    {
        _settings.XmasSleigh = value;
        SaveAppearance();
    }

    partial void OnXmasScenesChanged(bool value)
    {
        _settings.XmasScenes = value;
        SaveAppearance();
    }

    partial void OnXmasSnowCapsChanged(bool value)
    {
        _settings.XmasSnowCaps = value;
        SaveAppearance();
    }

    partial void OnXmasMusicOnChanged(bool value)
    {
        _settings.XmasMusic = value;
        SaveAppearance();
    }

    /// <summary>Âm lượng: MainWindow.Xmas đổi âm lượng đang phát và lưu (gom các lần kéo thanh trượt).</summary>
    partial void OnXmasVolumeChanged(double value) => _settings.XmasVolume = Math.Round(Math.Clamp(value, 0, 1), 2);

    /// <summary>Nút "Bề rộng tự động" trong cửa sổ Giao diện (giống nháy đúp mép phải thanh bên).</summary>
    [RelayCommand]
    private void AutoSidebarWidth() => ResetSidebarWidth();

    /// <summary>Khôi phục mặc định: theme tối, thanh bên cỡ Vừa / kiểu Chi tiết (như 3.0) / bề rộng tự động, mọi hiệu ứng bật, nhạc nền tắt.</summary>
    [RelayCommand]
    private void ResetAppearance()
    {
        IsDarkTheme = true;
        SidebarSizeMode = SidebarSizeMode.Medium;
        SidebarLayoutMode = SidebarLayoutMode.List;
        ResetSidebarWidth();
        ShowSystemPanel = true;
        AnimationsEnabled = true;
        XmasTheme = true;
        XmasSnow = true;
        XmasLights = true;
        XmasSleigh = true;
        XmasScenes = true;
        XmasSnowCaps = true;
        XmasMusicOn = false;
        SettingsService.Save(_settings);
    }
}
