using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Một UCP của sce_sys/uds được script fix15 sửa digest hoặc thay bằng bản rỗng (đường dẫn trong gói, tệp đã ghi).</summary>
/// <param name="Stub">true = UCP rỗng tạo từ NPWR id (nguồn thiếu hoặc không đọc được); false = chỉ tính lại digest, giữ nguyên các mục.</param>
public sealed record SonySdkUcpFix(string Relative, string Path, bool Stub, string NpCommunicationId);

/// <summary>
/// Phần UDS của <c>create-gp5-from-folder.py</c> (sdk-fpkg279-fix15, <c>prepare_ucp_stubs</c>): <c>sce_sys/uds/npbind.dat</c> hợp lệ cho
/// biết NPWR id của từng <c>udsNN.ucp</c> (theo chỉ số). UCP chỉ lệch digest thì tính lại digest và giữ mọi mục; UCP thiếu hoặc không đọc
/// được thì GP5 dùng một UCP rỗng đúng cấu trúc (mất dữ liệu UDS gốc trong gói, nguồn không đổi). Các tệp ghi vào
/// <c>.gp5-assets/&lt;tên&gt;/sce_sys/uds/</c> như script.
/// </summary>
public static partial class SonySdkUds
{
    public const uint UcpMagic = 0xB228C60A;

    public const uint NpbindMagic = 0xD294A018;

    private const int Header = 0x60;

    private const int Entry = 0x40;

    private const int Align = 0x10;

    public const int ConfigurationId = 54474;

    /// <summary><c>UCP_MEMBER_ORDER</c>.</summary>
    public static readonly IReadOnlyList<string> MemberOrder = ["enum_definition.json", "events_definition.json", "stats_definition.json", "stats_extraction.json"];

    /// <summary><c>UCP_ID_MEMBERS</c>: mục mang npCommunicationId.</summary>
    private static readonly HashSet<string> IdMembers = new(StringComparer.Ordinal) { "enum_definition.json", "stats_definition.json", "stats_extraction.json" };

    public const string NpbindPath = "sce_sys/uds/npbind.dat";

    [GeneratedRegex("^NPWR[0-9]{5}_[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex NpwrPattern();

    /// <summary><c>read_npbind_ids</c>: NPWR id theo thứ tự chỉ số; lỗi cấu trúc → InvalidDataException với thông báo của script.</summary>
    public static IReadOnlyList<string> ReadNpbindIds(string path, ReadOnlySpan<byte> data)
    {
        if (data.Length != 0x214 || BinaryPrimitives.ReadUInt32BigEndian(data) != NpbindMagic)
        {
            throw new InvalidDataException($"{path}: not an npbind.dat");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != 1 || BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]) != data.Length ||
            !SHA1.HashData(data[..0x200]).AsSpan().SequenceEqual(data[0x200..0x214]))
        {
            throw new InvalidDataException($"{path}: invalid version, size or SHA-1 authentication");
        }

        var entrySize = BinaryPrimitives.ReadUInt32BigEndian(data[0x14..]);
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[0x1C..]);
        if (entrySize < 4 || 0x80 + (ulong)count * entrySize > 0x200)
        {
            throw new InvalidDataException($"{path}: {count} entries of {PythonHex(entrySize)} bytes do not fit");
        }

        var ids = new List<string>((int)count);
        for (var index = 0; index < count; index++)
        {
            var at = 0x80 + index * (int)entrySize;
            var end = at + (int)entrySize;
            var found = false;
            while (at + 4 <= end)
            {
                int tag = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
                if (tag == 0)
                {
                    break;
                }

                if (at + 4 + length > end)
                {
                    throw new InvalidDataException($"{path}: entry {index} has a truncated TLV record");
                }

                if (tag == 0x10)
                {
                    var value = data.Slice(at + 4, length);
                    var zero = value.IndexOf((byte)0);
                    var raw = zero < 0 ? value : value[..zero];
                    foreach (var b in raw)
                    {
                        if (b > 0x7F)
                        {
                            throw new InvalidDataException($"{path}: entry {index} has an invalid NPWR id");
                        }
                    }

                    var npwr = Encoding.ASCII.GetString(raw);
                    if (!NpwrPattern().IsMatch(npwr))
                    {
                        throw new InvalidDataException($"{path}: entry {index} has an invalid NPWR id: '{npwr}'");
                    }

                    ids.Add(npwr);
                    found = true;
                    break;
                }

                at += 4 + length;
            }

            if (!found)
            {
                throw new InvalidDataException($"{path}: entry {index} has no NPWR id");
            }
        }

