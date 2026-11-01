using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Saves;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Màu trạng thái dùng trên trang Save (chấm trạng thái, chữ trạng thái của từng save).</summary>
public enum SaveTone
{
    Muted,
    Info,
    Ok,
    Warn,
    Danger,
}

/// <summary>Một dòng của hàng đợi resign: loại save (nhận theo nội dung), tên, vị trí và trạng thái.</summary>
public sealed partial class SaveJobRow : ObservableObject
{
    public SaveJobRow(string path, SaveKind kind, bool missingKey)
    {
        Path = path;
        Kind = kind;
        var trimmed = path.TrimEnd('\\', '/');
        Name = System.IO.Path.GetFileName(trimmed) is { Length: > 0 } name ? name : path;
        Folder = System.IO.Path.GetDirectoryName(trimmed) ?? path;
        MissingKey = missingKey;
        _stateKey = missingKey ? "Save.State.MissingBin" : "Save.State.Ready";
        _tone = missingKey ? SaveTone.Warn : SaveTone.Muted;
    }

    public string Path { get; }

    public SaveKind Kind { get; }

    public string Name { get; }

    public string Folder { get; }

    public bool MissingKey { get; }

    public string KindLabel => Loc.T("Save.Kind." + Kind);

    /// <summary>Màu của nhãn loại: PS4 mã hoá (xanh), PS5 mã hoá (tím), khoá PS4 (vàng), còn lại trung tính.</summary>
    public bool IsPs4Kind => Kind == SaveKind.Ps4Encrypted;

    public bool IsPs5Kind => Kind == SaveKind.Ps5Encrypted;

    public bool IsKeyKind => Kind == SaveKind.Ps4Key;

    [ObservableProperty] private string _stateKey;
    [ObservableProperty] private SaveTone _tone;
    [ObservableProperty] private bool _isSelected;

    public string StateText => Loc.T(StateKey);

    public bool IsInfo => Tone == SaveTone.Info;

    public bool IsOk => Tone == SaveTone.Ok;

    public bool IsWarn => Tone == SaveTone.Warn;

    public bool IsDanger => Tone == SaveTone.Danger;

    partial void OnStateKeyChanged(string value) => OnPropertyChanged(nameof(StateText));

    partial void OnToneChanged(SaveTone value)
    {
        OnPropertyChanged(nameof(IsInfo));
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsWarn));
        OnPropertyChanged(nameof(IsDanger));
    }

    public void SetState(string key, SaveTone tone)
    {
        StateKey = key;
        Tone = tone;
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(StateText));
    }
}

/// <summary>
/// Trang Resign save PS4 / PS5: đổi chủ save sang Account ID của bạn rồi chép thẳng vào máy qua garlic-savemgr (payload ELF chạy trên
/// PS5, nạp qua ELF loader cổng 9021, dịch vụ HTTP cổng 8082). Hàng đợi nhận thư mục save, tệp .zip, save PS5 mã hoá, cặp ảnh save
/// PS4 + .bin và param.sfo — tự nhận loại. Cách làm theo PSVIETHOA STUDIO (Ngô Phi Phương).
/// </summary>
public sealed partial class SaveViewModel : ObservableObject
{
    private static readonly Regex TitlePattern = new("^(CUSA|PPSA)[0-9]{5}$", RegexOptions.Compiled);

    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private byte[]? _elf;
    private int _logCount;

    public SaveViewModel(AppSettings settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _accountId = settings.SaveAccountId ?? string.Empty;
        _host = settings.SaveHost ?? string.Empty;
        _port = string.IsNullOrWhiteSpace(settings.SavePort) ? SaveResigner.GarlicPort.ToString() : settings.SavePort!;
        _userId = settings.SaveUserId ?? string.Empty;
        _titleId = settings.SaveTitleId ?? string.Empty;
        _install = settings.SaveInstall;
        _statusKey = "Save.Status.Ready";
        Jobs.CollectionChanged += (_, _) => RefreshQueue();
        Loc.Current.LanguageChanged += (_, _) => RefreshTexts();
        try
        {
            _elf = SaveResigner.BundledElf();
        }
        catch (IOException)
        {
            _elf = null;
        }

        RefreshQueue();
    }

