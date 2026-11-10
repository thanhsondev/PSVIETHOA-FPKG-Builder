using System.Buffers.Binary;
using PsViethoa.FpkgBuilder.Core.Localization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace PsViethoa.FpkgBuilder.Core.Zstd;

/// <summary>
/// Gói zstd AMPR của PS5 (định dạng của ps5-dump-forge, runtime ampr-emu-ng đọc trên máy):
/// <c>ampr_zstd.index</c> (AMPRZPK1) ở gốc game + các volume <c>ampr_zstd-000.pak</c>, <c>-001.pak</c>… (AMPRZDT1, mỗi file &lt; 4 GiB).
/// Mỗi file được cắt thành khối 256 KiB, mỗi khối là một frame zstd riêng (có content size + checksum, cửa sổ ≤ 256 KiB)
/// hoặc để thô khi zstd không làm nhỏ được. File hệ thống (eboot, *.prx/*.sprx/*.elf/*.self, *.ini, sce_sys/, sce_module/, fakelib*/)
/// luôn để rời; <c>fakelib/libSceAmpr.sprx</c> = ampr-emu-ng, <c>ampr_emu.index</c> (AMPRIDX3) liệt kê mọi file.
/// Viết theo docs/zstd.md của github.com/quer3q/ps5-dump-forge.
/// </summary>
public static partial class AmprZstd
{
    public const string IndexName = "ampr_zstd.index";
    public const string EmuIndexName = "ampr_emu.index";
    public const string RuntimePath = "fakelib/libSceAmpr.sprx";
    public const string JournalName = "ampr_commands.bin";
    private static readonly string[] LogNames = ["ampr_emu.log", "apr_emu.log"];
    private static readonly string[] Artifacts =
        ["ampr_assets.index", "ampr_assets.index.crc", "ampr_assets.index.runtime", EmuIndexName, RuntimePath, JournalName, "ampr_emu.log", "apr_emu.log"];
    public const int ChunkShift = 18;
    public const int ChunkSize = 1 << ChunkShift;
    public const int IndexHeaderSize = 128;
    public const int VolumeHeaderSize = 64;
    public const int MaxVolumes = 1 << 12;
    public const long VolumeLimit = (1L << 32) - (1L << 16);
    public const int DefaultLevel = 5;
    public const int MaxThreads = 32;
    private static ReadOnlySpan<byte> IndexMagic => "AMPRZPK1"u8;
    private static ReadOnlySpan<byte> VolumeMagic => "AMPRZDT1"u8;

    /// <summary>Done / Total: byte đã xử lý / tổng việc; PackedIn → PackedOut: byte đã nén vào gói → byte ghi ra (tỉ lệ nén hiện tại).</summary>
    public sealed record Progress(double Percent, double BytesPerSecond, string Detail, long Done = 0, long Total = 0, long PackedIn = 0, long PackedOut = 0);

    // ───────────────────────────── tên, luật để rời, băm ─────────────────────────────

    public static string VolumeName(int id) => $"ampr_zstd-{id:D3}.pak";

    /// <summary>Tên file gói zstd ở gốc game (ampr_zstd.index, ampr_zstd-*.pak), không phân biệt hoa thường.</summary>
    public static bool IsPackName(string rel)
    {
        if (rel.Contains('/')) return false;
        var p = rel.ToLowerInvariant();
        return p == IndexName || (p.StartsWith("ampr_zstd-", StringComparison.Ordinal) && p.EndsWith(".pak", StringComparison.Ordinal));
    }

    /// <summary>File luôn để rời: máy / runtime nạp trước (hoặc ngoài) lớp gói. Đường dẫn tương đối, dấu '/'.</summary>
    public static bool AlwaysLoose(string path)
    {
        int slash = path.LastIndexOf('/');
        var file = slash < 0 ? path : path[(slash + 1)..];
        var first = (slash < 0 ? path : path[..path.IndexOf('/')]).ToLowerInvariant();
        bool inDir = slash >= 0;
        return file.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".elf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".self", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".prx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase)
            // ps5-dump-forge 0.5.5: *.ini không bao giờ vào gói (dlc_emu.ini và file cấu hình người dùng sửa tay)
            || path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
            || (inDir && (first == "sce_sys" || first == "sce_module" || first.StartsWith("fakelib", StringComparison.Ordinal)))
            || Artifacts.Any(a => a.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>FNV-1a 64 (basis chuẩn) trên UTF-8 của đường dẫn tương đối, giữ hoa thường.</summary>
    public static ulong PathHash(string path) => PathHash(Encoding.UTF8.GetBytes(path));

    private static ulong PathHash(ReadOnlySpan<byte> bytes)
    {
        ulong h = 14695981039346656037UL;
        foreach (var b in bytes) h = (h ^ b) * 1099511628211UL;
        return h;
    }

    public static bool ValidPath(string path)
        => path.Length > 0 && path[0] != '/' && path.IndexOf('\\') < 0 && path.IndexOf('\0') < 0
           && path.Split('/').All(p => p is not ("" or "." or ".."));

    // ───────────────────────────── CRC-32 (IEEE) ─────────────────────────────

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> data) => Crc(0, data);

    /// <summary>CRC của header dài <paramref name="len"/> với <paramref name="n"/> byte tại <paramref name="at"/> coi như 0.</summary>
    private static uint CrcZeroed(ReadOnlySpan<byte> bytes, int at, int n, int len)
    {
        uint c = Crc(0, bytes[..at]);
        Span<byte> zero = stackalloc byte[n];
        zero.Clear();
        c = Crc(c, zero);
        return Crc(c, bytes[(at + n)..len]);
    }

    // ───────────────────────────── runtime ─────────────────────────────

    public enum RuntimeKind { None, Ng, AmprEmu, Other }

    public const string NgVersion = "ampr-emu-ng 0.3.4";
    public const string ReleaseVersion = "ampr_emu 0.4.2.1";
    private const string NgSha256 = "7D1BB260F954B755ED687733E52CCFA38C31440A9B88A6FBBA5B3B75E08BFA26";
    private const string ReleaseSha256 = "69E6C4D5E4F5FB83C9E01815DB5861C4C75734ACBF4595CAFA50D4C218116D1A";
    /// <summary>Các bản ampr-emu-ng cũ hơn (ps5-dump-forge runtime.rs) — vẫn nhận là runtime đọc gói zstd.</summary>
    /// <summary>Tên các bản ampr-emu-ng cũ (theo bảng pin của ps5-dump-forge vendor/ampr-emu-ng/README.md).</summary>
    private static readonly Dictionary<string, string> OlderNgNames = new()
    {
        ["1C1051F91BC7CBA19B15362B2B832114B53A4C3D6193F50C90FDF1AE5D78D268"] = "ampr-emu-ng 0.3.3",
        ["9A8F553A68D4E6E6BE11BD9F8D5519B5FAA68560761881966ABFF1DD65BF19F7"] = "ampr-emu-ng 0.3.2",
        ["BDD93CE2FBCC2D0986B16D77604CC0770D994CCC124B9051BA9F53AEAC39F56F"] = "ampr-emu-ng 0.3.1",
        ["5F39CA8EBE169AA15D8A8BA8EC2F9CD4195F509D55DAAF4B4EDBFA80B145035C"] = "ampr-emu-ng 0.3.0",
        ["DC7D59D551071DC7AD2002865EFE9E54EA0393523D1258EC1CE9FC7389A45BC4"] = "ampr-emu-ng 6972875 (sau 0.2.0)",
        ["7ACB2173D9691B5DFB94EA4007660920B7250111C5FD7A86C6EB91B763AD3D76"] = "ampr-emu-ng 0.2.0",
        ["69EE3B420C6798FA1B5FB5A9FED75479403BC8A64FE382DB301477B1BF705956"] = "ampr-emu-ng 0.1.0",
        ["0BF2C3E39580D937C20BC320323F6394FF624FB5839FC1160EF27CEEA764A4F7"] = Loc.T("Zstd.Core.001"),
    };

    private static byte[]? ngRuntime, releaseRuntime;
    /// <summary>ampr-emu-ng 0.3.4 (đọc gói zstd + LZ4), nhúng trong dll.</summary>
    public static byte[] NgRuntime => ngRuntime ??= LoadRuntime("PsViethoa.FpkgBuilder.Core.AmprEmuNg.sprx", NgSha256);
    /// <summary>ampr_emu 0.4.2.1 gốc — thay lại khi giải zstd.</summary>
    public static byte[] ReleaseRuntime => releaseRuntime ??= LoadRuntime("PsViethoa.FpkgBuilder.Core.AmprEmu.sprx", ReleaseSha256);

