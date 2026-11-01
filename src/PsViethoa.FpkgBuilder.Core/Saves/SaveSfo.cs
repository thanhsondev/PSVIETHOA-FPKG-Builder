using System.Buffers.Binary;
using System.Text;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Saves;

/// <summary>param.sfo hỏng hoặc không phải của save PS4 / PS5.</summary>
public sealed class SfoException(string message) : Exception(message);

/// <summary>Một mục trong param.sfo: chỉ số, khoá, định dạng, độ dài dùng / tối đa và vị trí dữ liệu trong tệp.</summary>
public sealed record SaveSfoEntry(int Index, string Key, ushort Format, uint Length, uint MaxLength, int DataOffset);

/// <summary>
/// Sửa param.sfo của save PS4 / PS5 tại chỗ (ACCOUNT_ID, TITLE_ID, PARAMS…) — không bao giờ đổi kích thước tệp. Chuyển từ
/// PSVIETHOA Save Resigner (Ngô Phi Phương).
/// </summary>
public static class SaveSfo
{
    private const uint Magic = 0x46535000;
    private const uint Version = 0x0101;
    private const ushort FmtUInt64 = 0x0404;
    private const ushort FmtUtf8 = 0x0204;
    private const ushort FmtBinary = 0x0004;

    public static List<SaveSfoEntry> Parse(byte[] buf)
    {
        if (buf.Length < 20)
        {
            throw new SfoException(Loc.T("Sfo.TooSmall"));
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(buf) != Magic)
        {
            throw new SfoException(Loc.T("Sfo.BadMagic"));
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
        if (version != Version)
        {
            throw new SfoException(Loc.F("Sfo.BadVersion", version.ToString("x"), Version.ToString("x")));
        }

        var keyOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8));
        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16));
        if (count is < 1 or > 32)
        {
            throw new SfoException(Loc.F("Sfo.BadCount", count));
        }

        if (keyOffset + count * 16 > buf.Length || dataOffset > buf.Length || 20 + count * 16 > buf.Length)
        {
            throw new SfoException(Loc.T("Sfo.BadHeader"));
        }

        var entries = new List<SaveSfoEntry>();
        for (var i = 0; i < count; i++)
        {
            var index = buf.AsSpan(20 + 16 * i);
            var keyStart = (int)(keyOffset + BinaryPrimitives.ReadUInt16LittleEndian(index));
            var end = keyStart < buf.Length ? Array.IndexOf(buf, (byte)0, keyStart) : -1;
            if (end < 0)
            {
                throw new SfoException(Loc.F("Sfo.KeyEnd", i));
            }

            var key = Encoding.UTF8.GetString(buf, keyStart, end - keyStart);
            var maxLength = BinaryPrimitives.ReadUInt32LittleEndian(index[8..]);
            var data = (long)dataOffset + BinaryPrimitives.ReadUInt32LittleEndian(index[12..]);
            if (data + maxLength > buf.Length)
            {
                throw new SfoException(Loc.F("Sfo.EntryOutside", key));
            }

            entries.Add(new SaveSfoEntry(i, key, BinaryPrimitives.ReadUInt16LittleEndian(index[2..]), BinaryPrimitives.ReadUInt32LittleEndian(index[4..]), maxLength, (int)data));
        }

        return entries;
    }

    public static SaveSfoEntry? Find(IEnumerable<SaveSfoEntry> entries, string key) => entries.FirstOrDefault(e => e.Key == key);

    public static string? ReadString(byte[] buf, IEnumerable<SaveSfoEntry> entries, string key)
    {
        var e = Find(entries, key);
        if (e == null || e.Format is not (FmtBinary or FmtUtf8))
        {
            return null;
        }

        var raw = buf.AsSpan(e.DataOffset, (int)e.MaxLength);
        var nul = raw.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? raw[..nul] : raw);
    }

    /// <summary>ACCOUNT_ID là 8 byte: uint64 (0x0404) trên PS4, nhị phân (0x0004) trên PS5.</summary>
    public static ulong? ReadAccountId(byte[] buf, IEnumerable<SaveSfoEntry> entries)
    {
        var e = Find(entries, "ACCOUNT_ID");
        return e == null || e.MaxLength < 8 ? null : BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(e.DataOffset));
    }

    /// <summary>Save PS5: ACCOUNT_ID kiểu nhị phân.</summary>
    public static bool IsPs5Format(byte[] buf, IEnumerable<SaveSfoEntry> entries) => Find(entries, "ACCOUNT_ID")?.Format == FmtBinary;

    public static void PatchAccountId(byte[] buf, IReadOnlyList<SaveSfoEntry> entries, ulong accountId)
    {
        var e = Find(entries, "ACCOUNT_ID") ?? throw new SfoException(Loc.T("Sfo.NoAccountId"));
        if (e.MaxLength < 8)
        {
            throw new SfoException(Loc.T("Sfo.AccountShort"));
        }

        if (e.Format is not (FmtUInt64 or FmtBinary))
        {
            throw new SfoException(Loc.F("Sfo.AccountFormat", e.Format.ToString("x4")));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(e.DataOffset), accountId);
        if (e.Format == FmtUInt64)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20 + 16 * e.Index + 4), 8);
        }
    }

    public static void PatchPs5UserId(byte[] buf, IReadOnlyList<SaveSfoEntry> entries, uint userId)
    {
        var e = Find(entries, "PARAMS") ?? throw new SfoException(Loc.T("Sfo.NoParams"));
        if (e.Format != FmtBinary || e.MaxLength < 8)
        {
            throw new SfoException(Loc.T("Sfo.BadParams"));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(e.DataOffset + 4), userId);
    }

    public static void PatchString(byte[] buf, IReadOnlyList<SaveSfoEntry> entries, string key, string value)
    {
        var e = Find(entries, key) ?? throw new SfoException(Loc.F("Sfo.NoKey", key));
        if (e.Format is not (FmtBinary or FmtUtf8))
        {
            throw new SfoException(Loc.F("Sfo.NotString", key));
        }

        var raw = Encoding.UTF8.GetBytes(value);
        if (raw.Length >= e.MaxLength)
        {
            throw new SfoException(Loc.F("Sfo.TooLong", key, value, raw.Length, e.MaxLength));
        }

        var target = buf.AsSpan(e.DataOffset, (int)e.MaxLength);
        target.Clear();
        raw.CopyTo(target);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20 + 16 * e.Index + 4), (uint)raw.Length + 1);
    }

    /// <summary>param.sfo đầu tiên trong một thư mục (sâu tối đa 8 cấp), hoặc chính tệp đó.</summary>
    public static string? FindParamSfo(string path)
    {
        if (File.Exists(path))
        {
            return Path.GetFileName(path).Equals("param.sfo", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        return Directory.Exists(path)
            ? SaveFiles.Walk(path, maxDepth: 8).FirstOrDefault(f => Path.GetFileName(f).Equals("param.sfo", StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>Đổi ACCOUNT_ID (và TITLE_ID nếu có) của một save. Trả về false khi lỗi (đã ghi nhật ký).</summary>
    public static bool ProcessPath(string path, ulong? accountId, string? titleId, bool backup, Action<string> log)
    {
        var sfoPath = FindParamSfo(path);
        if (sfoPath == null)
        {
            log("    [!] " + Loc.F("Resign.NoSfoIn", path));
            return false;
        }

        try
        {
            var buf = File.ReadAllBytes(sfoPath);
            var entries = Parse(buf);
            var oldId = ReadAccountId(buf, entries);
            if (accountId != null && oldId == null)
            {
                log("    [!] " + Loc.F("Resign.NoAccountInSfo", sfoPath));
                return false;
            }

            // Bản .bak cạnh param.sfo: lần sửa đầu tiên giữ lại bản gốc để hoàn tác.
            var backupPath = sfoPath + ".bak";
            if (backup && !File.Exists(backupPath))
            {
                File.Copy(sfoPath, backupPath);
            }

            if (accountId != null)
            {
                PatchAccountId(buf, entries, accountId.Value);
            }

            if (!string.IsNullOrEmpty(titleId))
            {
                PatchString(buf, entries, "TITLE_ID", titleId.ToUpperInvariant());
            }

            File.WriteAllBytes(sfoPath, buf);
            var check = Parse(buf);
            var report = "    [OK] " + sfoPath;
            if (ReadAccountId(buf, check) is { } newId)
            {
                report += $"  ACCOUNT_ID 0x{oldId:x16} → 0x{newId:x16}";
            }

            if (!string.IsNullOrEmpty(titleId))
            {
                report += "  TITLE_ID " + ReadString(buf, check, "TITLE_ID");
            }

            log(report);
            return true;
        }
        catch (Exception ex) when (ex is SfoException or IOException or UnauthorizedAccessException)
        {
            log($"    [!] {sfoPath}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Duyệt cây thư mục save theo thứ tự cố định.</summary>
public static class SaveFiles
{
    /// <summary>Duyệt từ trên xuống: tệp của một thư mục trước, rồi tới thư mục con, cả hai sắp theo tên.</summary>
    public static IEnumerable<string> Walk(string root, Func<string, bool>? enterDirectory = null, int maxDepth = int.MaxValue)
    {
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            for (var i = dirs.Length - 1; i >= 0; i--)
            {
                if (enterDirectory == null || enterDirectory(dirs[i]))
                {
                    pending.Push((dirs[i], depth + 1));
                }
            }
        }
    }
}