    // ── Tài khoản & máy PS5 ─────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _accountId;
    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _userId;
    [ObservableProperty] private string _titleId;
    [ObservableProperty] private bool _install;
    [ObservableProperty] private string _formError = string.Empty;
    [ObservableProperty] private string? _errorField;
    [ObservableProperty] private Ps5User? _selectedUser;
    [ObservableProperty] private bool _showUsers;

    // ── Trạng thái ──────────────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusKey;
    [ObservableProperty] private object?[] _statusArgs = Array.Empty<object?>();
    [ObservableProperty] private SaveTone _statusTone = SaveTone.Ok;
    [ObservableProperty] private string _runText = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private double _progressMaximum = 1;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty] private string _logFilter = string.Empty;

    public ObservableCollection<SaveJobRow> Jobs { get; } = new();

    public ObservableCollection<Ps5User> Users { get; } = new();

    public LogCollection Log { get; } = new();

    public ObservableCollection<LogEntry> VisibleLog { get; } = new();

    public string StatusText => Loc.F(StatusKey, StatusArgs);

    public bool StatusInfo => StatusTone == SaveTone.Info;

    public bool StatusOk => StatusTone == SaveTone.Ok;

    public bool StatusWarn => StatusTone == SaveTone.Warn;

    public bool StatusDanger => StatusTone == SaveTone.Danger;

    public string CountText => Loc.F(Jobs.Count == 1 ? "Save.CountOne" : "Save.Count", Jobs.Count);

    public bool HasJobs => Jobs.Count > 0;

    public string LogCountText => Loc.F(_logCount == 1 ? "Save.LogCountOne" : "Save.LogCount", _logCount);

    public bool CanStart => !IsBusy && Jobs.Count > 0;

    public string StartText => Loc.T(IsBusy ? "Save.Working" : "Save.Start");

    public string ElfInfo => _elf is { Length: > 0 } elf ? Loc.F("Save.ElfInfo", (elf.Length / 1048576.0).ToString("F2")) : Loc.T("Save.ElfMissing");

    public string DropTitle => Loc.T(IsDropTarget ? "Save.DropRelease" : "Save.DropTitle");

    public bool AccountError => ErrorField == nameof(AccountId);

    public bool HostError => ErrorField == nameof(Host);

    public bool PortError => ErrorField == nameof(Port);

    public bool UserError => ErrorField == nameof(UserId);

    public bool TitleError => ErrorField == nameof(TitleId);

    public bool HasFormError => !string.IsNullOrEmpty(FormError);

    partial void OnAccountIdChanged(string value) => ClearError(nameof(AccountId));

    partial void OnHostChanged(string value) => ClearError(nameof(Host));

    partial void OnPortChanged(string value) => ClearError(nameof(Port));

    partial void OnUserIdChanged(string value) => ClearError(nameof(UserId));

    partial void OnTitleIdChanged(string value) => ClearError(nameof(TitleId));

    partial void OnInstallChanged(bool value) => _settings.SaveInstall = value;

    partial void OnStatusKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnStatusArgsChanged(object?[] value) => OnPropertyChanged(nameof(StatusText));

    partial void OnStatusToneChanged(SaveTone value)
    {
        OnPropertyChanged(nameof(StatusInfo));
        OnPropertyChanged(nameof(StatusOk));
        OnPropertyChanged(nameof(StatusWarn));
        OnPropertyChanged(nameof(StatusDanger));
    }

    partial void OnIsDropTargetChanged(bool value) => OnPropertyChanged(nameof(DropTitle));

    partial void OnLogFilterChanged(string value) => RebuildVisibleLog();

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(StatusText));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        if (!value)
        {
            RefreshQueue();
        }
    }

    partial void OnErrorFieldChanged(string? value)
    {
        OnPropertyChanged(nameof(AccountError));
        OnPropertyChanged(nameof(HostError));
        OnPropertyChanged(nameof(PortError));
        OnPropertyChanged(nameof(UserError));
        OnPropertyChanged(nameof(TitleError));
    }

    partial void OnFormErrorChanged(string value) => OnPropertyChanged(nameof(HasFormError));

    partial void OnSelectedUserChanged(Ps5User? value)
    {
        if (value == null)
        {
            return;
        }

        UserId = value.Id;
        SetStatus("Save.Status.UserPicked", SaveTone.Ok, value.Name);
    }

    private void RefreshTexts()
    {
        foreach (var job in Jobs)
        {
            job.RefreshTexts();
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(LogCountText));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(ElfInfo));
        OnPropertyChanged(nameof(DropTitle));
        if (!IsBusy)
        {
            RefreshQueue();
        }
    }

    private void RefreshQueue()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
        if (!IsBusy)
        {
            RunText = Jobs.Count > 0 ? Loc.F("Save.Run.Waiting", Jobs.Count) : Loc.T("Save.Run.None");
        }
    }

    private void SetStatus(string key, SaveTone tone, params object?[] args)
    {
        StatusArgs = args;
        StatusKey = key;
        StatusTone = tone;
    }

    // ── Nhật ký ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ghi một dòng nhật ký (gọi được từ luồng nền).</summary>
    private void WriteLog(string line)
    {
        var level = line.Contains("[!]", StringComparison.Ordinal) ? LogLevel.Warning : line.Contains("[OK]", StringComparison.Ordinal) ? LogLevel.Success : LogLevel.Info;
        var entry = new LogEntry(level, "[RESIGN] " + line);
        Dispatcher.UIThread.Post(() =>
        {
            Log.AddBatch([entry]);
            _logCount++;
            OnPropertyChanged(nameof(LogCountText));
            if (Matches(entry))
            {
                VisibleLog.Add(entry);
            }
        });
    }

    private bool Matches(LogEntry entry) =>
        string.IsNullOrWhiteSpace(LogFilter) || entry.Message.Contains(LogFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RebuildVisibleLog()
    {
        VisibleLog.Clear();
        foreach (var entry in Log.Where(Matches))
        {
            VisibleLog.Add(entry);
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
        VisibleLog.Clear();
        _logCount = 0;
        OnPropertyChanged(nameof(LogCountText));
    }

    [RelayCommand]
    private async Task CopyLogAsync() =>
        await _dialogs.SetClipboardAsync(string.Join(Environment.NewLine, VisibleLog.Select(entry => $"{entry.Time:HH:mm:ss} {entry.Message}")));

    // ── Kiểm tra ô nhập (lỗi hiện ngay ở ô và ở khung lỗi trên cùng) ───────────────────────────────────────────────

    private void ClearError(string field)
    {
        if (ErrorField == field)
        {
            ErrorField = null;
            FormError = string.Empty;
        }
    }

    private void ShowFieldError(string field, string message)
    {
        ErrorField = field;
        FormError = message;
        SetStatus("Save.Status.CheckFields", SaveTone.Warn);
    }

    private string? RequireIp()
    {
        var ip = Host.Trim();
        if (ip.Length == 0)
        {
            ShowFieldError(nameof(Host), Loc.T("Save.Error.Ip"));
            return null;
        }

        return ip;
    }

    private bool TryPort(out int port)
    {
        if (!int.TryParse(Port.Trim(), out port) || port is < 1 or > 65535)
        {
            ShowFieldError(nameof(Port), Loc.T("Save.Error.Port"));
            return false;
        }

        return true;
    }

    private ResignOptions? ReadOptions(bool requireNetwork, bool requireUser = false)
    {
        var aid = SaveResigner.NormalizeHex(AccountId, 8);
        if (aid == null)
        {
            ShowFieldError(nameof(AccountId), Loc.T("Save.Error.Account"));
            return null;
        }

        var ip = Host.Trim();
        if (requireNetwork && ip.Length == 0)
        {
            ShowFieldError(nameof(Host), Loc.T("Save.Error.IpNeeded"));
            return null;
        }

        if (!TryPort(out var port))
        {
            return null;
        }

        uint user = 0;
        if (UserId.Trim().Length > 0)
        {
            var uid = SaveResigner.NormalizeHex(UserId, 4);
            if (uid == null)
            {
                ShowFieldError(nameof(UserId), Loc.T("Save.Error.User"));
                return null;
            }

            user = Convert.ToUInt32(uid, 16);
        }

        if ((Install || requireUser) && user == 0)
        {
            ShowFieldError(nameof(UserId), Loc.T(requireUser ? "Save.Error.UserNeeded" : "Save.Error.UserForInstall"));
            return null;
        }

        var title = TitleId.Trim().ToUpperInvariant();
        if (title.Length > 0 && !TitlePattern.IsMatch(title))
        {
            ShowFieldError(nameof(TitleId), Loc.T("Save.Error.Title"));
            return null;
        }

        ErrorField = null;
        FormError = string.Empty;
        SaveSettings();
        return new ResignOptions(Convert.ToUInt64(aid, 16), user, ip.Length > 0 ? ip : "127.0.0.1", port, Install, title.Length > 0 ? title : null);
    }

    private void SaveSettings()
    {
        _settings.SaveAccountId = AccountId.Trim();
        _settings.SaveHost = Host.Trim();
        _settings.SavePort = Port.Trim();
        _settings.SaveUserId = UserId.Trim();
        _settings.SaveTitleId = TitleId.Trim();
        _settings.SaveInstall = Install;
        SettingsService.Save(_settings);
    }

    // ── Hàng đợi ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Thêm tệp / thư mục vào hàng đợi (kéo thả hoặc chọn). Tệp .bin sealed key được thay bằng ảnh save đi kèm.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        if (IsBusy)
        {
            return;
        }

        var added = 0;
        foreach (var raw in paths)
        {
            string path;
            try
            {
                path = System.IO.Path.GetFullPath(raw.Trim().Trim('"'));
            }
            catch (Exception)
            {
                continue;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            if (File.Exists(path) && SaveResigner.IsPs4SealedKey(path))
            {
                var image = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileNameWithoutExtension(path));
                if (!File.Exists(image))
                {
                    WriteLog("[!] " + Loc.F("Save.Log.KeyWithoutImage", System.IO.Path.GetFileName(path)));
                    continue;
                }

                path = image;
            }

            if (Jobs.Any(job => job.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var kind = SaveResigner.Classify(path);
            Jobs.Add(new SaveJobRow(path, kind, kind == SaveKind.Ps4Encrypted && SaveResigner.FindPs4KeyForImage(path) == null));
            added++;
        }

        if (added == 0)
        {
            SetStatus("Save.Status.NothingNew", SaveTone.Warn);
            return;
        }

        WriteLog("[+] " + Loc.F("Save.Log.Added", added));
        SetStatus("Save.Status.Queued", SaveTone.Ok, Jobs.Count);
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = await _dialogs.PickFilesAsync(Loc.T("Save.PickFilesTitle"), null);
        AddPaths(files);
    }

    [RelayCommand]
    private async Task AddFoldersAsync()
    {
        var folders = await _dialogs.PickFoldersAsync(Loc.T("Save.PickFoldersTitle"), null);
        AddPaths(folders);
    }

    private bool CanEditQueue() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEditQueue))]
    private void RemoveSelected()
    {
        foreach (var job in Jobs.Where(job => job.IsSelected).ToList())
        {
            Jobs.Remove(job);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditQueue))]
    private void Clear()
    {
        if (Jobs.Count == 0)
        {
            return;
        }

        Jobs.Clear();
        SetStatus("Save.Status.Ready", SaveTone.Ok);
    }

    [RelayCommand]
    private async Task OpenLocationAsync()
    {
        if (Jobs.FirstOrDefault(job => job.IsSelected) is not { } job)
        {
            SetStatus("Save.Status.PickToOpen", SaveTone.Warn);
            return;
        }

        await _dialogs.OpenFolderAsync(Directory.Exists(job.Path) ? job.Path : job.Folder);
    }

    // ── Việc với máy PS5 ────────────────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ScanUsersAsync()
    {
        if (IsBusy || RequireIp() is not { } ip)
        {
            return;
        }

        SetStatus("Save.Status.Scanning", SaveTone.Info);
        try
        {
            var users = await Task.Run(() => SaveResigner.ListUsers(ip));
            Users.Clear();
            foreach (var user in users)
            {
                Users.Add(user);
            }

            ShowUsers = true;
            var current = SaveResigner.NormalizeHex(UserId, 4);
            SelectedUser = users.FirstOrDefault(user => user.Id == current) ?? users[0];
            WriteLog("[OK] " + Loc.F("Save.Log.Scanned", users.Count));
            SetStatus("Save.Status.Scanned", SaveTone.Ok, users.Count);
        }
        catch (Exception ex)
        {
            WriteLog("[!] " + Loc.F("Save.Log.ScanFailed", ex.Message));
            SetStatus("Save.Status.ScanFailed", SaveTone.Warn);
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsBusy || RequireIp() is not { } ip || !TryPort(out var port))
        {
            return;
        }

        SetStatus("Save.Status.Testing", SaveTone.Info);
        var (ready, lines) = await Task.Run(() =>
        {
            var output = new List<string>();
            var ok = false;
            if (port == SaveResigner.GarlicPort)
            {
                var garlic = SaveResigner.PingGarlic(ip, port, 4);
                ok = garlic.Ok;
                output.Add(ok ? "[OK] " + Loc.F("Save.Log.GarlicReady", ip, port) : "[!] " + Loc.F("Save.Log.GarlicFailed", ip, port, garlic.Message));
            }

            if (!ok)
            {
                try
                {
                    var daemon = SaveResigner.PingDaemon(ip, port);
                    ok = daemon.Ok;
                    output.Add(daemon.Ok ? "[OK] " + Loc.F("Save.Log.DaemonReady", ip, port, daemon.Message) : "[!] " + Loc.F("Save.Log.DaemonFailed", ip, port, daemon.Message));
                }
                catch (Exception ex)
                {
                    output.Add("[!] " + Loc.F("Save.Log.DaemonFailed", ip, port, ex.Message));
                }
            }

            return (ok, output);
        });
        foreach (var line in lines)
        {
            WriteLog(line);
        }

        SetStatus(ready ? "Save.Status.Connected" : "Save.Status.NoService", ready ? SaveTone.Ok : SaveTone.Warn);
    }

    [RelayCommand]
    private async Task LoadElfAsync()
    {
        if (IsBusy || RequireIp() is not { } ip)
        {
            return;
        }

        if (_elf is not { Length: > 0 } elf)
        {
            SetStatus("Save.Status.ElfMissing", SaveTone.Danger);
            return;
        }

        IsBusy = true;
        ProgressMaximum = 100;
        Progress = 0;
        RunText = Loc.T("Save.Run.SendingElf");
        SetStatus("Save.Status.LoadingElf", SaveTone.Info);
        try
        {
            var running = await Task.Run(() =>
            {
                SaveResigner.SendElf(ip, elf, WriteLog, (sent, total) => Dispatcher.UIThread.Post(() =>
                {
                    Progress = sent * 100.0 / Math.Max(1, total);
                    RunText = Loc.F("Save.Run.ElfPercent", Progress.ToString("F0"));
                }));
                for (var i = 0; i < 5; i++)
                {
                    Thread.Sleep(1000);
                    if (SaveResigner.PingGarlic(ip, SaveResigner.GarlicPort, 3).Ok)
                    {
                        return true;
                    }
                }

                return false;
            });
            WriteLog(running ? "[OK] " + Loc.F("Save.Log.GarlicUp", ip, SaveResigner.GarlicPort) : "[!] " + Loc.F("Save.Log.GarlicNotYet", SaveResigner.GarlicPort));
            SetStatus(running ? "Save.Status.GarlicUp" : "Save.Status.ElfSent", running ? SaveTone.Ok : SaveTone.Warn);
        }
        catch (Exception ex)
        {
            WriteLog("[!] " + Loc.F("Save.Log.ElfFailed", ex.Message));
            SetStatus("Save.Status.ElfFailed", SaveTone.Danger);
        }
        finally
        {
            IsBusy = false;
            RunText = StatusText;
        }
    }

    [RelayCommand]
    private async Task TransferAsync()
    {
        if (IsBusy || ReadOptions(requireNetwork: true, requireUser: true) is not { } options)
        {
            return;
        }

        SetStatus("Save.Status.ReadingSaves", SaveTone.Info);
        List<GarlicSave> choices;
        Dictionary<string, string> names;
        try
        {
            (choices, names) = await Task.Run(() =>
            {
                var saves = SaveResigner.ListSaves(options.Host);
                Dictionary<string, string> userNames;
                try
                {
                    userNames = SaveResigner.ListUsers(options.Host).ToDictionary(user => user.Id, user => user.Name);
                }
                catch (Exception)
                {
                    userNames = new Dictionary<string, string>();
                }

                return (saves.Where(save => save.Type == "ps5" && !save.Backup && !save.Uid.Equals(options.UserHex, StringComparison.OrdinalIgnoreCase)).ToList(), userNames);
            });
        }
        catch (Exception ex)
        {
            WriteLog("[!] " + Loc.F("Save.Log.SavesFailed", ex.Message));
            SetStatus("Save.Status.SavesFailed", SaveTone.Warn);
            return;
        }

        if (choices.Count == 0)
        {
            SetStatus("Save.Status.NoOtherSaves", SaveTone.Warn);
            return;
        }

        var source = await Views.TransferSaveWindow.PickAsync(_dialogs.Owner, choices, names, options.UserHex);
        if (source == null)
        {
            SetStatus("Save.Status.Ready", SaveTone.Ok);
            return;
        }

        IsBusy = true;
        ProgressIndeterminate = true;
        RunText = Loc.F("Save.Run.Transferring", source.TitleId, source.SaveName);
        SetStatus("Save.Status.Transferring", SaveTone.Info);
        try
        {
            var result = await Task.Run(() => SaveResigner.GarlicTransferSave(source, options, WriteLog));
            var title = (result as System.Text.Json.Nodes.JsonObject)?["title_id"]?.ToString() ?? source.TitleId;
            SetStatus("Save.Status.Transferred", SaveTone.Ok, title, options.UserHex);
        }
        catch (Exception ex)
        {
            WriteLog("[!] " + Loc.F("Save.Log.TransferFailed", ex.Message));
            SetStatus("Save.Status.TransferFailed", SaveTone.Danger);
        }
        finally
        {
            ProgressIndeterminate = false;
            IsBusy = false;
            RunText = StatusText;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (Jobs.Count == 0)
        {
            SetStatus("Save.Status.AddFirst", SaveTone.Warn);
            return;
        }

        var items = Jobs.ToList();
        var install = Install;
        var needsConsole = await Task.Run(() => items.Any(item => SaveResigner.NeedsConsole(item.Path, install)));
        if (ReadOptions(needsConsole) is not { } options)
        {
            return;
        }

        IsBusy = true;
        ProgressMaximum = items.Count;
        Progress = 0;
        SetStatus("Save.Status.Resigning", SaveTone.Info);
        foreach (var item in items)
        {
            item.SetState("Save.State.Waiting", SaveTone.Muted);
        }

        int ok = 0;
        int fail = 0;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                item.SetState("Save.State.Running", SaveTone.Info);
                RunText = Loc.F("Save.Run.Item", i + 1, items.Count, item.Name);
                WriteLog($"=== {item.Name} ===");
                try
                {
                    var result = await Task.Run(() => SaveResigner.ProcessAny(item.Path, options, WriteLog));
                    ok += result.Ok;
                    fail += result.Fail;
                    if (result.Fail > 0)
                    {
                        item.SetState(result.Ok > 0 ? "Save.State.Partial" : "Save.State.Failed", SaveTone.Danger);
                    }
                    else if (result.Ok == 0)
                    {
                        item.SetState("Save.State.Skipped", SaveTone.Muted);
                    }
                    else
                    {
                        item.SetState("Save.State.Done", SaveTone.Ok);
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    item.SetState("Save.State.Failed", SaveTone.Danger);
                    WriteLog("[!] " + ex.Message);
                }

                Progress = i + 1;
            }
        }
        finally
        {
            WriteLog("== " + Loc.F("Save.Log.Summary", ok, fail) + " ==");
            IsBusy = false;
            SetStatus("Save.Status.Summary", fail == 0 ? SaveTone.Ok : SaveTone.Warn, ok, fail);
            RunText = StatusText;
        }
    }
}
