using System.Reflection;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Ps4;

/// <summary>Tác vụ PS4 dừng hoặc lỗi; <see cref="Folder"/> là thư mục tác vụ được giữ lại (log, tệp đang tạo).</summary>
public sealed class Ps4JobFailureException(string message, string folder, Exception inner) : Exception(message, inner)
{
    public string Folder { get; } = folder;
}

/// <summary>
/// Điểm vào duy nhất của PS4 PKG (giao diện và dòng lệnh): xả, đóng base / update / DLC, gộp. Đóng gói xong thì chuyển PKG ra thư
/// mục xuất, chép log vào <see cref="LogRoot"/> rồi dọn thư mục tác vụ; lỗi thì giữ nguyên mọi thứ để xem lại.
/// </summary>
public static class Ps4JobRunner
{
    /// <summary>Tệp engine thực sự đọc từ gói base tham chiếu.</summary>
    private static readonly string[] ReferenceBaseFiles = { "eboot.bin", "sce_sys/keystone" };

    /// <summary>Thư mục lưu log các lượt đóng gói đã dọn (ứng dụng đặt vào thư mục dữ liệu của nó).</summary>
    public static string LogRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PSVIETHOA FPKG Builder", "ps4-logs");

    private static string AppVersion =>
        typeof(Ps4JobRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public static Task<Ps4JobResult> RunAsync(Ps4JobRequest request, Action<string, string>? log, Action<Ps4Progress>? progress, CancellationToken token) => Task.Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(request.Input) || string.IsNullOrWhiteSpace(request.Output))
        {
            throw new ArgumentException(Loc.T("Ps4.Job.PickPaths"));
        }

