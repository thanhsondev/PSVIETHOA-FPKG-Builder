using System.Diagnostics;
using System.Globalization;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Cli;

/// <summary>Bộ phân tích tham số dòng lệnh tối giản, không phụ thuộc thư viện ngoài.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    // Mọi giá trị của một tuỳ chọn lặp lại (ví dụ --include a --include b).
    private readonly Dictionary<string, List<string>> _multi = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = new();

    /// <summary>Tất cả giá trị của tuỳ chọn (theo thứ tự xuất hiện), rỗng nếu không có.</summary>
    public IReadOnlyList<string> GetAll(params string[] names)
    {
        var values = new List<string>();
        foreach (var name in names)
        {
            if (_multi.TryGetValue(name, out var list))
            {
                values.AddRange(list);
            }
        }

        return values;
    }

    private void Set(string name, string? value)
    {
        _options[name] = value;
        if (value != null)
        {
            if (!_multi.TryGetValue(name, out var list))
            {
                _multi[name] = list = new List<string>();
            }

            list.Add(value);
        }
    }

    public static Arguments Parse(IEnumerable<string> args)
    {
        var result = new Arguments();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var token = list[i];
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                var body = token[2..];
                var eq = body.IndexOf('=');
                if (eq > 0)
                {
                    result.Set(body[..eq], body[(eq + 1)..]);
                }
                else if (i + 1 < list.Count && IsValueToken(list[i + 1]))
                {
                    result.Set(body, list[++i]);
                }
                else
                {
                    result.Set(body, null);
                }
            }
            else if (token.StartsWith('-') && token.Length == 2 && !IsNumber(token))
            {
                var key = token[1..];
                if (i + 1 < list.Count && IsValueToken(list[i + 1]))
                {
                    result.Set(key, list[++i]);
                }
                else
                {
                    result.Set(key, null);
                }
            }
            else
            {
                result.Positionals.Add(token);
            }
        }

        return result;
    }

    /// <summary>Token đứng sau một tuỳ chọn là giá trị nếu không bắt đầu bằng '-' — trừ số âm (ví dụ --level -2, --shuffle-prediction-level -4).</summary>
    private static bool IsValueToken(string token) => !token.StartsWith('-') || IsNumber(token);

    private static bool IsNumber(string token) =>
        token.Length > 1 && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    public bool Has(params string[] names) => names.Any(_options.ContainsKey);

    public string? Get(params string[] names)
    {
        foreach (var name in names)
        {
            if (_options.TryGetValue(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    public int? GetInt(params string[] names) =>
        int.TryParse(Get(names), out var value) ? value : null;
}

internal static class CommandLine
{
    public static async Task<int> RunAsync(string[] args)
    {
        var arguments = Arguments.Parse(args);
        Loc.Current.SetLanguage(arguments.Get("lang") ?? Environment.GetEnvironmentVariable("FPKG_LANG") ?? Loc.DetectSystemLanguage());

        var command = arguments.Positionals.FirstOrDefault()?.ToLowerInvariant();
        if (command == null || arguments.Has("help", "h") || command is "help")
        {
            Console.WriteLine(Loc.T("Cli.Help"));
            Console.WriteLine();
            Console.WriteLine(Loc.T("Cli.Credits"));
            return command == null && !arguments.Has("help", "h") ? 1 : 0;
        }

        try
        {
            return command switch
            {
                "build" => await BuildAsync(arguments),
                "inspect" => Inspect(arguments),
                "install-dokan" => InstallDokan(),
                "dlc-from-ini" => DlcFromIni(arguments),
                "verify" => Verify(arguments),
                "clean-junk" => CleanJunk(arguments),
                "info" => Info(),
                "check-update" => CheckUpdate(),
                "pkg-info" => PackageCommands.Info(arguments),
                "pkg-list" => PackageCommands.List(arguments),
                "pkg-extract" => PackageCommands.Extract(arguments),
                "pkg-dlc-template" => PackageCommands.DlcTemplate(arguments),
                _ => Unknown(command),
            };
        }
        catch (BuildValidationException ex)
        {
            Console.Error.WriteLine(Loc.T("Cli.InvalidArgs"));
            foreach (var error in ex.Errors)
            {
                Console.Error.WriteLine($"  - [{error.Field}] {error.Message}");
            }

            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(Loc.T("Cli.Canceled"));
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(Loc.F("Cli.Error", ex.Message));
            if (Environment.GetEnvironmentVariable("FPKG_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex);
            }

            return 2;
        }
    }

    /// <summary>Gói trùng tên trong thư mục xuất: --overwrite ghi đè, --keep-existing giữ bản cũ, mặc định hỏi trên console.</summary>
    private static Func<IReadOnlyList<string>, OutputConflictChoice> ConflictPrompt(Arguments arguments) => existing =>
    {
        if (arguments.Has("overwrite"))
        {
            return OutputConflictChoice.Overwrite;
        }

        if (arguments.Has("keep-existing"))
        {
            return OutputConflictChoice.KeepExisting;
        }

        Console.WriteLine(Loc.F("Cli.ConflictFound", string.Join(", ", existing.Select(Path.GetFileName))));
        if (Console.IsInputRedirected)
        {
            Console.WriteLine(Loc.T("Cli.ConflictNeedChoice"));
            return OutputConflictChoice.Overwrite;
        }

        Console.Write(Loc.T("Conflict.Overwrite") + " [o] / " + Loc.T("Conflict.Keep") + " [k] / " + Loc.T("Common.Cancel") + " [c]? ");
        var key = Console.ReadLine()?.Trim().ToLowerInvariant();
        return key switch
        {
            "k" => OutputConflictChoice.KeepExisting,
            "c" => OutputConflictChoice.Cancel,
            _ => OutputConflictChoice.Overwrite,
        };
    };

    private static int Unknown(string command)
    {
        Console.Error.WriteLine(Loc.F("Cli.Unknown", command));
        return 1;
    }

    /// <summary>check-update: hỏi GitHub Releases xem có bản mới hơn phiên bản đang chạy không.</summary>
    private static int CheckUpdate()
    {
        try
        {
            var current = typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var info = UpdateChecker.CheckAsync(current, UpdateChecker.PlatformAssetHint(), CancellationToken.None, UpdateChecker.PreferredAssetToken()).GetAwaiter().GetResult();
            Console.WriteLine(info.IsNewer
                ? Loc.F("Update.CliAvailable", info.LatestVersion, current, info.AssetUrl ?? info.ReleaseUrl)
                : Loc.F("Update.UpToDate", current, info.LatestVersion));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Loc.F("Update.Failed", ex.Message));
            return 2;
        }
    }

    private static int Info()
    {
        Console.WriteLine("PSVIETHOA FPKG Builder CLI");
        Console.WriteLine(Loc.T("Cli.Credits"));
        Console.WriteLine(Loc.F("Cli.Os", Environment.OSVersion, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture));
        Console.WriteLine(Loc.F("Cli.Runtime", Environment.Version, Environment.ProcessorCount));
        Console.WriteLine(Loc.F("Cli.Library", BuildEngine.LibraryVersion));
        Console.WriteLine(Loc.T(BuildEngine.KeysAvailable ? "Cli.KeysReady" : "Cli.KeysMissing"));
        var backend = BuildPreparer.ResolveBackend(new BuildRequest(), out var dll);
        Console.WriteLine(Loc.F("Cli.DefaultBackend", Describe(backend) + (dll != null ? " — " + dll : string.Empty)));
        Console.WriteLine(Loc.F("Cli.MountBackend", PsViethoa.FpkgBuilder.Core.ExFat.ImageMounter.BackendLabel));
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine(Loc.F("Cli.MountBundled", PsViethoa.FpkgBuilder.Core.ExFat.DokanInstaller.BundledInstallerPath ?? Loc.T("Cli.No")));
        }

        Console.WriteLine();
        Console.WriteLine(ComponentProbe.Report(ComponentProbe.Run(dll), Loc.T("Cli.ComponentsHeader")));
        return 0;
    }

    /// <summary>dlc-from-ini: tạo một gói DLC riêng cho từng mục trong dlc_emu.ini của game.</summary>
    private static int DlcFromIni(Arguments arguments)
    {
        var source = arguments.Positionals.Skip(1).FirstOrDefault() ?? arguments.Get("source", "s");
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine(Loc.T("Cli.NeedSource"));
            return 1;
        }

        var entries = DlcEmuIni.Read(source, CancellationToken.None);
        if (entries.Count == 0)
        {
            Console.Error.WriteLine(Loc.T("Cli.DlcNeedIni"));
            return 1;
        }

        var output = arguments.Get("output", "o") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".", "dlc-pkg");
        var temp = arguments.Get("temp") ?? BuildPreparer.SuggestTemporaryFolder(output);
        Console.WriteLine(Loc.T("Cli.DlcHeader"));
        foreach (var entry in entries)
        {
            Console.WriteLine($"  - {entry.ContentId} ({entry.DownloadStatus})");
        }

        var title = arguments.Get("title", "t");
        var results = DlcPackageBuilder.BuildAll(entries, output, temp, title, e => Console.WriteLine("  " + e.Message), CancellationToken.None, ConflictPrompt(arguments));
        var ok = results.Count(r => r.Success);
        Console.WriteLine(Loc.F("Cli.DlcSummary", ok, results.Count, Path.GetFullPath(output)));
        return ok == results.Count ? 0 : 2;
    }

    /// <summary>Cài driver Dokan kèm theo (Windows) để gắn ảnh .exfat/.ffpfsc không sao chép.</summary>
    private static int InstallDokan()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(Loc.T("Cli.DokanWindowsOnly"));
            return 1;
        }

        var result = PsViethoa.FpkgBuilder.Core.ExFat.DokanInstaller.Install(TimeSpan.FromMinutes(10));
        switch (result.Outcome)
        {
            case PsViethoa.FpkgBuilder.Core.ExFat.DokanInstallOutcome.Installed:
                Console.WriteLine(Loc.F("Cli.DokanInstalled", result.Detail ?? PsViethoa.FpkgBuilder.Core.ExFat.DokanInstaller.BundledVersion));
                return 0;
            case PsViethoa.FpkgBuilder.Core.ExFat.DokanInstallOutcome.AlreadyInstalled:
                Console.WriteLine(Loc.F("Cli.DokanAlready", result.Detail ?? "?"));
                return 0;
            case PsViethoa.FpkgBuilder.Core.ExFat.DokanInstallOutcome.RebootRequired:
                Console.WriteLine(Loc.T("Cli.DokanReboot"));
                return 0;
            case PsViethoa.FpkgBuilder.Core.ExFat.DokanInstallOutcome.Cancelled:
                Console.WriteLine(Loc.T("Cli.DokanCancelled"));
                return 3;
            case PsViethoa.FpkgBuilder.Core.ExFat.DokanInstallOutcome.NotBundled:
                Console.Error.WriteLine(Loc.T("Cli.DokanNotBundled"));
                return 2;
            default:
                Console.Error.WriteLine(Loc.F("Cli.DokanFailed", result.ExitCode, result.Detail ?? string.Empty));
                return 2;
        }
    }

    private static string Describe(KrakenBackendKind backend) => Loc.T(backend switch
    {
        KrakenBackendKind.PublishingTools => "Cli.BackendNative",
        KrakenBackendKind.Uncompressed => "Cli.BackendNone",
        _ => "Cli.BackendBuiltIn",
    });

    private static string YesNo(bool value, bool loud = false) => Loc.T(value ? "Cli.Yes" : loud ? "Cli.NO" : "Cli.No");

    private static int Inspect(Arguments arguments)
    {
        var source = arguments.Positionals.Skip(1).FirstOrDefault() ?? arguments.Get("source", "s");
        if (string.IsNullOrWhiteSpace(source) || SourceLocator.Detect(source) == SourceKind.None)
        {
            Console.Error.WriteLine(Loc.T("Cli.NeedSource"));
            return 1;
        }

        var metadata = MetadataReader.Read(source, CancellationToken.None);
        if (metadata.IsGp5)
        {
            Console.WriteLine(Loc.F("Cli.SourceGp5", Path.GetFullPath(source), metadata.Gp5Layout ?? "—", metadata.Gp5RootFolder ?? "—"));
        }
        else if (metadata.IsExFat)
        {
            Console.WriteLine(metadata.IsPfsContainer
                ? Loc.F("Cli.SourcePfs", Path.GetFullPath(source), metadata.ContainerEntryName ?? "—", metadata.ContainerStoredLength is { } stored ? Formatters.Size(stored) : "—", metadata.VolumeLabel ?? "—", metadata.AppRootInImage)
                : Loc.F("Cli.SourceExFat", Path.GetFullPath(source), metadata.VolumeLabel ?? "—", metadata.AppRootInImage));
        }
        else
        {
            Console.WriteLine(Loc.F("Cli.Folder", Path.GetFullPath(source)));
        }

        Console.WriteLine(Loc.F("Cli.Layout", YesNo(metadata.HasSceSys, loud: true), YesNo(metadata.HasEboot), YesNo(metadata.IconPath != null || metadata.IconBytes != null)));
        Console.WriteLine(metadata.HasParamJson
            ? Loc.F("Cli.Param", metadata.ContentId ?? "—", metadata.Version ?? "—", metadata.SdkMajor?.ToString() ?? "—", metadata.Title ?? "—")
            : Loc.T("Cli.NoParam") + (metadata.ParamJsonError != null ? " (" + metadata.ParamJsonError + ")" : string.Empty));
        Console.WriteLine(MetadataReader.DescribePlayGo(metadata, BuildRequest.DefaultPlayGoChunks, !arguments.Has("keep-playgo")));

        var stats = FolderScanner.Scan(source, CancellationToken.None);
        Console.WriteLine(Loc.F("Cli.Stats", Formatters.Count(stats.FileCount), Formatters.Count(stats.DirectoryCount), Formatters.SizeWithBytes(stats.TotalBytes), Formatters.Size(stats.LargestFileBytes), stats.ScanDuration.TotalMilliseconds.ToString("0")));

        var junk = JunkFileFinder.Find(source, CancellationToken.None);
        Console.WriteLine(junk.Count == 0 ? Loc.T("Cli.NoJunk") : Loc.F("Cli.Junk", junk.Count));
        foreach (var item in junk.Take(10))
        {
            Console.WriteLine("  - " + item.Path);
        }

        Console.WriteLine(Loc.F("Cli.Ampr", metadata.Ampr.Relevant ? AmprInspector.Describe(metadata.Ampr) : Loc.T("Cli.AmprNone")));
        Console.WriteLine(Loc.F("Cli.DlcEmu", metadata.DlcEmu.Present ? DlcEmuInspector.Describe(metadata.DlcEmu) : Loc.T("Cli.DlcNone")));
        if (metadata.DlcEmu.Present)
        {
            var dlcEntries = DlcEmuIni.Read(source, CancellationToken.None);
            if (dlcEntries.Count > 0)
            {
                Console.WriteLine(Loc.T("Cli.DlcHeader"));
                foreach (var dlc in dlcEntries)
                {
                    Console.WriteLine($"  - {dlc.ContentId} ({dlc.DownloadStatus})");
                }
            }
        }

        var output = arguments.Get("output", "o") ?? BuildPreparer.SuggestOutputFolder(source);
        var temp = arguments.Get("temp") ?? BuildPreparer.SuggestTemporaryFolder(output);
        var staging = metadata.IsExFat && !PsViethoa.FpkgBuilder.Core.ExFat.ImageMounter.CanMountPath(source) ? stats.TotalBytes : 0;
        // Mặc định tạo bằng SDK Sony (không có ảnh trung gian) trừ khi --no-sony-sdk, thiếu bộ công cụ hay nguồn là dự án .gp5.
        var sonySdk = !arguments.Has("no-sony-sdk") && !metadata.IsGp5 && SonySdkToolchain.Resolve(out _) != null;
        var disk = DiskSpaceAdvisor.Check(output, temp, stats.TotalBytes, staging, sonySdk);
        Console.WriteLine(Loc.F("Cli.Disk", disk.Summary) + (disk.Sufficient ? string.Empty : Loc.T("Cli.DiskLow")));
        return 0;
    }

    private static int Verify(Arguments arguments)
    {
        var path = arguments.Positionals.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.Error.WriteLine(Loc.T("Cli.NeedPkg"));
            return 1;
        }

        var mode = string.Equals(arguments.Get("image"), "native", StringComparison.OrdinalIgnoreCase)
            ? OuterImageMode.Native
            : OuterImageMode.PlaintextNoAuth;
        var verification = PackageVerifier.Verify(path, mode, arguments.Has("sha256"), CancellationToken.None);
        PrintVerification(path, verification);

        // Kiểm tra nội dung bằng engine: nhanh mặc định, --full giải nén thử mọi tệp trong bộ nhớ.
        var passcode = arguments.Get("passcode") ?? new string('0', BuildRequest.PasscodeLength);
        var contents = PackageVerifier.VerifyContents(path, passcode, arguments.Has("full"), CancellationToken.None);
        PrintContents(contents);
        return contents.IsValid ? 0 : 2;
    }

    private static int CleanJunk(Arguments arguments)
    {
        var source = arguments.Positionals.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            Console.Error.WriteLine(Loc.T("Cli.NeedFolder"));
            return 1;
        }

        var junk = JunkFileFinder.Find(source, CancellationToken.None);
        foreach (var item in junk)
        {
            Console.WriteLine(Loc.T(item.IsDirectory ? "Cli.JunkDir" : "Cli.JunkFile") + item.Path);
        }

        if (arguments.Has("dry-run"))
        {
            Console.WriteLine(Loc.F("Cli.JunkDryRun", junk.Count));
            return 0;
        }

        var (deleted, errors) = JunkFileFinder.Delete(junk);
        Console.WriteLine(Loc.F("Cli.JunkDeleted", deleted, junk.Count));
        foreach (var error in errors)
        {
            Console.Error.WriteLine("  ! " + error);
        }

        return errors.Count == 0 ? 0 : 2;
    }

    private static async Task<int> BuildAsync(Arguments arguments)
    {
        var quiet = arguments.Has("quiet", "q");
        var source = arguments.Get("source", "s") ?? arguments.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
        var kind = SourceLocator.Detect(source);
        var output = arguments.Get("output", "o") ?? (kind != SourceKind.None ? BuildPreparer.SuggestOutputFolder(source) : string.Empty);

        if (arguments.Has("clean-junk") && kind == SourceKind.Folder)
        {
            var junk = JunkFileFinder.Find(source, CancellationToken.None);
            var (deleted, errors) = JunkFileFinder.Delete(junk);
            if (!quiet)
            {
                Console.WriteLine(Loc.F("Cli.CleanResult", deleted, junk.Count));
            }

            foreach (var error in errors)
            {
                Console.Error.WriteLine("  ! " + error);
            }
        }

        SourceMetadata? metadata = null;
        if (kind != SourceKind.None)
        {
            try
            {
                metadata = MetadataReader.Read(source, CancellationToken.None);
            }
            catch (Exception)
            {
                metadata = null;
            }
        }

        var request = new BuildRequest
        {
            SourcePath = source,
            OutputFolder = output,
            TemporaryFolder = arguments.Get("temp") ?? string.Empty,
            ContentId = arguments.Get("content-id", "c") ?? metadata?.ContentId ?? string.Empty,
            Title = arguments.Get("title", "t") ?? metadata?.Title ?? string.Empty,
            Version = arguments.Get("version", "v") ?? VersionHelper.CanonicalOrDefault(metadata?.Version),
            // Dự án GP5 mang passcode riêng: dùng khi không truyền --passcode.
            Passcode = arguments.Get("passcode") ?? metadata?.Gp5Passcode ?? new string('0', BuildRequest.PasscodeLength),
            Kind = (arguments.Get("kind") ?? "app").ToLowerInvariant() switch
            {
                "homebrew" => PackageKind.Homebrew,
                "dlc" or "ac" => PackageKind.DlcWithData,
                _ => PackageKind.Application,
            },
            ImageMode = string.Equals(arguments.Get("image"), "native", StringComparison.OrdinalIgnoreCase)
                ? OuterImageMode.Native
                : OuterImageMode.PlaintextNoAuth,
            ExFat = (arguments.Get("exfat") ?? "auto").ToLowerInvariant() switch
            {
                "mount" => ExFatStrategy.Mount,
                "extract" => ExFatStrategy.Extract,
                _ => ExFatStrategy.Auto,
            },
            Threads = arguments.GetInt("threads", "j") ?? 0,
            PlayGoChunks = arguments.GetInt("playgo") ?? BuildRequest.DefaultPlayGoChunks,
            SdkMajorOverride = arguments.GetInt("sdk"),
            Deterministic = !arguments.Has("no-deterministic"),
            ComputeSha256 = arguments.Has("sha256"),
            PublishingToolsPath = arguments.Get("dll"),
            PreventSleep = !arguments.Has("no-sleep-guard"),
        };

        BuildPresets.Apply(BuildPresets.ById(arguments.Get("preset")) ?? BuildPresets.Default, request);

        if (arguments.Get("backend") is { } backendText)
        {
            request.KrakenBackend = backendText.ToLowerInvariant() switch
            {
                "builtin" or "built-in" or "managed" => KrakenBackendKind.BuiltIn,
                "pubtools" or "oodle" or "native" => KrakenBackendKind.PublishingTools,
                "none" or "uncompressed" or "raw" => KrakenBackendKind.Uncompressed,
                _ => KrakenBackendKind.Auto,
            };
        }

        if (arguments.GetInt("level") is { } level)
        {
            request.KrakenLevel = level;
        }

        if (arguments.Get("pfs") is { } pfs)
        {
            request.PfsFormat = pfs.Trim().ToLowerInvariant() is "v3" or "3" ? PfsFormat.V3 : PfsFormat.V2;
        }

        if (arguments.GetInt("block-size") is { } blockKiB)
        {
            request.KrakenBlockKiB = blockKiB;
        }

        if (arguments.Get("shuffle") is { } shuffleText)
        {
            // Tên mẫu sai là lỗi tham số (mã thoát 1) thay vì âm thầm dùng "none".
            if (!Enum.TryParse<ShufflePatternKind>(shuffleText.Trim(), ignoreCase: true, out var parsed))
            {
                throw new BuildValidationException([new ValidationError(BuildPreparer.FieldShuffle, Loc.F("Cli.UnknownShuffle", shuffleText, string.Join(", ", Enum.GetNames<ShufflePatternKind>())))]);
            }

            request.ShufflePattern = parsed;
        }

        if (arguments.Has("shuffle-analysis"))
        {
            request.ShuffleAnalysis = true;
        }

        if (arguments.Get("shuffle-prediction-level") is { } predictionText && !string.Equals(predictionText, "auto", StringComparison.OrdinalIgnoreCase))
        {
            request.ShufflePredictionLevel = int.TryParse(predictionText, out var prediction) ? prediction : null;
        }

        if (arguments.Has("skip-pfs-input-check"))
        {
            request.SkipPfsInputCheck = true;
        }

        if (arguments.Has("keep-drm"))
        {
            request.ForceStandardDrm = false;
        }

        if (arguments.Has("no-sony-sdk") || arguments.Has("builtin-engine"))
        {
            request.UseSonySdk = false;
        }
        else
        {
            // SDK Sony: Publishing Tools tự quyết mức nén, PFS, PlayGo, SDK… — các cờ của engine không có tác dụng, báo để khỏi hiểu lầm.
            var ignored = new[]
                {
                    "preset", "level", "threads", "pfs", "block-size", "shuffle", "shuffle-analysis", "shuffle-prediction-level", "skip-pfs-input-check",
                    "playgo", "sdk", "backend", "dll", "no-deterministic", "no-layout-optimization", "source-mode", "project", "keep-playgo", "keep-required-fw", "keep-drm",
                }
                .Where(flag => arguments.Has(flag))
                // --playgo đặt số chunk của PlayGo dự phòng khi có --sdk-playgo-fallback.
                .Where(flag => !(flag == "playgo" && !arguments.Has("no-sdk-playgo-fallback")))
                .Select(flag => "--" + flag)
                .ToList();
            if (ignored.Count > 0)
            {
                Console.Error.WriteLine(Loc.F("Cli.SdkIgnoredFlags", string.Join(", ", ignored)));
            }
        }

        if (arguments.Has("no-sdk-prescan"))
        {
            request.SdkPrescan = false;
        }

        if (arguments.Has("no-sdk-playgo-fallback"))
        {
            request.SdkPlayGoFallback = false;
        }

        if (arguments.Get("sdk-compression-level") is { } sdkLevelText)
        {
            request.SdkCompressionLevel = int.TryParse(sdkLevelText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sdkLevel)
                ? sdkLevel
                : int.MinValue;
        }

        if (arguments.Has("keep-ampr"))
        {
            request.RemoveAmprLeftovers = false;
        }

        if (arguments.Has("keep-playgo"))
        {
            request.RemovePlayGoFiles = false;
        }

        if (arguments.Has("keep-version-uri"))
        {
            request.ClearVersionFileUri = false;
        }

        if (arguments.Has("keep-attribute3"))
        {
            request.ClearPlayGoAttributes = false;
        }

        if (arguments.Has("keep-required-fw"))
        {
            request.LowerRequiredFirmware = false;
        }

        if (arguments.Has("full-verify"))
        {
            request.FullVerify = true;
        }

        if (arguments.Has("strip-dlc-emu") || arguments.Has("no-dlc-emu"))
        {
            request.KeepDlcEmu = false;
        }

        if (arguments.Has("no-layout-optimization"))
        {
            request.LayoutOptimization = false;
        }

        if (arguments.Get("source-mode") is { } sourceMode)
        {
            request.SourceMode = sourceMode.Trim().ToLowerInvariant() switch
            {
                "folder" => SourceMode.Folder,
                "gp5" or "project" => SourceMode.Gp5Project,
                _ => SourceMode.Auto,
            };
        }

        if (arguments.Get("project") is { } project)
        {
            request.ProjectFilePath = project;
            if (request.SourceMode == SourceMode.Auto)
            {
                request.SourceMode = SourceMode.Gp5Project;
            }
        }

        if (string.IsNullOrEmpty(request.ContentId) && metadata != null)
        {
            request.ContentId = ContentIdHelper.Suggest(metadata.TitleId, request.Title);
            if (!quiet)
            {
                Console.WriteLine(Loc.F("Cli.SuggestedId", request.ContentId));
            }
        }

        var validationErrors = BuildPreparer.Validate(request);
        if (validationErrors.Count > 0)
        {
            throw new BuildValidationException(validationErrors);
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

        var renderer = new ConsoleProgressRenderer(quiet);
        var engine = new BuildEngine();
        var outcome = await engine.BuildAsync(
            request,
            renderer.Log,
            new Progress<BuildProgress>(renderer.Update),
            cancellation.Token,
            path =>
            {
                // Đĩa đầy: chờ người dùng giải phóng dung lượng rồi nhấn Enter để thử lại; 'q' hoặc không có bàn phím → huỷ.
                Console.Error.WriteLine();
                Console.Error.WriteLine(Loc.F("Build.DiskFullBody", path));
                Console.Error.Write(Loc.T("Cli.DiskFullPrompt"));
                if (Console.IsInputRedirected)
                {
                    return false;
                }

                var answer = Console.ReadLine();
                return answer != null && !answer.Trim().StartsWith("q", StringComparison.OrdinalIgnoreCase);
            },
            ConflictPrompt(arguments));

        renderer.Finish();

        foreach (var warning in outcome.Warnings)
        {
            Console.WriteLine(Loc.F("Cli.Warning", warning));
        }

        Console.WriteLine();
        Console.WriteLine(Loc.F("Cli.Success", Formatters.Duration(outcome.Elapsed)));
        PrintVerification(outcome.OutputPath, outcome.Verification);
        return 0;
    }

    private static void PrintVerification(string path, PackageVerification verification)
    {
        Console.WriteLine(Loc.F("Cli.File", path));
        Console.WriteLine(Loc.F("Cli.Type", verification.ContainerLabel));
        Console.WriteLine(Loc.F("Cli.Size", Formatters.SizeWithBytes(verification.Length)));
        Console.WriteLine(Loc.F("Cli.Fih", verification.SignedByte.ToString("X2"), verification.OuterMode.ToString("X4")));
        if (verification.SeedMarker != null)
        {
            Console.WriteLine(Loc.F("Cli.Marker", verification.SeedMarker));
        }

        Console.WriteLine(Loc.F("Cli.ContentId", verification.ContentId ?? "—", verification.EntryCount));
        if (verification.Sha256 != null)
        {
            Console.WriteLine(Loc.F("Cli.Sha", verification.Sha256));
        }

        if (verification.Contents is { } contents)
        {
            PrintContents(contents);
        }
    }

    private static void PrintContents(ContentVerification contents)
    {
        foreach (var check in contents.Checks)
        {
            Console.WriteLine(Loc.F("Plan.VerifyCheck", check));
        }

        foreach (var issue in contents.Issues)
        {
            Console.Error.WriteLine(Loc.F("Plan.VerifyIssue", issue.Stage, issue.Message));
        }

        Console.WriteLine(contents.IsValid
            ? Loc.F(contents.IsFull ? "Plan.VerifiedFull" : "Plan.VerifiedQuick", contents.Checks.Count, Formatters.Duration(contents.Elapsed))
            : Loc.F("Verify.ContentFailed", string.Join("; ", contents.Issues)));
    }
}

