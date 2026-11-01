using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Ps4;

/// <summary>
/// param.sfo sửa được (trình sửa SFO của Đóng Update / Gộp). Mục không đụng tới giữ nguyên byte; tệp chỉ được dựng lại (khoá sắp
/// xếp, dữ liệu căn 4 byte) để chuỗi đã sửa hoặc thêm có thể dài hơn độ dài tối đa cũ.
/// </summary>
public sealed class Ps4SfoDocument
{
    public const ushort Binary = 0x0004;
    public const ushort Utf8 = 0x0204;
    public const ushort Integer = 0x0404;

    private static readonly HashSet<string> HexKeys = new(StringComparer.Ordinal) { "ATTRIBUTE", "ATTRIBUTE2", "DOWNLOAD_DATA_SIZE", "SYSTEM_VER", "PARENTAL_LEVEL_FLAGS" };

    public sealed class Entry
    {
        public string Key { get; internal set; } = string.Empty;

        public ushort Format { get; internal set; }

        public int MaxLength { get; internal set; }

        /// <summary>Byte đang dùng (chuỗi Utf8 gồm cả NUL).</summary>
        public byte[] Data { get; internal set; } = Array.Empty<byte>();

        public string TypeLabel => Loc.T(Format switch { Utf8 => "Ps4.Sfo.TypeString", Integer => "Ps4.Sfo.TypeNumber", _ => "Ps4.Sfo.TypeBinary" });

        /// <summary>Chữ hiển thị: chuỗi, số (hex với khoá kiểu cờ) hoặc byte hex.</summary>
        public string Display
        {
            get
            {
                if (Format == Integer && Data.Length >= 4)
                {
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(Data);
                    return HexKeys.Contains(Key) ? "0x" + value.ToString("X8", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
                }

                if (Format == Utf8)
                {
                    var nul = Array.IndexOf(Data, (byte)0);
                    return Encoding.UTF8.GetString(Data, 0, nul >= 0 ? nul : Data.Length);
                }

                return Convert.ToHexString(Data);
            }
        }
    }

    public List<Entry> Entries { get; } = new();

    public Entry? this[string key] => Entries.FirstOrDefault(e => e.Key == key);