        // Xả vào <thư mục xuất>/CUSA12345-update (hoặc -base / -dlc) gồm image0 + Sc0, bố cục PS4 quen thuộc.
        var folderName = request.Kind == Ps4JobKind.Extract ? Ps4PackageReader.ExtractFolderName(request.Input) : null;
        using var job = new Ps4JobContext(request.Output, request.Kind.ToString().ToLowerInvariant(), token, log, progress, folderName);
        try
        {
            job.Log("PSVIETHOA FPKG Builder " + AppVersion + " • PS4 " + request.Kind);
            job.Log(Loc.F("Ps4.Job.Folder", job.Folder));
            job.Save("job.json", new
            {
                status = "running",
                kind = request.Kind.ToString(),
                input = request.Input,
                output = job.Folder,
                base_package = request.BasePackage,
                version = request.Version,
                verify = request.Verify,
                project_only = request.ProjectOnly,
                started = DateTimeOffset.Now,
                retains_all_output_on_failure = true,
            });
            Ps4JobResult result;
            if (request.Kind == Ps4JobKind.Extract)
            {
                result = Ps4PackageReader.Extract(request, job);
            }
            else
            {
                result = await BuildAsync(request, job).ConfigureAwait(false);
            }

            job.Save("result.json", result);
            if (result.Status == "completed" && result.Package != null)
            {
                return PublishAndClean(request, job, result);
            }

            return result;
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException || token.IsCancellationRequested;
            var packages = Directory.Exists(job.Folder) ? Directory.EnumerateFiles(job.Folder, "*.pkg", SearchOption.TopDirectoryOnly).ToArray() : [];
            job.Log(cancelled ? Loc.T("Ps4.Job.Stopped") : ex.Message, cancelled ? Ps4LogLevel.Stop : Ps4LogLevel.Error);
            job.Save("result.json", new
            {
                status = cancelled ? "cancelled" : "failed",
                error = ex.Message,
                retained_packages = packages,
                output_complete = false,
                log = job.LogPath,
                directory = job.Folder,
            });
            throw new Ps4JobFailureException(cancelled ? Loc.T("Ps4.Job.StoppedShort") : ex.Message, job.Folder, ex);
        }
    }, CancellationToken.None);

    private static Ps4JobResult PublishAndClean(Ps4JobRequest request, Ps4JobContext job, Ps4JobResult result)
    {
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Output));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.Folder));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(folder)!), output, StringComparison.OrdinalIgnoreCase) ||
            new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException(Loc.T("Ps4.Job.FolderNotInOutput"));
        }

        var source = result.Package!;
        var stem = Path.GetFileNameWithoutExtension(source);
        var target = Path.Combine(output, Path.GetFileName(source));
        for (var i = 1; File.Exists(target); i++)
        {
            target = Path.Combine(output, $"{stem}_{i}.pkg");
        }

        File.Move(source, target);
        var bytes = new FileInfo(target).Length;
        var logPath = Path.Combine(LogRoot, Path.GetFileName(folder) + ".log");
        var final = result with { Directory = output, Package = target, Project = null, Log = logPath, Bytes = bytes };
        try
        {
            Directory.CreateDirectory(LogRoot);
            job.Log(Loc.F("Ps4.Job.Moved", target), Ps4LogLevel.Ok);
            job.Save("result.json", final);
            File.Copy(job.LogPath, logPath, false);
            job.Dispose();
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            try
            {
                job.Dispose();
                Directory.CreateDirectory(LogRoot);
                if (!File.Exists(logPath))
                {
                    File.Copy(job.LogPath, logPath);
                }

                File.AppendAllText(logPath, Environment.NewLine + "[WARN] " + Loc.F("Ps4.Job.CleanupFailed", target, ex.Message) + Environment.NewLine);
            }
            catch (Exception)
            {
            }

            if (!File.Exists(logPath))
            {
                final = final with { Log = job.LogPath };
            }
        }

        return final;
    }

    /// <summary>Môi trường chạy orbis-pub-cmd (tạo WINEPREFIX lần đầu); đường dẫn trong GP4 đổi theo môi trường.</summary>
    private static async Task<Ps4Runtime> PrepareRuntimeAsync(Ps4JobRequest request, Ps4JobContext job)
    {
        var runtime = Ps4Engine.Resolve(out var problem) ?? throw new InvalidOperationException(problem);
        if (!string.IsNullOrWhiteSpace(request.NativeTool))
        {
            runtime = runtime with { ToolDirectory = Path.GetDirectoryName(Path.GetFullPath(request.NativeTool))! };
        }

        if (Ps4Engine.MissingVcRuntimes() is { Count: > 0 } missing)
        {
            job.Log(Loc.F("Ps4.Engine.VcMissing", string.Join(", ", missing)), Ps4LogLevel.Warn);
            if (!Ps4Engine.InstallVcRuntimes(line => job.Log(line), TimeSpan.FromMinutes(5)))
            {
                throw new InvalidOperationException(Loc.F("Ps4.Engine.VcStillMissing", string.Join(", ", Ps4Engine.MissingVcRuntimes())));
            }
        }

        await Ps4Engine.EnsurePrefixAsync(runtime, line => job.Log(line), job.Token).ConfigureAwait(false);
        if (runtime.UsesWine)
        {
            job.Log(Loc.F("Ps4.Job.ViaWine", runtime.WinePath));
        }

        return runtime;
    }

    /// <summary>
    /// Gắn ổ ngắn trong Wine cho thư mục tác vụ và các nguồn (thư mục cha của image để gồm cả Sc0, thư mục chứa PKG base) rồi cho mọi
    /// đường dẫn đưa vào engine đi qua đó.
    /// </summary>
    private static Ps4DriveMap MapDrives(Ps4Runtime runtime, Ps4JobRequest request, Ps4JobContext job)
    {
        var map = new Ps4DriveMap(runtime);
        map.Map(job.Folder);
        foreach (var input in new[] { request.Input, request.BasePackage })
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (File.Exists(input))
            {
                map.Map(Path.GetDirectoryName(Path.GetFullPath(input)));
                continue;
            }

            try
            {
                map.Map(Path.GetDirectoryName(Ps4ProjectBuilder.ResolveImage(input)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }

        job.ToolPath = map.ToolPath;
        return map;
    }

    private static async Task<Ps4JobResult> BuildAsync(Ps4JobRequest request, Ps4JobContext job)
    {
        // DLC luôn đóng với digest đầy đủ; lượt img_verify đọc lại chỉ chạy khi người dùng bật.
        var verify = request.Verify || request.Kind == Ps4JobKind.Dlc;
        var postVerify = request.Verify && request.Kind != Ps4JobKind.Dlc;
        job.Progress(new Ps4Progress(Loc.T("Ps4.Phase.ReadSource"), Loc.T("Ps4.Phase.ReadSourceDetail"), 1, 0, 0, 0, null));
        Ps4Runtime? runtime = null;
        if (!request.ProjectOnly)
        {
            runtime = await PrepareRuntimeAsync(request, job).ConfigureAwait(false);
        }

        using var drives = runtime != null ? MapDrives(runtime, request, job) : null;

        if (request.Kind == Ps4JobKind.Patch && !request.ProjectOnly)
        {
            request = await PrepareCompatiblePatchBaseAsync(request, job).ConfigureAwait(false);
        }

        var project = request.Kind == Ps4JobKind.Merge ? Ps4MergeBuilder.Prepare(request, job) : Ps4ProjectBuilder.Prepare(request, job);
        if (request.ProjectOnly || runtime == null)
        {
            job.Progress(new Ps4Progress(Loc.T("Ps4.Phase.Gp4Ready"), project.Path, 100, project.TotalBytes, project.TotalBytes, 0, 0));
            return new Ps4JobResult(job.Folder, null, project.Path, job.LogPath, false, project.Files.Count, project.TotalBytes, "project_ready");
        }

        var tool = string.IsNullOrWhiteSpace(request.NativeTool) ? Path.Combine(runtime.ToolDirectory, Ps4Engine.ToolFileName) : request.NativeTool;
        if (!File.Exists(tool))
        {
            throw new FileNotFoundException(Loc.T("Ps4.Job.ToolMissing"));
        }

        tool = Ps4Engine.Select(tool, !verify, job);
        var temp = Path.Combine(job.Folder, "_temp");
        Directory.CreateDirectory(temp);
        var arguments = new List<string> { "img_create", "--oformat", "pkg", "--no_progress_bar" };
        if (!verify)
        {
            arguments.Add("--skip_digest");
        }

        // Thư mục tạm và thư mục đầu ra đi qua ổ Z: (engine từ chối thư mục tạm trên ổ đã gắn); chỉ tệp nguồn trong GP4 cần đường ngắn.
        arguments.AddRange(["--tmp_path", runtime.ToolPath(temp), job.ToolPath(project.Path), runtime.ToolPath(job.Folder)]);
        await Ps4NativeRunner.RunAsync(runtime, tool, arguments, job, project.TotalBytes, false, request.Passcode, !verify, readsReferencePackage: request.Kind == Ps4JobKind.Patch)
            .ConfigureAwait(false);
        var packages = Directory.EnumerateFiles(job.Folder, "*.pkg", SearchOption.TopDirectoryOnly).ToArray();
        if (packages.Length != 1)
        {
            throw new IOException(Loc.T("Ps4.Job.NotOnePkg"));
        }

        var package = packages[0];
        var size = new FileInfo(package).Length;
        job.Log(Loc.F("Ps4.Job.Built", package), Ps4LogLevel.Ok);
        job.Save("package_built.json", new { path = package, bytes = size, build_finished = DateTimeOffset.Now, verification_pending = postVerify, no_automatic_deletion = true });
        job.Token.ThrowIfCancellationRequested();

        // Kiểm tra sau khi đóng không chặn việc giao gói: chỉ đọc header PKG (không quét lại nguồn, không đọc lại toàn bộ).
        var info = Ps4PackageReader.Inspect(package, request.Passcode);
        if (!info.PasscodeMatches || info.ContentId != project.ContentId || info.Category != project.Category || info.Version != project.Version || info.MasterVersion != project.MasterVersion)
        {
            job.Log(Loc.T("Ps4.Job.HeaderDiffers"), Ps4LogLevel.Warn);
        }

        if (postVerify)
        {
            job.Log(Loc.T("Ps4.Job.Verifying"), Ps4LogLevel.Ok);
            await Ps4NativeRunner.RunAsync(runtime, tool,
                ["img_verify", "--passcode", request.Passcode, "--integrity_check", "on", "--format_check", "off", "--no_progress_bar", "--tmp_path", runtime.ToolPath(temp), runtime.ToolPath(package)],
                job, size, true, request.Passcode).ConfigureAwait(false);
            job.Log(Loc.T("Ps4.Job.IntegrityPass"), Ps4LogLevel.Ok);
        }

        job.Save("package_info.json", info);
        job.Progress(new Ps4Progress(Loc.T("Ps4.Phase.Moving"), package, 99, size, size, 0, null));
        job.Log(Loc.T("Ps4.Job.MovingOut"), Ps4LogLevel.Ok);
        return new Ps4JobResult(job.Folder, package, project.Path, job.LogPath, postVerify, project.Files.Count, size, "completed");
    }

    /// <summary>
    /// Engine chỉ mở được base tham chiếu mã hoá cùng passcode với update đang đóng. Thay vì đóng lại cả game (hàng giờ với base
    /// 40 GB), dựng một base phụ nhỏ từ metadata + eboot + keystone gốc. Chỉ dùng lúc đóng gói: không bao giờ phát hành, base đã cài
    /// trên máy giữ nguyên.
    /// </summary>
    private static async Task<Ps4JobRequest> PrepareCompatiblePatchBaseAsync(Ps4JobRequest request, Ps4JobContext job)
    {
        if (string.IsNullOrWhiteSpace(request.BasePackage))
        {
            throw new ArgumentException(Loc.T("Ps4.Job.PickBase"));
        }

        var directPackage = File.Exists(request.BasePackage);
        var baseInfo = directPackage ? Ps4PackageReader.Inspect(request.BasePackage, request.Passcode) : Ps4MergeBuilder.InspectInput(request.BasePackage, false, request.Passcode);
        if (baseInfo.Category is not ("gd" or "gda"))
        {
            throw new InvalidDataException(Loc.T("Ps4.Job.RefNotBase"));
        }

        if (directPackage && baseInfo.PasscodeMatches)
        {
            job.Log(Loc.T("Ps4.Job.BaseSamePasscode"), Ps4LogLevel.Ok);
            return request with { BasePasscode = request.Passcode };
        }

        var stubImage = Path.Combine(job.Folder, "_reference_base", "image0");
        Directory.CreateDirectory(stubImage);
        if (directPackage)
        {
            var suppliedPass = string.IsNullOrWhiteSpace(request.BasePasscode) ? Ps4Defaults.Passcode : request.BasePasscode;
            Ps4ProjectBuilder.ValidatePasscode(suppliedPass, Loc.T("Ps4.Project.BasePasscodeLabel"));
            var readable = Ps4PackageReader.Inspect(request.BasePackage, suppliedPass);
            if (!readable.PasscodeMatches && !readable.RecoverableFakePackage)
            {
                throw new InvalidDataException(Loc.T("Ps4.Job.BaseUnreadable"));
            }

            job.Log(Loc.T("Ps4.Job.StubFromPkg"));
            var baseMeta = Loc.T("Ps4.Phase.ReadBaseMeta");
            using var extractJob = new Ps4JobContext(Path.Combine(job.Folder, "_reference_base", "src"), "refbase", job.Token,
                (line, level) => job.Log("[BASE] " + line, level),
                p => job.Progress(p with { Phase = baseMeta + ": " + p.Phase, Percent = 1 + p.Percent * 0.04 }));
            Ps4PackageReader.Extract(new Ps4JobRequest(Ps4JobKind.Extract, request.BasePackage, extractJob.Folder, Passcode: suppliedPass, Verify: false, ExtractOnly: ReferenceBaseFiles),
                extractJob, keepAllMetadata: true);
            Directory.Delete(stubImage);
            Directory.Move(Path.Combine(extractJob.Folder, "image0"), stubImage);
        }
        else
        {
            var folder = Ps4ProjectBuilder.ResolveImage(request.BasePackage);
            job.Log(Loc.T("Ps4.Job.StubFromFolder"));
            foreach (var relative in ReferenceBaseFiles)
            {
                var source = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                {
                    continue;
                }

                var target = Ps4Files.SafeChild(stubImage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
            }

            var sceSys = Path.Combine(folder, "sce_sys");
            if (Directory.Exists(sceSys))
            {
                foreach (var file in Directory.EnumerateFiles(sceSys))
                {
                    job.Token.ThrowIfCancellationRequested();
                    File.Copy(file, Path.Combine(Directory.CreateDirectory(Path.Combine(stubImage, "sce_sys")).FullName, Path.GetFileName(file)), true);
                }
            }
        }

        if (!File.Exists(Path.Combine(stubImage, "sce_sys", "param.sfo")))
        {
            throw new FileNotFoundException(Loc.T("Ps4.Job.StubNoSfo"));
        }

        var stubRequest = new Ps4JobRequest(Ps4JobKind.Base, stubImage, Path.Combine(job.Folder, "_reference_base", "pkg"),
            Passcode: request.Passcode, Version: baseInfo.Version, Verify: false, NativeTool: request.NativeTool);
        var stubPhase = Loc.T("Ps4.Phase.BuildStub");
        var stub = await RunAsync(stubRequest,
            (line, level) => job.Log(Loc.T("Ps4.Job.StubPrefix") + " " + line, level),
            p => job.Progress(p with { Phase = stubPhase + ": " + p.Phase, Percent = 5 + p.Percent * 0.05 }), job.Token).ConfigureAwait(false);
        if (stub.Package == null || !File.Exists(stub.Package))
        {
            throw new IOException(Loc.T("Ps4.Job.StubFailed"));
        }

        job.Log(Loc.F("Ps4.Job.StubReady", (stub.Bytes / 1e6).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)), Ps4LogLevel.Ok);
        job.BuildProgressStart = 10;
        return request with { BasePackage = stub.Package, BasePasscode = request.Passcode };
    }
}
