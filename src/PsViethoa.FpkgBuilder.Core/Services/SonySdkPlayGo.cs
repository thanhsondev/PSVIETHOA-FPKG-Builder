using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using LibProsperoPkg.PFS;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Một chunk PlayGo của gói gốc: mã, mask ngôn ngữ (bit 63 − id ngôn ngữ) và nhãn. <paramref name="LanguagesText"/>: chuỗi
/// <c>languages</c> ghi nguyên văn vào GP5 (bố cục script fix6), null = suy từ mask.</summary>
public sealed record PlayGoChunk(int Id, ulong LanguageMask, string Label, string? LanguagesText = null);

/// <summary>Một kịch bản PlayGo của gói gốc: số chunk cần trước khi chạy, thứ tự tải chunk, nhãn và kiểu (<c>type</c> của GP5).</summary>
public sealed record PlayGoScenario(int Id, int InitialChunkCount, IReadOnlyList<int> ChunkOrder, string Label, string Type = "playmode");

/// <summary>Tệp giữ chỗ 1 MiB cho một chunk ngôn ngữ (bố cục script fix6): đường dẫn trong gói, chunk và mã ngôn ngữ.</summary>
public sealed record PlayGoLanguagePayload(string Destination, int ChunkId, string Language);

/// <summary>
/// Cấu trúc PlayGo của gói gốc đọc từ <c>sce_sys/playgo-chunk.dat</c> (+ <c>playgo-ficm.dat</c>, <c>playgo-hash-table.dat</c>
/// khi có) của bản dump: chunk, ngôn ngữ, kịch bản và tệp nào thuộc chunk nào. Game nhiều ngôn ngữ thoại (Ghost of Yōtei…) đặt
/// mỗi gói thoại vào một chunk riêng và hỏi hệ thống chunk đó đã cài chưa; gói chỉ có 1 chunk (script gốc) thì game coi ngôn ngữ
/// chưa cài, làm mờ tuỳ chọn và đòi tải từ PSN. Giữ nguyên cấu trúc chunk khi tạo GP5 thì mọi chunk đều "đã cài" trong FPKG.
/// </summary>
/// <param name="SupportedLanguageMask">Mask ngôn ngữ chung của gói (header @56).</param>
/// <param name="DefaultLanguageId">Ngôn ngữ mặc định (header @36).</param>
/// <param name="DefaultScenarioId">Kịch bản mặc định (header @20).</param>
/// <param name="FileChunks">Hash bảng đường dẫn phẳng (PS5 flat path table) → chunk, từ playgo-ficm.dat + playgo-hash-table.dat; rỗng khi thiếu hai tệp đó.</param>
public sealed record PlayGoStructure(
    ulong SupportedLanguageMask,
    int DefaultLanguageId,
    int DefaultScenarioId,
    IReadOnlyList<PlayGoChunk> Chunks,
    IReadOnlyList<PlayGoScenario> Scenarios,
    IReadOnlyDictionary<ulong, int> FileChunks)
{
    public int ChunkCount => Chunks.Count;

    /// <summary>
    /// Bố cục của script create-gp5-from-folder.py (fix6) khi nguồn không còn playgo-chunk.dat: 100 chunk, mỗi ngôn ngữ trong
    /// playgo-scenario.json một chunk với tệp giữ chỗ 1 MiB, mọi chunk initial, kịch bản "0-99". GP5 ghi theo cú pháp của script
    /// (layer_no, languages trên mọi chunk, chunk="0" trên mọi tệp) để giống từng byte.
    /// </summary>
    public bool ScriptLayout { get; init; }

    /// <summary>Bố cục script: chuỗi <c>supported_languages</c> theo đúng thứ tự của playgo-scenario.json.</summary>
    public string? SupportedLanguagesText { get; init; }

    /// <summary>Bố cục script: <c>default_language</c> (chunkDefaultLanguage).</summary>
    public string? DefaultLanguageCode { get; init; }

    /// <summary>Bố cục script: nội dung playgo-scenario.json ghi ra (json.dumps(indent=2) + "\n", LF).</summary>
    public string? ScenarioJson { get; init; }

    /// <summary>Bố cục script: tệp giữ chỗ cho từng chunk ngôn ngữ.</summary>
    public IReadOnlyList<PlayGoLanguagePayload> LanguagePayloads { get; init; } = Array.Empty<PlayGoLanguagePayload>();

    /// <summary>Gói gốc chỉ có một chunk / một kịch bản: giữ nguyên cách của script gốc (không cần chunk_info riêng).</summary>
    public bool IsTrivial => Chunks.Count == 1 && Scenarios.Count == 1;

    /// <summary>Chunk mà tệp <paramref name="relativePath"/> (a/b/c) thuộc về trong gói gốc; null khi không có trong bảng.</summary>
    public int? ChunkOf(string relativePath)
    {
        if (FileChunks.Count == 0)
        {
            return null;
        }

        return FileChunks.TryGetValue(ProsperoPs5FlatPathTable.HashPath("/" + relativePath), out var chunk)
            ? chunk
            : FileChunks.TryGetValue(ProsperoPs5FlatPathTable.HashPath(relativePath), out chunk) ? chunk : null;
    }
}

