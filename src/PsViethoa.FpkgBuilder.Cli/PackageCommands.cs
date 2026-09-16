using System.Diagnostics;
using System.Text.RegularExpressions;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Cli;

/// <summary>Các lệnh đọc/giải nén gói: pkg-info, pkg-list, pkg-extract.</summary>
internal static class PackageCommands
{
    private static string DefaultPasscode => new('0', BuildRequest.PasscodeLength);

    /// <summary>pkg-info &lt;tệp.pkg&gt; [--passcode X] [--sha256]</summary>
    public static int Info(Arguments arguments)
    {
        if (!TryGetPackage(arguments, out var path))
        {
            return 1;
        }

        var info = PackageInspector.Inspect(path, arguments.Get("passcode") ?? DefaultPasscode, CancellationToken.None);
        string? sha256 = null;
        if (arguments.Has("sha256"))
        {
            sha256 = PackageInspector.ComputeSha256(path, CancellationToken.None);
        }

        Console.Write(PackageInspector.Describe(info, sha256));
        if (!info.CanExtract)
        {
            Console.WriteLine();
            Console.WriteLine(info.ExtractBlockedReason);
        }

        return 0;
    }

    /// <summary>pkg-dlc-template &lt;tệp.pkg&gt; [--output dir] [--passcode X] — xuất mẫu DLC (sce_sys + dự án .gp5) từ gói DLC có dữ liệu.</summary>
    public static int DlcTemplate(Arguments arguments)
    {
        if (!TryGetPackage(arguments, out var path))
        {
            return 1;
        }

        var output = arguments.Get("output", "o") ?? PackageReader.SuggestDlcTemplateFolder(path);
        var files = PackageReader.ExportDlcTemplate(path, output, arguments.Get("passcode") ?? DefaultPasscode, CancellationToken.None);
        foreach (var file in files)
        {
            Console.WriteLine("  " + file);
        }

        var gp5 = files.FirstOrDefault(file => file.EndsWith(".gp5", StringComparison.OrdinalIgnoreCase)) ?? "—";
        Console.WriteLine(Loc.F("DlcTemplate.Done", files.Count, gp5) + " " + Path.GetFullPath(output));
        return 0;
    }

