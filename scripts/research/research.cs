#:project ../../src/PsViethoa.FpkgBuilder.Core/PsViethoa.FpkgBuilder.Core.csproj
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false

// Công cụ nghiên cứu CHỈ ĐỌC nguồn game (PSVIETHOA FPKG Builder 2.2.0): dùng đúng mã của ứng dụng (SonySdkPlayGo, PackageReader).
//   dotnet run scripts/research/research.cs -- chunkmap <thư mục game> <out.json>
//   dotnet run scripts/research/research.cs -- pkgplaygo <gói.pkg> <thư mục xuất> [out.json]
//   dotnet run scripts/research/research.cs -- compare <thư mục game> <gói.pkg> <thư mục xuất> <out.json>
//   dotnet run scripts/research/research.cs -- mkfixture <thư mục game> <thư mục fixture> [số tệp mỗi chunk]
// Mọi đường dẫn GHI bị từ chối nếu nằm trên ổ được bảo vệ (mặc định G:\, đổi bằng PSVIETHOA_PROTECTED="G:\;H:\").

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsViethoa.FpkgBuilder.Core.Services;

// LibProsperoPkg chỉ được Core tham chiếu (HintPath) nên không dùng trực tiếp được trong ứng dụng một tệp: gọi HashPath qua reflection.
var hashPathMethod = typeof(SonySdkPlayGo).Assembly.GetReferencedAssemblies()
    .Select(name => System.Reflection.Assembly.Load(name))
    .Select(assembly => assembly.GetType("LibProsperoPkg.PFS.ProsperoPs5FlatPathTable"))
    .First(type => type != null)!
    .GetMethod("HashPath", [typeof(string)])!;
ulong HashPath(string path) => (ulong)hashPathMethod.Invoke(null, [path])!;

var protectedRoots = (Environment.GetEnvironmentVariable("PSVIETHOA_PROTECTED") ?? @"G:\")
    .Split(';', StringSplitOptions.RemoveEmptyEntries)
    .Select(root => Path.GetFullPath(root))
    .ToArray();

string Writable(string path)
{
    var full = Path.GetFullPath(path);
    foreach (var root in protectedRoots)
    {
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"REFUSED: write path {full} is on protected drive {root}");
        }
    }

    return full;
}

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

void WriteJson(string path, JsonNode node)
{
    path = Writable(path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, node.ToJsonString(jsonOptions));
}

JsonObject Describe(PlayGoStructure s) => new()
{
    ["chunkCount"] = s.Chunks.Count,
    ["scenarioCount"] = s.Scenarios.Count,
    ["supportedLanguages"] = string.Join(' ', SonySdkPlayGo.Codes(s.SupportedLanguageMask)),
    ["defaultLanguageId"] = s.DefaultLanguageId,
    ["defaultScenarioId"] = s.DefaultScenarioId,
    ["fileTableEntries"] = s.FileChunks.Count,
    ["chunks"] = new JsonArray(s.Chunks.Select(c => (JsonNode)new JsonObject
    {
        ["id"] = c.Id,
        ["label"] = c.Label,
        ["languages"] = c.LanguageMask == s.SupportedLanguageMask ? "*" : string.Join(' ', SonySdkPlayGo.Codes(c.LanguageMask)),
        ["mask"] = "0x" + c.LanguageMask.ToString("x16"),
    }).ToArray()),
    ["scenarios"] = new JsonArray(s.Scenarios.Select(x => (JsonNode)new JsonObject
    {
        ["id"] = x.Id,
        ["label"] = x.Label,
        ["initialChunkCount"] = x.InitialChunkCount,
        ["order"] = string.Join(' ', x.ChunkOrder),
    }).ToArray()),
};

IEnumerable<(string Relative, long Length)> WalkWithLength(string root)
{
    // FileInfo từ lần liệt kê đã có sẵn kích thước (FindFirstFile) — không mở/stat thêm từng tệp trên ổ USB.
    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
    foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
    {
        yield return (Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.Length);
    }
}

