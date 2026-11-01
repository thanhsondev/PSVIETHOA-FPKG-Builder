using System.Globalization;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Ps4;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Cli;

/// <summary>Các lệnh PS4 PKG: ps4-info, ps4-list, ps4-extract, ps4-base, ps4-patch, ps4-dlc, ps4-merge.</summary>
internal static class Ps4Commands
{
    /// <summary>ps4-info &lt;tệp.pkg | thư mục game&gt; [--passcode X]</summary>
    public static int Info(Arguments arguments)
    {
        if (!TryGetSource(arguments, out var source))
        {
            return 1;
        }

        var passcode = arguments.Get("passcode") ?? Ps4Defaults.Passcode;
        if (File.Exists(source))
        {
            var info = Ps4PackageReader.Inspect(source, passcode);
            Print("Ps4.Info.Title", info.Title);
            Print("Ps4.Info.TitleId", info.TitleId);
            Print("Ps4.Info.ContentId", info.ContentId);
            Print("Ps4.Info.Category", info.Category);
            Print("Ps4.Info.Version", info.Version);
            Print("Ps4.Info.MasterVersion", info.MasterVersion);
            Print("Ps4.Info.Size", Formatters.SizeWithBytes(info.Bytes));
            Print("Ps4.Info.Passcode", Loc.T(info.PasscodeMatches ? "Cli.Yes" : "Cli.No"));
            Print("Ps4.Info.FakeKey", Loc.T(info.RecoverableFakePackage ? "Cli.Yes" : "Cli.No"));
            return 0;
        }

        var folder = Ps4ProjectBuilder.InspectFolder(source);
        Print("Ps4.Info.Title", folder["TITLE"]);
        Print("Ps4.Info.TitleId", folder["TITLE_ID"]);
        Print("Ps4.Info.ContentId", folder["CONTENT_ID"]);
        Print("Ps4.Info.Category", folder["CATEGORY"]);
        Print("Ps4.Info.Version", folder["APP_VER"]);
        Console.WriteLine("  " + folder["VERSION_NOTE"]);
        return 0;
    }

    /// <summary>ps4-list &lt;tệp.pkg&gt; [--passcode X]</summary>
    public static int List(Arguments arguments)
    {
        if (!TryGetSource(arguments, out var source) || !RequireFile(source))
        {
            return 1;
        }

        var files = Ps4PackageReader.ListFiles(source, arguments.Get("passcode") ?? Ps4Defaults.Passcode);
        foreach (var file in files)
        {
            Console.WriteLine($"{file.Bytes.ToString("N0", CultureInfo.InvariantCulture),16}  {(file.IsMetadata ? "sce_sys" : "pfs"),-7}  {file.Path}");
        }

        Console.WriteLine(Loc.F("Ps4.Cli.ListSummary", files.Count, Formatters.SizeWithBytes(files.Sum(f => f.Bytes))));
        return 0;
    }

    public static Task<int> ExtractAsync(Arguments arguments) => RunAsync(arguments, Ps4JobKind.Extract);

    public static Task<int> BaseAsync(Arguments arguments) => RunAsync(arguments, Ps4JobKind.Base);

    public static Task<int> PatchAsync(Arguments arguments) => RunAsync(arguments, Ps4JobKind.Patch);

    public static Task<int> DlcAsync(Arguments arguments) => RunAsync(arguments, Ps4JobKind.Dlc);

    public static Task<int> MergeAsync(Arguments arguments) => RunAsync(arguments, Ps4JobKind.Merge);

    private static async Task<int> RunAsync(Arguments arguments, Ps4JobKind kind)
    {
        if (!TryGetSource(arguments, out var source))
        {
            return 1;
        }

        if (kind == Ps4JobKind.Extract && !RequireFile(source))
        {
            return 1;
        }

        if (kind is Ps4JobKind.Base or Ps4JobKind.Patch or Ps4JobKind.Dlc && File.Exists(source))
        {
            Console.Error.WriteLine(Loc.F("Ps4.Project.SourceIsPackage", Path.GetFullPath(source)));
            return 1;
        }

        var output = arguments.Get("output", "o");
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine(Loc.T("Ps4.Cli.NeedOutput"));
            return 1;
        }