        return ids;
    }

    /// <summary><c>ucp_digest</c>: SHA-1 của tệp với 20 byte digest (0x1C..0x30) đặt về 0.</summary>
    public static byte[] Digest(ReadOnlySpan<byte> data)
    {
        var zeroed = data.ToArray();
        zeroed.AsSpan(0x1C, 20).Clear();
        return SHA1.HashData(zeroed);
    }

    /// <summary><c>unpack_ucp</c>: các mục (tên, nội dung); lỗi cấu trúc → InvalidDataException với thông báo của script.</summary>
    public static IReadOnlyList<(string Name, byte[] Body)> Unpack(ReadOnlySpan<byte> data)
    {
        if (data.Length < Header)
        {
            throw new InvalidDataException($"{data.Length} bytes, shorter than the 0x60-byte header");
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        var version = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var size = BinaryPrimitives.ReadUInt64BigEndian(data[8..]);
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]);
        var entrySize = BinaryPrimitives.ReadUInt32BigEndian(data[0x14..]);
        if (magic != UcpMagic)
        {
            throw new InvalidDataException($"bad magic 0x{magic:x8}");
        }

        if (version != 1)
        {
            throw new InvalidDataException($"unsupported version {version}");
        }

        if (size != (ulong)data.Length)
        {
            throw new InvalidDataException($"size field {size} != file size {data.Length}");
        }

        if (entrySize != Entry)
        {
            throw new InvalidDataException($"entry size {PythonHex(entrySize)}, expected 0x40");
        }

        var tableEnd = Header + (long)count * Entry;
        if (tableEnd > data.Length)
        {
            throw new InvalidDataException($"{count} entries do not fit in the file");
        }

        var members = new List<(string, byte[])>((int)count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var entry = Header + index * Entry;
            var nameField = data.Slice(entry, 0x20);
            var zero = nameField.IndexOf((byte)0);
            // decode("latin1"): mỗi byte một ký tự U+0000..U+00FF.
            var name = Encoding.Latin1.GetString(zero < 0 ? nameField : nameField[..zero]);
            if (name.Length == 0 || seen.Contains(name) || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            {
                throw new InvalidDataException($"member {index} has an empty, duplicate or unsafe name: '{name}'");
            }

            seen.Add(name);
            var offset = BinaryPrimitives.ReadUInt64BigEndian(data[(entry + 0x20)..]);
            var length = BinaryPrimitives.ReadUInt64BigEndian(data[(entry + 0x28)..]);
            if (offset < (ulong)tableEnd || offset > (ulong)data.Length || length > (ulong)data.Length - offset)
            {
                throw new InvalidDataException($"member '{name}' runs past the end of the file");
            }

            members.Add((name, data.Slice((int)offset, (int)length).ToArray()));
        }

        return members;
    }

    /// <summary><c>ucp_problems</c>: danh sách vấn đề; rỗng = UCP dùng được.</summary>
    public static IReadOnlyList<string> Problems(ReadOnlySpan<byte> data, string npwr)
    {
        IReadOnlyList<(string Name, byte[] Body)> members;
        try
        {
            members = Unpack(data);
        }
        catch (InvalidDataException ex)
        {
            return [ex.Message];
        }

        var problems = new List<string>();
        var digest = Digest(data);
        if (!digest.AsSpan().SequenceEqual(data[0x1C..0x30]))
        {
            problems.Add($"digest is {Convert.ToHexStringLower(data[0x1C..0x30])}, should be {Convert.ToHexStringLower(digest)}");
        }

        var names = members.Select(member => member.Name).ToList();
        var missing = MemberOrder.Where(name => !names.Contains(name)).ToList();
        if (missing.Count > 0)
        {
            problems.Add("missing members: " + string.Join(", ", missing));
        }

        foreach (var (name, body) in members)
        {
            if (!IdMembers.Contains(name))
            {
                continue;
            }

            string? found;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"{name} is not valid JSON ('{document.RootElement.ValueKind.ToString().ToLowerInvariant()}' object has no attribute 'get')");
                    continue;
                }

                found = document.RootElement.TryGetProperty("npCommunicationId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()
                    : document.RootElement.TryGetProperty("npCommunicationId", out value) ? value.GetRawText() : null;
            }
            catch (JsonException ex)
            {
                problems.Add($"{name} is not valid JSON ({ex.Message})");
                continue;
            }

            if (found != npwr)
            {
                problems.Add($"{name} npCommunicationId is {(found == null ? "None" : "'" + found + "'")}, npbind says '{npwr}'");
            }
        }

        return problems;
    }

    /// <summary><c>pack_ucp</c>: UCP từ các mục, digest hợp lệ ở header. Mục đã đủ 16 byte vẫn được thêm trọn 16 byte đệm như tệp thật.</summary>
    public static byte[] Pack(IReadOnlyList<(string Name, byte[] Body)> members)
    {
        var tableEnd = Header + Entry * members.Count;
        var offsets = new List<int>(members.Count);
        var at = tableEnd;
        foreach (var (_, body) in members)
        {
            offsets.Add(at);
            at += body.Length + (Align - body.Length % Align);
        }

        var output = new byte[at];
        BinaryPrimitives.WriteUInt32BigEndian(output, UcpMagic);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(8), (ulong)at);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x10), (uint)members.Count);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0x14), Entry);
        for (var index = 0; index < members.Count; index++)
        {
            var entry = Header + index * Entry;
            var name = Encoding.UTF8.GetBytes(members[index].Name);
            name.CopyTo(output, entry);
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(entry + 0x20), (ulong)offsets[index]);
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(entry + 0x28), (ulong)members[index].Body.Length);
            members[index].Body.CopyTo(output, offsets[index]);
        }

        SHA1.HashData(output).CopyTo(output, 0x1C);
        return output;
    }

    /// <summary>
    /// UCP rỗng cho <paramref name="npwr"/> (<c>ucp_member_documents</c> + <c>json.dumps(separators=(", ", ": "))</c>): bốn mục theo
    /// <see cref="MemberOrder"/>, JSON một dòng như Python ghi.
    /// </summary>
    public static byte[] Stub(string npwr)
    {
        var id = JsonString(npwr);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enum_definition.json"] = $"{{\"schemaVersion\": \"1.1\", \"npCommunicationId\": {id}, \"enumDefinitionRevision\": 1, \"enumArray\": []}}",
            ["events_definition.json"] = $"{{\"configurationId\": {ConfigurationId}, \"events\": [], \"nextOffset\": 0, \"previousOffset\": 0, \"totalItemCount\": 0}}",
            ["stats_definition.json"] = $"{{\"schemaVersion\": \"1.1\", \"npCommunicationId\": {id}, \"statsDefinitionRevision\": 1, \"statDefinitionArray\": []}}",
            ["stats_extraction.json"] = $"{{\"schemaVersion\": \"1.1\", \"npCommunicationId\": {id}, \"statsExtractionRevision\": 1, \"statsExtractionRuleArray\": []}}",
        };
        return Pack(MemberOrder.Select(name => (name, Encoding.ASCII.GetBytes(documents[name]))).ToList());
    }

    /// <summary>
    /// <c>prepare_ucp_stubs</c> trên danh sách tệp của GP5: <paramref name="files"/> là (đường dẫn trong gói, tệp trên máy). Trả về các
    /// thay thế (chỉ số tệp → bản sửa/bản rỗng) và các UCP thêm mới (nguồn thiếu), ghi dòng log của script vào <paramref name="lines"/>.
    /// </summary>
    public static (Dictionary<int, SonySdkUcpFix> Replacements, List<SonySdkUcpFix> Additions) Prepare(
        IReadOnlyList<(string Relative, string FullPath)> files, string npbindSourcePath, string generatedSystem, List<string> lines, CancellationToken cancellationToken = default)
    {
        var replacements = new Dictionary<int, SonySdkUcpFix>();
        var additions = new List<SonySdkUcpFix>();
        var npbindIndex = -1;
        var byRelative = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < files.Count; index++)
        {
            // Như dict comprehension của script: trùng khoá (khác hoa thường) thì tệp sau thắng.
            var folded = PythonCaseFold.Fold(files[index].Relative);
            byRelative[folded] = index;
            if (folded == NpbindPath)
            {
                npbindIndex = index;
            }
        }

        // Script: chỉ khi root/sce_sys/uds/npbind.dat là một tệp (tệp của nguồn, kể cả khi nó bị loại khỏi GP5 — npbind không bao giờ bị loại).
        var npbind = npbindIndex >= 0 ? files[npbindIndex].FullPath : null;
        if (npbind == null || !File.Exists(npbind))
        {
            return (replacements, additions);
        }

        IReadOnlyList<string> ids;
        try
        {
            ids = ReadNpbindIds(npbindSourcePath, File.ReadAllBytes(npbind));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            lines.Add($"[Warn] {ex.Message}; UCP containers are packaged as they are");
            return (replacements, additions);
        }

        for (var index = 0; index < ids.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var npwr = ids[index];
            var relative = $"sce_sys/uds/uds{index:00}.ucp";
            var destination = System.IO.Path.Combine(generatedSystem, "uds", $"uds{index:00}.ucp");
            string reason;
            if (!byRelative.TryGetValue(PythonCaseFold.Fold(relative), out var sourceIndex))
            {
                sourceIndex = -1;
                reason = "missing";
            }
            else
            {
                var original = File.ReadAllBytes(files[sourceIndex].FullPath);
                var problems = Problems(original, npwr);
                if (problems.Count == 0)
                {
                    continue;
                }

                if (problems.Count == 1 && problems[0].StartsWith("digest is ", StringComparison.Ordinal))
                {
                    var repaired = original.ToArray();
                    Digest(original).CopyTo(repaired, 0x1C);
                    EnsureNew(destination, "generated UCP repair already exists");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, repaired);
                    replacements[sourceIndex] = new SonySdkUcpFix(files[sourceIndex].Relative, destination, false, npwr);
                    lines.Add($"UCP {relative} has a stale digest; repaired the digest without changing members");
                    continue;
                }

                reason = "invalid: " + string.Join("; ", problems);
            }

            var stub = Stub(npwr);
            EnsureNew(destination, "generated UCP stub already exists");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, stub);
            lines.Add($"[Warn] UCP {relative} is {reason}; GP5 uses an empty stub for {npwr} ({stub.Length} bytes); source is unchanged");
            if (sourceIndex >= 0)
            {
                replacements[sourceIndex] = new SonySdkUcpFix(files[sourceIndex].Relative, destination, true, npwr);
            }
            else
            {
                additions.Add(new SonySdkUcpFix(relative, destination, true, npwr));
            }
        }

        return (replacements, additions);
    }

    private static void EnsureNew(string path, string message)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"{message}: {path}");
        }
    }

    /// <summary>Chuỗi JSON ASCII (ensure_ascii=True) — NPWR id chỉ có chữ, số và "_".</summary>
    private static string JsonString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string PythonHex(uint value) => "0x" + value.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}
