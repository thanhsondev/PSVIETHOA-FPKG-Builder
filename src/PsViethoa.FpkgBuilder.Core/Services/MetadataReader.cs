using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Đọc sce_sys/param.json, icon và các tệp PlayGo từ thư mục nguồn, từ ảnh exFAT hoặc theo dự án GP5.</summary>
public static class MetadataReader
{
    private const long MaxParamJsonBytes = 8L * 1024 * 1024;
    private const long MaxIconBytes = 16L * 1024 * 1024;

    public static SourceMetadata Read(string sourcePath, CancellationToken cancellationToken)
    {
        return SourceLocator.Detect(sourcePath) switch
        {
            SourceKind.ExFatImage => ReadImage(sourcePath, cancellationToken),
            SourceKind.UfsImage => ReadUfsImage(sourcePath, cancellationToken),
            SourceKind.Gp5Project => ReadGp5(sourcePath, cancellationToken),
            _ => ReadFolder(sourcePath, cancellationToken),
        };
    }

    private static SourceMetadata ReadFolder(string sourceFolder, CancellationToken cancellationToken)
    {
        var metadata = new SourceMetadata();
        var sceSys = Path.Combine(sourceFolder, "sce_sys");
        metadata.HasSceSys = Directory.Exists(sceSys);
        metadata.HasEboot = File.Exists(Path.Combine(sourceFolder, "eboot.bin"));

        var paramPath = Path.Combine(sceSys, "param.json");
        if (File.Exists(paramPath))
        {
            ReadParamFile(paramPath, metadata);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var iconPath = Path.Combine(sceSys, "icon0.png");
        if (File.Exists(iconPath))
        {
            metadata.IconPath = iconPath;
        }

        metadata.HasPlayGoChunk = File.Exists(Path.Combine(sceSys, "playgo-chunk.dat"));
        metadata.HasPlayGoHashTable = File.Exists(Path.Combine(sceSys, "playgo-hash-table.dat"));
        metadata.HasPlayGoFicm = File.Exists(Path.Combine(sceSys, "playgo-ficm.dat"));
        metadata.HasPlayGoScenario = File.Exists(Path.Combine(sceSys, "playgo-scenario.json"));
        metadata.PlayGoInput = PlayGoCleanup.CheckFolder(sourceFolder);
        metadata.Ampr = AmprInspector.ScanFolder(sourceFolder, cancellationToken);
        metadata.DlcEmu = DlcEmuInspector.ScanFolder(sourceFolder, cancellationToken);
        return metadata;
    }

    private static SourceMetadata ReadImage(string imagePath, CancellationToken cancellationToken)
    {
        var metadata = new SourceMetadata { IsExFat = true };
        using var image = ExFatImage.Open(imagePath);
        metadata.VolumeLabel = image.VolumeLabel;
        metadata.IsPfsContainer = image.IsPfsContainer;
        metadata.ContainerEntryName = image.ContainerEntryName;
        metadata.ContainerStoredLength = image.ContainerStoredLength;

        var appRoot = SourceLocator.FindAppRoot(image);
        if (appRoot == null)
        {
            metadata.HasSceSys = false;
            return metadata;
        }

        metadata.AppRootInImage = appRoot.Path.TrimStart('/');
        var children = image.Enumerate(appRoot).ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
        metadata.HasEboot = children.TryGetValue("eboot.bin", out var eboot) && !eboot.IsDirectory;
        if (!children.TryGetValue("sce_sys", out var sceSys) || !sceSys.IsDirectory)
        {
            metadata.HasSceSys = false;
            return metadata;
        }

        metadata.HasSceSys = true;
        var system = image.Enumerate(sceSys).Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();

        if (system.TryGetValue("param.json", out var param) && param.Length <= MaxParamJsonBytes)
        {
            metadata.HasParamJson = true;
            metadata.ParamJsonPath = imagePath + "!/" + param.Path.TrimStart('/');
            try
            {
                using var stream = image.OpenRead(param);
                ReadParamJson(stream, metadata);
            }
            catch (JsonException ex)
            {
                metadata.HasParamJson = false;
                metadata.ParamJsonError = "param.json: " + ex.Message;
            }
        }

        if (system.TryGetValue("icon0.png", out var icon) && icon.Length > 0 && icon.Length <= MaxIconBytes)
        {
            try
            {
                metadata.IconBytes = image.ReadAllBytes(icon, MaxIconBytes);
            }
            catch (Exception)
            {
                metadata.IconBytes = null;
            }
        }

        metadata.HasPlayGoChunk = system.ContainsKey("playgo-chunk.dat");
        metadata.HasPlayGoHashTable = system.ContainsKey("playgo-hash-table.dat");
        metadata.HasPlayGoFicm = system.ContainsKey("playgo-ficm.dat");
        metadata.HasPlayGoScenario = system.ContainsKey("playgo-scenario.json");
        metadata.PlayGoInput = PlayGoCleanup.CheckImage(image, appRoot);
        metadata.Ampr = AmprInspector.ScanImage(image, appRoot, cancellationToken);
        metadata.DlcEmu = DlcEmuInspector.ScanImage(image, appRoot, cancellationToken);
        return metadata;
    }

    /// <summary>Nguồn là ảnh UFS2 (.ffpkg): đọc thẳng từ ảnh, không giải nén gì cả.</summary>
    private static SourceMetadata ReadUfsImage(string imagePath, CancellationToken cancellationToken)
    {
        var metadata = new SourceMetadata { IsUfs = true };
        using var image = UfsImage.Open(imagePath);

        var appRoot = SourceLocator.FindAppRoot(image);
        if (appRoot == null)
        {
            metadata.HasSceSys = false;
            return metadata;
        }

        metadata.AppRootInImage = appRoot.Path.TrimStart('/');
        var children = image.Enumerate(appRoot).ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
        metadata.HasEboot = children.TryGetValue("eboot.bin", out var eboot) && !eboot.IsDirectory;
        if (!children.TryGetValue("sce_sys", out var sceSys) || !sceSys.IsDirectory)
        {
            metadata.HasSceSys = false;
            return metadata;
        }

        metadata.HasSceSys = true;
        var system = image.Enumerate(sceSys).Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();

        if (system.TryGetValue("param.json", out var param) && param.Length <= MaxParamJsonBytes)
        {
            metadata.HasParamJson = true;
            metadata.ParamJsonPath = imagePath + "!/" + param.Path.TrimStart('/');
            try
            {
                using var stream = image.OpenRead(param);
                ReadParamJson(stream, metadata);
            }
            catch (JsonException ex)
            {
                metadata.HasParamJson = false;
                metadata.ParamJsonError = "param.json: " + ex.Message;
            }
        }

        if (system.TryGetValue("icon0.png", out var icon) && icon.Length > 0 && icon.Length <= MaxIconBytes)
        {
            try
            {
                metadata.IconBytes = image.ReadAllBytes(icon, MaxIconBytes);
            }
            catch (Exception)
            {
                metadata.IconBytes = null;
            }
        }

        metadata.HasPlayGoChunk = system.ContainsKey("playgo-chunk.dat");
        metadata.HasPlayGoHashTable = system.ContainsKey("playgo-hash-table.dat");
        metadata.HasPlayGoFicm = system.ContainsKey("playgo-ficm.dat");
        metadata.HasPlayGoScenario = system.ContainsKey("playgo-scenario.json");
        metadata.PlayGoInput = PlayGoCleanup.CheckImage(image, appRoot);
        return metadata;
    }

    /// <summary>Nguồn là dự án GP5: đọc param.json / icon / eboot / PlayGo theo các đường dẫn mà dự án phân giải.</summary>
    private static SourceMetadata ReadGp5(string projectPath, CancellationToken cancellationToken)
    {
        var project = Gp5ProjectInfo.Load(projectPath);
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = new SourceMetadata
        {
            IsGp5 = true,
            Gp5Layout = project.Layout,
            Gp5RootFolder = project.RootFolder,
            Gp5VolumeType = project.VolumeType,
            Gp5Passcode = project.Passcode is { Length: BuildRequest.PasscodeLength } passcode ? passcode : null,
            HasEboot = project.HasEboot,
            HasPlayGoChunk = project.HasPlayGoChunk,
            HasPlayGoHashTable = project.HasPlayGoHashTable,
            HasPlayGoFicm = project.HasPlayGoFicm,
            HasPlayGoScenario = project.HasPlayGoScenario,
            PlayGoInput = project.RootFolder != null && Directory.Exists(project.RootFolder)
                ? PlayGoCleanup.CheckFolder(project.RootFolder)
                : PlayGoCleanup.InputCheck.None,
        };

        var paramPath = project.ParamJsonPath;
        metadata.HasSceSys = paramPath != null && Directory.Exists(Path.GetDirectoryName(paramPath));
        if (paramPath != null && File.Exists(paramPath))
        {
            ReadParamFile(paramPath, metadata);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (project.IconPath != null && File.Exists(project.IconPath))
        {
            metadata.IconPath = project.IconPath;
        }

        return metadata;
    }

    /// <summary>Đọc một tệp param.json trên đĩa; lỗi cú pháp/IO được ghi vào ParamJsonError thay vì ném ra.</summary>
    private static void ReadParamFile(string paramPath, SourceMetadata metadata)
    {
        metadata.HasParamJson = true;
        metadata.ParamJsonPath = paramPath;
        try
        {
            using var stream = File.OpenRead(paramPath);
            ReadParamJson(stream, metadata);
        }
        catch (JsonException ex)
        {
            metadata.HasParamJson = false;
            metadata.ParamJsonError = "param.json: " + ex.Message;
        }
        catch (IOException ex)
        {
            metadata.HasParamJson = false;
            metadata.ParamJsonError = "param.json: " + ex.Message;
        }
    }

    private static void ReadParamJson(Stream stream, SourceMetadata metadata)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("root is not an object");
        }

        metadata.ContentId = ReadString(root, "contentId");
        metadata.TitleId = ReadString(root, "titleId") ?? ContentIdHelper.TitleIdOf(metadata.ContentId);
        metadata.Version = ReadString(root, "contentVersion");
        metadata.SdkVersionRaw = ReadString(root, "sdkVersion");
        if (SdkVersions.TryReadMajor(metadata.SdkVersionRaw, out var major))
        {
            metadata.SdkMajor = major;
        }

        if (root.TryGetProperty("applicationCategoryType", out var category) && category.ValueKind == JsonValueKind.Number &&
            category.TryGetInt32(out var categoryValue))
        {
            metadata.CategoryType = categoryValue;
        }

        metadata.ApplicationDrmType = ReadString(root, "applicationDrmType");
        metadata.Title = ReadLocalizedTitle(root);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadLocalizedTitle(JsonElement root)
    {
        if (!root.TryGetProperty("localizedParameters", out var localized) || localized.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (localized.TryGetProperty("defaultLanguage", out var defaultLanguage) &&
            defaultLanguage.ValueKind == JsonValueKind.String &&
            defaultLanguage.GetString() is { } language &&
            localized.TryGetProperty(language, out var block) &&
            block.ValueKind == JsonValueKind.Object &&
            block.TryGetProperty("titleName", out var titleName) &&
            titleName.ValueKind == JsonValueKind.String)
        {
            return titleName.GetString();
        }

        foreach (var property in localized.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("titleName", out var fallback) &&
                fallback.ValueKind == JsonValueKind.String)
            {
                return fallback.GetString();
            }
        }

        return null;
    }

    /// <summary>Mô tả trạng thái PlayGo.</summary>
    public static string DescribePlayGo(SourceMetadata metadata, int chunkCount, bool regenerate = false)
    {
        // Engine 0.6.8 không chép bảng playgo* của nguồn vào gói: luôn tạo lại, chỉ lấy số khối/kịch bản từ nguồn khi còn giữ tệp.
        if (regenerate && metadata.PlayGoFileCount + (metadata.HasPlayGoScenario ? 1 : 0) > 0)
        {
            return Loc.F("PlayGo.Regenerate", metadata.PlayGoFileCount + (metadata.HasPlayGoScenario ? 1 : 0), chunkCount);
        }

        var input = metadata.PlayGoInput;
        if (input.Invalid.Count > 0)
        {
            return Loc.F("PlayGo.Invalid", PlayGoCleanup.Describe(input.Invalid), input.Counts is { Chunks: > 0 } valid ? valid.Chunks : chunkCount);
        }

        return input.Counts switch
        {
            { Chunks: > 0 } counts => Loc.F("PlayGo.Import", counts.Chunks, counts.Scenarios),
            { } counts => Loc.F("PlayGo.ImportScenarios", chunkCount, counts.Scenarios),
            _ => Loc.F("PlayGo.Auto", chunkCount),
        };
    }
}