    public static Ps4SfoDocument Parse(byte[] buf)
    {
        if (buf.Length < 20 || BinaryPrimitives.ReadUInt32BigEndian(buf) != 0x00505346)
        {
            throw new InvalidDataException(Loc.T("Ps4.Sfo.BadMagic"));
        }

        var keyTable = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8));
        var dataTable = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12));
        var count = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(16));
        if (count is < 0 or > 512 || 20 + count * 16 > buf.Length || keyTable > buf.Length || dataTable > buf.Length)
        {
            throw new InvalidDataException(Loc.T("Ps4.Sfo.BadHeader"));
        }

        var document = new Ps4SfoDocument();
        for (var i = 0; i < count; i++)
        {
            var index = buf.AsSpan(20 + 16 * i);
            var keyStart = keyTable + BinaryPrimitives.ReadUInt16LittleEndian(index);
            var keyEnd = keyStart < buf.Length ? Array.IndexOf(buf, (byte)0, keyStart) : -1;
            if (keyEnd < 0)
            {
                throw new InvalidDataException(Loc.F("Ps4.Sfo.BadKey", i));
            }

            var format = BinaryPrimitives.ReadUInt16LittleEndian(index[2..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(index[4..]);
            var max = BinaryPrimitives.ReadInt32LittleEndian(index[8..]);
            var offset = dataTable + BinaryPrimitives.ReadInt32LittleEndian(index[12..]);
            if (max < 0 || length < 0 || length > max || offset < 0 || (long)offset + max > buf.Length)
            {
                throw new InvalidDataException(Loc.T("Ps4.Sfo.DataOutside"));
            }

            document.Entries.Add(new Entry
            {
                Key = Encoding.ASCII.GetString(buf, keyStart, keyEnd - keyStart),
                Format = format,
                MaxLength = max,
                Data = buf.AsSpan(offset, length).ToArray(),
            });
        }

        return document;
    }

    /// <summary>Đặt giá trị từ chữ người dùng nhập. Chuỗi được dài ra (độ dài tối đa nới, căn 4 byte).</summary>
    public void SetText(string key, string text)
    {
        var entry = this[key] ?? throw new KeyNotFoundException(key);
        entry.Data = Encode(entry.Format, text, key);
        if (entry.Data.Length > entry.MaxLength)
        {
            entry.MaxLength = Align(entry.Data.Length);
        }
    }

    public void Add(string key, ushort format, string text)
    {
        key = key.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(key, "^[A-Z0-9_]{1,64}$"))
        {
            throw new ArgumentException(Loc.T("Ps4.Sfo.KeyChars"));
        }

        if (this[key] != null)
        {
            throw new ArgumentException(Loc.F("Ps4.Sfo.KeyExists", key));
        }

        var data = Encode(format, text, key);
        Entries.Add(new Entry
        {
            Key = key,
            Format = format,
            Data = data,
            MaxLength = format == Integer ? 4 : Math.Max(Align(data.Length), format == Utf8 ? 128 : data.Length),
        });
    }

    public bool Remove(string key) => Entries.RemoveAll(e => e.Key == key) > 0;

    /// <summary>Null khi chữ hợp lệ với kiểu mục này, ngược lại là thông báo lỗi đã dịch.</summary>
    public static string? Validate(ushort format, string text, string key)
    {
        try
        {
            Encode(format, text, key);
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    public static int Align(int n) => (n + 3) & ~3;

    private static byte[] Encode(ushort format, string text, string key)
    {
        switch (format)
        {
            case Integer:
            {
                var trimmed = text.Trim();
                uint value;
                var ok = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                    : uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                if (!ok)
                {
                    throw new FormatException(Loc.F("Ps4.Sfo.NeedNumber", key));
                }

                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
                return bytes;
            }

            case Utf8:
                if (text.Contains('\0'))
                {
                    throw new FormatException(Loc.F("Ps4.Sfo.NoNul", key));
                }

                return Encoding.UTF8.GetBytes(text + "\0");

            default:
            {
                var hex = text.Replace(" ", string.Empty, StringComparison.Ordinal);
                if (hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
                {
                    throw new FormatException(Loc.F("Ps4.Sfo.NeedHex", key));
                }

                return Convert.FromHexString(hex);
            }
        }
    }

    public byte[] ToBytes()
    {
        var entries = Entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        var keyTable = 20 + entries.Count * 16;
        var keyBytes = entries.Sum(e => e.Key.Length + 1);
        var dataTable = Align(keyTable + keyBytes);
        var total = dataTable + entries.Sum(e => e.MaxLength);
        var buf = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(buf, 0x00505346);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 0x0101);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), keyTable);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), dataTable);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16), entries.Count);
        var keyOffset = 0;
        var dataOffset = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var index = buf.AsSpan(20 + 16 * i);
            BinaryPrimitives.WriteUInt16LittleEndian(index, (ushort)keyOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(index[2..], entry.Format);
            BinaryPrimitives.WriteInt32LittleEndian(index[4..], entry.Data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(index[8..], entry.MaxLength);
            BinaryPrimitives.WriteInt32LittleEndian(index[12..], dataOffset);
            Encoding.ASCII.GetBytes(entry.Key).CopyTo(buf, keyTable + keyOffset);
            entry.Data.CopyTo(buf, dataTable + dataOffset);
            keyOffset += entry.Key.Length + 1;
            dataOffset += entry.MaxLength;
        }

        return buf;
    }

    /// <summary>param.sfo mà một lượt Đóng Update / Gộp của nguồn này sẽ bắt đầu từ.</summary>
    public static (byte[] Bytes, string Origin) ReadSource(string input, Ps4JobKind kind)
    {
        if (File.Exists(input))
        {
            return (Ps4PackageReader.ReadParamSfo(input) ?? throw new InvalidDataException(Loc.T("Ps4.Sfo.PkgNoSfo")), input);
        }

        var image = Ps4ProjectBuilder.ResolveImage(input);
        var path = kind == Ps4JobKind.Merge ? Ps4ProjectBuilder.DetectVersion(image, Ps4JobKind.Patch).SfoPath : Ps4ProjectBuilder.LocateMetadata(image, "param.sfo");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(Loc.T("Ps4.Sfo.SourceNoSfo"));
        }

        return (File.ReadAllBytes(path), path);
    }
}