    /// <summary>pkg-list &lt;tệp.pkg&gt; [--passcode X] [--temp dir] [--include glob]…</summary>
    public static int List(Arguments arguments)
    {
        if (!TryGetPackage(arguments, out var path))
        {
            return 1;
        }

        using var reader = PackageReader.Open(path, arguments.Get("passcode") ?? DefaultPasscode, arguments.Get("temp"), CancellationToken.None, Console.Error.WriteLine);
        var patterns = ReadPatterns(arguments);
        var entries = Select(reader.Entries, patterns);
        var files = entries.Where(e => !e.IsDirectory).ToList();
        Console.WriteLine(Loc.F("Cli.PkgListHeader", Path.GetFullPath(path), reader.ImageMode == PackageImageMode.PlaintextNoAuth ? Loc.T("Extract.ModePlain") : Loc.T("Extract.ModeNative"), Formatters.Size(reader.LogicalImageSize)));
        Console.WriteLine(Loc.F("Cli.PkgEntries", Formatters.Count(files.Count), Formatters.Count(entries.Count(e => e.IsDirectory)), Formatters.SizeWithBytes(files.Sum(e => e.Size))));
        Console.WriteLine();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                Console.WriteLine($"{"",16}  {"",-11}  {entry.Path}/");
            }
            else
            {
                Console.WriteLine($"{entry.Size,16:N0}  {entry.CompressionLabel,-11}  {entry.Path}");
            }
        }

        return 0;
    }

    /// <summary>pkg-extract &lt;tệp.pkg&gt; --output &lt;dir&gt; [--include glob]… [--cnt] [--no-sce-sys] [--passcode X] [--threads N] [--temp dir]</summary>
    public static int Extract(Arguments arguments)
    {
        if (!TryGetPackage(arguments, out var path))
        {
            return 1;
        }

        var output = arguments.Get("output", "o");
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine(Loc.T("Cli.PkgNeedOutput"));
            return 1;
        }

        var passcode = arguments.Get("passcode") ?? DefaultPasscode;
        var threads = arguments.GetInt("threads", "j") ?? 0;
        var quiet = arguments.Has("quiet", "q");

        var info = PackageInspector.Inspect(path, passcode, CancellationToken.None);
        if (!info.CanExtract)
        {
            Console.Error.WriteLine(info.ExtractBlockedReason);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (!cancellation.IsCancellationRequested)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(Loc.T("Cli.CancelRequested"));
                cancellation.Cancel();
            }
        };

        var outputRoot = Path.GetFullPath(output);
        if (arguments.Has("cnt"))
        {
            // Cùng bố cục với giao diện: cnt/ và si/ nằm cạnh cây ứng dụng, không ghi đè sce_sys/ của ảnh trong.
            var cntFolder = Path.Combine(outputRoot, "cnt");
            var cnt = PackageReader.ExportCntEntries(path, cntFolder, passcode, cancellation.Token);
            Console.WriteLine(Loc.F("Cli.PkgCntExported", cnt.Count, cntFolder));
            var si = PackageReader.ExportSiEntries(path, Path.Combine(outputRoot, "si"), cancellation.Token);
            if (si.Count > 0)
            {
                Console.WriteLine(Loc.F("Cli.PkgSiExported", si.Count, Path.Combine(outputRoot, "si")));
            }
        }

        using var reader = PackageReader.Open(path, passcode, arguments.Get("temp"), cancellation.Token, quiet ? null : Console.Error.WriteLine);
        var patterns = ReadPatterns(arguments);
        var selected = Select(reader.Entries, patterns);
        var files = selected.Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0 && selected.Count == 0)
        {
            Console.Error.WriteLine(Loc.T("Cli.PkgNoMatch"));
            return 1;
        }

        Console.WriteLine(Loc.F("Cli.PkgExtracting", Formatters.Count(files.Count), Formatters.Size(files.Sum(e => e.Size)), outputRoot));
        var renderer = new ConsoleExtractRenderer(quiet);
        var result = reader.Extract(selected, outputRoot, renderer, cancellation.Token, threads);
        renderer.Finish();
        foreach (var warning in result.Warnings)
        {
            Console.WriteLine(Loc.F("Cli.Warning", warning));
        }

        Console.WriteLine(Loc.F("Cli.PkgExtracted", Formatters.Count(result.FileCount), Formatters.SizeWithBytes(result.TotalBytes), Formatters.Duration(result.Elapsed), result.OutputFolder));
        if (!arguments.Has("no-sce-sys") && info.Cnt != null)
        {
            // Bố cục Sony: cây ứng dụng + sce_sys/param.json, icon0.png, playgo… từ CNT → dùng lại được làm nguồn tạo gói.
            var sceSys = PackageReader.ExportSceSys(path, outputRoot, passcode, cancellation.Token);
            Console.WriteLine(Loc.F("Cli.PkgSceSysExported", sceSys.Count, Path.Combine(outputRoot, "sce_sys")));
        }
        return 0;
    }

    private static bool TryGetPackage(Arguments arguments, out string path)
    {
        path = arguments.Positionals.Skip(1).FirstOrDefault() ?? arguments.Get("package", "p") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.Error.WriteLine(Loc.T("Cli.NeedPkg"));
            return false;
        }

        return true;
    }

    private static IReadOnlyList<Regex> ReadPatterns(Arguments arguments)
    {
        var patterns = new List<Regex>();
        foreach (var raw in arguments.GetAll("include", "i"))
        {
            foreach (var piece in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                patterns.Add(GlobToRegex(piece));
            }
        }

        return patterns;
    }

    /// <summary>Chọn mục theo các mẫu glob (không có mẫu = tất cả). Thư mục được giữ khi có tệp con khớp.</summary>
    internal static List<PackageEntry> Select(IReadOnlyList<PackageEntry> entries, IReadOnlyList<Regex> patterns)
    {
        if (patterns.Count == 0)
        {
            return entries.ToList();
        }

        var files = entries.Where(e => !e.IsDirectory && patterns.Any(p => p.IsMatch(e.Path))).ToList();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var slash = file.Path.LastIndexOf('/');
            while (slash > 0)
            {
                directories.Add(file.Path[..slash]);
                slash = file.Path.LastIndexOf('/', slash - 1);
            }
        }

        var result = entries.Where(e => e.IsDirectory ? directories.Contains(e.Path) || patterns.Any(p => p.IsMatch(e.Path)) : files.Contains(e)).ToList();
        return result;
    }

    /// <summary>Glob → regex: ** = mọi thứ kể cả '/', * = trong một đoạn, ? = một ký tự. Không có '/' → khớp theo tên tệp ở mọi cấp.</summary>
    internal static Regex GlobToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/').TrimStart('/');
        var anywhere = !normalized.Contains('/');
        var pattern = new System.Text.StringBuilder(anywhere ? "^(?:.*/)?" : "^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '*')
            {
                if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                {
                    pattern.Append(".*");
                    i++;
                    if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                    {
                        i++;
                        pattern.Append("(?:/)?");
                    }
                }
                else
                {
                    pattern.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>Thanh tiến độ giải nén một dòng trong terminal.</summary>
internal sealed class ConsoleExtractRenderer : IProgress<ExtractProgress>
{
    private readonly bool _quiet;
    private readonly bool _interactive;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Stopwatch _lastDraw = Stopwatch.StartNew();
    private readonly object _gate = new();
    private string _lastLine = string.Empty;
    private int _lastPercent = -1;

    public ConsoleExtractRenderer(bool quiet)
    {
        _quiet = quiet;
        _interactive = !Console.IsOutputRedirected;
    }

    public void Report(ExtractProgress value)
    {
        if (_quiet)
        {
            return;
        }

        lock (_gate)
        {
            var percent = value.TotalBytes <= 0 ? 100.0 : value.DoneBytes * 100.0 / value.TotalBytes;
            if (_interactive)
            {
                if (_lastDraw.ElapsedMilliseconds < 120 && value.DoneFiles < value.TotalFiles)
                {
                    return;
                }

                var text = Loc.F("Cli.PkgProgress", Bar(percent), percent, value.DoneFiles, value.TotalFiles, Formatters.Size(value.DoneBytes), Formatters.Size(value.TotalBytes), Formatters.Clock(_elapsed.Elapsed), value.CurrentFile ?? string.Empty);
                var width = Math.Max(20, SafeWindowWidth() - 1);
                if (text.Length > width)
                {
                    text = text[..width];
                }

                Console.Write('\r' + text.PadRight(_lastLine.Length));
                _lastLine = text;
                _lastDraw.Restart();
            }
            else
            {
                var step = (int)percent / 10 * 10;
                if (step != _lastPercent)
                {
                    _lastPercent = step;
                    Console.WriteLine(Loc.F("Cli.PkgProgress", Bar(percent), percent, value.DoneFiles, value.TotalFiles, Formatters.Size(value.DoneBytes), Formatters.Size(value.TotalBytes), Formatters.Clock(_elapsed.Elapsed), string.Empty));
                }
            }
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            if (_interactive && _lastLine.Length > 0)
            {
                Console.Write('\r' + new string(' ', _lastLine.Length) + '\r');
                _lastLine = string.Empty;
            }
        }
    }

    private static string Bar(double percent)
    {
        const int width = 24;
        var filled = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * width);
        return new string('█', filled) + new string('░', width - filled);
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (Exception)
        {
            return 100;
        }
    }
}