/// <summary>
/// Đọc bảng PlayGo <c>plgx</c> phiên bản 0x1000 của PS5 (bố cục theo bộ kiểm tra gói của LibProsperoPkg 0.6.8 và bảng do Publishing
/// Tools 2.79 tạo ra) và tạo phần <c>chunk_info</c> cho GP5 theo đúng cú pháp Publishing Tools ghi ra (<c>gp5_chunk_add
/// --languages</c>, <c>gp5_scenario_add</c>, <c>gp5_file_add --chunk</c>).
/// </summary>
public static class SonySdkPlayGo
{
    /// <summary>Mã ngôn ngữ PlayGo của PS5 theo id (bit 63 − id trong mask), đúng bảng của Publishing Tools.</summary>
    public static readonly IReadOnlyList<string> LanguageCodes =
    [
        "ja-JP", "en-US", "fr-FR", "es-ES", "de-DE", "it-IT", "nl-NL", "pt-PT", "ru-RU", "ko-KR", "zh-Hant", "zh-Hans", "fi-FI", "sv-SE",
        "da-DK", "no-NO", "pl-PL", "pt-BR", "en-GB", "tr-TR", "es-419", "ar-AE", "fr-CA", "cs-CZ", "hu-HU", "el-GR", "ro-RO", "th-TH",
        "vi-VN", "id-ID", "uk-UA",
    ];

    public const string ChunkFileName = "playgo-chunk.dat";
    public const string FicmFileName = "playgo-ficm.dat";
    public const string HashTableFileName = "playgo-hash-table.dat";
    public const string ScenarioFileName = "playgo-scenario.json";

    /// <summary>Bit trong mask của ngôn ngữ <paramref name="id"/>.</summary>
    public static ulong LanguageBit(int id) => 1UL << (63 - id);

    /// <summary>Danh sách mã ngôn ngữ có trong <paramref name="mask"/>, theo thứ tự id (bit không có mã thì bỏ qua).</summary>
    public static IReadOnlyList<string> Codes(ulong mask)
    {
        var codes = new List<string>();
        for (var id = 0; id < LanguageCodes.Count; id++)
        {
            if ((mask & LanguageBit(id)) != 0)
            {
                codes.Add(LanguageCodes[id]);
            }
        }

        return codes;
    }