    private static byte[] LoadRuntime(string name, string sha)
    {
        using var s = typeof(AmprZstd).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(Loc.T("Zstd.Core.002") + name);
        var b = new byte[s.Length];
        s.ReadExactly(b);
        if (Convert.ToHexString(SHA256.HashData(b)) != sha) throw new InvalidDataException(Loc.T("Zstd.Core.003") + name + Loc.T("Zstd.Core.004"));
        return b;
    }

    public static (RuntimeKind Kind, string Note) ClassifyRuntime(string gameDir)
    {
        var p = Path.Combine(gameDir, "fakelib", "libSceAmpr.sprx");
        if (!File.Exists(p)) return (RuntimeKind.None, Loc.T("Zstd.Core.005"));
        var b = File.ReadAllBytes(p);
        var sha = Convert.ToHexString(SHA256.HashData(b));
        if (sha == NgSha256) return (RuntimeKind.Ng, NgVersion);
        if (OlderNgNames.TryGetValue(sha, out var older)) return (RuntimeKind.Ng, older + Loc.T("Zstd.Core.006") + NgVersion[12..] + ")");
        if (sha == ReleaseSha256) return (RuntimeKind.AmprEmu, ReleaseVersion);
        if (b.AsSpan().IndexOf("AMPRZPK1"u8) >= 0)
        {
            var m = System.Text.RegularExpressions.Regex.Match(Encoding.Latin1.GetString(b), @"ampr-emu-ng \d+\.\d+\.\d+");
            return (RuntimeKind.Ng, (m.Success ? m.Value : "ampr-emu-ng") + Loc.T("Zstd.Core.007"));
        }
        if (b.AsSpan().IndexOf("ampr_emu"u8) >= 0) return (RuntimeKind.AmprEmu, Loc.T("Zstd.Core.008"));
        return (RuntimeKind.Other, Loc.F("Zstd.Core.009", b.Length));
    }

    // ───────────────────────────── ampr_emu.index (AMPRIDX3) ─────────────────────────────

    private const ulong EmuBasis = 1469598103934665603UL;   // basis của định dạng (FNV-64 chuẩn bỏ chữ số cuối) — ampr_emu tự tính như vậy

    private static byte[] EmuKey(string path)
    {
        var b = Encoding.UTF8.GetBytes(path);
        for (int i = 0; i < b.Length; i++) b[i] = b[i] switch { (byte)'\\' => (byte)'/', >= (byte)'A' and <= (byte)'Z' => (byte)(b[i] + 0x20), _ => b[i] };
        return b;
    }

    public static ulong EmuHash(string path)
    {
        ulong h = EmuBasis;
        foreach (var x in EmuKey(path)) h = (h ^ x) * 0x100000001B3UL;
        return Math.Max(h, 1);
    }

