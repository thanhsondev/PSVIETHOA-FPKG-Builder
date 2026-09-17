using System.Text.Json;
using LibProsperoPkg.GP5;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Kiểm thử nguồn dự án GP5: nhận diện, metadata, thống kê theo mặt nạ loại trừ, kiểm tra request
/// và tạo gói thật từ dự án có đường dẫn gốc tương đối.
/// </summary>
public sealed class Gp5Tests : IDisposable
{
    private const string ContentId = "UP9000-PPSA00003_00-PSVIETHOAGP5TEST";
    private const string NormalPasscode = "PSVIETHOAGP5PASSCODE000000000000";
    private const string GarbageText = "this is not a gp5 project";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-gp5-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;
    private readonly string _projects;

    public Gp5Tests()
    {
        _app = Path.Combine(_root, "src");
        _projects = Path.Combine(_root, "projects");
        Directory.CreateDirectory(Path.Combine(_app, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(_app, "data", "sub"));
        Directory.CreateDirectory(_projects);

        File.WriteAllText(
            Path.Combine(_app, "sce_sys", "param.json"),
            "{\"contentId\":\"" + ContentId + "\",\"contentVersion\":\"01.000.000\",\"sdkVersion\":\"0x0450000000000000\"," +
            "\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"GP5 Test\"}}}");
        File.WriteAllBytes(Path.Combine(_app, "eboot.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(_app, "data", "a.bin"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(_app, "data", "sub", "b.bin"), new byte[500]);

        // Hai tệp bị mặt nạ file_exclude mặc định ("*.gp5;…;keystone;…") loại khỏi bố cục Normal.
        File.WriteAllText(Path.Combine(_app, "old.gp5"), GarbageText);
        File.WriteAllBytes(Path.Combine(_app, "keystone"), new byte[64]);

        // Normal (gốc tuyệt đối), Normal (gốc tương đối "../src") và Flat (liệt kê từng tệp) trong thư mục anh em.
        Gp5Project.WriteTo(Gp5Creator.FromFolder(_app, Gp5VolumeType.prospero_app, NormalPasscode, null), Path.Combine(_projects, "normal.gp5"));
        Gp5Project.WriteTo(Gp5Creator.FromFolder(_app, Gp5VolumeType.prospero_app, new string('0', 32), "../src"), Path.Combine(_projects, "rel.gp5"));
        Gp5Project.WriteTo(Gp5Creator.FromFolderExplicit(_app, Gp5VolumeType.prospero_app, new string('0', 32)), Path.Combine(_projects, "flat.gp5"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private string Project(string name) => Path.Combine(_projects, name);

    [Theory]
    [InlineData("normal.gp5", "Normal")]
    [InlineData("rel.gp5", "Normal")]
    [InlineData("flat.gp5", "Flat")]
    public void SourceLocator_DetectsAndResolvesProjects(string name, string layout)
    {
        var path = Project(name);
        Assert.True(SourceLocator.HasGp5Extension(path));
        Assert.Equal(SourceKind.Gp5Project, SourceLocator.Detect(path));
        Assert.Equal(SourceKind.None, SourceLocator.Detect(Path.Combine(_projects, "missing.gp5")));

        var source = SourceLocator.Resolve(path);
        Assert.True(source.IsGp5);
        Assert.False(source.IsExFat);
        Assert.Equal(Path.GetFullPath(path), source.Path);
        Assert.Equal(Path.GetFullPath(_projects), source.ProjectDirectory);

        var project = Gp5ProjectInfo.Load(path);
        Assert.Equal(layout, project.Layout);
        Assert.Equal("prospero_app", project.VolumeType);
        Assert.Equal(Path.GetFullPath(_app), project.RootFolder);
        Assert.True(File.Exists(project.ParamJsonPath));
        Assert.True(project.HasEboot);
        Assert.False(project.HasPlayGoChunk);
        Assert.Equal(0, project.MissingFileCount);
    }

    [Theory]
    [InlineData("normal.gp5", "Normal", NormalPasscode)]
    [InlineData("rel.gp5", "Normal", "00000000000000000000000000000000")]
    [InlineData("flat.gp5", "Flat", "00000000000000000000000000000000")]
    public void MetadataReader_ReadsProjectMetadata(string name, string layout, string passcode)
    {
        var metadata = MetadataReader.Read(Project(name), CancellationToken.None);
        Assert.True(metadata.IsGp5);
        Assert.False(metadata.IsExFat);
        Assert.Equal(layout, metadata.Gp5Layout);
        Assert.Equal(Path.GetFullPath(_app), metadata.Gp5RootFolder);
        Assert.Equal("prospero_app", metadata.Gp5VolumeType);
        Assert.Equal(passcode, metadata.Gp5Passcode);
        Assert.True(metadata.HasSceSys);
        Assert.True(metadata.HasParamJson);
        Assert.True(metadata.HasEboot);
        Assert.Equal(ContentId, metadata.ContentId);
        Assert.Equal("PPSA00003", metadata.TitleId);
        Assert.Equal("GP5 Test", metadata.Title);
        Assert.Equal("01.000.000", metadata.Version);
        Assert.Equal(4, metadata.SdkMajor);
        Assert.Null(metadata.IconPath);
        Assert.Null(metadata.IconBytes);
        Assert.Equal(0, metadata.PlayGoFileCount);
    }

    [Fact]
    public void FolderScanner_HonoursMasksInNormalLayoutAndListsEverythingInFlatLayout()
    {
        var paramLength = new FileInfo(Path.Combine(_app, "sce_sys", "param.json")).Length;
        var garbageLength = new FileInfo(Path.Combine(_app, "old.gp5")).Length;

        var normal = FolderScanner.Scan(Project("normal.gp5"), CancellationToken.None);
        Assert.Equal(4, normal.FileCount);
        Assert.Equal(3, normal.DirectoryCount);
        Assert.Equal(1024 + 3000 + 500 + paramLength, normal.TotalBytes);
        Assert.Equal(3000, normal.LargestFileBytes);

        var relative = FolderScanner.Scan(Project("rel.gp5"), CancellationToken.None);
        Assert.Equal(normal.FileCount, relative.FileCount);
        Assert.Equal(normal.TotalBytes, relative.TotalBytes);

        var flat = FolderScanner.Scan(Project("flat.gp5"), CancellationToken.None);
        Assert.Equal(6, flat.FileCount);
        Assert.Equal(3, flat.DirectoryCount);
        Assert.Equal(1024 + 3000 + 500 + 64 + paramLength + garbageLength, flat.TotalBytes);

        var entries = Gp5ProjectInfo.Load(Project("flat.gp5")).EnumerateFiles().ToList();
        Assert.Contains(entries, e => e.DestinationPath == "sce_sys/param.json");
        Assert.Contains(entries, e => e.DestinationPath == "data/sub/b.bin" && e.Length == 500);
        Assert.All(entries, e => Assert.True(File.Exists(e.SourcePath)));

        var normalEntries = Gp5ProjectInfo.Load(Project("normal.gp5")).EnumerateFiles().Select(e => e.DestinationPath).ToList();
        Assert.DoesNotContain("old.gp5", normalEntries);
        Assert.DoesNotContain("keystone", normalEntries);
        Assert.Contains("eboot.bin", normalEntries);

        Assert.Empty(JunkFileFinder.Find(Project("normal.gp5"), CancellationToken.None));
    }

    [Fact]
    public void BuildPreparer_ValidatesProjects()
    {
        var request = new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = Project("rel.gp5"),
            OutputFolder = Path.Combine(_root, "rel-pkg"),
            TemporaryFolder = Path.Combine(_root, "tmp"),
            ContentId = ContentId,
            KrakenBackend = KrakenBackendKind.BuiltIn,
        };

        Assert.Empty(BuildPreparer.Validate(request));

        // Gợi ý xuất đặt cạnh thư mục chứa .gp5: thư viện từ chối xuất vào bên trong thư mục đó.
        Assert.Equal(Path.Combine(_root, "rel-pkg"), BuildPreparer.SuggestOutputFolder(request.SourcePath));

        var normalized = BuildPreparer.Normalize(request);
        Assert.Equal(SourceMode.Gp5Project, normalized.SourceMode);
        Assert.Equal(Path.GetFullPath(request.SourcePath), normalized.ProjectFilePath);
        Assert.Equal(Path.GetFullPath(request.SourcePath), normalized.SourcePath);

        // Thư mục xuất / tạm nằm trong thư mục gốc mà dự án trỏ tới.
        request.OutputFolder = Path.Combine(_app, "out");
        Assert.Contains(BuildPreparer.Validate(request), e => e.Field == BuildPreparer.FieldOutput && e.Message == Loc.T("Val.OutputInsideSource"));
        request.OutputFolder = Path.Combine(_root, "rel-pkg");
        request.TemporaryFolder = Path.Combine(_app, "data", "tmp");
        Assert.Contains(BuildPreparer.Validate(request), e => e.Field == BuildPreparer.FieldTemporary && e.Message == Loc.T("Val.TempInsideSource"));
        request.TemporaryFolder = Path.Combine(_root, "tmp");

        // Thư mục xuất / tạm nằm trong thư mục chứa tệp .gp5 (thư viện coi đó là thư mục nguồn).
        request.OutputFolder = Project("rel-pkg");
        Assert.Contains(BuildPreparer.Validate(request), e => e.Field == BuildPreparer.FieldOutput && e.Message == Loc.T("Val.OutputInsideProject"));
        request.OutputFolder = Path.Combine(_root, "rel-pkg");
        request.TemporaryFolder = Project("tmp");
        Assert.Contains(BuildPreparer.Validate(request), e => e.Field == BuildPreparer.FieldTemporary && e.Message == Loc.T("Val.TempInsideProject"));
        request.TemporaryFolder = Path.Combine(_root, "tmp");

        // Tệp .gp5 rác.
        request.SourcePath = Path.Combine(_app, "old.gp5");
        var errors = BuildPreparer.Validate(request);
        Assert.Contains(errors, e => e.Field == BuildPreparer.FieldSource && e.Message.StartsWith(Loc.T("Val.Gp5Invalid"), StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => Gp5ProjectInfo.Load(request.SourcePath));

        // Dự án hợp lệ nhưng thư mục gốc không có sce_sys/param.json.
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        var orphan = Gp5Project.Create(Gp5VolumeType.prospero_app, new string('0', 32));
        orphan.RootDir = new Gp5RootDir { SourcePath = "../empty" };
        Gp5Project.WriteTo(orphan, Project("orphan.gp5"));
        request.SourcePath = Project("orphan.gp5");
        Assert.Contains(BuildPreparer.Validate(request), e => e.Field == BuildPreparer.FieldSource && e.Message == Loc.T("Val.Gp5NoParam"));
    }

    [Fact]
    public async Task Engine_BuildsPackageFromRelativeRootProject()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var request = new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = Project("rel.gp5"),
            OutputFolder = Path.Combine(_root, "rel-pkg"),
            TemporaryFolder = Path.Combine(_root, "tmp"),
            ContentId = ContentId,
            Title = "GP5 Test",
            KrakenBackend = KrakenBackendKind.BuiltIn,
            KrakenLevel = 2,
            PreventSleep = false,
        };

        Assert.Empty(BuildPreparer.Validate(request));

        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        Assert.True(File.Exists(outcome.OutputPath));
        Assert.Equal("FullDebug", outcome.Verification.ContainerType);
        Assert.Equal(ContentId, outcome.Verification.ContentId);
        Assert.Contains(log, e => e.Message.Contains("rel.gp5", StringComparison.Ordinal) && e.Message.Contains("Normal", StringComparison.Ordinal));
    }

    [Fact]
    public void Localization_TablesShareTheSameKeysIncludingGp5()
    {
        static HashSet<string> Keys(string code)
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"PsViethoa.FpkgBuilder.Core.Localization.{code}.json");
            Assert.NotNull(stream);
            using var document = JsonDocument.Parse(stream!);
            return document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        }

        var vi = Keys(Loc.Vietnamese);
        var en = Keys(Loc.English);
        Assert.Empty(vi.Except(en));
        Assert.Empty(en.Except(vi));

        foreach (var key in new[]
                 {
                     "Step1.PickGp5", "Step1.PickGp5Tip", "Pick.Gp5", "Pick.Gp5Filter", "Meta.Gp5Chip", "Meta.Gp5Root",
                     "Val.Gp5Invalid", "Val.Gp5NoParam", "Val.OutputInsideProject", "Val.TempInsideProject", "Plan.SourceGp5", "Cli.SourceGp5",
                     "Advanced.SourceMode", "SourceMode.Auto", "SourceMode.Folder", "Advanced.SourceModeHint",
                 })
        {
            Assert.Contains(key, en);
        }
    }
}