IEnumerable<string> Walk(string root) => WalkWithLength(root).Select(item => item.Relative);

JsonObject ChunkMap(string game)
{
    var sceSys = Path.Combine(game, "sce_sys");
    var structure = SonySdkPlayGo.TryRead(sceSys, out var problem);
    var result = new JsonObject { ["game"] = game, ["problem"] = problem };
    if (structure == null)
    {
        result["structure"] = null;
        return result;
    }

    result["structure"] = Describe(structure);
    var counts = new Dictionary<int, (int Files, long Bytes, List<string> Samples)>();
    var unmatched = new List<string>();
    var unmatchedCount = 0;
    var seenHashes = new HashSet<ulong>();
    var excludedByToolkit = new JsonArray();
    var total = 0;
    foreach (var (relative, length) in WalkWithLength(game))
    {
        total++;
        seenHashes.Add(HashPath("/" + relative));
        seenHashes.Add(HashPath(relative));
        var chunk = structure.ChunkOf(relative);
        var reason = SonySdkProject.SkipReason(relative, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SonySdkProject.KeystonePath });
        if (reason != null)
        {
            excludedByToolkit.Add($"{relative} ({reason}) chunk={(chunk?.ToString() ?? "none")}");
        }

        if (chunk == null)
        {
            unmatchedCount++;
            if (unmatched.Count < 300)
            {
                unmatched.Add(relative);
            }

            continue;
        }

        if (!counts.TryGetValue(chunk.Value, out var entry))
        {
            entry = (0, 0, new List<string>());
        }

        if (entry.Samples.Count < 6)
        {
            entry.Samples.Add(relative);
        }

        counts[chunk.Value] = (entry.Files + 1, entry.Bytes + length, entry.Samples);
    }

    var missing = structure.FileChunks.Keys.Count(hash => !seenHashes.Contains(hash));
    var missingPerChunk = structure.FileChunks.Where(pair => !seenHashes.Contains(pair.Key)).GroupBy(pair => pair.Value).OrderBy(g => g.Key)
        .ToDictionary(g => g.Key.ToString(), g => g.Count());
    result["filesOnDisk"] = total;
    result["filesMatchedInTable"] = total - unmatchedCount;
    result["filesNotInTable"] = unmatchedCount;
    result["filesNotInTableSample"] = new JsonArray(unmatched.Select(x => (JsonNode)x!).ToArray());
    result["tableEntriesWithoutFile"] = missing;
    result["tableEntriesWithoutFilePerChunk"] = JsonSerializer.SerializeToNode(missingPerChunk);
    result["excludedByToolkit"] = excludedByToolkit;
    result["perChunk"] = new JsonArray(counts.OrderBy(pair => pair.Key).Select(pair => (JsonNode)new JsonObject
    {
        ["chunk"] = pair.Key,
        ["label"] = structure.Chunks[pair.Key].Label,
        ["files"] = pair.Value.Files,
        ["bytes"] = pair.Value.Bytes,
        ["samples"] = new JsonArray(pair.Value.Samples.Select(x => (JsonNode)x!).ToArray()),
    }).ToArray());
    return result;
}

(PlayGoStructure? Structure, JsonObject Info) PackagePlayGo(string pkg, string outDir)
{
    outDir = Writable(outDir);
    Directory.CreateDirectory(outDir);
    var written = PackageReader.ExportCntEntries(pkg, outDir, new string('0', 32), CancellationToken.None);
    var info = new JsonObject { ["package"] = pkg, ["exported"] = new JsonArray(written.Select(x => (JsonNode)x!).ToArray()) };
    byte[]? Read(string name)
    {
        var path = Directory.EnumerateFiles(outDir, name, SearchOption.AllDirectories).FirstOrDefault();
        return path == null ? null : File.ReadAllBytes(path);
    }

    var chunk = Read("playgo-chunk.dat");
    if (chunk == null)
    {
        info["structure"] = null;
        return (null, info);
    }

    var structure = SonySdkPlayGo.Parse(chunk, Read("playgo-ficm.dat"), Read("playgo-hash-table.dat"));
    info["structure"] = Describe(structure);
    var param = Read("param.json");
    if (param != null)
    {
        info["param"] = JsonNode.Parse(param);
    }

    var scenario = Read("playgo-scenario.json");
    if (scenario != null)
    {
        info["playgoScenarioJson"] = System.Text.Encoding.UTF8.GetString(scenario);
    }

    return (structure, info);
}

