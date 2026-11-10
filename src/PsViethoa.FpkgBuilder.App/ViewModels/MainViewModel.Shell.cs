using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.App.ViewModels.Shell;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>
/// Khung ứng dụng 3.0.0: thanh bên chia nhóm theo hệ máy, trang đang mở và đầu trang. Các trang PS5 FPKG vẫn là bốn chế độ cũ
/// (Tạo gói, Tạo gói Update, Giải nén gói, Hàng chờ) — chọn mục PS5 chỉ bật đúng chế độ đó, cơ chế tạo gói giữ nguyên.
/// </summary>
public partial class MainViewModel
{
    private bool _syncingPage;
    private AppPage? _buildPage;

    /// <summary>Các nhóm trên thanh bên, theo thứ tự PS5 FPKG → PS4 PKG → Kết nối PS5 → Save game.</summary>
    public IReadOnlyList<NavGroup> NavGroups { get; private set; } = Array.Empty<NavGroup>();

    /// <summary>Trang PS4 PKG: xả, đóng base / update / DLC, gộp base + update.</summary>
    public Ps4ViewModel Ps4 { get; private set; } = null!;

    /// <summary>Trang ZSTD PS5 (beta): đóng / giải / vá gói ampr_zstd.</summary>
    public ZstdViewModel Zstd { get; private set; } = null!;

    /// <summary>Trang FTP PS5.</summary>
    public FtpViewModel Ftp { get; private set; } = null!;

    /// <summary>Trang Resign save PS4 / PS5.</summary>
    public SaveViewModel Save { get; private set; } = null!;

    [ObservableProperty] private AppPage _currentPage = AppPage.Ps5Build;

    /// <summary>Người dùng thu gọn thanh bên (chỉ còn biểu tượng).</summary>
    [ObservableProperty] private bool _sidebarCollapsed;

    /// <summary>Cửa sổ hẹp: thanh bên tự thu gọn để trang tạo gói vẫn đủ chỗ.</summary>
    [ObservableProperty] private bool _sidebarNarrow;

    public bool IsSidebarCompact => SidebarCollapsed || SidebarNarrow;

    public double SidebarWidth => IsSidebarCompact ? 76 : 272;

    public bool IsPs5Page => CurrentPage is AppPage.Ps5Build or AppPage.Ps5Update or AppPage.Ps5Extract or AppPage.Ps5Queue;

    public bool IsPs4Page => CurrentPage is AppPage.Ps4Extract or AppPage.Ps4Base or AppPage.Ps4Patch or AppPage.Ps4Dlc or AppPage.Ps4Merge;

    public bool IsFtpPage => CurrentPage == AppPage.Ftp;

    public bool IsZstdPage => CurrentPage is AppPage.ZstdPack or AppPage.ZstdUnpack or AppPage.ZstdPatch;

    /// <summary>Vá ZSTD (thêm bản Việt hoá vào game đã đóng) chỉ có trong giao diện tiếng Việt.</summary>
    private static bool ZstdPatchAllowed => Loc.Current.Language == Loc.Vietnamese;

    /// <summary>Màn hình Tạo gói / Tạo gói Update của PS5 đang hiện (chế độ cũ và đang ở nhóm PS5).</summary>
    public bool ShowPs5Build => IsPs5Page && ShowBuildView;

    public bool ShowPs5Extract => IsPs5Page && IsExtractMode;

    public bool ShowPs5Queue => IsPs5Page && IsQueueMode;

    public bool IsSavePage => CurrentPage == AppPage.SaveResign;

    public NavItem? CurrentNav => NavGroups.SelectMany(group => group.Items).FirstOrDefault(item => item.Page == CurrentPage);

    public NavGroup? CurrentGroup => NavGroups.FirstOrDefault(group => group.Items.Any(item => item.Page == CurrentPage));

    public Platform PagePlatform => CurrentNav?.Platform ?? Platform.Ps5;