        var basePackage = arguments.Get("base") ?? string.Empty;
        if (kind is Ps4JobKind.Patch or Ps4JobKind.Merge && string.IsNullOrWhiteSpace(basePackage))
        {
            Console.Error.WriteLine(Loc.T("Ps4.Cli.NeedBase"));
            return 1;
        }

        var request = new Ps4JobRequest(
            kind,
            Path.GetFullPath(source),
            Path.GetFullPath(output),
            string.IsNullOrWhiteSpace(basePackage) ? string.Empty : Path.GetFullPath(basePackage),
            arguments.Get("passcode") ?? Ps4Defaults.Passcode,
            arguments.Get("version") ?? string.Empty,
            arguments.Has("verify"),
            arguments.Has("gp4-only"),
            arguments.Get("engine") ?? string.Empty,
            arguments.GetAll("only").ToArray() is { Length: > 0 } only ? only : null,
            arguments.Get("entitlement-key") ?? string.Empty,
            arguments.Get("base-passcode") ?? string.Empty,
            arguments.Get("sfo") ?? string.Empty);

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        var quiet = arguments.Has("quiet", "q");
        var lastLine = 0;
        var lastDraw = DateTime.MinValue;
        try
        {
            var result = await Ps4JobRunner.RunAsync(request,
                (line, _) =>
                {
                    if (quiet)
                    {
                        return;
                    }

                    ClearProgress(ref lastLine);
                    Console.WriteLine(line);
                },
                progress =>
                {
                    if (quiet || Console.IsErrorRedirected || (DateTime.Now - lastDraw).TotalMilliseconds < 250)
                    {
                        return;
                    }

                    lastDraw = DateTime.Now;
                    var speed = progress.BytesPerSecond > 0 ? " · " + Formatters.Rate(progress.BytesPerSecond) : string.Empty;
                    var text = $"{progress.Percent,5:0.0}% {progress.Phase}{speed}";
                    ClearProgress(ref lastLine);
                    Console.Error.Write(text);
                    lastLine = text.Length;
                },
                cancellation.Token).ConfigureAwait(false);
            ClearProgress(ref lastLine);
            Console.WriteLine();
            Console.WriteLine(result.Package != null
                ? Loc.F("Ps4.Cli.Built", result.Package, Formatters.SizeWithBytes(result.Bytes))
                : result.Project != null
                    ? Loc.F("Ps4.Cli.ProjectReady", result.Project)
                    : Loc.F("Ps4.Cli.Extracted", result.Directory, result.FileCount, Formatters.SizeWithBytes(result.Bytes)));
            Console.WriteLine(Loc.F("Ps4.Cli.Log", result.Log));
            return 0;
        }
        catch (Ps4JobFailureException ex) when (ex.InnerException is OperationCanceledException || cancellation.IsCancellationRequested)
        {
            ClearProgress(ref lastLine);
            Console.Error.WriteLine(Loc.F("Ps4.Cli.Stopped", ex.Folder));
            return 3;
        }
        catch (Ps4JobFailureException ex)
        {
            ClearProgress(ref lastLine);
            Console.Error.WriteLine(Loc.F("Cli.Error", ex.Message));
            Console.Error.WriteLine(Loc.F("Ps4.Cli.Kept", ex.Folder));
            if (Environment.GetEnvironmentVariable("FPKG_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.InnerException);
            }

            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static void ClearProgress(ref int length)
    {
        if (length <= 0 || Console.IsErrorRedirected)
        {
            return;
        }

        Console.Error.Write("\r" + new string(' ', length) + "\r");
        length = 0;
    }

    private static void Print(string key, string value) => Console.WriteLine($"  {Loc.T(key),-26} {value}");

    private static bool TryGetSource(Arguments arguments, out string source)
    {
        source = arguments.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
        if (source.Length == 0)
        {
            Console.Error.WriteLine(Loc.T("Ps4.Cli.NeedSource"));
            return false;
        }

        if (!File.Exists(source) && !Directory.Exists(source))
        {
            Console.Error.WriteLine(Loc.F("Ps4.Cli.SourceMissing", source));
            return false;
        }

        return true;
    }

    private static bool RequireFile(string source)
    {
        if (File.Exists(source))
        {
            return true;
        }

        Console.Error.WriteLine(Loc.T("Ps4.Cli.NeedPkg"));
        return false;
    }
}
