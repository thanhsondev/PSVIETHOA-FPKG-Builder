using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PsViethoa.FpkgBuilder.Core.Ftp;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Một dòng game trong cột "FPKG đã cài trên PS5": icon tải dần, tên, PPSA · phiên bản · dung lượng, nơi cài.</summary>
public sealed partial class RemoteGameRow : ObservableObject
{
    public RemoteGameRow(RemoteGame game)
    {
        Game = game;
    }

    public RemoteGame Game { get; }

    public string Title => Game.DisplayTitle;

    public string TitleId => Game.TitleId;

    /// <summary>"PPSA03355 · v01.003 · 8 GB".</summary>
    public string Meta => Game.TitleId + (Game.Version.Length > 0 ? "  ·  v" + Game.Version : string.Empty) + "  ·  " + Formatters.Size(Game.PkgSize);

    /// <summary>"ext1", "usb0" hoặc "Bộ nhớ trong".</summary>
    public string LocationText => Game.Location == RemoteGame.InternalLocation ? Loc.T("Remote.Internal") : Game.Location;

    public bool IsRetail => !Game.IsFake;

    public string Tip => Game.OriginalName.Length > 0 ? Game.PkgPath + "\n" + Loc.F("Remote.OriginalName", Game.OriginalName) : Game.PkgPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    public bool HasIcon => Icon != null;

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(LocationText));
        OnPropertyChanged(nameof(Tip));
    }
}

/// <summary>
/// Một tệp trong gói đang mở: ô tích, icon theo loại tệp, tên đậm + thư mục mờ bên dưới, cỡ màu vàng. Gói có thể có hơn 240 000 tệp
/// nên dòng chỉ giữ tham chiếu <see cref="PackageEntry"/>; chuỗi tính khi được vẽ.
/// </summary>
public sealed partial class RemoteFileRow : ObservableObject
{
    private static readonly (string IconKey, string Color)[] Kinds =
    [
        ("IconCpu", "#38BDF8"),           // 0: thực thi
        ("IconImage", "#F472B6"),         // 1: ảnh
        ("IconCodeBrackets", "#A3E635"),  // 2: văn bản / cấu hình
        ("IconPlay", "#C084FC"),          // 3: âm thanh / phim
        ("IconArchive", "#FBBF24"),       // 4: gói dữ liệu
        ("IconFile", "#94A3B8"),          // 5: khác
    ];

    private static readonly Geometry?[] Geometries = new Geometry?[Kinds.Length];
    private static readonly IBrush[] Brushes = Kinds.Select(k => (IBrush)new ImmutableSolidColorBrush(Color.Parse(k.Color))).ToArray();

    private readonly RemotePkgViewModel _owner;
    private bool _isChecked;

    public RemoteFileRow(PackageEntry entry, RemotePkgViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public PackageEntry Entry { get; }

    public string Path => Entry.Path;

    public string Name => System.IO.Path.GetFileName(Entry.Path.TrimEnd('/'));

    /// <summary>Thư mục chứa tệp, vd "/Content/Audio/FMOD/Build".</summary>
    public string Dir
    {
        get
        {
            var path = Entry.Path.TrimStart('/');
            var slash = path.LastIndexOf('/');
            return slash < 0 ? "/" : "/" + path[..slash];
        }
    }

    public string SizeText => Formatters.Size(Entry.Size);

    public Geometry? Icon
    {
        get
        {
            var kind = Kind;
            if (Geometries[kind] == null && Avalonia.Application.Current?.TryGetResource(Kinds[kind].IconKey, null, out var value) == true)
            {
                Geometries[kind] = value as Geometry;
            }

            return Geometries[kind];
        }
    }

    public IBrush IconBrush => Brushes[Kind];

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _owner.OnFileCheckedChanged();
            }
        }
    }

    /// <summary>Đặt ô tích mà không báo VM (tích / bỏ hàng loạt: VM tự đếm lại một lần).</summary>
    internal void SetCheckedQuiet(bool value)
    {
        if (_isChecked != value)
        {
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));
        }
    }

    private int Kind => System.IO.Path.GetExtension(Entry.Path).ToLowerInvariant() switch
    {
        ".bin" or ".elf" or ".self" or ".prx" or ".sprx" or ".dll" or ".exe" => 0,
        ".png" or ".jpg" or ".jpeg" or ".dds" or ".gnf" or ".tga" or ".bmp" => 1,
        ".json" or ".xml" or ".txt" or ".ini" or ".cfg" or ".csv" or ".lua" => 2,
        ".bnk" or ".wem" or ".at9" or ".wav" or ".ogg" or ".mp3" or ".fsb" or ".xma" or ".mp4" or ".bk2" or ".usm" or ".webm" or ".bik" => 3,
        ".pak" or ".forge" or ".psarc" or ".arc" or ".pck" or ".big" or ".dat" or ".rpf" or ".bundle" or ".cpk" or ".ucas" or ".utoc" or ".pac" => 4,
        _ => 5,
    };
}
