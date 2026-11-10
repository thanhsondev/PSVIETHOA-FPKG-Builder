using System.Buffers.Binary;
using System.Text;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Một chunk của bố cục PlayGo nguồn (fix15): nhãn (có thể rỗng) và các mã ngôn ngữ của mask, theo thứ tự id.</summary>
public sealed record PlayGoSourceChunk(string Label, IReadOnlyList<string> Languages);

/// <summary>Một kịch bản của bảng playgo-chunk.dat nguồn (fix15 <c>read_source_scenarios</c>): nhãn, số chunk initial, danh sách chunk theo thứ tự.</summary>
public sealed record PlayGoSourceScenario(string Label, int InitialChunkCount, IReadOnlyList<int> ChunkIds);

/// <summary>
/// Bố cục PlayGo của nguồn theo script fix15 (<c>SourcePlayGoLayout</c>): nhãn và ngôn ngữ từng chunk, chunk của từng tệp theo hash đường
/// dẫn (seed/key lấy từ header của playgo-hash-table.dat) và — khi bảng hợp lệ — các kịch bản retail.
/// </summary>
public sealed class PlayGoSourceLayout(IReadOnlyList<PlayGoSourceChunk> chunks, IReadOnlyDictionary<ulong, int> fileChunks, ulong seed, ulong key, IReadOnlyList<PlayGoSourceScenario>? scenarios)
{
    public IReadOnlyList<PlayGoSourceChunk> Chunks { get; } = chunks;

    public IReadOnlyDictionary<ulong, int> FileChunks { get; } = fileChunks;

    public ulong Seed { get; } = seed;

    public ulong Key { get; } = key;

    /// <summary>Kịch bản của bảng nguồn; null khi bảng kịch bản không hợp lệ (script: "every scenario will list all chunks").</summary>
    public IReadOnlyList<PlayGoSourceScenario>? Scenarios { get; } = scenarios;

    /// <summary><c>languages</c>: mọi ngôn ngữ có trong ít nhất một chunk, theo thứ tự bảng ngôn ngữ PlayGo.</summary>
    public IReadOnlyList<string> Languages
    {
        get
        {
            var present = new HashSet<string>(Chunks.SelectMany(chunk => chunk.Languages), StringComparer.Ordinal);
            return SonySdkPlayGo.LanguageCodes.Where(present.Contains).ToList();
        }
    }

    public ulong HashOf(string path) => SonySdkPlayGoSource.PathHash(path, Seed, Key);

    /// <summary>Chunk của tệp <paramref name="path"/> (a/b/c, không có "/" đầu) trong bảng nguồn; null khi không có.</summary>
    public int? ChunkOf(string path) => FileChunks.TryGetValue(HashOf(path), out var chunk) ? chunk : null;
}

/// <summary>
/// Đọc bảng PlayGo của nguồn đúng như <c>create-gp5-from-folder.py</c> của bộ sdk-fpkg279-fix15: số chunk từ header
/// <c>playgo-chunk.dat</c>, bố cục (nhãn, ngôn ngữ, tệp → chunk, kịch bản) khi có đủ ba tệp. Script dừng hẳn khi bảng không hợp lệ
/// (ValueError); ở đây lỗi được trả về để công cụ cảnh báo và dùng bố cục dự phòng của chính script — như bản dump đặt nhầm tên tệp
/// sce_sys (Control PPSA34547, PPSA29802: playgo-ficm.dat chứa bảng FLT hoặc một bản param.json).
/// </summary>
public static class SonySdkPlayGoSource
{
    /// <summary><c>MAX_PLAYGO_CHUNK_COUNT</c> của fix15.</summary>
    public const int MaxChunks = 800;

    /// <summary><c>MAX_PLAYGO_SCENARIO_COUNT</c> (SCE_PLAYGO_MAX_SCENARIO).</summary>
    public const int MaxScenarios = 5;

    private const ulong AllLanguagesMask = ulong.MaxValue;

    private const ulong HashMix = 0x8000000080008081UL;