    /// <summary>Dòng nhỏ trên tiêu đề trang: "PS5 FPKG · 01".</summary>
    public string PageEyebrow => CurrentGroup is { } group && CurrentNav is { } item ? group.Title + "  ·  " + item.Index : string.Empty;

    public string PageTitle => CurrentNav?.Title ?? string.Empty;

    public string PageDescription => Loc.T("Page." + CurrentPage + ".Description");

    public IBrush PageAccentBrush => IsDarkTheme ? ShellBrushes.Accent(PagePlatform) : ShellBrushes.AccentStrong(PagePlatform);

    /// <summary>Chân trang: phiên bản và nền tảng.</summary>
    public string SidebarVersionText => Loc.F("Shell.Version", AppInfo.Version);

    private void InitShell()
    {
        Ps4 = new Ps4ViewModel(_settings, _dialogs);
        Ftp = new FtpViewModel(_settings, _dialogs);
        Zstd = new ZstdViewModel(_settings, _dialogs);
        Save = new SaveViewModel(_settings, _dialogs);

        NavGroups =
        [
            new NavGroup(Platform.Ps5, "Nav.Group.Ps5",
            [
                new NavItem(AppPage.Ps5Build, Platform.Ps5, 1, "Nav.Ps5Build", "Nav.Ps5Build.Sub", "Nav.Ps5Build.Tip", "IconPackage"),
                new NavItem(AppPage.Ps5Update, Platform.Ps5, 2, "Nav.Ps5Update", "Nav.Ps5Update.Sub", "Nav.Ps5Update.Tip", "IconRefresh"),
                new NavItem(AppPage.Ps5Extract, Platform.Ps5, 3, "Nav.Ps5Extract", "Nav.Ps5Extract.Sub", "Nav.Ps5Extract.Tip", "IconPackageDown"),
                new NavItem(AppPage.Ps5Queue, Platform.Ps5, 4, "Nav.Ps5Queue", "Nav.Ps5Queue.Sub", "Nav.Ps5Queue.Tip", "IconFileTree"),
            ]),
            new NavGroup(Platform.Ps5, "Nav.Group.Zstd",
            [
                new NavItem(AppPage.ZstdPack, Platform.Ps5, 5, "Nav.ZstdPack", "Nav.ZstdPack.Sub", "Nav.ZstdPack.Tip", "IconZip"),
                new NavItem(AppPage.ZstdUnpack, Platform.Ps5, 6, "Nav.ZstdUnpack", "Nav.ZstdUnpack.Sub", "Nav.ZstdUnpack.Tip", "IconFolderDownload"),
                new NavItem(AppPage.ZstdPatch, Platform.Ps5, 7, "Nav.ZstdPatch", "Nav.ZstdPatch.Sub", "Nav.ZstdPatch.Tip", "IconPencil"),
            ]),
            new NavGroup(Platform.Ps4, "Nav.Group.Ps4",
            [
                new NavItem(AppPage.Ps4Extract, Platform.Ps4, 8, "Nav.Ps4Extract", "Nav.Ps4Extract.Sub", "Nav.Ps4Extract.Tip", "IconPackageDown"),
                new NavItem(AppPage.Ps4Base, Platform.Ps4, 9, "Nav.Ps4Base", "Nav.Ps4Base.Sub", "Nav.Ps4Base.Tip", "IconPackage"),
                new NavItem(AppPage.Ps4Patch, Platform.Ps4, 10, "Nav.Ps4Patch", "Nav.Ps4Patch.Sub", "Nav.Ps4Patch.Tip", "IconRefresh"),
                new NavItem(AppPage.Ps4Dlc, Platform.Ps4, 11, "Nav.Ps4Dlc", "Nav.Ps4Dlc.Sub", "Nav.Ps4Dlc.Tip", "IconPuzzle"),
                new NavItem(AppPage.Ps4Merge, Platform.Ps4, 12, "Nav.Ps4Merge", "Nav.Ps4Merge.Sub", "Nav.Ps4Merge.Tip", "IconMerge"),
            ]),
            new NavGroup(Platform.Ftp, "Nav.Group.Ftp",
            [
                new NavItem(AppPage.Ftp, Platform.Ftp, 13, "Nav.Ftp", "Nav.Ftp.Sub", "Nav.Ftp.Tip", "IconLan"),
            ]),
            new NavGroup(Platform.Save, "Nav.Group.Save",
            [
                new NavItem(AppPage.SaveResign, Platform.Save, 14, "Nav.SaveResign", "Nav.SaveResign.Sub", "Nav.SaveResign.Tip", "IconSave"),
            ]),
        ];

        foreach (var item in NavGroups.SelectMany(group => group.Items))
        {
            item.PropertyChanged += OnNavItemChanged;
        }

        ApplyZstdPatchVisibility();

        PropertyChanged += OnShellSourceChanged;
        Extraction.PropertyChanged += OnShellSourceChanged;
        Queue.PropertyChanged += OnShellSourceChanged;
        Ps4.PropertyChanged += OnShellSourceChanged;
        Ftp.PropertyChanged += OnShellSourceChanged;
        Zstd.PropertyChanged += OnShellSourceChanged;
        Save.PropertyChanged += OnShellSourceChanged;
        ApplyPageSelection();
    }