JsonObject Compare(string game, string pkg, string outDir)
{
    var original = SonySdkPlayGo.TryRead(Path.Combine(game, "sce_sys"), out var problem) ?? throw new InvalidDataException("source PlayGo unreadable: " + problem);
    var (rebuilt, info) = PackagePlayGo(pkg, outDir);
    var report = new JsonObject { ["game"] = game, ["package"] = pkg, ["original"] = Describe(original), ["package"] = info };
    if (rebuilt == null)
    {
        report["verdict"] = "package has no playgo-chunk.dat";
        return report;
    }

    var differences = new JsonArray();
    if (original.Chunks.Count != rebuilt.Chunks.Count)
    {
        differences.Add($"chunk count {original.Chunks.Count} -> {rebuilt.Chunks.Count}");
    }

    if (original.Scenarios.Count != rebuilt.Scenarios.Count)
    {
        differences.Add($"scenario count {original.Scenarios.Count} -> {rebuilt.Scenarios.Count}");
    }

    if (original.SupportedLanguageMask != rebuilt.SupportedLanguageMask)
    {
        differences.Add($"supported mask {original.SupportedLanguageMask:x16} -> {rebuilt.SupportedLanguageMask:x16}");
    }

    if (original.DefaultLanguageId != rebuilt.DefaultLanguageId)
    {
        differences.Add($"default language {original.DefaultLanguageId} -> {rebuilt.DefaultLanguageId}");
    }

    if (original.DefaultScenarioId != rebuilt.DefaultScenarioId)
    {
        differences.Add($"default scenario {original.DefaultScenarioId} -> {rebuilt.DefaultScenarioId}");
    }

    foreach (var chunk in original.Chunks)
    {
        if (chunk.Id >= rebuilt.Chunks.Count)
        {
            continue;
        }

        var other = rebuilt.Chunks[chunk.Id];
        if (other.LanguageMask != chunk.LanguageMask)
        {
            differences.Add($"chunk {chunk.Id} mask {chunk.LanguageMask:x16} -> {other.LanguageMask:x16}");
        }

        if (other.Label != chunk.Label)
        {
            differences.Add($"chunk {chunk.Id} label '{chunk.Label}' -> '{other.Label}'");
        }
    }

    foreach (var scenario in original.Scenarios)
    {
        if (scenario.Id >= rebuilt.Scenarios.Count)
        {
            continue;
        }

        var other = rebuilt.Scenarios[scenario.Id];
        if (other.InitialChunkCount != scenario.InitialChunkCount)
        {
            differences.Add($"scenario {scenario.Id} initial {scenario.InitialChunkCount} -> {other.InitialChunkCount}");
        }

        if (!other.ChunkOrder.SequenceEqual(scenario.ChunkOrder))
        {
            differences.Add($"scenario {scenario.Id} order '{string.Join(' ', scenario.ChunkOrder)}' -> '{string.Join(' ', other.ChunkOrder)}'");
        }

        if (other.Label != scenario.Label)
        {
            differences.Add($"scenario {scenario.Id} label '{scenario.Label}' -> '{other.Label}'");
        }
    }

    // Ánh xạ tệp → chunk theo hash: tệp gốc có trong gói mới phải cùng chunk.
    var sameChunk = 0;
    var otherChunk = 0;
    var absent = 0;
    var mismatches = new JsonArray();
    foreach (var (hash, chunk) in original.FileChunks)
    {
        if (!rebuilt.FileChunks.TryGetValue(hash, out var now))
        {
            absent++;
            continue;
        }

        if (now == chunk)
        {
            sameChunk++;
        }
        else
        {
            otherChunk++;
            if (mismatches.Count < 50)
            {
                mismatches.Add($"hash {hash:x16}: chunk {chunk} -> {now}");
            }
        }
    }

    var added = rebuilt.FileChunks.Keys.Count(hash => !original.FileChunks.ContainsKey(hash));
    report["differences"] = differences;
    report["fileMap"] = new JsonObject
    {
        ["originalEntries"] = original.FileChunks.Count,
        ["packageEntries"] = rebuilt.FileChunks.Count,
        ["sameChunk"] = sameChunk,
        ["differentChunk"] = otherChunk,
        ["notInPackage"] = absent,
        ["onlyInPackage"] = added,
        ["mismatchSample"] = mismatches,
    };
    report["verdict"] = differences.Count == 0 && otherChunk == 0 ? "IDENTICAL-STRUCTURE" : "DIFFERENT";
    return report;
}