    /// <summary>
    /// <c>read_source_chunk_count</c>: số chunk của header <c>sce_sys/playgo-chunk.dat</c>; null khi không có tệp. Header không hợp lệ thì
    /// script dừng — trả về null kèm <paramref name="problem"/> (thông báo như ValueError của script).
    /// </summary>
    public static int? ReadChunkCount(string sceSysFolder, out string? problem)
    {
        problem = null;
        var path = Path.Combine(sceSysFolder, SonySdkPlayGo.ChunkFileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            Span<byte> header = stackalloc byte[0x18];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var size = stream.Length;
            if (read != 0x18 || !header[..4].SequenceEqual("plgx"u8) ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 0x1000 || BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != 0 ||
                size < 0x100 || BinaryPrimitives.ReadUInt32LittleEndian(header[0x10..]) != size)
            {
                problem = $"{path}: invalid PS5 playgo-chunk.dat header";
                return null;
            }

            int chunks = BinaryPrimitives.ReadUInt16LittleEndian(header[0x0a..]);
            int scenarios = BinaryPrimitives.ReadUInt16LittleEndian(header[0x0e..]);
            if (chunks is < 1 or > MaxChunks || scenarios is < 1 or > MaxScenarios)
            {
                problem = $"{path}: unsupported PlayGo counts: {chunks} chunks, {scenarios} scenarios";
                return null;
            }

            return chunks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{path}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// <c>read_source_playgo_layout</c>: null khi thiếu một trong ba tệp, hoặc khi có nhiều chunk mà mọi tệp đều ở chunk 0 (script dùng
    /// bố cục dự phòng); <paramref name="problem"/> khác null khi bảng không hợp lệ (script dừng). Cảnh báo script in ra (bit ngôn ngữ lạ,
    /// bảng kịch bản hỏng) thêm vào <paramref name="lines"/> theo đúng thứ tự.
    /// </summary>
    public static PlayGoSourceLayout? ReadLayout(string sceSysFolder, int chunkCount, List<string> lines, out string? problem)
    {
        problem = null;
        var chunkPath = Path.Combine(sceSysFolder, SonySdkPlayGo.ChunkFileName);
        var hashPath = Path.Combine(sceSysFolder, SonySdkPlayGo.HashTableFileName);
        var ficmPath = Path.Combine(sceSysFolder, SonySdkPlayGo.FicmFileName);
        try
        {
            if (!(File.Exists(chunkPath) && File.Exists(hashPath) && File.Exists(ficmPath)))
            {
                return null;
            }

            var warnings = new List<string>();
            var layout = ReadLayoutCore(chunkPath, hashPath, ficmPath, chunkCount, warnings);
            lines.AddRange(warnings);
            return layout;
        }
        catch (InvalidDataException ex)
        {
            problem = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return null;
        }
    }

    private static PlayGoSourceLayout? ReadLayoutCore(string chunkPath, string hashPath, string ficmPath, int chunkCount, List<string> lines)
    {
        var fileChunks = new Dictionary<ulong, int>();
        ulong seed, key;
        using (var hashStream = new FileStream(hashPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        using (var ficmStream = new FileStream(ficmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            var hashHeader = new byte[0x38];
            if (hashStream.ReadAtLeast(hashHeader, hashHeader.Length, throwOnEndOfStream: false) != 0x38 ||
                BinaryPrimitives.ReadUInt32LittleEndian(hashHeader) != 1 ||
                !hashHeader.AsSpan(0x18, 4).SequenceEqual("\u007fFLT"u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(hashHeader.AsSpan(0x08)) != 0x38)
            {
                throw new InvalidDataException($"{hashPath}: unrecognized header");
            }

            var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(hashHeader.AsSpan(0x24));
            seed = BinaryPrimitives.ReadUInt64LittleEndian(hashHeader.AsSpan(0x28));
            key = BinaryPrimitives.ReadUInt64LittleEndian(hashHeader.AsSpan(0x30));
            if (BinaryPrimitives.ReadUInt32LittleEndian(hashHeader.AsSpan(0x0c)) != (ulong)fileCount * 8 || hashStream.Length != 0x38 + (long)fileCount * 8)
            {
                throw new InvalidDataException($"{hashPath}: size does not match {fileCount} entries");
            }

            var ficmHeader = new byte[0x10];
            if (ficmStream.ReadAtLeast(ficmHeader, ficmHeader.Length, throwOnEndOfStream: false) != 0x10 ||
                BinaryPrimitives.ReadUInt32LittleEndian(ficmHeader) != 1 ||
                BinaryPrimitives.ReadUInt32LittleEndian(ficmHeader.AsSpan(0x08)) != 0x10 ||
                BinaryPrimitives.ReadUInt32LittleEndian(ficmHeader.AsSpan(0x0c)) != (ulong)fileCount * 2 ||
                ficmStream.Length != 0x10 + (long)fileCount * 2)
            {
                throw new InvalidDataException($"{ficmPath}: does not describe {fileCount} files");
            }

            var hashBlock = new byte[8192 * 8];
            var ficmBlock = new byte[8192 * 2];
            ulong previous = 0;
            var first = true;
            for (long start = 0; start < fileCount; start += 8192)
            {
                var count = (int)Math.Min(8192, fileCount - start);
                if (hashStream.ReadAtLeast(hashBlock.AsSpan(0, count * 8), count * 8, throwOnEndOfStream: false) != count * 8 ||
                    ficmStream.ReadAtLeast(ficmBlock.AsSpan(0, count * 2), count * 2, throwOnEndOfStream: false) != count * 2)
                {
                    throw new InvalidDataException($"{hashPath} / {ficmPath}: truncated file map");
                }

                for (var index = 0; index < count; index++)
                {
                    var pathHash = BinaryPrimitives.ReadUInt64LittleEndian(hashBlock.AsSpan(index * 8));
                    int chunkId = BinaryPrimitives.ReadUInt16LittleEndian(ficmBlock.AsSpan(index * 2));
                    if (!first && pathHash <= previous)
                    {
                        throw new InvalidDataException($"{hashPath}: hashes must be unique and strictly increasing");
                    }

                    if (chunkId >= chunkCount)
                    {
                        throw new InvalidDataException($"{ficmPath}: invalid chunk id/reserved byte: {chunkId}");
                    }

                    previous = pathHash;
                    first = false;
                    fileChunks[pathHash] = chunkId;
                }
            }
        }

        if (chunkCount > 1 && !fileChunks.Values.Any(chunk => chunk != 0))
        {
            return null;
        }

        // Chỉ cần thuộc tính và nhãn chunk, không cần bảng extent/kịch bản.
        var data = File.ReadAllBytes(chunkPath);
        if (data.Length < 0x100 || !data.AsSpan(0, 4).SequenceEqual("plgx"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) != 0x1000 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10)) != data.Length ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0a)) != chunkCount)
        {
            throw new InvalidDataException($"{chunkPath}: truncated or changed descriptor");
        }

        long attrsOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xc0));
        long attrsSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xc4));
        long labelsOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xd0));
        long labelsSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xd4));
        if (attrsOffset < 0x100 || attrsSize != chunkCount * 0x20L || attrsOffset + attrsSize > data.Length)
        {
            throw new InvalidDataException($"{chunkPath}: chunk attribute table does not match {chunkCount} chunks");
        }

        if (labelsOffset < 0x100 || labelsOffset + labelsSize > data.Length ||
            Math.Max(attrsOffset, labelsOffset) < Math.Min(attrsOffset + attrsSize, labelsOffset + labelsSize))
        {
            throw new InvalidDataException($"{chunkPath}: invalid or overlapping chunk label table");
        }

        var attributes = data.AsSpan((int)attrsOffset, (int)attrsSize);
        var labels = data.AsSpan((int)labelsOffset, (int)labelsSize);
        ulong knownMask = 0;
        for (var bit = 0; bit < SonySdkPlayGo.LanguageCodes.Count; bit++)
        {
            knownMask |= 1UL << (63 - bit);
        }

        var chunks = new List<PlayGoSourceChunk>(chunkCount);
        for (var chunkId = 0; chunkId < chunkCount; chunkId++)
        {
            var entry = chunkId * 0x20;
            // Bit 63 là ja-JP, rồi lần lượt theo bảng ngôn ngữ PlayGo.
            var mask = BinaryPrimitives.ReadUInt64LittleEndian(attributes[(entry + 0x10)..]);
            var labelAt = BinaryPrimitives.ReadUInt32LittleEndian(attributes[(entry + 0x1c)..]);
            if (labelAt >= labels.Length)
            {
                throw new InvalidDataException($"{chunkPath}: chunk {chunkId} label offset is out of bounds");
            }

            var end = labels[(int)labelAt..].IndexOf((byte)0);
            if (end < 0)
            {
                throw new InvalidDataException($"{chunkPath}: chunk {chunkId} label is not null-terminated");
            }

            string label;
            try
            {
                label = StrictUtf8.GetString(labels.Slice((int)labelAt, end));
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException($"{chunkPath}: chunk {chunkId} label is not UTF-8", ex);
            }

            if (HasInvalidXmlCharacter(label))
            {
                throw new InvalidDataException($"{chunkPath}: chunk {chunkId} label contains invalid XML characters");
            }

            var languages = new List<string>();
            for (var bit = 0; bit < SonySdkPlayGo.LanguageCodes.Count; bit++)
            {
                if (((mask >> (63 - bit)) & 1) != 0)
                {
                    languages.Add(SonySdkPlayGo.LanguageCodes[bit]);
                }
            }

            if ((mask & ~knownMask) != 0 && mask != AllLanguagesMask)
            {
                lines.Add($"[Warn] PlayGo chunk {chunkId} in {chunkPath} sets unrecognized language bits (mask {PythonHex(mask)}); the chunk is emitted for its known languages only.");
            }

            chunks.Add(new PlayGoSourceChunk(label.Length > 0 ? label : languages.Count > 0 ? $"Chunk #{chunkId}" : string.Empty, languages));
        }

        return new PlayGoSourceLayout(chunks, fileChunks, seed, key, ReadScenarios(data, chunkPath, chunkCount, lines));
    }

    /// <summary><c>read_source_scenarios</c>: null (kèm cảnh báo của script) khi bảng kịch bản không hợp lệ.</summary>
    private static List<PlayGoSourceScenario>? ReadScenarios(byte[] data, string chunkPath, int chunkCount, List<string> lines)
    {
        int scenarioCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0e));
        long tableAt = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xe0));
        long tableSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xe4));
        long listsAt = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xe8));
        long listsSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xec));
        long namesAt = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xf0));
        long namesSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0xf4));
        try
        {
            if (tableSize != scenarioCount * 0x20L || Math.Min(tableAt, Math.Min(listsAt, namesAt)) < 0x100 ||
                Math.Max(tableAt + tableSize, Math.Max(listsAt + listsSize, namesAt + namesSize)) > data.Length)
            {
                throw new InvalidDataException("scenario sections do not match the header");
            }

            var lists = data.AsSpan((int)listsAt, (int)listsSize);
            var names = data.AsSpan((int)namesAt, (int)namesSize);
            var scenarios = new List<PlayGoSourceScenario>(scenarioCount);
            for (var index = 0; index < scenarioCount; index++)
            {
                var entry = (int)tableAt + index * 0x20;
                int initial = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entry + 0x14));
                int total = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entry + 0x16));
                long listAt = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 0x18));
                long nameAt = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 0x1c));
                if (listAt + total * 2L > lists.Length || initial > total || nameAt >= names.Length || names[(int)nameAt..].IndexOf((byte)0) < 0)
                {
                    throw new InvalidDataException($"scenario {index} points outside its sections");
                }

                var ids = new List<int>(total);
                for (var item = 0; item < total; item++)
                {
                    ids.Add(BinaryPrimitives.ReadUInt16LittleEndian(lists[((int)listAt + item * 2)..]));
                }

                if (ids.Any(id => id >= chunkCount) || ids.Distinct().Count() != total)
                {
                    throw new InvalidDataException($"scenario {index} has an invalid chunk list");
                }

                var nameEnd = names[(int)nameAt..].IndexOf((byte)0);
                string label;
                try
                {
                    label = StrictUtf8.GetString(names.Slice((int)nameAt, nameEnd));
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException($"scenario {index} label is not UTF-8 ({ex.Message})", ex);
                }

                if (HasInvalidXmlCharacter(label))
                {
                    throw new InvalidDataException($"scenario {index} label contains invalid XML characters");
                }

                scenarios.Add(new PlayGoSourceScenario(label, initial, ids));
            }

            return scenarios;
        }
        catch (InvalidDataException ex)
        {
            lines.Add($"[Warn] PlayGo: ignoring the scenario tables in {chunkPath}: {ex.Message}; every scenario will list all chunks.");
            return null;
        }
    }

    /// <summary><c>chunk_ranges</c>: gộp các đoạn id tăng liên tiếp, giữ thứ tự của nguồn ("0-31 33 32").</summary>
    public static string ChunkRanges(IReadOnlyList<int> ids)
    {
        var parts = new List<string>();
        int? start = null;
        var previous = 0;
        foreach (var id in ids)
        {
            if (start != null && id == previous + 1)
            {
                previous = id;
                continue;
            }

            if (start != null)
            {
                parts.Add(start == previous ? start.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{start}-{previous}");
            }

            start = previous = id;
        }

        if (start != null)
        {
            parts.Add(start == previous ? start.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{start}-{previous}");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// <c>playgo_path_hash</c>: hash một đường dẫn trong ảnh (không có "/" đầu) như playgo-hash-table.dat lưu, với seed/key của header tệp
    /// đó. Đường dẫn mã hoá UTF-8 rồi viết hoa ASCII (bytes.upper() của Python).
    /// </summary>
    public static ulong PathHash(string path, ulong seed, ulong key)
    {
        var data = Encoding.UTF8.GetBytes(path);
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] is >= (byte)'a' and <= (byte)'z')
            {
                data[index] -= 0x20;
            }
        }

        ulong a = seed, b = Rol(seed, 11), c = Rol(seed, 23);
        ulong tail = 0;
        if (data.Length > 0)
        {
            var blocks = (data.Length - 1) / 8;
            var rest = (data.Length - 1) % 8;
            for (var index = 0; index < blocks; index++)
            {
                a ^= BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(index * 8));
                (a, b, c) = Mix(a, b, c);
            }

            var tailBytes = data.AsSpan(blocks * 8, rest + 1);
            for (var index = tailBytes.Length - 1; index >= 0; index--)
            {
                tail = (tail << 8) | tailBytes[index];
            }
        }

        return Mix(tail ^ a ^ key, b, c).A;
    }

    private static ulong Rol(ulong value, int count) => (value << count) | (value >> (64 - count));

    private static (ulong A, ulong B, ulong C) Mix(ulong a, ulong b, ulong c)
    {
        var x = Rol(Rol(c ^ b, 5) ^ a, 53);
        var y = Rol(Rol(c ^ a, 17) ^ b, 11);
        var z = Rol(Rol(b ^ a, 1) ^ c, 59);
        return ((~y & z) ^ x ^ HashMix, (~z & x) ^ y, (~x & y) ^ z);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>
    /// Ký tự XML không hợp lệ theo kiểm tra của script: điều khiển (trừ tab/LF/CR), U+FFFE/U+FFFF. Script còn loại nửa cặp surrogate, nhưng
    /// chuỗi giải mã UTF-8 chặt không bao giờ có chúng (ký tự ngoài BMP là một code point trong Python, một cặp hợp lệ trong .NET).
    /// </summary>
    private static bool HasInvalidXmlCharacter(string text)
    {
        foreach (var c in text)
        {
            if ((c < 0x20 && c is not ('\t' or '\n' or '\r')) || c is '￾' or '￿')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>f"{mask:#x}"</c> của Python: "0x" + hex thường, không đệm 0.</summary>
    private static string PythonHex(ulong value) => "0x" + value.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}
