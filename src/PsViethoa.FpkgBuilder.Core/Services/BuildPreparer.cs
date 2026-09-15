using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Chuẩn hoá và kiểm tra một BuildRequest trước khi đưa vào engine.</summary>
public static class BuildPreparer
{
    public const string FieldSource = "Source";
    public const string FieldOutput = "Output";
    public const string FieldTemporary = "Temporary";
    public const string FieldContentId = "ContentId";
    public const string FieldPasscode = "Passcode";
    public const string FieldVersion = "Version";
    public const string FieldThreads = "Threads";
    public const string FieldPlayGo = "PlayGo";
    public const string FieldSdk = "Sdk";
    public const string FieldKrakenLevel = "KrakenLevel";
    public const string FieldPublishingTools = "PublishingTools";
    public const string FieldExFat = "ExFat";
    public const string FieldKrakenBlock = "KrakenBlock";
    public const string FieldShuffle = "Shuffle";
    public const string FieldProject = "Project";

    /// <summary>
    /// Gợi ý thư mục xuất: cạnh nguồn, tên "&lt;nguồn&gt;-pkg" (bỏ đuôi .exfat/.gp5 nếu là tệp).
    /// Với dự án .gp5, thư viện coi thư mục chứa tệp .gp5 là thư mục nguồn và từ chối xuất vào bên trong nó,
    /// nên gợi ý được đặt cạnh thư mục đó.
    /// </summary>
    public static string SuggestOutputFolder(string sourcePath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        var parent = Directory.GetParent(full)?.FullName ?? full;
        var name = File.Exists(full) ? Path.GetFileNameWithoutExtension(full) : Path.GetFileName(full);
        if (File.Exists(full) && SourceLocator.HasGp5Extension(full))
        {
            parent = Directory.GetParent(parent)?.FullName ?? parent;
        }

        return Path.Combine(parent, name + "-pkg");
    }

    /// <summary>
    /// Gợi ý thư mục tạm cùng ổ đĩa với thư mục xuất (tránh sao chép chéo ổ).
    /// Gốc ổ thường không cho người dùng thường ghi (ví dụ /home trên Linux, "/" trên macOS), nên mỗi ứng viên đều
    /// được thử ghi thật trước khi chọn; không nơi nào ghi được thì lùi về thư mục cache của người dùng.
    /// </summary>
    public static string SuggestTemporaryFolder(string outputFolder)
    {
        foreach (var candidate in TemporaryFolderCandidates(outputFolder))
        {
            if (CanCreateDirectory(candidate))
            {
                return candidate;
            }
        }

        return PlatformTemporaryFolder();
    }