/// <summary>Vẽ thanh tiến trình một dòng trong terminal, in nhật ký phía trên.</summary>
internal sealed class ConsoleProgressRenderer
{
    private readonly bool _quiet;
    private readonly bool _interactive;
    private readonly object _gate = new();
    private readonly Stopwatch _lastDraw = Stopwatch.StartNew();
    private string _lastLine = string.Empty;
    private BuildProgress? _latest;

    public ConsoleProgressRenderer(bool quiet)
    {
        _quiet = quiet;
        _interactive = !Console.IsOutputRedirected;
    }

    public void Log(LogEntry entry)
    {
        if (_quiet && entry.Level == LogLevel.Info)
        {
            return;
        }

        lock (_gate)
        {
            ClearLine();
            var prefix = entry.Level switch
            {
                LogLevel.Warning => "⚠ ",
                LogLevel.Error => "✖ ",
                LogLevel.Success => "✔ ",
                _ => "  ",
            };
            Console.WriteLine($"{entry.TimeText} {prefix}{entry.Message}");
            if (_interactive)
            {
                Redraw();
            }
        }
    }

    public void Update(BuildProgress progress)
    {
        lock (_gate)
        {
            _latest = progress;
            if (_lastDraw.ElapsedMilliseconds < 120 && !progress.IsComplete)
            {
                return;
            }

            Redraw();
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            ClearLine();
        }
    }

    private void Redraw()
    {
        if (_latest == null)
        {
            return;
        }

        var p = _latest;
        var eta = p.Eta is { } remaining && !p.IsComplete ? Loc.F("Cli.ProgressEta", Formatters.Clock(remaining)) : string.Empty;
        var rate = p.Throughput != null && !p.IsComplete ? " · " + p.Throughput : string.Empty;
        var text = Loc.F("Cli.Progress", Bar(p.OverallPercent), p.OverallPercent, p.Phase, p.PhasePercent, Formatters.Clock(p.Elapsed), eta + rate);

        if (_interactive)
        {
            var width = Math.Max(20, SafeWindowWidth() - 1);
            if (text.Length > width)
            {
                text = text[..width];
            }

            Console.Write('\r' + text.PadRight(_lastLine.Length));
            _lastLine = text;
        }
        else if (p.IsComplete || p.PhasePercent % 10 == 0)
        {
            Console.WriteLine(text);
        }

        _lastDraw.Restart();
    }

    private void ClearLine()
    {
        if (_interactive && _lastLine.Length > 0)
        {
            Console.Write('\r' + new string(' ', _lastLine.Length) + '\r');
            _lastLine = string.Empty;
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