    /// <summary>Bảng file AMPRIDX3 cho mọi file của game (đường dẫn trong game, không có /app0/), mọi bản ghi cùng mtime.</summary>
    public static byte[] BuildEmuIndex(IEnumerable<(string Path, long Size)> files, long mtime)
    {
        var rows = files.Select(f => { var p = "/app0/" + f.Path.TrimStart('/'); return (Key: EmuKey(p), Path: p, f.Size); }).ToList();
        rows.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        for (int i = 1; i < rows.Count; i++)
            if (rows[i - 1].Key.AsSpan().SequenceEqual(rows[i].Key))
                throw new InvalidDataException(Loc.F("Zstd.Core.010", rows[i - 1].Path, rows[i].Path));
        var records = new byte[rows.Count * 24];
        var paths = new MemoryStream();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = records.AsSpan(i * 24);
            var pb = Encoding.UTF8.GetBytes(rows[i].Path);
            BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)paths.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], (uint)pb.Length);
            BinaryPrimitives.WriteInt64LittleEndian(r[8..], rows[i].Size);
            BinaryPrimitives.WriteInt64LittleEndian(r[16..], mtime);
            paths.Write(pb); paths.WriteByte(0);
        }
        int count = (int)Math.Max(2, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, rows.Count * 2)));
        int mask = count - 1;
        var slots = new (ulong H, uint Index, uint Flags)[count];
        for (int i = 0; i < rows.Count; i++)
        {
            ulong h = EmuHash(rows[i].Path);
            int at = (int)(h & (ulong)mask);
            bool shared = false;
            while (slots[at].Index != 0)
            {
                if (slots[at].H == h) { slots[at].Flags |= 1; shared = true; }
                at = (at + 1) & mask;
            }
            slots[at] = (h, (uint)i + 1, shared ? 1u : 0u);
        }
        long slotsAt = (0x30 + records.Length + paths.Length + 15) & ~15L;
        var outp = new byte[slotsAt + count * 16];
        "AMPRIDX3"u8.CopyTo(outp);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(12), 24);
        BinaryPrimitives.WriteUInt64LittleEndian(outp.AsSpan(0x10), (ulong)rows.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(outp.AsSpan(0x18), (ulong)paths.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(outp.AsSpan(0x20), (ulong)slotsAt);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(0x28), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(0x2C), (uint)count);
        records.CopyTo(outp, 0x30);
        paths.ToArray().CopyTo(outp, 0x30 + records.Length);
        for (int i = 0; i < count; i++)
        {
            var s = outp.AsSpan((int)slotsAt + i * 16);
            BinaryPrimitives.WriteUInt64LittleEndian(s, slots[i].H);
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], slots[i].Index);
            BinaryPrimitives.WriteUInt32LittleEndian(s[12..], slots[i].Flags);
        }
        return outp;
    }

    // ───────────────────────────── index AMPRZPK1 ─────────────────────────────

    public sealed record FileRec(string Path, long Size, int FirstChunk, bool StoreOnly)
    {
        public int ChunkCount => (int)((Size + ChunkSize - 1) >> ChunkShift);
    }

    public readonly record struct Chunk(uint Offset, int Volume, int Stored, bool Zstd)
    {
        public ulong Pack()
        {
            if (Volume < 0 || Volume >= MaxVolumes || Stored <= 0 || Stored > ChunkSize || Offset < VolumeHeaderSize || (long)Offset + Stored > VolumeLimit)
                throw new InvalidDataException(Loc.T("Zstd.Core.011"));
            return Offset | ((ulong)Volume << 32) | ((ulong)(Stored - 1) << 44) | ((Zstd ? 1UL : 0UL) << 62);
        }

        public static Chunk Unpack(ulong w)
        {
            if (w >> 62 > 1) throw new InvalidDataException(Loc.T("Zstd.Core.012"));
            var c = new Chunk((uint)w, (int)((w >> 32) & 0xFFF), (int)(((w >> 44) & 0x3FFFF) + 1), w >> 62 == 1);
            c.Pack();
            return c;
        }
    }

    public sealed class Index
    {
        public byte[] ArchiveId = new byte[16];
        public byte Level = DefaultLevel;
        public List<FileRec> Files = [];
        public List<Chunk> Chunks = [];
        public List<long> VolumeSizes = [];
        public long Logical => Files.Sum(f => f.Size);
        public long Stored => VolumeSizes.Sum(s => s - VolumeHeaderSize);

        private static int Compare((ulong H, byte[] P) a, (ulong H, byte[] P) b)
            => a.H != b.H ? a.H.CompareTo(b.H) : a.P.AsSpan().SequenceCompareTo(b.P);

        public static void SortFiles<T>(List<T> files, Func<T, string> path)
        {
            var keys = files.ToDictionary(f => f!, f => { var p = Encoding.UTF8.GetBytes(path(f)); return (PathHash(p), p); });
            files.Sort((a, b) => Compare(keys[a!], keys[b!]));
        }

        public void Validate()
        {
            if (Level is < 1 or > 22 || VolumeSizes.Count > MaxVolumes || Chunks.Count == int.MaxValue) throw Bad(Loc.T("Zstd.Core.013"));
            long next = 0;
            (ulong H, byte[] P)? prev = null;
            foreach (var f in Files)
            {
                var pb = Encoding.UTF8.GetBytes(f.Path);
                if (!ValidPath(f.Path) || pb.Length > ushort.MaxValue || f.FirstChunk != next) throw Bad(Loc.T("Zstd.Core.014") + f.Path);
                var key = (PathHash(pb), pb);
                if (prev is { } p && Compare(p, key) >= 0) throw Bad(Loc.T("Zstd.Core.015"));
                prev = key;
                int count = f.ChunkCount;
                if (next + count > Chunks.Count) throw Bad(Loc.T("Zstd.Core.016") + f.Path);
                for (int n = 0; n < count; n++)
                {
                    var c = Chunks[(int)next + n];
                    c.Pack();
                    long logical = Math.Min(ChunkSize, f.Size - (long)n * ChunkSize);
                    if ((!c.Zstd && c.Stored != logical) || (c.Zstd && c.Stored >= logical) || (f.StoreOnly && c.Zstd)) throw Bad(Loc.T("Zstd.Core.017") + f.Path);
                }
                next += count;
            }
            if (next != Chunks.Count) throw Bad(Loc.T("Zstd.Core.018"));
            var set = Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var f in Files)
                for (int at = f.Path.IndexOf('/'); at > 0; at = f.Path.IndexOf('/', at + 1))
                    if (set.Contains(f.Path[..at])) throw Bad(Loc.F("Zstd.Core.019", f.Path[..at]));
            if (Chunks.Count == 0) { if (VolumeSizes.Count != 0) throw Bad(Loc.T("Zstd.Core.020")); return; }
            int vol = 0; long cursor = VolumeHeaderSize;
            foreach (var c in Chunks)
            {
                if (c.Volume != vol)
                {
                    if (c.Volume != vol + 1 || cursor == VolumeHeaderSize || VolumeSizes.Count <= vol || VolumeSizes[vol] != cursor) throw Bad(Loc.T("Zstd.Core.021"));
                    vol = c.Volume; cursor = VolumeHeaderSize;
                }
                if (c.Offset != cursor) throw Bad(Loc.T("Zstd.Core.022"));
                cursor += c.Stored;
                if (cursor > VolumeLimit) throw Bad(Loc.T("Zstd.Core.023"));
            }
            if (vol + 1 != VolumeSizes.Count || VolumeSizes[vol] != cursor) throw Bad(Loc.T("Zstd.Core.024"));
        }

        public byte[] Encode()
        {
            Validate();
            var names = Files.Select(f => Encoding.UTF8.GetBytes(f.Path)).ToList();
            long stringsLen = names.Sum(n => (long)n.Length);
            if (stringsLen > uint.MaxValue) throw Bad(Loc.T("Zstd.Core.025"));
            long filesOff = IndexHeaderSize, chunksOff = filesOff + Files.Count * 32L, volumesOff = chunksOff + Chunks.Count * 8L, stringsOff = volumesOff + VolumeSizes.Count * 8L;
            var b = new byte[stringsOff + stringsLen];
            IndexMagic.CopyTo(b);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), IndexHeaderSize);
            b[16] = ChunkShift; b[17] = Level;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), (uint)VolumeSizes.Count);
            ArchiveId.CopyTo(b, 24);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40), (ulong)Files.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(48), (ulong)Chunks.Count);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(56), filesOff);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(64), chunksOff);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(72), volumesOff);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(80), stringsOff);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(88), stringsLen);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(96), Logical);
            long pathOff = 0;
            for (int i = 0; i < Files.Count; i++)
            {
                var r = b.AsSpan((int)(filesOff + i * 32L), 32);
                BinaryPrimitives.WriteUInt64LittleEndian(r, PathHash(names[i]));
                BinaryPrimitives.WriteInt64LittleEndian(r[8..], Files[i].Size);
                BinaryPrimitives.WriteUInt32LittleEndian(r[16..], (uint)Files[i].FirstChunk);
                BinaryPrimitives.WriteUInt32LittleEndian(r[20..], (uint)pathOff);
                BinaryPrimitives.WriteUInt16LittleEndian(r[24..], (ushort)names[i].Length);
                BinaryPrimitives.WriteUInt16LittleEndian(r[26..], (ushort)(Files[i].StoreOnly ? 1 : 0));
                names[i].CopyTo(b, stringsOff + pathOff);
                pathOff += names[i].Length;
            }
            for (int i = 0; i < Chunks.Count; i++) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)(chunksOff + i * 8L)), Chunks[i].Pack());
            for (int i = 0; i < VolumeSizes.Count; i++) BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan((int)(volumesOff + i * 8L)), VolumeSizes[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(112), Crc32(b.AsSpan(IndexHeaderSize)));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(116), CrcZeroed(b, 112, 8, IndexHeaderSize));
            return b;
        }

        public static Index Decode(byte[] b)
        {
            if (b.Length < IndexHeaderSize || !b.AsSpan(0, 8).SequenceEqual(IndexMagic)) throw Bad(Loc.T("Zstd.Core.026"));
            uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
            ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
            if (U32(8) != 1 || U32(12) != IndexHeaderSize) throw Bad(Loc.T("Zstd.Core.027"));
            if (b[16] != ChunkShift) throw Bad(Loc.F("Zstd.Core.028", b[16]));
            if (b[17] is < 1 or > 22 || b[18] != 0 || b[19] != 0 || U64(104) != 0 || U64(120) != 0) throw Bad("header");
            if (U32(116) != CrcZeroed(b, 112, 8, IndexHeaderSize)) throw Bad("CRC header");
            ulong vc = U32(20), fc = U64(40), cc = U64(48);
            if (vc > MaxVolumes || cc >= uint.MaxValue || fc > int.MaxValue) throw Bad(Loc.T("Zstd.Core.029"));
            ulong filesOff = U64(56), chunksOff = U64(64), volumesOff = U64(72), stringsOff = U64(80), stringsLen = U64(88);
            if (filesOff != IndexHeaderSize || chunksOff != filesOff + fc * 32 || volumesOff != chunksOff + cc * 8 || stringsOff != volumesOff + vc * 8
                || stringsOff + stringsLen != (ulong)b.Length) throw Bad(Loc.T("Zstd.Core.030"));
            if (U32(112) != Crc32(b.AsSpan(IndexHeaderSize))) throw Bad(Loc.T("Zstd.Core.031"));
            var idx = new Index { Level = b[17] };
            b.AsSpan(24, 16).CopyTo(idx.ArchiveId);
            ulong pathOff = 0;
            for (int i = 0; i < (int)fc; i++)
            {
                int r = (int)filesOff + i * 32;
                ulong hash = U64(r); long size = (long)U64(r + 8); uint first = U32(r + 16); uint po = U32(r + 20);
                int len = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(r + 24)); int flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(r + 26));
                if (po != pathOff || (flags & ~1) != 0 || U32(r + 28) != 0 || size < 0 || pathOff + (ulong)len > stringsLen) throw Bad(Loc.T("Zstd.Core.014") + i);
                var pb = b.AsSpan((int)(stringsOff + po), len);
                if (PathHash(pb) != hash) throw Bad(Loc.T("Zstd.Core.032") + i);
                string path;
                try { path = new UTF8Encoding(false, true).GetString(pb); } catch (DecoderFallbackException) { throw Bad(Loc.T("Zstd.Core.033")); }
                idx.Files.Add(new FileRec(path, size, (int)first, (flags & 1) != 0));
                pathOff += (ulong)len;
            }
            if (pathOff != stringsLen) throw Bad(Loc.T("Zstd.Core.034"));
            for (int i = 0; i < (int)cc; i++) idx.Chunks.Add(Chunk.Unpack(U64((int)chunksOff + i * 8)));
            for (int i = 0; i < (int)vc; i++) idx.VolumeSizes.Add((long)U64((int)volumesOff + i * 8));
            if ((ulong)idx.Logical != U64(96)) throw Bad(Loc.T("Zstd.Core.035"));
            idx.Validate();
            return idx;
        }
    }

    private static InvalidDataException Bad(string what) => new(Loc.T("Zstd.Core.036") + what);

    private static byte[] VolumeHeader(int id, int count, byte[] archiveId, long payload)
    {
        var b = new byte[VolumeHeaderSize];
        VolumeMagic.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), VolumeHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)id);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), (uint)count);
        archiveId.CopyTo(b, 24);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40), payload);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(60), CrcZeroed(b, 60, 4, VolumeHeaderSize));
        return b;
    }

    private static void CheckVolumeHeader(ReadOnlySpan<byte> b, int id, int count, byte[] archiveId, long fileSize)
    {
        if (!b[..8].SequenceEqual(VolumeMagic) || BinaryPrimitives.ReadUInt32LittleEndian(b[8..]) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(b[12..]) != VolumeHeaderSize
            || b[48..60].IndexOfAnyExcept((byte)0) >= 0 || BinaryPrimitives.ReadUInt32LittleEndian(b[60..]) != CrcZeroed(b, 60, 4, VolumeHeaderSize))
            throw Bad($"header {VolumeName(id)}");
        if (BinaryPrimitives.ReadUInt32LittleEndian(b[16..]) != id || BinaryPrimitives.ReadUInt32LittleEndian(b[20..]) != count || !b.Slice(24, 16).SequenceEqual(archiveId)
            || BinaryPrimitives.ReadInt64LittleEndian(b[40..]) != fileSize - VolumeHeaderSize)
            throw Bad(Loc.F("Zstd.Core.037", VolumeName(id)));
    }

    // ───────────────────────────── khối zstd ─────────────────────────────

    private static Compressor NewCompressor(int level)
    {
        var c = new Compressor(level);
        c.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, ChunkShift);
        c.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        c.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 1);
        return c;
    }

    /// <summary>Nén một khối thành một frame vào <paramref name="dst"/>; trả 0 nếu frame không nhỏ hơn khối (để thô).</summary>
    private static int CompressChunk(Compressor c, ReadOnlySpan<byte> raw, Span<byte> dst)
    {
        if (raw.Length <= 1) return 0;
        if (!c.TryWrap(raw, dst[..(raw.Length - 1)], out int n)) return 0;
        return n > 0 && n < raw.Length ? n : 0;
    }

    /// <summary>Giải một khối: thô thì chép; zstd phải là đúng 1 frame có content size = cỡ khối, có checksum, không dictionary, cửa sổ ≤ 256 KiB.</summary>
    private static void DecodeChunk(Decompressor d, ReadOnlySpan<byte> stored, bool zstd, Span<byte> dst)
    {
        if (!zstd)
        {
            if (stored.Length != dst.Length) throw Bad(Loc.T("Zstd.Core.038"));
            stored.CopyTo(dst);
            return;
        }
        if (stored.Length < 6 || BinaryPrimitives.ReadUInt32LittleEndian(stored) != 0xFD2FB528u) throw Bad("frame ZSTD");
        byte fhd = stored[4];
        if ((fhd & 0x04) == 0 || (fhd & 0x03) != 0) throw Bad(Loc.T("Zstd.Core.039"));
        bool single = (fhd & 0x20) != 0;
        int at = 5;
        if (!single)
        {
            byte wd = stored[at++];
            long bas = 1L << (10 + (wd >> 3));
            if (bas + bas / 8 * (wd & 7) > ChunkSize) throw Bad(Loc.T("Zstd.Core.040"));
        }
        int fcsFlag = fhd >> 6;
        int fcsSize = fcsFlag switch { 0 => single ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        if (fcsSize == 0 || stored.Length < at + fcsSize) throw Bad(Loc.T("Zstd.Core.041"));
        long declared = fcsSize switch
        {
            1 => stored[at],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(stored[at..]) + 256,
            4 => BinaryPrimitives.ReadUInt32LittleEndian(stored[at..]),
            _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(stored[at..])
        };
        if (declared != dst.Length) throw Bad(Loc.T("Zstd.Core.042"));
        int n;
        try { n = d.Unwrap(stored, dst); }
        catch (Exception ex) when (ex is ZstdException or ArgumentException) { throw Bad("frame ZSTD / checksum: " + ex.Message); }
        if (n != dst.Length) throw Bad(Loc.T("Zstd.Core.043"));
    }

    // ───────────────────────────── đọc gói ─────────────────────────────

    public enum EntryKind { Loose, Packed }
    public sealed record Entry(string Path, long Size, EntryKind Kind);

    public sealed record Info(string Folder, string Title, string TitleId, string ContentVersion, Index? Index, IReadOnlyList<Entry> Loose,
        RuntimeKind Runtime, string RuntimeNote, string Problem)
    {
        /// <summary>ampr_emu.index lệch với file thật (game đã đóng ZSTD): thiếu file, sai cỡ, file đã mất. Rỗng = khớp.</summary>
        public IReadOnlyList<string> EmuIssues { get; init; } = [];
        public string EmuSummary { get; init; } = "";
        public bool Packed => Index != null;
        /// <summary>File rời thật (không tính ampr_zstd.index / ampr_zstd-NNN.pak).</summary>
        public long LooseBytes => Loose.Where(e => !IsPackName(e.Path)).Sum(e => e.Size);
        public int LooseCount => Loose.Count(e => !IsPackName(e.Path));
        public long PackedLogical => Index?.Logical ?? 0;
        public long PackedStored => Index?.Stored ?? 0;
        /// <summary>Dung lượng thật trên ổ: mọi file trong thư mục (đã gồm các volume .pak và index).</summary>
        public long DiskBytes => Loose.Sum(e => e.Size);
        public int ZstdChunks => Index?.Chunks.Count(c => c.Zstd) ?? 0;

        public IReadOnlyList<Entry> Entries()
        {
            var all = new List<Entry>(Loose.Where(e => !IsPackName(e.Path)));
            if (Index != null) all.AddRange(Index.Files.Select(f => new Entry(f.Path, f.Size, EntryKind.Packed)));
            all.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return all;
        }
    }

    /// <summary>Thư mục game PS5 (có eboot.bin hoặc sce_sys/param.json), đóng zstd hay chưa. Ném lỗi nếu không phải thư mục game.</summary>
    public static bool LooksLikeGame(string dir)
        => Directory.Exists(dir) && (File.Exists(Path.Combine(dir, "eboot.bin")) || File.Exists(Path.Combine(dir, "sce_sys", "param.json")) || File.Exists(Path.Combine(dir, IndexName)));

    private static List<(string Rel, string Full, long Size)> Walk(string root, List<string>? emptyDirs = null)
    {
        var list = new List<(string, string, long)>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        int baseLen = Path.GetFullPath(root).TrimEnd('\\', '/').Length + 1;
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            bool any = false;
            foreach (var e in d.EnumerateFileSystemInfos())
            {
                any = true;
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (e is DirectoryInfo sub) stack.Push(sub);
                else if (e is FileInfo f) list.Add((f.FullName[baseLen..].Replace('\\', '/'), f.FullName, f.Length));
            }
            if (!any && emptyDirs != null && d.FullName.Length > baseLen) emptyDirs.Add(d.FullName[baseLen..].Replace('\\', '/'));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    public static Info Read(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\', '/');
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var files = Walk(folder);
        var loose = files.Select(f => new Entry(f.Rel, f.Size, EntryKind.Loose)).ToList();
        Index? idx = null; string problem = "";
        var ip = Path.Combine(folder, IndexName);
        if (File.Exists(ip))
        {
            idx = Index.Decode(File.ReadAllBytes(ip));
            for (int v = 0; v < idx.VolumeSizes.Count; v++)
            {
                var vp = Path.Combine(folder, VolumeName(v));
                if (!File.Exists(vp)) { problem = Loc.T("Zstd.Core.044") + VolumeName(v); break; }
                long len = new FileInfo(vp).Length;
                if (len != idx.VolumeSizes[v]) { problem = Loc.F("Zstd.Core.045", VolumeName(v), len, idx.VolumeSizes[v]); break; }
                var head = new byte[VolumeHeaderSize];
                using (var fs = File.OpenRead(vp)) fs.ReadExactly(head);
                try { CheckVolumeHeader(head, v, idx.VolumeSizes.Count, idx.ArchiveId, len); } catch (InvalidDataException ex) { problem = ex.Message; break; }
            }
            var packedSet = idx.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var clash = loose.FirstOrDefault(l => packedSet.Contains(l.Path));
            if (clash != null && problem.Length == 0) problem = Loc.T("Zstd.Core.046") + clash.Path;
        }
        else if (File.Exists(Path.Combine(folder, "ampr_assets.index"))) problem = Loc.T("Zstd.Core.047");
        var (rk, rnote) = ClassifyRuntime(folder);
        var (title, tid, ver) = ReadParam(folder);
        var info = new Info(folder, title, tid, ver, idx, loose, rk, rnote, problem);
        if (idx == null) return info;
        var (issues, summary) = CheckEmuIndex(info);
        return info with { EmuIssues = issues, EmuSummary = summary };
    }

    // ───────────────────────────── kiểm / sửa ampr_emu.index ─────────────────────────────

    /// <summary>Đọc bảng file AMPRIDX3: (đường dẫn trong game không có /app0/, cỡ, mtime). Null nếu không đọc được.</summary>
    public static List<(string Path, long Size, long Mtime)>? ReadEmuIndex(string file)
    {
        try
        {
            var b = File.ReadAllBytes(file);
            if (b.Length < 0x30 || !b.AsSpan(0, 8).SequenceEqual("AMPRIDX3"u8)) return null;
            long recSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
            long n = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(0x10));
            long pathsLen = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(0x18));
            if (recSize < 24 || n < 0 || n > b.Length || pathsLen > b.Length || 0x30 + n * recSize + pathsLen > b.Length) return null;
            long pathsAt = 0x30 + n * recSize;
            var list = new List<(string, long, long)>((int)n);
            for (long i = 0; i < n; i++)
            {
                var r = b.AsSpan((int)(0x30 + i * recSize));
                long po = BinaryPrimitives.ReadUInt32LittleEndian(r), pl = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
                if (po + pl > pathsLen) return null;
                var p = Encoding.UTF8.GetString(b, (int)(pathsAt + po), (int)pl);
                if (p.StartsWith("/app0/", StringComparison.OrdinalIgnoreCase)) p = p[6..];
                list.Add((p, BinaryPrimitives.ReadInt64LittleEndian(r[8..]), BinaryPrimitives.ReadInt64LittleEndian(r[16..])));
            }
            return list;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool EmuIgnored(string rel)
        => IsPackName(rel) || rel.Equals(EmuIndexName, StringComparison.OrdinalIgnoreCase) || rel.Equals(JournalName, StringComparison.OrdinalIgnoreCase)
           || rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".vhnew", StringComparison.OrdinalIgnoreCase);

    /// <summary>So ampr_emu.index với file thật. Runtime trả stat() (cỡ file) theo index này, và file không có trong index thì game không thấy:
    /// sửa file rời sau khi đóng mà không ghi lại index (hay gặp ở bản tải về) làm game đọc cụt file hoặc không thấy file mới.</summary>
    public static (IReadOnlyList<string> Issues, string Summary) CheckEmuIndex(Info info)
    {
        var emuPath = Path.Combine(info.Folder, EmuIndexName);
        if (!File.Exists(emuPath)) return ([Loc.T("Zstd.Core.044") + EmuIndexName], Loc.T("Zstd.Core.044") + EmuIndexName);
        var emu = ReadEmuIndex(emuPath);
        if (emu == null) return ([EmuIndexName + Loc.T("Zstd.Core.048")], EmuIndexName + Loc.T("Zstd.Core.048"));
        var real = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in info.Loose) if (!EmuIgnored(e.Path)) real[e.Path] = e.Size;
        if (info.Index != null) foreach (var f in info.Index.Files) real[f.Path] = f.Size;
        var inEmu = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();
        int wrong = 0, missing = 0, gone = 0;
        foreach (var (p, size, _) in emu)
        {
            inEmu.Add(p);
            if (!real.TryGetValue(p, out var rs)) { if (!EmuIgnored(p)) { gone++; issues.Add(Loc.F("Zstd.Core.049", p)); } }
            else if (rs != size) { wrong++; issues.Add(Loc.F("Zstd.Core.050", p, size, rs)); }
        }
        foreach (var p in real.Keys.Where(p => !inEmu.Contains(p)).OrderBy(p => p, StringComparer.Ordinal)) { missing++; issues.Add(Loc.F("Zstd.Core.051", p)); }
        if (issues.Count == 0) return ([], "");
        var parts = new List<string>();
        if (wrong > 0) parts.Add(Loc.F("Zstd.Core.052", wrong));
        if (missing > 0) parts.Add(Loc.F("Zstd.Core.053", missing));
        if (gone > 0) parts.Add(Loc.F("Zstd.Core.054", gone));
        return (issues, EmuIndexName + Loc.T("Zstd.Core.055") + string.Join(", ", parts));
    }

    /// <summary>Ghi lại ampr_emu.index của game đã đóng ZSTD theo file hiện có (giữ mtime cũ). Bản cũ chép ra cạnh game:
    /// <c>&lt;game&gt;_ampr_emu.index.&lt;thời gian&gt;.bak</c>. Trả đường dẫn bản sao lưu ("" nếu không có bản cũ).</summary>
    public static string RepairEmuIndex(string game, Action<string>? log = null)
    {
        game = Path.GetFullPath(game).TrimEnd('\\', '/');
        var info = Read(game);
        if (info.Index == null) throw new InvalidDataException(Loc.T("Zstd.Core.056") + IndexName + ").");
        if (info.Problem.Length > 0) throw new InvalidDataException(info.Problem);
        var src = Path.Combine(game, EmuIndexName);
        long mtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string bak = "";
        if (File.Exists(src))
        {
            if (ReadEmuIndex(src) is { Count: > 0 } old) mtime = old[0].Mtime;
            bak = Path.Combine(Path.GetDirectoryName(game)!, Path.GetFileName(game) + "_" + EmuIndexName + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bak");
            File.Copy(src, bak);
        }
        foreach (var i in info.EmuIssues.Take(20)) log?.Invoke("  " + i);
        if (info.EmuIssues.Count > 20) log?.Invoke(Loc.F("Zstd.Core.057", info.EmuIssues.Count - 20));
        WriteEmuIndex(game, info.Index, mtime);
        var after = Read(game);
        if (after.EmuIssues.Count > 0) throw new InvalidDataException(Loc.T("Zstd.Core.058") + after.EmuSummary);
        log?.Invoke(Loc.F("Zstd.Core.059", EmuIndexName, ReadEmuIndex(src)!.Count) + (bak.Length > 0 ? Loc.F("Zstd.Core.060", bak) : "") + ".");
        return bak;
    }


    private static (string Title, string TitleId, string Version) ReadParam(string folder)
    {
        try
        {
            var p = Path.Combine(folder, "sce_sys", "param.json");
            if (!File.Exists(p)) return ("", "", "");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(p));
            var r = doc.RootElement;
            string tid = r.TryGetProperty("titleId", out var t) ? t.GetString() ?? "" : "";
            string ver = r.TryGetProperty("contentVersion", out var cv) ? cv.GetString() ?? "" : "";
            string title = "";
            if (r.TryGetProperty("localizedParameters", out var lp))
            {
                string lang = lp.TryGetProperty("defaultLanguage", out var dl) ? dl.GetString() ?? "en-US" : "en-US";
                foreach (var l in new[] { lang, "en-US", "en-GB" })
                    if (lp.TryGetProperty(l, out var o) && o.TryGetProperty("titleName", out var tn) && tn.GetString() is { Length: > 0 } s) { title = s; break; }
            }
            return (title, tid, ver);
        }
        catch { return ("", "", ""); }
    }

    // ───────────────────────────── đóng gói ─────────────────────────────

    public sealed record PackOptions(int Level = DefaultLevel, int Threads = 0, bool Verify = false);
    public sealed record PackResult(string Folder, int PackedFiles, int LooseFiles, int Volumes, long Logical, long Stored, int Chunks, int ZstdChunks, TimeSpan Elapsed);

    private sealed class Slot
    {
        public readonly byte[] Raw = new byte[ChunkSize];
        public readonly byte[] Out = new byte[ChunkSize];
        public int File, Len, OutLen;
    }

    /// <summary>
    /// Đọc trước nguyên file nhỏ (≤ 8 MiB) theo thứ tự, nhiều file cùng lúc (tối đa 16 đọc song song, 256 MiB đang giữ),
    /// bộ đệm thuê từ ArrayPool. File lớn trả null: đọc tuần tự bằng FileStream như cũ.
    /// </summary>
    private sealed class Prefetcher : IDisposable
    {
        private const int SmallMax = 8 << 20, Window = 256, MaxInflightMiB = 256;
        private readonly List<(string Path, long Size)> files;
        private readonly CancellationToken ct;
        private readonly Task<byte[]>?[] tasks;
        private readonly SemaphoreSlim io = new(16, 16);
        private int scheduled, consumedUpTo;
        private long inflight;

        public Prefetcher(List<(string Path, long Size)> files, CancellationToken ct)
        {
            this.files = files; this.ct = ct;
            tasks = new Task<byte[]>?[files.Count];
            Schedule();
        }

        private void Schedule()
        {
            while (scheduled < files.Count && scheduled - consumedUpTo < Window && inflight < (long)MaxInflightMiB << 20)
            {
                int i = scheduled++;
                var (path, size) = files[i];
                if (size > SmallMax) continue;
                Interlocked.Add(ref inflight, size);
                tasks[i] = Task.Run(async () =>
                {
                    await io.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Max(1, size));
                        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
                        if (RandomAccess.GetLength(h) != size) throw new IOException(Loc.F("Zstd.Core.061", path));
                        int done = 0;
                        while (done < size)
                        {
                            int n = RandomAccess.Read(h, buf.AsSpan(done, (int)size - done), done);
                            if (n <= 0) throw new IOException(Loc.F("Zstd.Core.062", path));
                            done += n;
                        }
                        return buf;
                    }
                    finally { io.Release(); }
                }, ct);
            }
        }

        /// <summary>Nội dung file i (bộ đệm thuê, dài ≥ cỡ file) hoặc null nếu là file lớn.</summary>
        public byte[]? Take(int i, string rel)
        {
            consumedUpTo = i;
            Schedule();
            var t = tasks[i];
            if (t == null) return null;
            tasks[i] = null;
            byte[] b;
            try { b = t.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is not OperationCanceledException and not IOException) { throw new IOException(rel + ": " + ex.Message, ex); }
            Interlocked.Add(ref inflight, -files[i].Size);
            return b;
        }

        public void Release(byte[] b) { System.Buffers.ArrayPool<byte>.Shared.Return(b); Schedule(); }

        public void Dispose()
        {
            foreach (var t in tasks)
                if (t != null) try { System.Buffers.ArrayPool<byte>.Shared.Return(t.GetAwaiter().GetResult()); } catch { }
            io.Dispose();
        }
    }

    private static int ThreadCount(int t) => Math.Clamp(t > 0 ? t : Environment.ProcessorCount, 1, MaxThreads);

    /// <summary>
    /// Đóng thư mục game <paramref name="gameDir"/> thành bản zstd ở <paramref name="outDir"/> (thư mục mới hoặc rỗng; nguồn không bị sửa):
    /// file rời được chép, mọi file còn lại vào ampr_zstd-NNN.pak, cài ampr-emu-ng vào fakelib/libSceAmpr.sprx, ghi ampr_emu.index mới.
    /// </summary>
    public static PackResult Pack(string gameDir, string outDir, PackOptions opt, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        gameDir = Path.GetFullPath(gameDir).TrimEnd('\\', '/');
        outDir = Path.GetFullPath(outDir).TrimEnd('\\', '/');
        if (opt.Level is < 1 or > 22) throw new ArgumentException(Loc.T("Zstd.Core.063"));
        if (!Directory.Exists(gameDir)) throw new DirectoryNotFoundException(gameDir);
        if (string.Equals(gameDir, outDir, StringComparison.OrdinalIgnoreCase) || outDir.StartsWith(gameDir + "\\", StringComparison.OrdinalIgnoreCase) || gameDir.StartsWith(outDir + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.T("Zstd.Core.064"));
        if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any()) throw new IOException(Loc.T("Zstd.Core.065") + outDir);

        var empty = new List<string>();
        var all = Walk(gameDir, empty);
        var rootNames = all.Select(f => f.Rel.Split('/')[0]).Concat(empty.Select(e => e.Split('/')[0])).Distinct().ToList();
        if (rootNames.Any(IsPackName)) throw new IOException(Loc.T("Zstd.Core.066"));
        if (all.Any(f => f.Rel.Equals("ampr_assets.index", StringComparison.OrdinalIgnoreCase)))
            throw new IOException(Loc.T("Zstd.Core.067"));
        if (!all.Any(f => f.Rel.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase))) log?.Invoke(Loc.T("Zstd.Core.068"));
        var bad = all.FirstOrDefault(f => !ValidPath(f.Rel) || Encoding.UTF8.GetByteCount(f.Rel) > ushort.MaxValue);
        if (bad.Rel != null) throw new IOException(Loc.T("Zstd.Core.069") + bad.Rel);

        // Danh sách file của game đầu ra: bỏ nhật ký / bảng cũ / runtime cũ, thêm runtime ampr-emu-ng.
        var files = new List<(string Rel, string? Full, long Size)>();
        foreach (var f in all)
        {
            if (f.Rel.Equals(JournalName, StringComparison.OrdinalIgnoreCase) || LogNames.Any(l => l.Equals(f.Rel, StringComparison.OrdinalIgnoreCase))) { log?.Invoke(Loc.T("Zstd.Core.070") + f.Rel); continue; }
            if (f.Rel.Equals(EmuIndexName, StringComparison.OrdinalIgnoreCase) || f.Rel.Equals(RuntimePath, StringComparison.OrdinalIgnoreCase)) continue;
            files.Add((f.Rel, f.Full, f.Size));
        }
        var (rk, rnote) = ClassifyRuntime(gameDir);
        log?.Invoke(rk switch
        {
            RuntimeKind.None => Loc.F("Zstd.Core.071", NgVersion, RuntimePath),
            RuntimeKind.Ng => Loc.F("Zstd.Core.072", NgVersion, rnote),
            _ => Loc.F("Zstd.Core.073", rnote, NgVersion)
        });
        var runtime = NgRuntime;
        files.Add((RuntimePath, null, runtime.Length));
        long mtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var emu = BuildEmuIndex(files.Select(f => (f.Rel, f.Size)), mtime);

        var packed = files.Where(f => f.Size > 0 && f.Full != null && !AlwaysLoose(f.Rel)).ToList();
        var loose = files.Where(f => !(f.Size > 0 && f.Full != null && !AlwaysLoose(f.Rel))).ToList();
        Index.SortFiles(packed, f => f.Rel);
        long packedBytes = packed.Sum(f => f.Size), looseBytes = loose.Where(f => f.Full != null).Sum(f => f.Size);
        int threads = ThreadCount(opt.Threads);
        log?.Invoke(Loc.F("Zstd.Core.074", packed.Count, FormatSize(packedBytes), loose.Count, FormatSize(looseBytes), opt.Level, threads));

        long maxVolumes = Math.Max(1, (packedBytes + (VolumeLimit - VolumeHeaderSize - ChunkSize) - 1) / (VolumeLimit - VolumeHeaderSize - ChunkSize));
        if (maxVolumes > MaxVolumes) throw new IOException(Loc.T("Zstd.Core.075"));
        Directory.CreateDirectory(outDir);
        var drive = new DriveInfo(Path.GetPathRoot(outDir)!);
        long need = looseBytes + packedBytes + maxVolumes * VolumeHeaderSize + emu.Length + runtime.Length + (64L << 20);
        if (drive.AvailableFreeSpace < need) throw new IOException(Loc.F("Zstd.Core.076", drive.Name, FormatSize(drive.AvailableFreeSpace), FormatSize(need)));

        long work = looseBytes + packedBytes * (opt.Verify ? 2 : 1), done = 0, packedIn = 0, packedOut = 0;
        var last = Stopwatch.StartNew();
        void Report(string detail, bool force = false)
        {
            if (!force && last.ElapsedMilliseconds < 200) return;
            last.Restart();
            progress?.Report(new Progress(Math.Min(100, done * 100.0 / Math.Max(1, work)), done / Math.Max(0.001, sw.Elapsed.TotalSeconds), detail, done, work, packedIn, packedOut));
        }

        // 1. File rời + thư mục rỗng + runtime + ampr_emu.index (chép nhiều file cùng lúc).
        object reportGate = new();
        try
        {
            Parallel.ForEach(loose, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, () => new byte[1 << 20], (f, _, buf) =>
            {
                var dst = Path.Combine(outDir, f.Rel.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (f.Full == null) { File.WriteAllBytes(dst, runtime); return buf; }
                CopyFile(f.Full, dst, buf, n => { Interlocked.Add(ref done, n); lock (reportGate) Report(Loc.T("Zstd.Core.077") + f.Rel); }, ct);
                return buf;
            }, _ => { });
        }
        catch (AggregateException ae) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.Flatten().InnerExceptions[0]).Throw(); }
        foreach (var e in empty) Directory.CreateDirectory(Path.Combine(outDir, e.Replace('/', '\\')));
        File.WriteAllBytes(Path.Combine(outDir, EmuIndexName), emu);

        // 2. Gói: đọc tuần tự, nén song song theo lô, ghi theo đúng thứ tự.
        var idx = new Index { Level = (byte)opt.Level };
        RandomNumberGenerator.Fill(idx.ArchiveId);
        var hashes = new byte[packed.Count][];
        int batch = threads * 2;
        // 3 bộ khe xoay vòng: đọc bộ k+1 trong lúc nén bộ k và ghi bộ k-1 (đọc / nén / ghi chồng lấp nhau).
        var sets = Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, batch).Select(_ => new Slot()).ToArray()).ToArray();
        // Đọc trước file nhỏ song song (đóng game nhiều file nhỏ bị nghẽn ở độ trễ mở / đọc từng file, không phải ở SSD).
        using var prefetch = new Prefetcher(packed.Select(f => (f.Full!, f.Size)).ToList(), ct);
        using var compressors = new ThreadLocal<Compressor>(() => NewCompressor(opt.Level), true);
        var po = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct };
        FileStream? vol = null;
        long cursor = 0;
        int fileNo = 0; FileStream? src = null; IncrementalHash? sha = null; long srcLeft = 0;
        byte[]? mem = null; int memPos = 0;
        try
        {
            int Fill(Slot[] set)
            {
                int n = 0;
                while (n < set.Length)
                {
                    if (src == null && mem == null)
                    {
                        if (fileNo >= packed.Count) break;
                        var f = packed[fileNo];
                        idx.Files.Add(new FileRec(f.Rel, f.Size, 0, false));   // FirstChunk điền lúc ghi
                        mem = prefetch.Take(fileNo, f.Rel); memPos = 0;
                        if (mem == null)
                        {
                            src = new FileStream(f.Full!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                            if (src.Length != f.Size) throw new IOException(Loc.F("Zstd.Core.061", f.Rel));
                        }
                        sha = opt.Verify ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                        srcLeft = f.Size;
                    }
                    var s = set[n];
                    int want = (int)Math.Min(ChunkSize, srcLeft);
                    if (mem != null) { Buffer.BlockCopy(mem, memPos, s.Raw, 0, want); memPos += want; }
                    else src!.ReadExactly(s.Raw, 0, want);
                    sha?.AppendData(s.Raw, 0, want);
                    s.File = fileNo; s.Len = want; s.OutLen = 0;
                    srcLeft -= want; n++;
                    if (srcLeft == 0)
                    {
                        if (mem != null) { prefetch.Release(mem); mem = null; }
                        else { if (src!.ReadByte() >= 0) throw new IOException(Loc.F("Zstd.Core.078", packed[fileNo].Rel)); src.Dispose(); src = null; }
                        if (sha != null) { hashes[fileNo] = sha.GetHashAndReset(); sha.Dispose(); sha = null; }
                        fileNo++;
                    }
                }
                return n;
            }
            void Compress(Slot[] set, int n) => Parallel.For(0, n, po, i =>
            {
                var s = set[i];
                s.OutLen = CompressChunk(compressors.Value!, s.Raw.AsSpan(0, s.Len), s.Out);
            });
            var firstChunk = new int[packed.Count];
            for (int i = 0; i < firstChunk.Length; i++) firstChunk[i] = -1;
            void Write(Slot[] set, int n)
            {
                for (int i = 0; i < n; i++)
                {
                    var s = set[i];
                    bool z = s.OutLen > 0;
                    int stored = z ? s.OutLen : s.Len;
                    if (vol == null || cursor + stored > VolumeLimit)
                    {
                        if (vol != null) { vol.Flush(); idx.VolumeSizes.Add(cursor); vol.Dispose(); }
                        int id = idx.VolumeSizes.Count;
                        if (id >= MaxVolumes) throw new IOException(Loc.T("Zstd.Core.079"));
                        vol = new FileStream(Path.Combine(outDir, VolumeName(id)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4 << 20);
                        vol.Write(new byte[VolumeHeaderSize]);
                        cursor = VolumeHeaderSize;
                    }
                    if (firstChunk[s.File] < 0) firstChunk[s.File] = idx.Chunks.Count;
                    idx.Chunks.Add(new Chunk((uint)cursor, idx.VolumeSizes.Count, stored, z));
                    vol.Write(z ? s.Out : s.Raw, 0, stored);
                    cursor += stored;
                    done += s.Len; packedIn += s.Len; packedOut += stored;
                }
                Report(Loc.F("Zstd.Core.080", packed[Math.Min(set[n - 1].File, packed.Count - 1)].Rel));
            }

            int cur = 0;
            int count = Fill(sets[cur]);
            Task? writing = null;
            try
            {
                while (count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var compressing = sets[cur]; int cn = count;
                    var task = Task.Run(() => Compress(compressing, cn), ct);
                    int next = Fill(sets[(cur + 1) % 3]);         // bộ này đã ghi xong (đợi ở vòng trước)
                    task.GetAwaiter().GetResult();
                    writing?.GetAwaiter().GetResult();             // ghi theo đúng thứ tự
                    writing = Task.Run(() => Write(compressing, cn));
                    cur = (cur + 1) % 3;
                    count = next;
                }
                writing?.GetAwaiter().GetResult();
            }
            catch { try { writing?.Wait(); } catch { } throw; }
            if (vol != null) { vol.Flush(); idx.VolumeSizes.Add(cursor); vol.Dispose(); vol = null; }
            for (int i = 0; i < idx.Files.Count; i++)
                idx.Files[i] = idx.Files[i] with { FirstChunk = firstChunk[i] >= 0 ? firstChunk[i] : (i == 0 ? 0 : idx.Files[i - 1].FirstChunk + idx.Files[i - 1].ChunkCount) };
        }
        finally
        {
            src?.Dispose(); sha?.Dispose(); vol?.Dispose();
            if (mem != null) prefetch.Release(mem);
            foreach (var c in compressors.Values) c.Dispose();
        }

        // 3. Header volume (biết số volume mới ghi được) rồi index.
        for (int v = 0; v < idx.VolumeSizes.Count; v++)
        {
            using var fs = new FileStream(Path.Combine(outDir, VolumeName(v)), FileMode.Open, FileAccess.Write, FileShare.None);
            fs.Write(VolumeHeader(v, idx.VolumeSizes.Count, idx.ArchiveId, idx.VolumeSizes[v] - VolumeHeaderSize));
        }
        File.WriteAllBytes(Path.Combine(outDir, IndexName), idx.Encode());
        int zc = idx.Chunks.Count(c => c.Zstd);
        log?.Invoke(Loc.F("Zstd.Core.081", idx.VolumeSizes.Count, idx.Chunks.Count, zc, FormatSize(packedBytes), FormatSize(idx.Stored), 100.0 * idx.Stored / Math.Max(1, packedBytes)));

        // 4. Kiểm tra: đọc lại index + header volume như runtime, giải từng file, so SHA-256 với file nguồn.
        if (opt.Verify)
        {
            var info = Read(outDir);
            if (info.Problem.Length > 0) throw new InvalidDataException(Loc.T("Zstd.Core.082") + info.Problem);
            var byPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int i = 0; i < packed.Count; i++) byPath[packed[i].Rel] = hashes[i];
            using var reader = new PackReader(info, threads);
            foreach (var f in info.Index!.Files)
            {
                ct.ThrowIfCancellationRequested();
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                reader.Decode(f, (data) => h.AppendData(data.Span), n => { done += n; Report(Loc.T("Zstd.Core.083") + f.Path); }, ct);
                if (!h.GetHashAndReset().AsSpan().SequenceEqual(byPath[f.Path])) throw new InvalidDataException(Loc.T("Zstd.Core.084") + f.Path);
            }
            log?.Invoke(Loc.F("Zstd.Core.085", idx.VolumeSizes.Count, info.Index.Files.Count));
        }
        done = work; Report(Loc.T("Zstd.Core.Done"), true);
        return new PackResult(outDir, packed.Count, loose.Count, idx.VolumeSizes.Count, packedBytes, idx.Stored, idx.Chunks.Count, zc, sw.Elapsed);
    }

    private static void CopyFile(string from, string to, byte[] buf, Action<int> advanced, CancellationToken ct)
    {
        using (var i = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
        using (var o = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
        {
            if (i.Length > (8 << 20)) o.SetLength(i.Length);
            int n;
            while ((n = i.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); o.Write(buf, 0, n); advanced(n); }
        }
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }

    // ───────────────────────────── giải gói ─────────────────────────────

    /// <summary>Đọc khối của file trong gói, giải song song theo lô, trả dữ liệu theo đúng thứ tự.</summary>
    private sealed class PackReader : IDisposable
    {
        private readonly Info info;
        private readonly SafeFileHandle?[] handles;
        private readonly int threads;
        private readonly ThreadLocal<Decompressor> decompressors = new(() => new Decompressor(), true);
        private readonly int batch;
        private byte[][]? stored;
        private byte[][][]? plain;

        public PackReader(Info info, int threads)
        {
            if (info.Index == null) throw new InvalidOperationException(Loc.T("Zstd.Core.086"));
            if (info.Problem.Length > 0) throw new InvalidDataException(info.Problem);
            this.info = info; this.threads = threads;
            handles = new SafeFileHandle?[info.Index.VolumeSizes.Count];
            batch = Math.Clamp(threads * 4, 32, 128);              // 8–32 MB mỗi lô
        }

        private SafeFileHandle Handle(int v)
        {
            lock (handles) return handles[v] ??= File.OpenHandle(Path.Combine(info.Folder, VolumeName(v)), FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        /// <summary>Giải một file lớn: lô khối giải song song, và lô vừa giải được đưa cho <paramref name="sink"/> (theo đúng thứ tự)
        /// trên một tác vụ riêng trong khi lô sau đang giải — ghi ra ổ và giải nén chạy chồng nhau thay vì chờ nhau.</summary>
        public void Decode(FileRec f, Action<ReadOnlyMemory<byte>> sink, Action<int> advanced, CancellationToken ct)
        {
            stored ??= Enumerable.Range(0, batch).Select(_ => new byte[ChunkSize]).ToArray();
            plain ??= [Enumerable.Range(0, batch).Select(_ => new byte[ChunkSize]).ToArray(), Enumerable.Range(0, batch).Select(_ => new byte[ChunkSize]).ToArray()];
            var po = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct };
            int count = f.ChunkCount, set = 0;
            Task? writing = null;
            try
            {
                for (int at = 0; at < count; at += batch)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(batch, count - at), first = at;
                    var outSet = plain[set];
                    Parallel.For(0, n, po, i =>
                    {
                        int k = first + i;
                        var c = info.Index!.Chunks[f.FirstChunk + k];
                        int logical = (int)Math.Min(ChunkSize, f.Size - (long)k * ChunkSize);
                        var buf = stored[i];
                        int got = RandomAccess.Read(Handle(c.Volume), buf.AsSpan(0, c.Stored), c.Offset);
                        if (got != c.Stored) throw Bad(Loc.F("Zstd.Core.087", VolumeName(c.Volume)));
                        DecodeChunk(decompressors.Value!, buf.AsSpan(0, c.Stored), c.Zstd, outSet[i].AsSpan(0, logical));
                    });
                    writing?.GetAwaiter().GetResult();             // lô trước đã ghi xong (và báo lỗi ghi nếu có)
                    writing = Task.Run(() =>
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int logical = (int)Math.Min(ChunkSize, f.Size - (long)(first + i) * ChunkSize);
                            sink(outSet[i].AsMemory(0, logical));
                            advanced(logical);
                        }
                    });
                    set ^= 1;
                }
                writing?.GetAwaiter().GetResult();
                writing = null;
            }
            finally
            {
                try { writing?.Wait(); } catch { }                 // lỗi giữa chừng: không trả bộ đệm khi tác vụ ghi còn dùng
            }
        }

        /// <summary>Giải tuần tự một file vào <paramref name="o"/> — gọi song song được từ nhiều luồng (mỗi luồng một file).</summary>
        public void DecodeTo(FileRec f, Stream o, Action<int> advanced, CancellationToken ct)
        {
            var st = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);
            var pl = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                var d = decompressors.Value!;
                for (int k = 0; k < f.ChunkCount; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var c = info.Index!.Chunks[f.FirstChunk + k];
                    int logical = (int)Math.Min(ChunkSize, f.Size - (long)k * ChunkSize);
                    int got = RandomAccess.Read(Handle(c.Volume), st.AsSpan(0, c.Stored), c.Offset);
                    if (got != c.Stored) throw Bad(Loc.F("Zstd.Core.087", VolumeName(c.Volume)));
                    DecodeChunk(d, st.AsSpan(0, c.Stored), c.Zstd, pl.AsSpan(0, logical));
                    o.Write(pl, 0, logical);
                    advanced(logical);
                }
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(st); System.Buffers.ArrayPool<byte>.Shared.Return(pl); }
        }

        public void Dispose()
        {
            foreach (var h in handles) h?.Dispose();
            foreach (var d in decompressors.Values) d.Dispose();
            decompressors.Dispose();
        }
    }

    public sealed record UnpackOptions(bool ReplaceRuntime = true, int Threads = 0);
    public sealed record UnpackResult(string Folder, int Files, long Bytes, TimeSpan Elapsed);

    /// <summary>
    /// Giải bản zstd ra <paramref name="outDir"/> (thư mục mới hoặc rỗng). <paramref name="only"/> = null: game đầy đủ như chưa đóng gói
    /// (bỏ ampr_zstd.*, runtime thay bằng ampr_emu 0.4.2.1 gốc nếu chọn, ampr_emu.index ghi mới). Có danh sách: chỉ xả đúng các file đó
    /// (rời hoặc trong gói), giữ đường dẫn, không đụng runtime / bảng.
    /// </summary>
    public static UnpackResult Unpack(Info info, string outDir, IReadOnlySet<string>? only, UnpackOptions opt, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        if (info.Index == null) throw new InvalidOperationException(Loc.T("Zstd.Core.088"));
        if (info.Problem.Length > 0) throw new InvalidDataException(info.Problem);
        outDir = Path.GetFullPath(outDir).TrimEnd('\\', '/');
        if (string.Equals(outDir, info.Folder, StringComparison.OrdinalIgnoreCase) || outDir.StartsWith(info.Folder + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.T("Zstd.Core.089"));
        if (only == null && Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any()) throw new IOException(Loc.T("Zstd.Core.065") + outDir);
        bool full = only == null;
        bool Want(string p) => only == null || only.Contains(p);

        var loose = info.Loose.Where(e => !IsPackName(e.Path) && Want(e.Path)).ToList();
        if (full)
            loose = loose.Where(e =>
            {
                if (e.Path.Equals(JournalName, StringComparison.OrdinalIgnoreCase) || LogNames.Any(l => l.Equals(e.Path, StringComparison.OrdinalIgnoreCase))) { log?.Invoke(Loc.T("Zstd.Core.070") + e.Path); return false; }
                if (e.Path.Equals(EmuIndexName, StringComparison.OrdinalIgnoreCase)) return false;
                if (opt.ReplaceRuntime && e.Path.Equals(RuntimePath, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToList();
        var packed = info.Index.Files.Where(f => Want(f.Path)).ToList();
        long total = loose.Sum(e => e.Size) + packed.Sum(f => f.Size), done = 0;
        var drive = new DriveInfo(Path.GetPathRoot(outDir)!);
        Directory.CreateDirectory(outDir);
        if (drive.AvailableFreeSpace < total + (64L << 20)) throw new IOException(Loc.F("Zstd.Core.090", drive.Name, FormatSize(drive.AvailableFreeSpace), FormatSize(total)));
        var last = Stopwatch.StartNew();
        void Report(string d, bool force = false)
        {
            if (!force && last.ElapsedMilliseconds < 200) return;
            lock (last)
            {
                if (!force && last.ElapsedMilliseconds < 200) return;
                last.Restart();
            }
            long now = Interlocked.Read(ref done);
            progress?.Report(new Progress(Math.Min(100, now * 100.0 / Math.Max(1, total)), now / Math.Max(0.001, sw.Elapsed.TotalSeconds), d, now, total));
        }
        string Dest(string rel) { var d = Path.Combine(outDir, rel.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(d)!); return d; }

        int count = 0;
        int threadsN = ThreadCount(opt.Threads);
        var filesPo = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(threadsN, 16), CancellationToken = ct };
        try
        {
            Parallel.ForEach(loose, filesPo, () => new byte[1 << 20], (e, _, buf) =>
            {
                var dst = Dest(e.Path);
                if (!full && File.Exists(dst)) File.Delete(dst);
                CopyFile(Path.Combine(info.Folder, e.Path.Replace('/', '\\')), dst, buf, n => { Interlocked.Add(ref done, n); Report(Loc.T("Zstd.Core.077") + e.Path); }, ct);
                Interlocked.Increment(ref count);
                return buf;
            }, _ => { });
        }
        catch (AggregateException ae) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.Flatten().InnerExceptions[0]).Throw(); }
        using (var reader = new PackReader(info, threadsN))
        {
            // File lớn: giải song song theo khối (một file một lúc). File nhỏ: nhiều file cùng lúc, mỗi file một luồng.
            const int BigChunks = 16;
            foreach (var f in packed.Where(f => f.ChunkCount > BigChunks))
            {
                ct.ThrowIfCancellationRequested();
                using var o = new FileStream(Dest(f.Path), full ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                o.SetLength(f.Size);
                reader.Decode(f, data => o.Write(data.Span), n => { Interlocked.Add(ref done, n); Report(Loc.T("Zstd.Core.091") + f.Path); }, ct);
                Interlocked.Increment(ref count);
            }
            try
            {
                Parallel.ForEach(packed.Where(f => f.ChunkCount <= BigChunks), filesPo, f =>
                {
                    using var o = new FileStream(Dest(f.Path), full ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    reader.DecodeTo(f, o, n => { Interlocked.Add(ref done, n); Report(Loc.T("Zstd.Core.091") + f.Path); }, ct);
                    Interlocked.Increment(ref count);
                });
            }
            catch (AggregateException ae) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.Flatten().InnerExceptions[0]).Throw(); }
        }
        if (full)
        {
            if (opt.ReplaceRuntime)
            {
                File.WriteAllBytes(Dest(RuntimePath), ReleaseRuntime);
                log?.Invoke(Loc.F("Zstd.Core.092", RuntimePath, ReleaseVersion));
            }
            // Thư mục rỗng của bản đóng gói vẫn giữ.
            foreach (var d in Directory.EnumerateDirectories(info.Folder, "*", SearchOption.AllDirectories))
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.CreateDirectory(Path.Combine(outDir, Path.GetRelativePath(info.Folder, d)));
            var written = Walk(outDir).Select(f => (f.Rel, f.Size)).Where(f => !f.Rel.Equals(EmuIndexName, StringComparison.OrdinalIgnoreCase)).ToList();
            File.WriteAllBytes(Path.Combine(outDir, EmuIndexName), BuildEmuIndex(written, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            log?.Invoke(Loc.F("Zstd.Core.093", EmuIndexName, written.Count));
        }
        done = total; Report(Loc.T("Zstd.Core.Done"), true);
        return new UnpackResult(outDir, count, total, sw.Elapsed);
    }

    public static string FormatSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0.0} KB",
        _ => $"{b} B"
    };
}