    /// <summary>Ứng viên thư mục tạm, ưu tiên cùng ổ với thư mục xuất.</summary>
    private static IEnumerable<string> TemporaryFolderCandidates(string outputFolder)
    {
        string? mount = null;
        try
        {
            mount = DiskSpaceAdvisor.ResolveMountPoint(outputFolder);
        }
        catch (Exception)
        {
        }

        if (!string.IsNullOrEmpty(mount))
        {
            var isSystemVolume = OperatingSystem.IsWindows()
                ? string.Equals(mount, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)
                : mount == "/" || mount.StartsWith("/System/", StringComparison.Ordinal);

            if (!isSystemVolume)
            {
                yield return Path.Combine(mount, "fpkg-temp");
            }
        }

        // Gốc ổ không ghi được (thường gặp: /home thuộc root): dùng thư mục cạnh thư mục xuất — vẫn cùng ổ đĩa.
        if (!string.IsNullOrWhiteSpace(outputFolder))
        {
            var full = FullPathOrEmpty(outputFolder);
            if (!string.IsNullOrEmpty(full))
            {
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent))
                {
                    yield return Path.Combine(parent, "fpkg-temp");
                }
            }
        }

        yield return PlatformTemporaryFolder();
    }

    /// <summary>Thư mục tạm mặc định theo nền tảng (không phụ thuộc thư mục xuất).</summary>
    private static string PlatformTemporaryFolder()
    {
        if (OperatingSystem.IsMacOS())
        {
            var caches = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "PSVIETHOA FPKG Builder");
            return Path.Combine(caches, "temp");
        }

        if (OperatingSystem.IsLinux())
        {
            // Nhiều bản Linux gắn /tmp bằng tmpfs (nằm trong RAM): ảnh giải nén hàng chục GB sẽ ăn sạch bộ nhớ.
            // Dùng thư mục cache theo chuẩn XDG nằm trên đĩa thật thay vì Path.GetTempPath().
            var cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(cacheHome))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    cacheHome = Path.Combine(home, ".cache");
                }
            }

            if (!string.IsNullOrWhiteSpace(cacheHome))
            {
                return Path.Combine(cacheHome, "psviethoa-fpkg-builder", "temp");
            }
        }

        return Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), "fpkg-temp");
    }

    /// <summary>Thử tạo (rồi dọn) thư mục để biết có ghi được thật không — quyền trên gốc ổ hay thay đổi.</summary>
    private static bool CanCreateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var existed = Directory.Exists(path);
            Directory.CreateDirectory(path);

            var probe = Path.Combine(path, ".fpkg-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            using (File.Create(probe))
            {
            }

            File.Delete(probe);

            if (!existed)
            {
                try
                {
                    Directory.Delete(path);
                }
                catch (Exception)
                {
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Trả về bản sao đã chuẩn hoá (đường dẫn đầy đủ, Content ID viết hoa, phiên bản chuẩn, thư mục tạm mặc định).</summary>
    public static BuildRequest Normalize(BuildRequest request)
    {
        var result = request.Clone();
        result.SourcePath = FullPathOrEmpty(request.SourcePath);
        result.OutputFolder = FullPathOrEmpty(request.OutputFolder);
        result.ContentId = ContentIdHelper.Normalize(request.ContentId);
        result.Title = (request.Title ?? string.Empty).Trim();
        result.Passcode = request.Passcode ?? string.Empty;
        result.Version = VersionHelper.TryCanonicalize(request.Version, out var canonical) ? canonical : (request.Version ?? string.Empty).Trim();

        var temporary = (request.TemporaryFolder ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(temporary) && !string.IsNullOrEmpty(result.OutputFolder))
        {
            temporary = SuggestTemporaryFolder(result.OutputFolder);
        }

        result.TemporaryFolder = FullPathOrEmpty(temporary);
        result.PublishingToolsPath = string.IsNullOrWhiteSpace(request.PublishingToolsPath) ? null : FullPathOrEmpty(request.PublishingToolsPath);
        result.ProjectFilePath = string.IsNullOrWhiteSpace(request.ProjectFilePath) ? null : FullPathOrEmpty(request.ProjectFilePath);

        // Nguồn là tệp .gp5: luôn dùng đúng dự án đó (thư viện nhận ProjectFilePath = tệp, SourceFolder = thư mục chứa tệp).
        if (SourceLocator.Detect(result.SourcePath) == SourceKind.Gp5Project)
        {
            result.SourceMode = SourceMode.Gp5Project;
            result.ProjectFilePath = result.SourcePath;
        }

        return result;
    }

    /// <summary>Kiểm tra request (đã hoặc chưa chuẩn hoá). Trả về danh sách lỗi theo trường.</summary>
    public static IReadOnlyList<ValidationError> Validate(BuildRequest request)
    {
        var errors = new List<ValidationError>();
        var normalized = Normalize(request);

        var kind = SourceLocator.Detect(normalized.SourcePath);
        string? gp5Root = null;
        switch (kind)
        {
            case SourceKind.None:
                errors.Add(new ValidationError(FieldSource, Loc.T("Val.SourceMissing")));
                break;
            case SourceKind.Folder:
                if (!Directory.Exists(Path.Combine(normalized.SourcePath, "sce_sys")))
                {
                    errors.Add(new ValidationError(FieldSource, Loc.T("Val.SourceNoSceSys")));
                }

                break;
            case SourceKind.ExFatImage:
                try
                {
                    SourceLocator.Resolve(normalized.SourcePath);
                }
                catch (InvalidDataException ex)
                {
                    errors.Add(new ValidationError(FieldSource, ex.Message));
                }
                catch (Exception ex)
                {
                    errors.Add(new ValidationError(FieldSource, Loc.T("Val.ExFatInvalid") + " " + ex.Message));
                }

                if (normalized.ExFat == ExFatStrategy.Mount && !ImageMounter.IsAvailable && !SourceLocator.HasPfsContainerExtension(normalized.SourcePath) && !PfsContainer.IsContainer(normalized.SourcePath))
                {
                    errors.Add(new ValidationError(FieldExFat, Loc.T("Val.ExFatMountUnsupported")));
                }

                break;
            case SourceKind.UfsImage:
                try
                {
                    SourceLocator.Resolve(normalized.SourcePath);
                }
                catch (InvalidDataException ex)
                {
                    errors.Add(new ValidationError(FieldSource, ex.Message));
                }
                catch (Exception ex)
                {
                    errors.Add(new ValidationError(FieldSource, Loc.T("Val.ExFatInvalid") + " " + ex.Message));
                }

                break;
            case SourceKind.Gp5Project:
                try
                {
                    var project = Gp5ProjectInfo.Load(normalized.SourcePath);
                    gp5Root = project.RootFolder;
                    if (project.ParamJsonPath == null || !File.Exists(project.ParamJsonPath))
                    {
                        errors.Add(new ValidationError(FieldSource, Loc.T("Val.Gp5NoParam")));
                    }
                }
                catch (InvalidDataException ex)
                {
                    errors.Add(new ValidationError(FieldSource, ex.Message));
                }
                catch (Exception ex)
                {
                    errors.Add(new ValidationError(FieldSource, Loc.T("Val.Gp5Invalid") + " " + ex.Message));
                }

                break;
        }

        // Các thư mục không được chứa thư mục xuất/tạm: thư mục nguồn; với dự án GP5 là thư mục ứng dụng mà dự án
        // trỏ tới và thư mục chứa tệp .gp5 (thư viện coi thư mục đó là SourceFolder và từ chối xuất/tạm bên trong).
        var protectedRoots = new List<(string Root, string OutputKey, string TempKey)>();
        switch (kind)
        {
            case SourceKind.Folder:
                protectedRoots.Add((normalized.SourcePath, "Val.OutputInsideSource", "Val.TempInsideSource"));
                break;
            case SourceKind.Gp5Project:
                if (gp5Root != null)
                {
                    protectedRoots.Add((gp5Root, "Val.OutputInsideSource", "Val.TempInsideSource"));
                }

                protectedRoots.Add((Path.GetDirectoryName(normalized.SourcePath) ?? normalized.SourcePath, "Val.OutputInsideProject", "Val.TempInsideProject"));
                break;
        }

        if (string.IsNullOrEmpty(normalized.OutputFolder))
        {
            errors.Add(new ValidationError(FieldOutput, Loc.T("Val.OutputMissing")));
        }

        foreach (var (root, outputKey, tempKey) in protectedRoots)
        {
            if (!string.IsNullOrEmpty(normalized.OutputFolder) && IsInside(normalized.OutputFolder, root) && errors.All(e => e.Field != FieldOutput))
            {
                errors.Add(new ValidationError(FieldOutput, Loc.T(outputKey)));
            }

            if (!string.IsNullOrEmpty(normalized.TemporaryFolder) && IsInside(normalized.TemporaryFolder, root) && errors.All(e => e.Field != FieldTemporary))
            {
                errors.Add(new ValidationError(FieldTemporary, Loc.T(tempKey)));
            }
        }

        if (!ContentIdHelper.IsValid(normalized.ContentId))
        {
            errors.Add(new ValidationError(FieldContentId, Loc.F("Val.ContentId", ContentIdHelper.Format)));
        }

        if (normalized.Passcode.Length != BuildRequest.PasscodeLength || normalized.Passcode.Any(c => c > 127 || char.IsControl(c)))
        {
            errors.Add(new ValidationError(FieldPasscode, Loc.T("Val.Passcode")));
        }

        if (!VersionHelper.TryCanonicalize(normalized.Version, out _))
        {
            errors.Add(new ValidationError(FieldVersion, Loc.T("Val.Version")));
        }

        if (normalized.KrakenLevel is < BuildRequest.MinKrakenLevel or > BuildRequest.MaxKrakenLevel)
        {
            errors.Add(new ValidationError(FieldKrakenLevel, Loc.T("Val.KrakenLevel")));
        }

        if (normalized.Threads is < 0 or > BuildRequest.MaxThreads)
        {
            errors.Add(new ValidationError(FieldThreads, Loc.T("Val.Threads")));
        }

        if (normalized.PlayGoChunks is < BuildRequest.MinPlayGoChunks or > BuildRequest.MaxPlayGoChunks)
        {
            errors.Add(new ValidationError(FieldPlayGo, Loc.T("Val.PlayGo")));
        }

        if (normalized.SdkMajorOverride is { } sdk && sdk is < BuildRequest.MinSdkMajor or > BuildRequest.MaxSdkMajor)
        {
            errors.Add(new ValidationError(FieldSdk, Loc.T("Val.Sdk")));
        }

        if (normalized.KrakenBlockKiB is < BuildRequest.MinKrakenBlockKiB or > BuildRequest.MaxKrakenBlockKiB)
        {
            errors.Add(new ValidationError(FieldKrakenBlock, Loc.T("Val.KrakenBlock")));
        }

        if (normalized.ShufflePredictionLevel is { } prediction && prediction is < BuildRequest.MinKrakenLevel or > BuildRequest.MaxKrakenLevel)
        {
            errors.Add(new ValidationError(FieldShuffle, Loc.T("Val.ShufflePrediction")));
        }

        if (normalized.SourceMode == SourceMode.Gp5Project &&
            (string.IsNullOrWhiteSpace(normalized.ProjectFilePath) || !File.Exists(normalized.ProjectFilePath)))
        {
            errors.Add(new ValidationError(FieldProject, Loc.T("Val.ProjectMissing")));
        }

        if (normalized.KrakenBackend == KrakenBackendKind.PublishingTools)
        {
            if (!PublishingToolsLocator.IsSupportedPlatform)
            {
                errors.Add(new ValidationError(FieldPublishingTools, Loc.T("Val.PubToolsPlatform")));
            }
            else if (PublishingToolsLocator.Find(normalized.PublishingToolsPath) == null)
            {
                errors.Add(new ValidationError(FieldPublishingTools, Loc.T("Val.PubToolsMissing")));
            }
        }
        else if (!string.IsNullOrEmpty(normalized.PublishingToolsPath) && normalized.KrakenBackend == KrakenBackendKind.Auto &&
                 PublishingToolsLocator.IsSupportedPlatform && !File.Exists(normalized.PublishingToolsPath))
        {
            errors.Add(new ValidationError(FieldPublishingTools, Loc.T("Val.PubToolsPath")));
        }

        return errors;
    }

    /// <summary>Quyết định backend Kraken thật sự sẽ dùng cho request.</summary>
    public static KrakenBackendKind ResolveBackend(BuildRequest request, out string? publishingToolsPath)
    {
        publishingToolsPath = null;
        switch (request.KrakenBackend)
        {
            case KrakenBackendKind.Uncompressed:
                return KrakenBackendKind.Uncompressed;
            case KrakenBackendKind.BuiltIn:
                return KrakenBackendKind.BuiltIn;
            case KrakenBackendKind.PublishingTools:
                publishingToolsPath = PublishingToolsLocator.Find(request.PublishingToolsPath);
                return KrakenBackendKind.PublishingTools;
            default:
                if (PublishingToolsLocator.IsSupportedPlatform)
                {
                    publishingToolsPath = PublishingToolsLocator.Find(request.PublishingToolsPath);
                    if (publishingToolsPath != null)
                    {
                        return KrakenBackendKind.PublishingTools;
                    }
                }

                return KrakenBackendKind.BuiltIn;
        }
    }

    public static bool IsInside(string candidate, string container)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(container))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(container));
        if (string.Equals(inner, outer, comparison))
        {
            return true;
        }

        return (inner + Path.DirectorySeparatorChar).StartsWith(outer + Path.DirectorySeparatorChar, comparison);
    }

    private static string FullPathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }
}
