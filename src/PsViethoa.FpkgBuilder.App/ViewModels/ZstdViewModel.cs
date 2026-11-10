using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using PsViethoa.FpkgBuilder.Core.Zstd;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Ba việc của trang ZSTD PS5.</summary>
public enum ZstdMode
{
    Pack,
    Unpack,
    Patch,
}

/// <summary>
/// Trang ZSTD PS5 (beta): đóng / giải / vá gói <c>ampr_zstd</c> theo định dạng ps5-dump-forge, runtime ampr-emu-ng trên máy.
/// Lõi là <see cref="AmprZstd"/> (từ PSVIETHOA STUDIO 2.1.0). Vá = thêm hoặc thay tệp (bản Việt hoá) mà không giải cả game, có hoàn tác.
/// </summary>
public sealed partial class ZstdViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _pendingLog = new();
    private readonly DispatcherTimer _tick;
    private readonly Stopwatch _elapsed = new();
    private CancellationTokenSource? _cancellation;
    private AmprZstd.Progress? _pendingProgress;
    private AmprZstd.PatchPlan? _plan;
    private int _inspection;

    public ZstdViewModel(AppSettings settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Tick());
        _sourcePath = settings.ZstdSourcePath ?? string.Empty;
        _outputFolder = settings.ZstdOutputFolder ?? string.Empty;
        _level = settings.ZstdLevel is >= 1 and <= 22 ? settings.ZstdLevel : AmprZstd.DefaultLevel;
        ThreadChoices = BuildThreadChoices();
        _threadIndex = 0;
        if (settings.ZstdMode is { } saved && Enum.TryParse<ZstdMode>(saved, out var savedMode) && (savedMode != ZstdMode.Patch || ShowPatchTab))
        {
            _mode = savedMode;
        }
        Loc.Current.LanguageChanged += (_, _) => RefreshTexts();
        if (_sourcePath.Length > 0)
        {
            _ = InspectAsync();
        }
    }

    // ───────────────────────────── chế độ ─────────────────────────────

    [ObservableProperty] private ZstdMode _mode = ZstdMode.Pack;

    public bool IsPack => Mode == ZstdMode.Pack;

    public bool IsUnpack => Mode == ZstdMode.Unpack;

    public bool IsPatch => Mode == ZstdMode.Patch;

    public bool ShowOutput => !IsPatch;

    /// <summary>Thẻ Vá ZSTD (thêm bản Việt hoá vào game đã đóng) chỉ hiện khi giao diện tiếng Việt.</summary>
    public bool ShowPatchTab => Loc.Current.Language == Loc.Vietnamese;

    public string ModeHint => Loc.T("Zstd.Ui.Hint." + Mode);

    public string StartText => Loc.T("Zstd.Ui.Start." + Mode);

    public string SourceLabel => Loc.T(IsPack ? "Zstd.Ui.Source.Game" : "Zstd.Ui.Source.Packed");

    [RelayCommand]
    private void SetMode(ZstdMode mode)
    {
        if (!IsBusy && (mode != ZstdMode.Patch || ShowPatchTab))
        {
            Mode = mode;
        }
    }

    partial void OnModeChanged(ZstdMode value)
    {
        _settings.ZstdMode = value.ToString();
        if (!IsBusy)
        {
            Succeeded = Failed = false;
            Percent = 0;
            PhaseText = DetailText = string.Empty;
            SpeedText = RemainingText = ProcessedText = RatioText = "—";
        }

        OnPropertyChanged(nameof(IsPack));
        OnPropertyChanged(nameof(IsUnpack));
        OnPropertyChanged(nameof(IsPatch));
        OnPropertyChanged(nameof(ShowOutput));
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(HeaderChip));
        OnPropertyChanged(nameof(DropTitle));
        OnPropertyChanged(nameof(DropSubtitle));
        OnPropertyChanged(nameof(PreviewHeading));
        OnPropertyChanged(nameof(RatioLabel));
        OnPropertyChanged(nameof(AdvancedHeader));
        OnPropertyChanged(nameof(OutputLabel));
        OnPropertyChanged(nameof(StatusLine));
        _plan = null;
        PlanText = string.Empty;
        SuggestOutput();
        RefreshInfoTexts();
        StartCommand.NotifyCanExecuteChanged();
    }

    // ───────────────────────────── thiết lập ─────────────────────────────

    [ObservableProperty] private string _sourcePath;
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private string _patchFolder = string.Empty;
    [ObservableProperty] private int _level;
    [ObservableProperty] private bool _verify;
    [ObservableProperty] private bool _keepRuntime;
    [ObservableProperty] private int _threadIndex;

    public IReadOnlyList<string> ThreadChoices { get; private set; }

    private static readonly int[] ThreadSteps = [1, 2, 4, 6, 8, 12, 16, 24, 32];

    private int Threads => ThreadIndex <= 0 ? 0 : ThreadSteps.Where(Fits).ElementAtOrDefault(ThreadIndex - 1);

    private static bool Fits(int t) => t <= Math.Max(Environment.ProcessorCount, 1) * 2 && t <= AmprZstd.MaxThreads;

    private static IReadOnlyList<string> BuildThreadChoices() =>
        [Loc.F("Zstd.Ui.ThreadsAuto", Math.Min(Environment.ProcessorCount, AmprZstd.MaxThreads)), .. ThreadSteps.Where(Fits).Select(t => Loc.F("Zstd.Ui.ThreadsN", t))];

    partial void OnSourcePathChanged(string value)
    {
        _settings.ZstdSourcePath = value;
        _plan = null;
        PlanText = string.Empty;
        _ = InspectAsync();
    }

    partial void OnOutputFolderChanged(string value) => _settings.ZstdOutputFolder = value;

    partial void OnPatchFolderChanged(string value)
    {
        _plan = null;
        PlanText = string.Empty;
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnLevelChanged(int value)
    {
        _settings.ZstdLevel = value;
        RaisePresets();
        _plan = null;
        PlanText = string.Empty;
    }

    // ───────────────────────────── mức nén dựng sẵn ─────────────────────────────

    /// <summary>Bốn mức như Studio: Nhanh 1 · Cân bằng 5 (mặc định ps5-dump-forge) · Nhỏ 12 · Nhỏ nhất 19.</summary>
    public static readonly int[] PresetLevels = [1, AmprZstd.DefaultLevel, 12, 19];

    public bool IsPresetFast => Level == PresetLevels[0];

    public bool IsPresetBalanced => Level == PresetLevels[1];

    public bool IsPresetSmall => Level == PresetLevels[2];

    public bool IsPresetSmallest => Level == PresetLevels[3];

    public string PresetHint => Loc.F(Level switch
    {
        1 => "Zstd.Ui.PresetHint.Fast",
        AmprZstd.DefaultLevel => "Zstd.Ui.PresetHint.Balanced",
        12 => "Zstd.Ui.PresetHint.Small",
        19 => "Zstd.Ui.PresetHint.Smallest",
        _ => "Zstd.Ui.PresetHint.Custom",
    }, Level);

    [RelayCommand]
    private void SetPreset(string level)
    {
        if (!IsBusy && int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            Level = value;
        }
    }

    private void RaisePresets()
    {
        OnPropertyChanged(nameof(IsPresetFast));
        OnPropertyChanged(nameof(IsPresetBalanced));
        OnPropertyChanged(nameof(IsPresetSmall));
        OnPropertyChanged(nameof(IsPresetSmallest));
        OnPropertyChanged(nameof(PresetHint));
    }

    // ───────────────────────────── đầu trang, khung game, kế hoạch ─────────────────────────────

    public string HeaderChip => Loc.T("Zstd.Ui.Chip." + Mode);

    public string AdvancedHeader => Loc.T(IsUnpack ? "Zstd.Ui.Advanced.Unpack" : "Zstd.Ui.Advanced");

    public string OutputLabel => Loc.T(IsPack ? "Zstd.Ui.Output.Pack" : "Zstd.Ui.Output.Unpack");

    /// <summary>Dòng trạng thái trên thanh tiến độ: việc đang chạy, kết quả, hoặc gợi ý bước tiếp theo.</summary>
    public string StatusLine => IsBusy || Succeeded || Failed
        ? (DetailText.Length > 0 ? PhaseText + "  ·  " + DetailText : PhaseText)
        : Loc.T("Zstd.Ui.Idle." + Mode);

    [ObservableProperty] private Bitmap? _heroIcon;

    public bool HasHeroIcon => HeroIcon != null;

    partial void OnHeroIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasHeroIcon));

    private string? _heroIconPath;

    private void LoadHeroIcon(string? folder)
    {
        var path = folder == null ? null : Path.Combine(folder, "sce_sys", "icon0.png");
        if (path == _heroIconPath)
        {
            return;
        }

        _heroIconPath = path;
        try
        {
            HeroIcon = path != null && File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception)
        {
            HeroIcon = null;
        }
    }

    public string DropTitle => Loc.T("Zstd.Ui.Drop.Title." + Mode);

    public string DropSubtitle => Loc.T("Zstd.Ui.Drop.Sub." + Mode);

    public string PreviewHeading => Loc.T("Zstd.Ui.Preview." + Mode);

    [ObservableProperty] private string _heroSubtitle = string.Empty;
    [ObservableProperty] private string _previewText = string.Empty;

    private void BuildPreview()
    {
        var info = Info;
        LoadHeroIcon(info?.Folder);
        var text = new StringBuilder();
        if (info == null)
        {
            HeroSubtitle = Loc.T("Zstd.Ui.Hero.Empty." + Mode);
            PreviewText = Loc.T("Zstd.Ui.PreviewIntro." + Mode);
            return;
        }

        HeroSubtitle = string.Join("  ·  ", new[] { info.TitleId, info.ContentVersion.Length > 0 ? "v" + info.ContentVersion : string.Empty, info.Packed ? Loc.T("Zstd.Cli.Packed") : Loc.T("Zstd.Cli.NotPacked") }.Where(x => x.Length > 0));
        if (IsPatch && PlanText.Length > 0)
        {
            PreviewText = PlanText;
            return;
        }

        if (IsPack && !info.Packed)
        {
            var files = info.Loose.Where(e => !e.Path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                                             && !e.Path.Equals(AmprZstd.JournalName, StringComparison.OrdinalIgnoreCase)
                                             && !e.Path.Equals(AmprZstd.EmuIndexName, StringComparison.OrdinalIgnoreCase)
                                             && !e.Path.Equals(AmprZstd.RuntimePath, StringComparison.OrdinalIgnoreCase)).ToList();
            var packed = files.Where(e => e.Size > 0 && !AmprZstd.AlwaysLoose(e.Path)).ToList();
            var loose = files.Except(packed).ToList();
            text.AppendLine(Loc.F("Zstd.Ui.Preview.IntoPack", packed.Count, Formatters.Size(packed.Sum(e => e.Size))));
            text.AppendLine(Loc.F("Zstd.Ui.Preview.Loose", loose.Count, Formatters.Size(loose.Sum(e => e.Size))));
            text.AppendLine(Loc.F("Zstd.Ui.Preview.RuntimeChange", info.RuntimeNote, AmprZstd.NgVersion));
            text.AppendLine();
            text.AppendLine(Loc.T("Zstd.Ui.Preview.Largest"));
            foreach (var e in packed.OrderByDescending(e => e.Size).Take(10))
            {
                text.AppendLine($"  {Formatters.Size(e.Size),10}  {e.Path}");
            }
        }
        else if (info.Index is { } index)
        {
            text.AppendLine(Loc.F("Zstd.Ui.Preview.InPack", index.Files.Count, Formatters.Size(index.Logical), Formatters.Size(index.Stored),
                (100.0 * index.Stored / Math.Max(1, index.Logical)).ToString("F1", CultureInfo.InvariantCulture)));
            text.AppendLine(Loc.F("Zstd.Ui.Preview.Loose", info.LooseCount, Formatters.Size(info.LooseBytes)));
            text.AppendLine(Loc.F("Zstd.Ui.Preview.Volumes", index.VolumeSizes.Count, index.Level));
            text.AppendLine(Loc.F("Zstd.Ui.Runtime", info.RuntimeNote));
            if (IsPatch)
            {
                text.AppendLine();
                text.AppendLine(Loc.T("Zstd.Ui.PreviewIntro.Patch"));
            }
            else
            {
                text.AppendLine();
                text.AppendLine(Loc.T("Zstd.Ui.Preview.Largest"));
                foreach (var f in index.Files.OrderByDescending(f => f.Size).Take(10))
                {
                    text.AppendLine($"  {Formatters.Size(f.Size),10}  {f.Path}");
                }
            }
        }
        else
        {
            text.AppendLine(InfoBadge);
        }

        if (info.EmuSummary.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("! " + info.EmuSummary);
            foreach (var issue in info.EmuIssues.Take(12))
            {
                text.AppendLine("  " + issue);
            }
        }

        PreviewText = text.ToString().TrimEnd();
    }

    private static string Clean(string? path) => (path ?? string.Empty).Trim().Trim('"');

    private void SuggestOutput()
    {
        var source = Clean(SourcePath);
        if (source.Length == 0 || !Directory.Exists(source) || IsPatch)
        {
            return;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source)) ?? source;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        OutputFolder = Path.Combine(parent, name + (IsPack ? "_zstd" : "_unpacked"));
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        var path = await _dialogs.PickFolderAsync(SourceLabel, Directory.Exists(Clean(SourcePath)) ? Clean(SourcePath) : null);
        if (path != null)
        {
            SourcePath = path;
            SuggestOutput();
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var path = await _dialogs.PickFolderAsync(Loc.T("Zstd.Ui.Output"), null);
        if (path != null)
        {
            OutputFolder = path;
        }
    }

    [RelayCommand]
    private async Task BrowsePatchAsync()
    {
        var path = await _dialogs.PickFolderAsync(Loc.T("Zstd.Ui.PatchFolder"), null);
        if (path != null)
        {
            PatchFolder = path;
        }
    }

    /// <summary>Kéo thả: thư mục đã đóng ZSTD → ô nguồn; ở thẻ Vá, thư mục chưa đóng → ô thư mục vá.</summary>
    public void DropPath(string path)
    {
        if (IsBusy || !Directory.Exists(path))
        {
            return;
        }

        if (IsPatch && !File.Exists(Path.Combine(path, AmprZstd.IndexName)) && Info != null)
        {
            PatchFolder = path;
            return;
        }

        SourcePath = path;
        SuggestOutput();
    }

    // ───────────────────────────── thông tin nguồn ─────────────────────────────

    [ObservableProperty] private AmprZstd.Info? _info;
    [ObservableProperty] private bool _isInspecting;
    [ObservableProperty] private string _infoTitle = string.Empty;
    [ObservableProperty] private string _infoTitleId = "—";
    [ObservableProperty] private string _infoVersion = "—";
    [ObservableProperty] private string _infoState = string.Empty;
    [ObservableProperty] private string _infoRuntime = string.Empty;
    [ObservableProperty] private string _infoDetails = string.Empty;
    [ObservableProperty] private string _infoBadge = string.Empty;
    [ObservableProperty] private bool _infoOk;
    [ObservableProperty] private bool _infoWarn;
    [ObservableProperty] private bool _canReindex;
    [ObservableProperty] private string _planText = string.Empty;

    [RelayCommand]
    private Task InspectAsync() => InspectCoreAsync();

    private async Task InspectCoreAsync()
    {
        var id = Interlocked.Increment(ref _inspection);
        var source = Clean(SourcePath);
        if (source.Length == 0 || !Directory.Exists(source))
        {
            Info = null;
            RefreshInfoTexts(source.Length == 0 ? null : Loc.T("Zstd.Ui.Badge.NoFolder"));
            return;
        }

        IsInspecting = true;
        try
        {
            var info = await Task.Run(() => AmprZstd.Read(source));
            if (id != _inspection)
            {
                return;
            }

            Info = info;
            RefreshInfoTexts();
        }
        catch (Exception ex)
        {
            if (id == _inspection)
            {
                Info = null;
                RefreshInfoTexts(ex.Message);
            }
        }
        finally
        {
            if (id == _inspection)
            {
                IsInspecting = false;
            }
        }
    }

    private void RefreshInfoTexts(string? error = null)
    {
        var info = Info;
        InfoTitle = info?.Title is { Length: > 0 } title ? title : info != null ? Path.GetFileName(info.Folder) : Loc.T("Zstd.Ui.NoSource");
        InfoTitleId = info?.TitleId is { Length: > 0 } tid ? tid : "—";
        InfoVersion = info?.ContentVersion is { Length: > 0 } v ? v : "—";
        InfoState = info == null ? string.Empty : Loc.T(info.Packed ? "Zstd.Cli.Packed" : "Zstd.Cli.NotPacked");
        InfoRuntime = info == null ? string.Empty : Loc.F("Zstd.Ui.Runtime", info.RuntimeNote);
        CanReindex = info is { Packed: true, EmuIssues.Count: > 0 } && !IsBusy;
        var details = new StringBuilder();
        if (info != null)
        {
            if (info.Index is { } index)
            {
                details.AppendLine(Loc.F("Zstd.Cli.Pack", index.Level, index.Files.Count, index.Chunks.Count, info.ZstdChunks, index.VolumeSizes.Count,
                    Formatters.Size(info.PackedLogical), Formatters.Size(info.PackedStored)));
            }

            details.AppendLine(Loc.F("Zstd.Cli.Loose", info.LooseCount, Formatters.Size(info.LooseBytes), Formatters.Size(info.DiskBytes)));
            if (info.EmuSummary.Length > 0)
            {
                details.AppendLine();
                details.AppendLine("! " + info.EmuSummary);
                foreach (var issue in info.EmuIssues.Take(12))
                {
                    details.AppendLine("  " + issue);
                }
            }
        }

        InfoDetails = details.ToString().TrimEnd();
        string badge;
        bool ok = false, warn = false;
        if (error != null)
        {
            badge = error;
            warn = true;
        }
        else if (info == null)
        {
            badge = Loc.T("Zstd.Ui.Badge.Empty");
        }
        else if (info.Problem.Length > 0)
        {
            badge = info.Problem;
            warn = true;
        }
        else if (IsPack && info.Packed)
        {
            badge = Loc.T("Zstd.Ui.Badge.AlreadyPacked");
            warn = true;
        }
        else if (!IsPack && !info.Packed)
        {
            badge = Loc.T("Zstd.Ui.Badge.NotPacked");
            warn = true;
        }
        else if (info.EmuSummary.Length > 0)
        {
            badge = Loc.F("Zstd.Ui.Badge.EmuStale", info.EmuSummary);
            warn = true;
        }
        else
        {
            badge = Loc.T(IsPack ? "Zstd.Ui.Badge.ReadyPack" : IsUnpack ? "Zstd.Ui.Badge.ReadyUnpack" : "Zstd.Ui.Badge.ReadyPatch");
            ok = true;
        }

        InfoBadge = badge;
        InfoOk = ok;
        InfoWarn = warn;
        BuildPreview();
        StartCommand.NotifyCanExecuteChanged();
        PlanCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTexts()
    {
        OnPropertyChanged(nameof(ShowPatchTab));
        OnPropertyChanged(nameof(AdvancedHeader));
        OnPropertyChanged(nameof(OutputLabel));
        OnPropertyChanged(nameof(StatusLine));

        ThreadChoices = BuildThreadChoices();
        OnPropertyChanged(nameof(ThreadChoices));
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(HeaderChip));
        OnPropertyChanged(nameof(DropTitle));
        OnPropertyChanged(nameof(DropSubtitle));
        OnPropertyChanged(nameof(PreviewHeading));
        OnPropertyChanged(nameof(RatioLabel));
        RaisePresets();
        RefreshInfoTexts();
    }

    // ───────────────────────────── chạy ─────────────────────────────

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _phaseText = string.Empty;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _remainingText = "—";
    [ObservableProperty] private string _processedText = "—";
    [ObservableProperty] private string _ratioText = "—";

    public string RatioLabel => Loc.T(IsPack ? "Zstd.Ui.Metric.Ratio" : "Zstd.Ui.Metric.Written");
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _lastFolder = string.Empty;

    partial void OnPhaseTextChanged(string value) => OnPropertyChanged(nameof(StatusLine));

    partial void OnDetailTextChanged(string value) => OnPropertyChanged(nameof(StatusLine));

    partial void OnSucceededChanged(bool value) => OnPropertyChanged(nameof(StatusLine));

    partial void OnFailedChanged(bool value) => OnPropertyChanged(nameof(StatusLine));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusLine));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        PlanCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        CanReindex = Info is { Packed: true, EmuIssues.Count: > 0 } && !value;
    }

    private bool CanStart() => !IsBusy && Info is { } info && info.Problem.Length == 0 && Mode switch
    {
        ZstdMode.Pack => !info.Packed && Clean(OutputFolder).Length > 0,
        ZstdMode.Unpack => info.Packed && Clean(OutputFolder).Length > 0,
        _ => info.Packed && Directory.Exists(Clean(PatchFolder)),
    };

    partial void OnInfoChanged(AmprZstd.Info? value) => StartCommand.NotifyCanExecuteChanged();

    /// <summary>Móc kiểm thử (PSVIETHOA_ZSTD_AUTOSTART): bỏ hộp xác nhận trước khi vá.</summary>
    public bool SkipConfirm { get; set; }

    private bool CanPlan() => !IsBusy && IsPatch && Info is { Packed: true } && Directory.Exists(Clean(PatchFolder));

    private bool CanUndo() => !IsBusy && IsPatch && Info is { } info && AmprZstd.UndoFolders(info.Folder).Count > 0;

    [RelayCommand(CanExecute = nameof(CanPlan))]
    private async Task PlanAsync() => await MakePlanAsync();

    private async Task<AmprZstd.PatchPlan?> MakePlanAsync()
    {
        if (Info is not { } info)
        {
            return null;
        }

        try
        {
            var patchDir = Clean(PatchFolder);
            var level = Level;
            var plan = await Task.Run(() => AmprZstd.PlanPatch(info, patchDir, level, CancellationToken.None));
            _plan = plan;
            var text = new StringBuilder();
            text.AppendLine(Loc.F("Zstd.Cli.PlanSummary",
                plan.Count(AmprZstd.PatchKind.LooseReplace) + plan.Count(AmprZstd.PatchKind.LooseNew),
                plan.Count(AmprZstd.PatchKind.InPlace), plan.Count(AmprZstd.PatchKind.Rewrite), Formatters.Size(plan.RewriteCopyBytes)));
            foreach (var item in plan.Items.Take(200))
            {
                text.AppendLine($"{Loc.T("Zstd.Ui.Kind." + item.Kind),-14} {Formatters.Size(item.Size),10}  {item.Path}" + (item.Note.Length > 0 ? "  · " + item.Note : string.Empty));
            }

            foreach (var skipped in plan.Skipped.Take(50))
            {
                text.AppendLine(Loc.F("Zstd.Ui.Skipped", skipped));
            }

            PlanText = text.ToString().TrimEnd();
            BuildPreview();
            return plan;
        }
        catch (Exception ex)
        {
            _plan = null;
            PlanText = ex.Message;
            AddLog(new LogEntry(LogLevel.Error, ex.Message));
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Info is not { } info)
        {
            return;
        }

        var output = Clean(OutputFolder);
        AmprZstd.PatchPlan? plan = null;
        if (IsPatch)
        {
            plan = _plan is { } p && p.Info.Folder == info.Folder && p.PatchDir == Clean(PatchFolder) && p.Level == Level ? p : await MakePlanAsync();
            if (plan == null)
            {
                return;
            }

            if (!SkipConfirm && !await _dialogs.ConfirmAsync(Loc.T("Zstd.Ui.Start.Patch"), Loc.F("Zstd.Ui.PatchConfirm", info.Folder, plan.Items.Count), Loc.T("Zstd.Ui.Start.Patch"), Loc.T("Common.Cancel")))
            {
                return;
            }
        }

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        IsBusy = true;
        Succeeded = Failed = false;
        Percent = 0;
        PhaseText = Loc.T("Zstd.Ui.Phase." + Mode);
        DetailText = string.Empty;
        SpeedText = RemainingText = ProcessedText = RatioText = "—";
        StatusText = PhaseText;
        _elapsed.Restart();
        _tick.Start();
        var progress = new Progress<AmprZstd.Progress>(p => _pendingProgress = p);
        void Log(string line) => _pendingLog.Enqueue(new LogEntry(line.StartsWith('!') ? LogLevel.Warning : LogLevel.Info, line));
        var threads = Threads;
        var level = Level;
        var verify = Verify;
        var keepRuntime = KeepRuntime;
        AddLog(new LogEntry(LogLevel.Info, Loc.F("Zstd.Ui.Log.Start", Loc.T("Zstd.Ui.Tab." + Mode), info.Folder)));
        try
        {
            string result;
            switch (Mode)
            {
                case ZstdMode.Pack:
                {
                    var r = await Task.Run(() => AmprZstd.Pack(info.Folder, output, new AmprZstd.PackOptions(level, threads, verify), progress, token, Log), token);
                    LastFolder = r.Folder;
                    result = Loc.F("Zstd.Cli.PackResult", r.Folder, r.PackedFiles, r.LooseFiles, r.Volumes, r.Chunks, r.ZstdChunks,
                        Formatters.Size(r.Logical), Formatters.Size(r.Stored), r.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture));
                    break;
                }

                case ZstdMode.Unpack:
                {
                    var r = await Task.Run(() => AmprZstd.Unpack(info, output, null, new AmprZstd.UnpackOptions(!keepRuntime, threads), progress, token, Log), token);
                    LastFolder = r.Folder;
                    result = Loc.F("Zstd.Cli.UnpackResult", r.Folder, r.Files, Formatters.Size(r.Bytes), r.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture));
                    break;
                }

                default:
                {
                    var r = await Task.Run(() => AmprZstd.Patch(plan!, progress, token, Log, threads), token);
                    LastFolder = r.Folder;
                    result = Loc.F("Zstd.Cli.PatchResult", r.Folder, r.Files, r.InPlace, r.Rewritten, r.Loose, Formatters.Size(r.CopiedBytes),
                        r.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture), r.UndoDir);
                    _plan = null;
                    break;
                }
            }

            Tick();
            Percent = 100;
            Succeeded = true;
            PhaseText = Loc.T("Zstd.Core.Done");
            DetailText = result;
            StatusText = result;
            AddLog(new LogEntry(LogLevel.Success, result));
        }
        catch (Exception ex)
        {
            Tick();
            var stopped = ex is OperationCanceledException || token.IsCancellationRequested;
            Failed = !stopped;
            PhaseText = Loc.T(stopped ? "Zstd.Ui.Stopped" : "Zstd.Ui.Failed");
            DetailText = stopped ? Loc.T("Zstd.Ui.StoppedDetail") : ex.Message;
            StatusText = PhaseText + " · " + DetailText;
            AddLog(new LogEntry(stopped ? LogLevel.Warning : LogLevel.Error, DetailText));
        }
        finally
        {
            _elapsed.Stop();
            _tick.Stop();
            ElapsedText = Duration(_elapsed.Elapsed.TotalSeconds);
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
            await InspectCoreAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (Info is not { } info || AmprZstd.UndoFolders(info.Folder).FirstOrDefault() is not { } dir)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Zstd.Ui.Undo"), Loc.F("Zstd.Ui.UndoConfirm", dir), Loc.T("Zstd.Ui.Undo"), Loc.T("Common.Cancel"), destructive: true))
        {
            return;
        }

        await RunSimpleAsync(() => AmprZstd.Undo(dir, line => _pendingLog.Enqueue(new LogEntry(LogLevel.Info, line))));
    }

    [RelayCommand]
    private async Task ReindexAsync()
    {
        if (Info is not { } info)
        {
            return;
        }

        await RunSimpleAsync(() => AmprZstd.RepairEmuIndex(info.Folder, line => _pendingLog.Enqueue(new LogEntry(LogLevel.Info, line))));
    }

    private async Task RunSimpleAsync(Action work)
    {
        IsBusy = true;
        _tick.Start();
        try
        {
            await Task.Run(work);
            Tick();
            AddLog(new LogEntry(LogLevel.Success, Loc.T("Zstd.Core.Done")));
        }
        catch (Exception ex)
        {
            Tick();
            AddLog(new LogEntry(LogLevel.Error, ex.Message));
        }
        finally
        {
            _tick.Stop();
            IsBusy = false;
            await InspectCoreAsync();
        }
    }

    [RelayCommand]
    private async Task OpenResultAsync()
    {
        var target = LastFolder.Length > 0 ? LastFolder : Clean(IsPatch ? SourcePath : OutputFolder);
        if (target.Length > 0 && Directory.Exists(target))
        {
            await _dialogs.OpenFolderAsync(target);
        }
    }

    private void Tick()
    {
        while (_pendingLog.TryDequeue(out var entry))
        {
            AddLog(entry);
        }

        if (Interlocked.Exchange(ref _pendingProgress, null) is { } p)
        {
            Percent = Math.Max(Percent, Math.Clamp(p.Percent, 0, 100));
            DetailText = p.Detail;
            SpeedText = p.BytesPerSecond > 0 ? Formatters.Rate(p.BytesPerSecond) : "—";
            RemainingText = p.BytesPerSecond > 0 && p.Total > p.Done ? Duration((p.Total - p.Done) / p.BytesPerSecond) : "—";
            ProcessedText = p.Total > 0 ? $"{Formatters.Size(p.Done)} / {Formatters.Size(p.Total)}" : "—";
            RatioText = IsPack ? (p.PackedIn > 0 ? (100.0 * p.PackedOut / p.PackedIn).ToString("F1", CultureInfo.InvariantCulture) + "%" : "—") : Formatters.Size(p.Done);
            StatusText = $"{PhaseText} · {Percent:0}%";
        }

        ElapsedText = Duration(_elapsed.Elapsed.TotalSeconds);
    }

    private static string Duration(double seconds) =>
        seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    // ───────────────────────────── nhật ký ─────────────────────────────

    public LogCollection Log { get; } = new();

    public ObservableCollection<LogEntry> VisibleLog { get; } = new();

    [ObservableProperty] private string _logFilter = string.Empty;

    public string LogCountText => Log.Count.ToString("N0", CultureInfo.InvariantCulture);

    partial void OnLogFilterChanged(string value) => RebuildVisibleLog();

    private void AddLog(LogEntry entry)
    {
        Log.AddBatch([entry]);
        OnPropertyChanged(nameof(LogCountText));
        if (MatchesFilter(entry))
        {
            VisibleLog.Add(entry);
            if (VisibleLog.Count > LogCollection.Capacity)
            {
                RebuildVisibleLog();
            }
        }
    }

    private bool MatchesFilter(LogEntry entry) =>
        string.IsNullOrWhiteSpace(LogFilter) || entry.Message.Contains(LogFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RebuildVisibleLog()
    {
        VisibleLog.Clear();
        foreach (var entry in Log.Where(MatchesFilter))
        {
            VisibleLog.Add(entry);
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
        VisibleLog.Clear();
        OnPropertyChanged(nameof(LogCountText));
    }

    [RelayCommand]
    private async Task CopyLogAsync() =>
        await _dialogs.SetClipboardAsync(string.Join(Environment.NewLine, VisibleLog.Select(entry => $"{entry.TimeText} {entry.Message}")));
}