void MakeFixture(string game, string fixture, int perChunk)
{
    fixture = Writable(fixture);
    if (Directory.Exists(fixture))
    {
        throw new InvalidOperationException("fixture folder already exists: " + fixture);
    }

    var structure = SonySdkPlayGo.TryRead(Path.Combine(game, "sce_sys"), out var problem) ?? throw new InvalidDataException("no PlayGo: " + problem);
    Directory.CreateDirectory(Path.Combine(fixture, "sce_sys"));
    foreach (var name in new[] { "param.json", "keystone", "playgo-chunk.dat", "playgo-ficm.dat", "playgo-hash-table.dat", "playgo-scenario.json" })
    {
        var source = Path.Combine(game, "sce_sys", name);
        if (File.Exists(source))
        {
            File.WriteAllBytes(Path.Combine(fixture, "sce_sys", name), File.ReadAllBytes(source));
        }
    }

    var taken = new Dictionary<int, int>();
    var random = new Random(1234);
    var created = 0;
    foreach (var relative in Walk(game))
    {
        if (relative.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var chunk = structure.ChunkOf(relative) ?? -1;
        taken.TryGetValue(chunk, out var count);
        var keep = relative.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) || count < perChunk;
        if (!keep)
        {
            continue;
        }

        taken[chunk] = count + 1;
        var target = Path.Combine(fixture, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var data = new byte[4096 + random.Next(0, 60000)];
        random.NextBytes(data.AsSpan(0, data.Length / 3));
        File.WriteAllBytes(target, data);
        created++;
    }

    Console.WriteLine($"fixture {fixture}: {created} placeholder files, chunks sampled: {string.Join(", ", taken.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"))}");
}

try
{
    switch (args.FirstOrDefault())
    {
        case "chunkmap":
            WriteJson(args[2], ChunkMap(args[1]));
            Console.WriteLine("wrote " + args[2]);
            break;
        case "pkgplaygo":
            var (_, info) = PackagePlayGo(args[1], args[2]);
            if (args.Length > 3)
            {
                WriteJson(args[3], info);
            }
            else
            {
                Console.WriteLine(info.ToJsonString(jsonOptions));
            }

            break;
        case "compare":
            var report = Compare(args[1], args[2], args[3]);
            WriteJson(args[4], report);
            Console.WriteLine($"{report["verdict"]}: {report["differences"]?.ToJsonString()} fileMap={report["fileMap"]?.ToJsonString()}");
            break;
        case "mkfixture":
            MakeFixture(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 2);
            break;
        default:
            Console.Error.WriteLine("commands: chunkmap | pkgplaygo | compare | mkfixture");
            return 1;
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 2;
}
