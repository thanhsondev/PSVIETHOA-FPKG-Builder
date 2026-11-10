using System.Globalization;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;
using PsViethoa.FpkgBuilder.Core.Zstd;

namespace PsViethoa.FpkgBuilder.Cli;

/// <summary>
/// Gói ZSTD PS5 (beta, định dạng ps5-dump-forge, runtime ampr-emu-ng): zstd-info, zstd-list, zstd-pack, zstd-unpack,
/// zstd-reindex, zstd-patch, zstd-undo.
/// </summary>
internal static class ZstdCommands
{
    /// <summary>zstd-info &lt;thư mục game&gt;</summary>
    public static int Info(Arguments arguments) => Run(arguments, info => 0);

    /// <summary>zstd-list &lt;thư mục game&gt;: một dòng mỗi tệp — Z (trong gói) / - (rời), cỡ, đường dẫn.</summary>
    public static int List(Arguments arguments) => Run(arguments, info =>
    {
        foreach (var entry in info.Entries())
        {
            Console.WriteLine($"{(entry.Kind == AmprZstd.EntryKind.Packed ? "Z" : "-")}\t{entry.Size}\t{entry.Path}");
        }

        return 0;
    });

    /// <summary>zstd-reindex &lt;thư mục game đã đóng&gt;: ghi lại ampr_emu.index theo tệp thật (bản cũ cạnh thư mục game).</summary>
    public static int Reindex(Arguments arguments) => Run(arguments, info =>
    {
        if (info.EmuIssues.Count == 0)
        {
            Console.WriteLine(Loc.T("Zstd.Cli.ReindexNotNeeded"));
            return 0;
        }

        AmprZstd.RepairEmuIndex(arguments.Positionals[1], Log);
        return 0;
    });