    /// <summary>Đọc cấu trúc PlayGo từ thư mục sce_sys; null (kèm lý do) khi thiếu playgo-chunk.dat hoặc tệp không hợp lệ.</summary>
    public static PlayGoStructure? TryRead(string sceSysFolder, out string? problem)
    {
        problem = null;
        var chunkPath = Path.Combine(sceSysFolder, ChunkFileName);
        if (!File.Exists(chunkPath))
        {
            return null;
        }

        try
        {
            var ficmPath = Path.Combine(sceSysFolder, FicmFileName);
            var hashPath = Path.Combine(sceSysFolder, HashTableFileName);
            return Parse(
                File.ReadAllBytes(chunkPath),
                File.Exists(ficmPath) ? File.ReadAllBytes(ficmPath) : null,
                File.Exists(hashPath) ? File.ReadAllBytes(hashPath) : null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>Phân tích playgo-chunk.dat (bắt buộc) và cặp ficm + hash-table (tuỳ chọn, phải hợp lệ cả hai mới lấy ánh xạ tệp).</summary>
    public static PlayGoStructure Parse(ReadOnlySpan<byte> chunk, ReadOnlySpan<byte> ficm, ReadOnlySpan<byte> hashTable)
    {
        if (chunk.Length < 256 || !chunk[..4].SequenceEqual("plgx"u8) || BinaryPrimitives.ReadUInt16LittleEndian(chunk[4..]) != 0x1000)
        {
            throw new InvalidDataException("playgo-chunk.dat: not a PS5 plgx 0x1000 table");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[16..]);
        if (length != chunk.Length)
        {
            throw new InvalidDataException($"playgo-chunk.dat: stored length {length} does not match file length {chunk.Length}");
        }

        int chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(chunk[10..]);
        int scenarioCount = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
        if (chunkCount is < 1 or > 255 || scenarioCount is < 1 or > 64)
        {
            throw new InvalidDataException($"playgo-chunk.dat: unsupported counts ({chunkCount} chunks, {scenarioCount} scenarios)");
        }

        int defaultScenario = BinaryPrimitives.ReadUInt16LittleEndian(chunk[20..]);
        int defaultLanguage = BinaryPrimitives.ReadUInt16LittleEndian(chunk[36..]);
        var supportedMask = BinaryPrimitives.ReadUInt64LittleEndian(chunk[56..]);
        if (supportedMask == 0 || defaultLanguage > 63 || (supportedMask & LanguageBit(defaultLanguage)) == 0)
        {
            throw new InvalidDataException("playgo-chunk.dat: the default language is not in the header language mask");
        }

        var chunkAttributes = Section(chunk, 192, "chunk attributes");
        var chunkLabels = Section(chunk, 208, "chunk labels");
        var scenarioAttributes = Section(chunk, 224, "scenario attributes");
        var scenarioChunkRefs = Section(chunk, 232, "scenario chunk references");
        var scenarioLabels = Section(chunk, 240, "scenario labels");
        if (chunkAttributes.Size < chunkCount * 32L || scenarioAttributes.Size < scenarioCount * 32L)
        {
            throw new InvalidDataException("playgo-chunk.dat: attribute tables are truncated");
        }

        var chunks = new List<PlayGoChunk>(chunkCount);
        for (var id = 0; id < chunkCount; id++)
        {
            var record = chunkAttributes.Offset + id * 32;
            var mask = BinaryPrimitives.ReadUInt64LittleEndian(chunk[(record + 16)..]);
            if (mask == 0 || (mask & ~supportedMask) != 0)
            {
                throw new InvalidDataException($"playgo-chunk.dat: chunk {id} has an invalid language mask");
            }

            var labelOffset = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(record + 28)..]);
            chunks.Add(new PlayGoChunk(id, mask, Label(chunk, chunkLabels, labelOffset) ?? $"Chunk #{id}"));
        }

        var scenarios = new List<PlayGoScenario>(scenarioCount);
        for (var id = 0; id < scenarioCount; id++)
        {
            var record = scenarioAttributes.Offset + id * 32;
            int initial = BinaryPrimitives.ReadUInt16LittleEndian(chunk[(record + 20)..]);
            int total = BinaryPrimitives.ReadUInt16LittleEndian(chunk[(record + 22)..]);
            var listOffset = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(record + 24)..]);
            if (total < 1 || total > chunkCount || initial < 1 || initial > total || listOffset + total * 2L > (long)scenarioChunkRefs.Size)
            {
                throw new InvalidDataException($"playgo-chunk.dat: scenario {id} has an invalid chunk list");
            }

            var order = new List<int>(total);
            for (var index = 0; index < total; index++)
            {
                int chunkId = BinaryPrimitives.ReadUInt16LittleEndian(chunk[(scenarioChunkRefs.Offset + (int)listOffset + index * 2)..]);
                if (chunkId >= chunkCount || order.Contains(chunkId))
                {
                    throw new InvalidDataException($"playgo-chunk.dat: scenario {id} references chunk {chunkId} incorrectly");
                }

                order.Add(chunkId);
            }

            var labelOffset = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(record + 28)..]);
            scenarios.Add(new PlayGoScenario(id, initial, order, Label(chunk, scenarioLabels, labelOffset) ?? $"Scenario #{id}"));
        }

        if (defaultScenario >= scenarioCount)
        {
            throw new InvalidDataException("playgo-chunk.dat: the default scenario id is outside the scenario table");
        }

        var files = new Dictionary<ulong, int>();
        if (!ficm.IsEmpty && !hashTable.IsEmpty)
        {
            // FICM: header 16 byte (version 1, offset 16, kích thước bản đồ) rồi 2 byte mỗi tệp (chunk, dự phòng); FLT: header 56 byte
            // ("\x7fFLT" @24, số hash @36) rồi 8 byte mỗi tệp — cùng thứ tự với FICM.
            if (ficm.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(ficm) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(ficm[8..]) == 16 &&
                hashTable.Length >= 56 && BinaryPrimitives.ReadUInt32LittleEndian(hashTable) == 1 && hashTable[24..28].SequenceEqual("FLT"u8) &&
                BinaryPrimitives.ReadUInt32LittleEndian(hashTable[8..]) == 56)
            {
                var mapBytes = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(ficm[12..]), (uint)(ficm.Length - 16));
                var hashCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(hashTable[36..]), (uint)((hashTable.Length - 56) / 8));
                var count = Math.Min(hashCount, mapBytes / 2);
                for (var index = 0; index < count; index++)
                {
                    int chunkId = ficm[16 + index * 2];
                    if (chunkId < chunkCount)
                    {
                        files[BinaryPrimitives.ReadUInt64LittleEndian(hashTable[(56 + index * 8)..])] = chunkId;
                    }
                }
            }
        }

        return new PlayGoStructure(supportedMask, defaultLanguage, defaultScenario, chunks, scenarios, files);
    }

    /// <summary>Số chunk tối đa của PlayGo PS5 (bảng plgx và Publishing Tools).</summary>
    public const int MaxChunks = 255;

    /// <summary>Số kịch bản tối đa của PlayGo PS5.</summary>
    public const int MaxScenarios = 64;

    /// <summary>
    /// Cấu trúc dự phòng (tuỳ chọn) cho nguồn không còn <c>playgo-chunk.dat</c> — đa số bản dump đã bỏ bảng này nên không biết chunk
    /// ngôn ngữ/chế độ chơi gốc. Giống engine tích hợp (mặc định 100 chunk "mọi ngôn ngữ"): <paramref name="chunkCount"/> chunk trống
    /// ngoài chunk 0, <paramref name="scenarioCount"/> kịch bản (lấy từ playgo-scenario.json gốc nếu còn), mỗi kịch bản cần đủ mọi
    /// chunk trước khi chạy — gói FPKG cài trọn nên mọi chunk game hỏi tới đều "đã cài". Mask 0 = không ghi ngôn ngữ vào GP5 (SDK tự
    /// đặt như script gốc). Publishing Tools 2.79 nhận 100 và 255 chunk trống, 1–2 kịch bản (đã thử).
    /// </summary>
    public static PlayGoStructure Fallback(int chunkCount, int scenarioCount)
    {
        chunkCount = Math.Clamp(chunkCount, 1, MaxChunks);
        scenarioCount = Math.Clamp(scenarioCount, 1, MaxScenarios);
        var order = Enumerable.Range(0, chunkCount).ToList();
        return new PlayGoStructure(
            0,
            1,
            0,
            order.Select(id => new PlayGoChunk(id, 0, $"Chunk #{id}")).ToList(),
            Enumerable.Range(0, scenarioCount).Select(id => new PlayGoScenario(id, chunkCount, order, $"Scenario #{id}")).ToList(),
            new Dictionary<ulong, int>());
    }

    /// <summary>Script fix6: tối đa 5 kịch bản (SCE_PLAYGO_MAX_SCENARIO của SDK), 100 chunk, tệp giữ chỗ 1 MiB mỗi chunk ngôn ngữ.</summary>
    public const int ScriptScenarioLimit = 5;

    public const int ScriptChunkCount = 100;

    public const int LanguagePayloadSize = 1024 * 1024;

    /// <summary>Thư mục trong gói chứa các tệp giữ chỗ ngôn ngữ (bố cục script fix6).</summary>
    public const string LanguagePayloadFolder = "playgo-languages";

    /// <summary>
    /// Bố cục PlayGo của script create-gp5-from-folder.py (fix6) cho nguồn không còn bảng gốc: đọc <c>sce_sys/playgo-scenario.json</c>
    /// (thiếu → kịch bản mặc định với ngôn ngữ mặc định của param.json), lấy danh sách ngôn ngữ (<c>chunkSupportedLanguages</c>, hoặc
    /// các khoá bản địa hoá của kịch bản), mỗi ngôn ngữ một chunk 1..L với tệp giữ chỗ, các chunk còn lại "mọi ngôn ngữ", mọi kịch bản
    /// gồm cả 100 chunk và initial = 100. Tệp scenario không hợp lệ theo luật của script → dùng mặc định và báo <paramref name="warning"/>
    /// (script gốc dừng hẳn).
    /// </summary>
    public static PlayGoStructure ScriptFallback(string sceSysFolder, string fallbackLanguage, out string? warning, int chunkCount = ScriptChunkCount)
    {
        warning = null;
        chunkCount = Math.Clamp(chunkCount, 2, MaxChunks);
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var code in LanguageCodes)
        {
            codes[PythonCaseFold.Fold(code)] = code;
        }

        string Canonical(string value) => codes.TryGetValue(PythonCaseFold.Fold(value.Trim()), out var canonical) ? canonical : value.Trim();
        var fallback = codes.TryGetValue(PythonCaseFold.Fold(fallbackLanguage), out var canonicalFallback) ? canonicalFallback : "en-US";

        var path = Path.Combine(sceSysFolder, ScenarioFileName);
        JsonObject value;
        List<string>? supportedLanguages = null;
        try
        {
            value = ParseScenario(path, fallback, out supportedLanguages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            warning = ex.Message;
            value = DefaultScenarioObject(fallback);
            supportedLanguages = null;
        }

        // Từ đây value đã qua kiểm tra của ParseScenario (hoặc là mặc định).
        var count = value["scenarioCount"]!.GetValue<int>();
        var defaultId = value["scenarioDefaultId"]!.GetValue<int>();
        var language = Canonical(value["scenarioDefaultLanguage"]!.GetValue<string>());
        var definitions = new List<PlayGoScenario>();
        var order = Enumerable.Range(0, chunkCount).ToList();
        foreach (var node in value["scenarios"]!.AsArray())
        {
            var scenario = node!.AsObject();
            var id = scenario["id"]!.GetValue<int>();
            var type = scenario["type"]!.GetValue<string>().Trim();
            var title = scenario[language] is JsonObject localized && localized["title"] is JsonValue titleValue && titleValue.TryGetValue<string>(out var titleText) && !string.IsNullOrWhiteSpace(titleText)
                ? titleText.Trim()
                : $"Scenario #{id}";
            definitions.Add(new PlayGoScenario(id, chunkCount, order, title, type));
        }

        definitions.Sort((a, b) => a.Id.CompareTo(b.Id));
        var chunkDefault = Canonical(value["chunkDefaultLanguage"] is JsonValue chunkDefaultValue && chunkDefaultValue.TryGetValue<string>(out var chunkDefaultText) ? chunkDefaultText : language);
        // Tệp cũ không có chunkSupportedLanguages: danh sách suy ra chỉ dùng cho GP5, script không ghi nó vào scenario JSON.
        var languages = supportedLanguages ?? value["chunkSupportedLanguages"]!.AsArray().Select(item => Canonical(item!.GetValue<string>())).ToList();
        var all = string.Join(' ', languages);
        var chunks = new List<PlayGoChunk>(chunkCount);
        var payloads = new List<PlayGoLanguagePayload>();
        ulong supportedMask = 0;
        foreach (var code in languages)
        {
            var index = LanguageIndex(code);
            if (index >= 0)
            {
                supportedMask |= LanguageBit(index);
            }
        }

        for (var id = 0; id < chunkCount; id++)
        {
            var own = id >= 1 && id <= languages.Count ? languages[id - 1] : null;
            var mask = own == null ? supportedMask : LanguageIndex(own) is var languageIndex && languageIndex >= 0 ? LanguageBit(languageIndex) : 0;
            chunks.Add(new PlayGoChunk(id, mask, $"Chunk #{id}", own ?? all));
            if (own != null)
            {
                var safe = System.Text.RegularExpressions.Regex.Replace(own, "[^A-Za-z0-9._-]", "_");
                payloads.Add(new PlayGoLanguagePayload($"{LanguagePayloadFolder}/{id:00}-{safe}.bin", id, own));
            }
        }

        return new PlayGoStructure(supportedMask, Math.Max(0, LanguageIndex(chunkDefault)), defaultId, chunks, definitions, new Dictionary<ulong, int>())
        {
            ScriptLayout = true,
            SupportedLanguagesText = all,
            DefaultLanguageCode = chunkDefault,
            ScenarioJson = PythonJson.Serialize(value) + "\n",
            LanguagePayloads = payloads,
        };
    }

    private static int LanguageIndex(string code)
    {
        for (var index = 0; index < LanguageCodes.Count; index++)
        {
            if (string.Equals(LanguageCodes[index], code, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>default_scenario() của script: đủ 5 khoá theo đúng thứ tự.</summary>
    public static JsonObject DefaultScenarioObject(string language) => new()
    {
        ["chunkDefaultLanguage"] = language,
        ["chunkSupportedLanguages"] = new JsonArray(language),
        ["scenarioCount"] = 1,
        ["scenarioDefaultId"] = 0,
        ["scenarioDefaultLanguage"] = language,
        ["scenarios"] = new JsonArray(new JsonObject
        {
            ["id"] = 0,
            ["type"] = "playmode",
            [language] = new JsonObject { ["title"] = "Scenario #0", ["description"] = "Default play scenario" },
        }),
    };

    /// <summary>
    /// write_scenario() của script: đọc playgo-scenario.json (utf-8-sig), kiểm tra scenarioCount 1..5, scenarioDefaultId, ngôn ngữ mặc định
    /// hợp lệ, mảng scenarios đủ và id liên tục, bổ sung chunkSupportedLanguages khi thiếu (mọi khoá bản địa hoá + ngôn ngữ mặc định).
    /// Không hợp lệ → InvalidDataException với lý do của script.
    /// </summary>
    private static JsonObject ParseScenario(string path, string fallbackLanguage, out List<string>? supportedLanguages)
    {
        supportedLanguages = null;
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return DefaultScenarioObject(fallbackLanguage);
        }

        var bytes = File.ReadAllBytes(path);
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        var json = bytes.AsSpan().StartsWith(bom) ? bytes.AsSpan(bom.Length) : bytes.AsSpan();
        if (System.Text.Json.Nodes.JsonNode.Parse(json) is not JsonObject value)
        {
            throw new InvalidDataException($"{path} must contain a JSON object");
        }

        var codes = LanguageCodes.ToDictionary(PythonCaseFold.Fold, code => code, StringComparer.Ordinal);
        string Canonical(string text) => codes.TryGetValue(PythonCaseFold.Fold(text.Trim()), out var canonical) ? canonical : throw new InvalidDataException($"{path} has unsupported language: '{text}'");

        if (value["scenarioCount"] is not JsonValue countValue || !countValue.TryGetValue<int>(out var count) || count is < 1 or > ScriptScenarioLimit)
        {
            throw new InvalidDataException($"{path} scenarioCount must be between 1 and {ScriptScenarioLimit}");
        }

        if (value["scenarioDefaultId"] is not JsonValue defaultValue || !defaultValue.TryGetValue<int>(out var defaultId) || defaultId < 0 || defaultId >= count)
        {
            throw new InvalidDataException($"{path} scenarioDefaultId is outside the scenario table");
        }

        if (value["scenarioDefaultLanguage"] is not JsonValue languageValue || !languageValue.TryGetValue<string>(out var languageText) || string.IsNullOrWhiteSpace(languageText))
        {
            throw new InvalidDataException($"{path} has no valid scenarioDefaultLanguage");
        }

        var language = Canonical(languageText);
        if (value["scenarios"] is not JsonArray scenarios || scenarios.Count != count)
        {
            throw new InvalidDataException($"{path} scenarios must contain exactly {count} entries");
        }

        var ids = new HashSet<int>();
        foreach (var node in scenarios)
        {
            if (node is not JsonObject scenario)
            {
                throw new InvalidDataException($"{path} contains a non-object scenario entry");
            }

            if (scenario["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id) || id < 0 || id >= count || !ids.Add(id))
            {
                throw new InvalidDataException($"{path} has an invalid or duplicate scenario id");
            }

            if (scenario["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type) || string.IsNullOrWhiteSpace(type))
            {
                throw new InvalidDataException($"{path} scenario {id} has no valid type");
            }

            if (scenario[language] is not JsonObject)
            {
                throw new InvalidDataException($"{path} scenario {id} has no '{language}' localization");
            }
        }

        var chunkDefault = value["chunkDefaultLanguage"] is JsonValue chunkDefaultValue && chunkDefaultValue.TryGetValue<string>(out var chunkDefaultText)
            ? (string.IsNullOrWhiteSpace(chunkDefaultText) ? throw new InvalidDataException($"{path} has no valid chunkDefaultLanguage") : Canonical(chunkDefaultText))
            : language;
        List<string> supported;
        if (value["chunkSupportedLanguages"] is null)
        {
            // Tệp cũ không có danh sách: mọi khoá bản địa hoá của các kịch bản + ngôn ngữ mặc định (đúng thứ tự script duyệt).
            supported = [chunkDefault];
            foreach (var node in scenarios)
            {
                foreach (var (key, localized) in node!.AsObject())
                {
                    if (codes.TryGetValue(PythonCaseFold.Fold(key), out var canonical) && localized is JsonObject && !supported.Contains(canonical))
                    {
                        supported.Add(canonical);
                    }
                }
            }

        }
        else if (value["chunkSupportedLanguages"] is JsonArray array && array.Count > 0 && array.All(item => item is JsonValue v && v.TryGetValue<string>(out var t) && !string.IsNullOrWhiteSpace(t)))
        {
            supported = array.Select(item => Canonical(item!.GetValue<string>())).ToList();
        }
        else
        {
            throw new InvalidDataException($"{path} chunkSupportedLanguages must be a non-empty string array");
        }

        if (supported.Select(PythonCaseFold.Fold).Distinct().Count() != supported.Count)
        {
            throw new InvalidDataException($"{path} chunkSupportedLanguages contains duplicates");
        }

        if (!supported.Any(item => PythonCaseFold.Fold(item) == PythonCaseFold.Fold(chunkDefault)))
        {
            throw new InvalidDataException($"{path} chunkDefaultLanguage is not present in chunkSupportedLanguages");
        }

        if (supported.Count >= ScriptChunkCount)
        {
            throw new InvalidDataException($"{path} declares too many chunk languages for {ScriptChunkCount} PlayGo chunks");
        }

        supportedLanguages = supported;
        return value;
    }

    /// <summary>
    /// Số kịch bản trong <c>sce_sys/playgo-scenario.json</c> của nguồn khi tệp hợp lệ (scenarioCount 1..64 khớp mảng scenarios, id
    /// 0..n-1 theo thứ tự); null khi thiếu hoặc không hợp lệ. Bản dump thường bỏ playgo-chunk.dat nhưng giữ tệp này (The Last of Us
    /// Part I/II: 2 kịch bản "câu chuyện chính" và "Left Behind"/"No Return").
    /// </summary>
    public static int? SourceScenarioCount(string sceSysFolder)
    {
        try
        {
            var path = Path.Combine(sceSysFolder, ScenarioFileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > 1024 * 1024)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
            var json = bytes.AsSpan().StartsWith(bom) ? bytes.AsSpan(bom.Length) : bytes.AsSpan();
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject root ||
                root["scenarioCount"] is not System.Text.Json.Nodes.JsonValue countValue || !countValue.TryGetValue<int>(out var count) ||
                count is < 1 or > MaxScenarios ||
                root["scenarios"] is not System.Text.Json.Nodes.JsonArray scenarios || scenarios.Count != count)
            {
                return null;
            }

            for (var index = 0; index < count; index++)
            {
                if (scenarios[index] is not System.Text.Json.Nodes.JsonObject scenario ||
                    scenario["id"] is not System.Text.Json.Nodes.JsonValue id || !id.TryGetValue<int>(out var scenarioId) || scenarioId != index)
                {
                    return null;
                }
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Phần <c>&lt;chunk_info&gt;</c> của GP5 cho cấu trúc gói gốc, đúng cú pháp Publishing Tools tự ghi (thụt lề như phần còn lại
    /// của GP5). Chunk có mask bằng mask chung thì không ghi <c>languages</c> (mặc định của SDK = mọi ngôn ngữ hỗ trợ). Mask chung
    /// bằng 0 (cấu trúc dự phòng) thì không ghi ngôn ngữ nào, như script gốc.
    /// </summary>
    public static string ChunkInfoXml(PlayGoStructure structure)
    {
        var xml = new StringBuilder();
        if (structure.ScriptLayout)
        {
            // Đúng thứ tự thuộc tính của script (ElementTree giữ thứ tự chèn): id, label, layer_no, languages; kịch bản "0-99".
            xml.Append("    <chunk_info chunk_count=\"").Append(structure.Chunks.Count).Append("\" scenario_count=\"").Append(structure.Scenarios.Count).Append("\">\n");
            xml.Append("      <chunks supported_languages=\"").Append(SonySdkProject.EscapeAttribute(structure.SupportedLanguagesText ?? string.Empty))
                .Append("\" default_language=\"").Append(SonySdkProject.EscapeAttribute(structure.DefaultLanguageCode ?? "en-US")).Append("\">\n");
            foreach (var chunk in structure.Chunks)
            {
                xml.Append("        <chunk id=\"").Append(chunk.Id).Append("\" label=\"").Append(SonySdkProject.EscapeAttribute(chunk.Label))
                    .Append("\" layer_no=\"0\" languages=\"").Append(SonySdkProject.EscapeAttribute(chunk.LanguagesText ?? structure.SupportedLanguagesText ?? string.Empty)).Append("\" />\n");
            }

            xml.Append("      </chunks>\n");
            xml.Append("      <scenarios default_id=\"").Append(structure.DefaultScenarioId).Append("\">\n");
            foreach (var scenario in structure.Scenarios)
            {
                xml.Append("        <scenario id=\"").Append(scenario.Id).Append("\" type=\"").Append(SonySdkProject.EscapeAttribute(scenario.Type))
                    .Append("\" initial_chunk_count=\"").Append(structure.Chunks.Count).Append("\" label=\"").Append(SonySdkProject.EscapeAttribute(scenario.Label)).Append("\">")
                    .Append("0-").Append(structure.Chunks.Count - 1).Append("</scenario>\n");
            }

            xml.Append("      </scenarios>\n");
            xml.Append("    </chunk_info>\n");
            return xml.ToString();
        }

        xml.Append("    <chunk_info chunk_count=\"").Append(structure.Chunks.Count).Append("\" scenario_count=\"").Append(structure.Scenarios.Count).Append("\">\n");
        if (structure.SupportedLanguageMask == 0)
        {
            xml.Append("      <chunks>\n");
        }
        else
        {
            xml.Append("      <chunks supported_languages=\"").Append(string.Join(' ', Codes(structure.SupportedLanguageMask)))
                .Append("\" default_language=\"").Append(structure.DefaultLanguageId < LanguageCodes.Count ? LanguageCodes[structure.DefaultLanguageId] : "en-US").Append("\">\n");
        }

        foreach (var chunk in structure.Chunks)
        {
            xml.Append("        <chunk id=\"").Append(chunk.Id).Append('"');
            if (structure.SupportedLanguageMask != 0 && chunk.LanguageMask != structure.SupportedLanguageMask)
            {
                xml.Append(" languages=\"").Append(string.Join(' ', Codes(chunk.LanguageMask))).Append('"');
            }

            xml.Append(" label=\"").Append(SonySdkProject.EscapeAttribute(chunk.Label)).Append("\" />\n");
        }

        xml.Append("      </chunks>\n");
        xml.Append("      <scenarios default_id=\"").Append(structure.DefaultScenarioId).Append("\">\n");
        foreach (var scenario in structure.Scenarios)
        {
            // Mọi chunk đều "initial": gói FPKG được cài trọn nên không có chunk nào tải sau. Giữ initial_chunk_count gốc (Yōtei: 32/35)
            // khiến game mở lên hỏi hệ thống về các chunk còn lại → hộp "Something went wrong (CE-108111-2)" dù vẫn chơi được;
            // engine LibProsperoPkg 0.6.9 cũng đánh dấu mọi chunk initial. Thứ tự chunk và ngôn ngữ từng chunk vẫn như gói gốc.
            xml.Append("        <scenario id=\"").Append(scenario.Id).Append("\" type=\"playmode\" initial_chunk_count=\"").Append(scenario.ChunkOrder.Count)
                .Append("\" label=\"").Append(SonySdkProject.EscapeAttribute(scenario.Label)).Append("\">")
                .Append(string.Join(' ', scenario.ChunkOrder)).Append("</scenario>\n");
        }

        xml.Append("      </scenarios>\n");
        xml.Append("    </chunk_info>\n");
        return xml.ToString();
    }

    private readonly record struct LayoutSection(int Offset, int Size);

    private static LayoutSection Section(ReadOnlySpan<byte> chunk, int pointer, string name)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(chunk[pointer..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(pointer + 4)..]);
        if (offset > (uint)chunk.Length || size > (uint)chunk.Length - offset)
        {
            throw new InvalidDataException($"playgo-chunk.dat: the {name} section is outside the file");
        }

        return new LayoutSection((int)offset, (int)size);
    }

    /// <summary>Chuỗi ASCII kết thúc bằng 0 trong vùng nhãn; null khi rỗng hoặc ngoài vùng.</summary>
    private static string? Label(ReadOnlySpan<byte> chunk, LayoutSection labels, uint relativeOffset)
    {
        if (relativeOffset >= (uint)labels.Size)
        {
            return null;
        }

        var start = labels.Offset + (int)relativeOffset;
        var end = labels.Offset + labels.Size;
        var terminator = chunk[start..end].IndexOf((byte)0);
        var slice = terminator < 0 ? chunk[start..end] : chunk[start..(start + terminator)];
        if (slice.Length == 0)
        {
            return null;
        }

        foreach (var value in slice)
        {
            if (value is < 32 or > 126)
            {
                return null;
            }
        }

        return Encoding.ASCII.GetString(slice);
    }
}