    /// <summary>Mục Vá ZSTD chỉ hiện khi giao diện tiếng Việt; đổi sang tiếng Anh khi đang ở đó thì về Đóng ZSTD.</summary>
    private void ApplyZstdPatchVisibility()
    {
        var allowed = ZstdPatchAllowed;
        foreach (var item in NavGroups.SelectMany(group => group.Items).Where(item => item.Page == AppPage.ZstdPatch))
        {
            item.IsShown = allowed;
        }

        if (!allowed && CurrentPage == AppPage.ZstdPatch && !Zstd.IsBusy)
        {
            CurrentPage = AppPage.ZstdPack;
        }
    }

    /// <summary>Đặt trang đã mở lần trước (cài đặt), sau khi các chế độ PS5 đã nạp xong.</summary>
    private void RestorePage()
    {
        if (Enum.TryParse<AppPage>(_settings.LastPage, out var page) && Enum.IsDefined(page))
        {
            CurrentPage = page == AppPage.ZstdPatch && !ZstdPatchAllowed ? AppPage.ZstdPack : page;
        }

        SidebarCollapsed = _settings.SidebarCollapsed;
    }

    [RelayCommand]
    private void Navigate(AppPage page) => CurrentPage = page;

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    partial void OnSidebarCollapsedChanged(bool value)
    {
        _settings.SidebarCollapsed = value;
        OnPropertyChanged(nameof(IsSidebarCompact));
        OnPropertyChanged(nameof(SidebarWidth));
    }

