using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.App.Controls;

/// <summary>
/// Khung "Hệ thống" ở thanh bên (port từ PSVIETHOA STUDIO 2.1.0): tên CPU, tải CPU %, nhiệt CPU, rồi mỗi ổ vật lý một khối
/// (chữ ổ, kiểu + model, nhiệt độ, % bận, tốc độ đọc/ghi). Thanh chuyển vàng rồi đỏ khi gần ngưỡng.
/// Số liệu từ Windows (PDH + IOCTL), không cần quyền admin / driver; 1 giây/lần, dừng khi cửa sổ thu nhỏ. macOS / Linux: ẩn.
/// </summary>
public sealed class SystemPanel : UserControl
{
    private sealed record Meter(TextBlock Value, ProgressBar Bar);

    private sealed record DiskRow(TextBlock Name, TextBlock Temp, ProgressBar Busy, TextBlock Speed);

    // Màu theo theme (giao diện sáng: nền trắng hơi trong, chữ đậm; tối: như PSVIETHOA STUDIO). Đổi ngay khi người dùng đổi theme.
    private readonly SolidColorBrush Ink = new(), Muted = new(), Faint = new(), Track = new(), Accent = new(), CardBg = new(), CardLine = new();
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    private readonly StackPanel _disks = new();
    private readonly TextBlock _title = new() { FontSize = 10.5, FontWeight = FontWeight.Bold, LetterSpacing = 0.8 };
    private readonly TextBlock _rate = new() { FontSize = 9.5, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _cpuName = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 5) };
    private readonly TextBlock _cpuLabel = new(), _tempLabel = new();
    private readonly Meter _cpuLoad, _cpuTemp;
    private readonly StackPanel _tempBox;
    private readonly Dictionary<int, DiskRow> _rows = [];
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromSeconds(1) };
    private SystemMonitor? _monitor;
    private string _diskKey = "", _tempSource = "?";
    private int _seen = -1;

    public SystemPanel()
    {
        ApplyTheme(false);
        _title.Foreground = Faint;
        _rate.Foreground = Faint;
        _cpuName.Foreground = Muted;
        IsVisible = OperatingSystem.IsWindows();
        Focusable = false;
        var head = new Grid();
        head.Children.Add(_title);
        head.Children.Add(_rate);

        _cpuLoad = MakeMeter(_cpuLabel, out var loadBox);
        _cpuTemp = MakeMeter(_tempLabel, out _tempBox);
        var cpu = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*"), Margin = new Thickness(0, 0, 0, 1) };
        Grid.SetColumn(_tempBox, 2);
        cpu.Children.Add(loadBox);
        cpu.Children.Add(_tempBox);

        Content = new Border
        {
            Background = CardBg,
            BorderBrush = CardLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 7, 10, 8),
            Child = new StackPanel { Spacing = 0, Children = { head, _cpuName, cpu, _disks } }
        };
        Texts();
        Loc.Current.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(() => { Texts(); _diskKey = ""; });
        _ui.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { ApplyTheme(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light); Start(); };
        ActualThemeVariantChanged += (_, _) => ApplyTheme(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light);
        DetachedFromVisualTree += (_, _) => Stop();
    }

    /// <summary>Đổi màu tại chỗ (các brush dùng chung nên mọi chữ / thanh cập nhật ngay).</summary>
    private void ApplyTheme(bool light)
    {
        Ink.Color = Color.Parse(light ? "#10261C" : "#E8EEF5");
        Muted.Color = Color.Parse(light ? "#3F5A4C" : "#93A4BA");
        Faint.Color = Color.Parse(light ? "#5F7A6C" : "#6F8197");
        Track.Color = Color.Parse(light ? "#26000000" : "#33475569");
        Accent.Color = Color.Parse(light ? "#15803D" : "#4ADE80");
        CardBg.Color = Color.Parse(light ? "#D9FFFFFF" : "#99102619");
        CardLine.Color = Color.Parse(light ? "#B7D5C6" : "#40335F45");
    }

    private void Texts()
    {
        _title.Text = Loc.T("Sys.Title");
        _rate.Text = Loc.T("Sys.Rate");
        _cpuLabel.Text = "CPU";
        _tempLabel.Text = Loc.T("Sys.CpuTemp");
        var (shortName, full) = CpuName();
        _cpuName.Text = shortName;
        ToolTip.SetTip(_cpuName, Loc.F("Sys.CpuTip", full, Environment.ProcessorCount));
        ToolTip.SetTip(_cpuLoad.Bar.Parent as Control ?? this, Loc.T("Sys.CpuLoadTip"));
        _tempSource = "?";
    }

    private void Start()
    {
        if (!OperatingSystem.IsWindows() || _monitor != null) return;
        _monitor = new SystemMonitor();
        _ui.Start();
        Refresh();
    }

    private void Stop()
    {
        _ui.Stop();
        if (OperatingSystem.IsWindows()) _monitor?.Dispose();
        _monitor = null;
    }

    [SupportedOSPlatform("windows")]
    private void RefreshWindows()
    {
        if (_monitor == null || TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized }) return;
        if (_monitor.Version == _seen) return;
        _seen = _monitor.Version;
        Update(_monitor.Latest);
    }

    private void Refresh()
    {
        if (OperatingSystem.IsWindows()) RefreshWindows();
    }

    /// <summary>Tên CPU từ registry (ProcessorNameString), gọn lại: bỏ (R)/(TM)/"CPU @ x GHz".</summary>
    private static (string Short, string Full) CpuName()
    {
        var full = "";
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                full = (key?.GetValue("ProcessorNameString") as string ?? "").Trim();
            }
            catch (Exception)
            {
            }
        }

        if (full.Length == 0) return ("CPU · " + Loc.F("Sys.Threads", Environment.ProcessorCount), "CPU");
        var s = Regex.Replace(full, @"\((R|TM|tm|r)\)|®|™", "");
        s = Regex.Replace(s, @"\s+(CPU\s*)?@\s*[\d.]+\s*GHz", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(\d+-Core|\w+-Core)\s+Processor$|\s+Processor$|\s+CPU$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return (s, full);
    }

    private Meter MakeMeter(TextBlock label, out StackPanel box)
    {
        label.FontSize = 9.5;
        label.FontWeight = FontWeight.Bold;
        label.Foreground = Faint;
        label.VerticalAlignment = VerticalAlignment.Center;
        var value = new TextBlock { Text = "—", FontFamily = Mono, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Right };
        var grid = new Grid();
        grid.Children.Add(label);
        grid.Children.Add(value);
        var bar = new ProgressBar { Height = 4, MinHeight = 4, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 3, 0, 0), Background = Track, Foreground = Ok, CornerRadius = new CornerRadius(2) };
        box = new StackPanel { Children = { grid, bar } };
        return new Meter(value, bar);
    }

    private void Set(Meter m, string text, double percent, IBrush tone)
    {
        if (m.Value.Text != text) m.Value.Text = text;
        m.Bar.Value = Math.Clamp(percent, 0, 100);
        m.Bar.Foreground = tone;
        m.Value.Foreground = tone == Ok ? Ink : tone;
    }

    private static IBrush Tone(double value, double warn, double danger) => value >= danger ? Danger : value >= warn ? Warn : Ok;

    private static string Rate(double bps) => bps >= 1024 * 1024 ? $"{bps / 1048576:0} MB/s" : bps >= 1024 ? $"{bps / 1024:0} KB/s" : "0";

    private void Update(SystemSample? s)
    {
        if (s == null)
        {
            Set(_cpuLoad, "—", 0, Ok);
            Set(_cpuTemp, "—", 0, Ok);
            return;
        }

        Set(_cpuLoad, $"{s.CpuPercent:0} %", s.CpuPercent, Tone(s.CpuPercent, 75, 92));
        if (s.CpuTempSource != _tempSource)
        {
            _tempSource = s.CpuTempSource;
            ToolTip.SetTip(_tempBox, Loc.T(_tempSource switch
            {
                "ASUS" => "Sys.TempAsus",
                "ACPI" => "Sys.TempAcpi",
                "ACPI-static" => "Sys.TempStatic",
                _ => "Sys.TempNone"
            }));
        }

        if (s.CpuTempC is { } ct) Set(_cpuTemp, $"{ct} °C", ct, Tone(ct, 85, 95));
        else Set(_cpuTemp, "—", 0, Ok);

        var key = string.Join(",", s.Disks.Select(d => $"{d.Number}:{d.Letters}:{d.Model}"));
        if (key != _diskKey)
        {
            _diskKey = key;
            Rebuild(s.Disks);
        }

        foreach (var d in s.Disks)
        {
            if (!_rows.TryGetValue(d.Number, out var r)) continue;
            var warn = d.WarnC ?? 70;
            var tempText = d.TempC is { } t ? $"{t} °C" : "— °C";
            if (r.Temp.Text != tempText) r.Temp.Text = tempText;
            r.Temp.Foreground = d.TempC is { } tc ? (tc >= warn ? Danger : tc >= warn - 8 ? Warn : Ink) : Faint;
            r.Busy.Value = d.BusyPercent;
            r.Busy.Foreground = d.BusyPercent >= 95 ? Warn : Ok;
            var active = d.ReadBps + d.WriteBps > 1024 * 1024;
            var text = $"{d.BusyPercent,3:0}%  ↓{Rate(d.ReadBps)} ↑{Rate(d.WriteBps)}";
            if (r.Speed.Text != text) r.Speed.Text = text;
            r.Speed.Foreground = active ? Accent : Muted;
            r.Name.Foreground = active ? Accent : Ink;
        }
    }

    private void Rebuild(IReadOnlyList<DiskSample> disks)
    {
        _disks.Children.Clear();
        _rows.Clear();
        foreach (var d in disks)
        {
            var name = new TextBlock { Text = d.Letters.Length > 0 ? d.Letters : Loc.F("Sys.Disk", d.Number), FontFamily = Mono, FontWeight = FontWeight.Bold, FontSize = 11.5, Foreground = Ink, Margin = new Thickness(0, 0, 6, 0) };
            var temp = new TextBlock { FontFamily = Mono, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 0, 0, 0) };
            var model = new TextBlock { Text = (d.Kind.Length > 0 ? d.Kind + " · " : "") + d.Model, FontSize = 10.5, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var title = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
            DockPanel.SetDock(name, Dock.Left);
            DockPanel.SetDock(temp, Dock.Right);
            title.Children.Add(name);
            title.Children.Add(temp);
            title.Children.Add(model);

            var busy = new ProgressBar { Height = 3, MinHeight = 3, Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, Background = Track, Foreground = Ok, CornerRadius = new CornerRadius(2) };
            var speed = new TextBlock { FontFamily = Mono, FontSize = 10, Foreground = Muted, Margin = new Thickness(8, 0, 0, 0) };
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(speed, 1);
            line.Children.Add(busy);
            line.Children.Add(speed);
            var block = new StackPanel { Children = { title, line } };
            ToolTip.SetTip(block, Loc.F("Sys.DiskTip", d.Number, d.Model, d.Kind.Length > 0 ? "(" + d.Kind + ")" : "",
                d.WarnC is { } w ? Loc.F("Sys.DiskWarn", w) : ""));
            _disks.Children.Add(block);
            _rows[d.Number] = new DiskRow(name, temp, busy, speed);
        }
    }
}