    /// <summary>zstd-pack &lt;thư mục game&gt; --output &lt;thư mục mới&gt; [--level 1..22] [--threads N] [--verify]</summary>
    public static int Pack(Arguments arguments)
    {
        if (!TrySource(arguments, out var source) || !TryOutput(arguments, out var output))
        {
            return 1;
        }

        using var cancellation = Cancellation();
        var result = AmprZstd.Pack(source, output,
            new AmprZstd.PackOptions(Int(arguments, "level", AmprZstd.DefaultLevel), Int(arguments, "threads", 0), arguments.Has("verify")),
            ProgressPrinter(5), cancellation.Token, Log);
        Console.WriteLine(Loc.F("Zstd.Cli.PackResult", result.Folder, result.PackedFiles, result.LooseFiles, result.Volumes, result.Chunks, result.ZstdChunks,
            Formatters.Size(result.Logical), Formatters.Size(result.Stored), result.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)));
        return 0;
    }

    /// <summary>zstd-unpack &lt;thư mục đã đóng&gt; --output &lt;thư mục&gt; [--keep-runtime] [--file a/b.bin]... [--threads N]</summary>
    public static int Unpack(Arguments arguments)
    {
        if (!TrySource(arguments, out var source) || !TryOutput(arguments, out var output))
        {
            return 1;
        }

        var info = AmprZstd.Read(source);
        PrintInfo(info);
        var only = arguments.GetAll("file").Count > 0 ? arguments.GetAll("file").ToHashSet(StringComparer.Ordinal) : null;
        using var cancellation = Cancellation();
        var result = AmprZstd.Unpack(info, output, only, new AmprZstd.UnpackOptions(!arguments.Has("keep-runtime"), Int(arguments, "threads", 0)),
            ProgressPrinter(5), cancellation.Token, Log);
        Console.WriteLine(Loc.F("Zstd.Cli.UnpackResult", result.Folder, result.Files, Formatters.Size(result.Bytes),
            result.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)));
        return 0;
    }

    /// <summary>zstd-patch &lt;game đã đóng&gt; &lt;thư mục vá cùng cấu trúc&gt; [--level N] [--plan]</summary>
    public static int Patch(Arguments arguments)
    {
        if (arguments.Positionals.Count < 3)
        {
            Console.Error.WriteLine(Loc.T("Zstd.Cli.PatchUsage"));
            return 1;
        }

        using var cancellation = Cancellation();
        var info = AmprZstd.Read(arguments.Positionals[1]);
        var plan = AmprZstd.PlanPatch(info, arguments.Positionals[2], Int(arguments, "level", AmprZstd.DefaultLevel), cancellation.Token);
        foreach (var item in plan.Items)
        {
            Console.WriteLine($"PLAN {item.Kind,-12} {item.Size,14:N0} {item.Path} | {item.Note}");
        }

        foreach (var skipped in plan.Skipped)
        {
            Console.WriteLine("SKIP " + skipped);
        }

        Console.WriteLine(Loc.F("Zstd.Cli.PlanSummary",
            plan.Count(AmprZstd.PatchKind.LooseReplace) + plan.Count(AmprZstd.PatchKind.LooseNew),
            plan.Count(AmprZstd.PatchKind.InPlace), plan.Count(AmprZstd.PatchKind.Rewrite), Formatters.Size(plan.RewriteCopyBytes)));
        if (arguments.Has("plan"))
        {
            return 0;
        }

        var result = AmprZstd.Patch(plan, ProgressPrinter(10), cancellation.Token, Log);
        Console.WriteLine(Loc.F("Zstd.Cli.PatchResult", result.Folder, result.Files, result.InPlace, result.Rewritten, result.Loose,
            Formatters.Size(result.CopiedBytes), result.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture), result.UndoDir));
        return 0;
    }

    /// <summary>zstd-undo &lt;thư mục hoàn tác | thư mục game (lần vá mới nhất)&gt;</summary>
    public static int Undo(Arguments arguments)
    {
        if (!TrySource(arguments, out var source))
        {
            return 1;
        }

        var dir = Directory.Exists(Path.Combine(source, "volumes")) || File.Exists(Path.Combine(source, "undo.json"))
            ? source
            : AmprZstd.UndoFolders(source).FirstOrDefault();
        if (dir == null)
        {
            Console.Error.WriteLine(Loc.F("Zstd.Cli.NoUndo", source));
            return 1;
        }

        AmprZstd.Undo(dir, Log);
        return 0;
    }

    private static int Run(Arguments arguments, Func<AmprZstd.Info, int> then)
    {
        if (!TrySource(arguments, out var source))
        {
            return 1;
        }

        var info = AmprZstd.Read(source);
        PrintInfo(info);
        return then(info);
    }

    private static void PrintInfo(AmprZstd.Info info)
    {
        Console.WriteLine(Loc.F("Zstd.Cli.Game", info.TitleId, info.Title, info.ContentVersion,
            Loc.T(info.Packed ? "Zstd.Cli.Packed" : "Zstd.Cli.NotPacked"), info.RuntimeNote));
        if (info.Index is { } index)
        {
            Console.WriteLine(Loc.F("Zstd.Cli.Pack", index.Level, index.Files.Count, index.Chunks.Count, info.ZstdChunks, index.VolumeSizes.Count,
                Formatters.Size(info.PackedLogical), Formatters.Size(info.PackedStored)));
        }

        Console.WriteLine(Loc.F("Zstd.Cli.Loose", info.LooseCount, Formatters.Size(info.LooseBytes), Formatters.Size(info.DiskBytes)));
        if (info.Problem.Length > 0)
        {
            Console.WriteLine("! " + info.Problem);
        }

        if (info.EmuSummary.Length > 0)
        {
            Console.WriteLine(Loc.F("Zstd.Cli.EmuStale", info.EmuSummary));
            foreach (var issue in info.EmuIssues.Take(30))
            {
                Console.WriteLine("  " + issue);
            }
        }
    }

    private static bool TrySource(Arguments arguments, out string source)
    {
        source = arguments.Positionals.Skip(1).FirstOrDefault() ?? arguments.Get("source", "s") ?? string.Empty;
        if (Directory.Exists(source))
        {
            return true;
        }

        Console.Error.WriteLine(Loc.T("Zstd.Cli.NeedFolder"));
        return false;
    }

    private static bool TryOutput(Arguments arguments, out string output)
    {
        output = arguments.Get("output", "o") ?? string.Empty;
        if (output.Length > 0)
        {
            return true;
        }

        Console.Error.WriteLine(Loc.T("Zstd.Cli.NeedOutput"));
        return false;
    }

    private static int Int(Arguments arguments, string name, int fallback) =>
        int.TryParse(arguments.Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static void Log(string line) => Console.WriteLine(line);

    private static CancellationTokenSource Cancellation()
    {
        var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        return cancellation;
    }

    private static IProgress<AmprZstd.Progress> ProgressPrinter(double step)
    {
        var last = -1.0;
        var gate = new object();
        return new SyncProgress<AmprZstd.Progress>(p =>
        {
            lock (gate)
            {
                if (last < 0 || p.Percent >= last + step || p.Percent >= 100)
                {
                    Console.WriteLine($"{p.Percent.ToString("F1", CultureInfo.InvariantCulture)}% | {Formatters.Size((long)p.BytesPerSecond)}/s | {p.Detail}");
                    last = p.Percent;
                }
            }
        });
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