    partial void OnSidebarNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarCompact));
        OnPropertyChanged(nameof(SidebarWidth));
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        DebugLog.Write("Page: " + value + (Environment.GetEnvironmentVariable("PSVIETHOA_TRACE_PAGE") == "1" ? Environment.NewLine + Environment.StackTrace : string.Empty));
        _settings.LastPage = value.ToString();
        if (!_syncingPage)
        {
            _syncingPage = true;
            try
            {
                switch (value)
                {
                    case AppPage.Ps5Build:
                        IsBuildMode = true;
                        break;
                    case AppPage.Ps5Update:
                        IsUpdateMode = true;
                        break;
                    case AppPage.Ps5Extract:
                        IsExtractMode = true;
                        break;
                    case AppPage.Ps5Queue:
                        IsQueueMode = true;
                        break;
                    case AppPage.Ps4Extract:
                        Ps4.Mode = Ps4Mode.Extract;
                        break;
                    case AppPage.Ps4Base:
                        Ps4.Mode = Ps4Mode.Base;
                        break;
                    case AppPage.Ps4Patch:
                        Ps4.Mode = Ps4Mode.Patch;
                        break;
                    case AppPage.Ps4Dlc:
                        Ps4.Mode = Ps4Mode.Dlc;
                        break;
                    case AppPage.Ps4Merge:
                        Ps4.Mode = Ps4Mode.Merge;
                        break;
                    case AppPage.Ftp:
                        Ftp.EnsureLoaded();
                        break;
                    case AppPage.ZstdPatch when !ZstdPatchAllowed:
                        // Vá ZSTD chỉ có trong giao diện tiếng Việt (thanh bên đã ẩn mục; đây chặn đường vào khác: cài đặt, móc kiểm thử).
                        if (!Zstd.IsBusy)
                        {
                            Zstd.Mode = ZstdMode.Pack;
                        }

                        CurrentPage = AppPage.ZstdPack;
                        return;
                    case AppPage.ZstdPack or AppPage.ZstdUnpack or AppPage.ZstdPatch:
                        var mode = value switch { AppPage.ZstdUnpack => ZstdMode.Unpack, AppPage.ZstdPatch => ZstdMode.Patch, _ => ZstdMode.Pack };
                        if (Zstd.IsBusy && Zstd.Mode != mode)
                        {
                            // Đang chạy một việc ZSTD: giữ trang của việc đó (một trang dùng chung bộ tiến độ / nhật ký).
                            CurrentPage = Zstd.Mode switch { ZstdMode.Unpack => AppPage.ZstdUnpack, ZstdMode.Patch => AppPage.ZstdPatch, _ => AppPage.ZstdPack };
                            return;
                        }

                        Zstd.Mode = mode;
                        break;
                }
            }
            finally
            {
                _syncingPage = false;
            }
        }

        ApplyPageSelection();
        BuildCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FooterStatusText));
    }

    /// <summary>Gọi khi một chế độ PS5 được bật từ nơi khác (cài đặt, phím tắt, móc kiểm thử): mở trang PS5 tương ứng.</summary>
    private void SyncPageFromPs5Mode()
    {
        RaisePs5Visibility();
        if (_syncingPage || NavGroups.Count == 0)
        {
            return;
        }

        var page = IsUpdateMode ? AppPage.Ps5Update : IsExtractMode ? AppPage.Ps5Extract : IsQueueMode ? AppPage.Ps5Queue : AppPage.Ps5Build;
        _syncingPage = true;
        try
        {
            CurrentPage = page;
        }
        finally
        {
            _syncingPage = false;
        }
    }

    private void ApplyPageSelection()
    {
        foreach (var group in NavGroups)
        {
            var active = false;
            foreach (var item in group.Items)
            {
                var selected = item.Page == CurrentPage;
                item.IsSelected = selected;
                active |= selected;
            }

            group.IsActive = active;
        }

        OnPropertyChanged(nameof(IsPs5Page));
        OnPropertyChanged(nameof(IsPs4Page));
        OnPropertyChanged(nameof(IsFtpPage));
        OnPropertyChanged(nameof(IsZstdPage));
        OnPropertyChanged(nameof(IsSavePage));
        RaisePs5Visibility();
        RefreshPageHeader();
    }

    private void RaisePs5Visibility()
    {
        OnPropertyChanged(nameof(ShowPs5Build));
        OnPropertyChanged(nameof(ShowPs5Extract));
        OnPropertyChanged(nameof(ShowPs5Queue));
    }

    private void RefreshPageHeader()
    {
        OnPropertyChanged(nameof(CurrentNav));
        OnPropertyChanged(nameof(CurrentGroup));
        OnPropertyChanged(nameof(PagePlatform));
        OnPropertyChanged(nameof(PageEyebrow));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(PageAccentBrush));
    }

    /// <summary>Bấm một mục (RadioButton) → mở trang đó; các mục khác được bỏ chọn qua <see cref="ApplyPageSelection"/>.</summary>
    private void OnNavItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavItem.IsSelected) && sender is NavItem { IsSelected: true } item && item.Page != CurrentPage)
        {
            CurrentPage = item.Page;
        }
    }

    private void OnShellSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == this && e.PropertyName == nameof(IsDarkTheme))
        {
            OnPropertyChanged(nameof(PageAccentBrush));
        }

        if (sender is Ps4ViewModel or FtpViewModel or SaveViewModel or ZstdViewModel && e.PropertyName == "StatusText")
        {
            OnPropertyChanged(nameof(FooterStatusText));
        }

        if (sender is ZstdViewModel && e.PropertyName == nameof(ZstdViewModel.IsBusy))
        {
            UpdateBusyBadges();
        }

        if (e.PropertyName is nameof(IsBuilding) or nameof(ExtractionViewModel.IsBusy) or nameof(QueueViewModel.IsBusy)
            or nameof(ExtractionViewModel.IsLoading) or nameof(ExtractionViewModel.IsExtracting) or nameof(ExtractionViewModel.IsHashing)
            or nameof(ExtractionViewModel.IsVerifying) or nameof(QueueViewModel.IsRunning))
        {
            UpdateBusyBadges();
        }
    }

    /// <summary>Gọi từ OnIsBuildingChanged: nhớ trang nào đã bắt đầu lượt tạo gói để chỉ mục đó nhấp nháy.</summary>
    private void TrackBuildPage(bool building)
    {
        _buildPage = building ? (IsUpdateMode ? AppPage.Ps5Update : AppPage.Ps5Build) : null;
        UpdateBusyBadges();
    }

    /// <summary>Trang và nhóm nào đang chạy việc nền: chấm nhấp nháy ở mục, quầng sáng nhịp chậm ở huy hiệu nhóm.</summary>
    private void UpdateBusyBadges()
    {
        foreach (var group in NavGroups)
        {
            var busy = false;
            foreach (var item in group.Items)
            {
                item.IsBusy = item.Page switch
                {
                    AppPage.Ps5Build or AppPage.Ps5Update => IsBuilding && _buildPage == item.Page,
                    AppPage.Ps5Extract => Extraction.IsBusy,
                    AppPage.Ps5Queue => Queue.IsBusy,
                    AppPage.Ps4Extract or AppPage.Ps4Base or AppPage.Ps4Patch or AppPage.Ps4Dlc or AppPage.Ps4Merge => Ps4.IsBusy && Ps4.BusyMode == Ps4ModeOf(item.Page),
                    AppPage.Ftp => Ftp.IsBusy,
                    AppPage.ZstdPack => Zstd.IsBusy && Zstd.Mode == ZstdMode.Pack,
                    AppPage.ZstdUnpack => Zstd.IsBusy && Zstd.Mode == ZstdMode.Unpack,
                    AppPage.ZstdPatch => Zstd.IsBusy && Zstd.Mode == ZstdMode.Patch,
                    AppPage.SaveResign => Save.IsBusy,
                    _ => false,
                };
                busy |= item.IsBusy;
            }

            group.IsBusy = busy;
        }
    }

    private static Ps4Mode? Ps4ModeOf(AppPage page) => page switch
    {
        AppPage.Ps4Extract => Ps4Mode.Extract,
        AppPage.Ps4Base => Ps4Mode.Base,
        AppPage.Ps4Patch => Ps4Mode.Patch,
        AppPage.Ps4Dlc => Ps4Mode.Dlc,
        AppPage.Ps4Merge => Ps4Mode.Merge,
        _ => null,
    };

    private void RefreshShellTexts()
    {
        ApplyZstdPatchVisibility();
        foreach (var group in NavGroups)
        {
            group.RefreshTexts();
        }

        RefreshPageHeader();
        OnPropertyChanged(nameof(SidebarVersionText));
    }
}
