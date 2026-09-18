using System.Buffers.Binary;
using System.Text;
using LibProsperoPkg.PFS;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Một chunk PlayGo của gói gốc: mã, mask ngôn ngữ (bit 63 − id ngôn ngữ) và nhãn.</summary>
public sealed record PlayGoChunk(int Id, ulong LanguageMask, string Label);

/// <summary>Một kịch bản PlayGo của gói gốc: số chunk cần trước khi chạy, thứ tự tải chunk và nhãn.</summary>
public sealed record PlayGoScenario(int Id, int InitialChunkCount, IReadOnlyList<int> ChunkOrder, string Label);

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
