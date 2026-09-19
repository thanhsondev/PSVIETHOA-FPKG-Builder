using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Tóm tắt những gì bước chuyển đổi đã sửa.</summary>
public sealed record SonySdkConversionReport(int ClearedEncryptionFlags, long OuterBlocks, int CntHeaderWrapBytes, int PlayGoCrcBlocks);

/// <summary>
/// Chuyển gói thô của Publishing Tools 2.79 đã vá (lớp PFS ngoài và các mục CNT được bảo vệ để nguyên không mã hoá) sang
/// profile chẩn đoán PLAINTEXT_NOAUTH mà LibProsperoPkg đọc và PS5 (kstuff) cài được — bản chuyển thể từng bước của
/// <c>scripts/postprocess-sdk279-plaintext.py</c> trong bộ công cụ sdk-fpkg729-fix:
/// gắn dấu <c>PPRPLAIN-NOAUTH!</c> vào superblock, bỏ cờ mã hoá giả của các mục CNT, niêm phong lại các hash không-RSA bị
/// ảnh hưởng (imagedigs, FIH, bảng digest, rollup, header), dựng lại lớp bọc RSA-3072 tất định của header CNT và sửa bảng
/// CRC32C PlayGo trong kho SI. Bản Python sao chép sang tệp mới rồi sửa; ở đây sửa tại chỗ trên tệp đầu ra (cùng kết quả,
/// không phải ghi gói hai lần).
/// </summary>
public static class SonySdkConverter
{
    public const int BlockSize = 0x10000;

    public static readonly byte[] PlaintextSeed = "PPRPLAIN-NOAUTH!"u8.ToArray();

    private static readonly byte[] FihMagic = [0x7F, (byte)'F', (byte)'I', (byte)'H'];
    private static readonly byte[] CntMagic = [0x7F, (byte)'C', (byte)'N', (byte)'T'];

    private const int EntryMetaSize = 0x20;
    private const uint EntryFlagEncrypted = 0x80000000;
    private const uint ImageDigestsEntryId = 0x040A;
    private const uint DigestTableEntryId = 0x0001;
    private const uint EntryMetasEntryId = 0x0100;
    private const int CntHeaderWrapBytes = 384;

    private readonly record struct Entry(uint Id, uint Flags, uint Offset, uint Size);

    /// <summary>
    /// Gói đã ở dạng PLAINTEXT_NOAUTH (dấu <c>PPRPLAIN-NOAUTH!</c> ở superblock ngoài @0x370)? Bộ công cụ fix6 (profile
    /// sdk279-plaintext-direct-v3) ghi thẳng dạng này trong img_create nên không còn bước chuyển đổi; bộ cũ thì chưa có dấu.
    /// </summary>
    public static bool IsAlreadyPlaintext(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fih = ReadExact(stream, 0, BlockSize);
            if (!fih.AsSpan(0, 4).SequenceEqual(FihMagic))
            {
                return false;
            }

            var superblockOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x20));
            if (superblockOffset <= 0 || superblockOffset + BlockSize > stream.Length)
            {
                return false;
            }

            var superblock = ReadExact(stream, superblockOffset, 0x380);
            return superblock.AsSpan(0x370, PlaintextSeed.Length).SequenceEqual(PlaintextSeed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Chuyển đổi tại chỗ. <paramref name="progress"/> nhận phần trăm 0..100 của bước này.</summary>
    public static SonySdkConversionReport ConvertInPlace(string path, Action<double, string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Invoke(5, "Reading FIH, outer-PFS and CNT metadata");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.RandomAccess);
        var fileSize = stream.Length;
        var fih = ReadExact(stream, 0, BlockSize);
        if (!fih.AsSpan(0, 4).SequenceEqual(FihMagic))
        {
            throw new InvalidDataException("only finalized FIH packages are supported");
        }

        var outerOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x10));
        var outerSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x18));
        var superblockOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x20));
        var cntOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x58));
        RequireRange(fileSize, outerOffset, outerSize, "outer PFS");
        RequireRange(fileSize, superblockOffset, BlockSize, "outer superblock");
        RequireRange(fileSize, cntOffset, BlockSize, "CNT header");
        if (superblockOffset < outerOffset || superblockOffset >= outerOffset + outerSize)
        {
            throw new InvalidDataException("FIH superblock does not lie inside the outer PFS");
        }

        var superblock = ReadExact(stream, superblockOffset, BlockSize);
        if (BinaryPrimitives.ReadUInt64LittleEndian(superblock) != 2 || !superblock.AsSpan(8, 4).SequenceEqual(new byte[] { 0x0B, 0x2A, 0x33, 0x01 }))
        {
            throw new InvalidDataException("FIH does not point to a PS5 outer-PFS superblock");
        }

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(0x1C));
        if (mode != 0x000D)
        {
            throw new InvalidDataException($"unsupported outer-PFS mode 0x{mode:X4}; expected 0x000D");
        }

        var cnt = ReadExact(stream, cntOffset, BlockSize);
        if (!cnt.AsSpan(0, 4).SequenceEqual(CntMagic))
        {
            throw new InvalidDataException("FIH does not point to a CNT header");
        }

        var entries = ParseEntries(cnt);
        var touched = new SortedSet<long>();

        void MarkTouched(long offset, long size)
        {
            if (size <= 0)
            {
                return;
            }

            for (var block = offset / BlockSize; block <= (offset + size - 1) / BlockSize; block++)
            {
                touched.Add(block);
            }
        }

        // 64 KiB đầu của CNT chứa cả trường header lẫn dữ liệu các mục nhỏ của gói nwonly: giữ bản trong bộ nhớ khớp với mọi lần
        // ghi, để lần ghi header cuối cùng không khôi phục một bản ghi bảng digest cũ.
        void WriteCnt(long relativeOffset, ReadOnlySpan<byte> data)
        {
            RequireRange(fileSize - cntOffset, relativeOffset, data.Length, "CNT update");
            stream.Position = cntOffset + relativeOffset;
            stream.Write(data);
            MarkTouched(cntOffset + relativeOffset, data.Length);
            var first = Math.Max(relativeOffset, 0);
            var last = Math.Min(relativeOffset + data.Length, BlockSize);
            if (first < last)
            {
                data.Slice((int)(first - relativeOffset), (int)(last - first)).CopyTo(cnt.AsSpan((int)first));
            }
        }

        // Đổi dấu nhận dạng trước, rồi tạo lại mọi hash bao trực tiếp nó. Superblock vốn không mã hoá cả trong ảnh nwonly thường.
        PlaintextSeed.CopyTo(superblock.AsSpan(0x370));
        superblock.AsSpan(0x380, 32).Clear();
        Sha3(superblock.AsSpan(0, 0x5A0)).CopyTo(superblock.AsSpan(0x380));
        WriteAt(stream, superblockOffset, superblock);
        MarkTouched(superblockOffset, superblock.Length);
        var superblockDigest = Sha3(superblock);
        progress?.Invoke(20, "Updating plaintext outer-PFS marker and superblock digest");

        // Bản vá identity-CBC của SDK để dữ liệu các mục này ở dạng rõ nhưng vẫn bật cờ mã hoá: bỏ bit đó để trình đọc dùng
        // đúng kích thước logic thay vì giải mã dữ liệu rõ. Mọi bit chính sách / chỉ số khoá khác giữ nguyên.
        var cleared = new List<int>();
        var table = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x18));
        for (var index = 0; index < entries.Count; index++)
        {
            if ((entries[index].Flags & EntryFlagEncrypted) != 0)
            {
                var record = (int)table + index * EntryMetaSize;
                BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(record + 8), entries[index].Flags & ~EntryFlagEncrypted);
                cleared.Add(index);
            }
        }

        entries = ParseEntries(cnt);

        // imagedigs.dat: một SHA3 đảo ngược cho mỗi khối 64 KiB của PFS ngoài — chỉ bản ghi của superblock đổi.
        var imageEntryIndex = entries.FindIndex(entry => entry.Id == ImageDigestsEntryId);
        if (imageEntryIndex < 0)
        {
            throw new InvalidDataException("CNT has no imagedigs.dat entry");
        }

        var imageEntry = entries[imageEntryIndex];
        var superblockIndex = (superblockOffset - outerOffset) / BlockSize;
        var imageRecordOffset = superblockIndex * 32;
        if (outerSize % BlockSize != 0 || imageRecordOffset + 32 > imageEntry.Size)
        {
            throw new InvalidDataException("imagedigs.dat does not cover the outer-PFS superblock");
        }

        WriteAt(stream, cntOffset, cnt);
        MarkTouched(cntOffset, cnt.Length);
        var reversedDigest = superblockDigest.ToArray();
        Array.Reverse(reversedDigest);
        WriteCnt(imageEntry.Offset + imageRecordOffset, reversedDigest);

        // Tham chiếu chéo FIH/CNT: ghi digest ảnh game mới vào FIH trước, rồi mới tính fixed-info.
        superblockDigest.CopyTo(fih.AsSpan(0x30));
        WriteAt(stream, 0, fih);
        MarkTouched(0, fih.Length);
        var fixedInfoDigest = Sha3(fih);
        superblockDigest.CopyTo(cnt.AsSpan(0x440));
        fixedInfoDigest.CopyTo(cnt.AsSpan(0x460));
        PlaintextSeed.CopyTo(cnt.AsSpan(0x4A0));

        // Bảng digest từng mục: cập nhật bản ghi của imagedigs, entry-meta và các mục vừa bỏ cờ (ô của chính bảng luôn là 0).
        var digestEntryIndex = entries.FindIndex(entry => entry.Id == DigestTableEntryId);
        if (digestEntryIndex < 0)
        {
            throw new InvalidDataException("CNT has no per-entry digest table");
        }

        var digestEntry = entries[digestEntryIndex];
        if (digestEntry.Size < entries.Count * 32L)
        {
            throw new InvalidDataException("CNT per-entry digest table is truncated");
        }

        var metasEntryIndex = entries.FindIndex(entry => entry.Id == EntryMetasEntryId);
        if (metasEntryIndex < 0)
        {
            throw new InvalidDataException("CNT has no entry-meta table");
        }

        foreach (var index in new SortedSet<int>(cleared) { metasEntryIndex, imageEntryIndex })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            WriteCnt(digestEntry.Offset + index * 32L, HashRange(stream, cntOffset + entry.Offset, entry.Size));
        }

        progress?.Invoke(45, "Rebuilding FIH/CNT cross-references and entry digests");

        // Bốn dấu niêm phong thân CNT. Tiền ảnh của chúng không chứa bốn trường này nên thứ tự ghi không ảnh hưởng.
        var rollupOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(0x20));
        var rollupSize = (long)BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x1C));
        RequireRange(fileSize - cntOffset, rollupOffset, rollupSize, "CNT rollup");
        WriteCnt(0x100, HashRange(stream, cntOffset + rollupOffset, rollupSize));

        var scCount = BinaryPrimitives.ReadUInt16BigEndian(cnt.AsSpan(0x14));
        var scEntries = entries.Where(entry => entry.Id is 0x0010 or 0x0020 or 0x0080 or 0x0100).ToList();
        if (scEntries.Count != 4)
        {
            throw new InvalidDataException("CNT does not contain the expected SC entries");
        }

        var shortRanges = scEntries.Select(entry => (cntOffset + entry.Offset, (long)entry.Size)).ToList();
        shortRanges[^1] = (cntOffset + scEntries[^1].Offset, Math.Min(scEntries[^1].Size, scCount * (long)EntryMetaSize));
        WriteCnt(0x120, HashRanges(stream, shortRanges));
        WriteCnt(0x140, HashRange(stream, cntOffset + digestEntry.Offset, digestEntry.Size));

        var bodyOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(0x20));
        var bodySize = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(0x28));
        RequireRange(fileSize - cntOffset, bodyOffset, bodySize, "CNT body");
        WriteCnt(0x160, HashRange(stream, cntOffset + bodyOffset, bodySize));

        var imageKeyOffset = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x510));
        var imageKeySize = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x514));
        var mandatoryOffset = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x518));
        var mandatorySize = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x51C));
        RequireRange(fileSize - cntOffset, imageKeyOffset, imageKeySize, "CNT image-key descriptor");
        RequireRange(fileSize - cntOffset, mandatoryOffset, mandatorySize, "CNT mandatory descriptor");
        var descriptorDigests = new byte[64];
        HashRange(stream, cntOffset + imageKeyOffset, imageKeySize).CopyTo(descriptorDigests, 0);
        HashRange(stream, cntOffset + mandatoryOffset, mandatorySize).CopyTo(descriptorDigests, 32);
        WriteCnt(0x520, descriptorDigests);

        cnt.AsSpan(0xFE0, 32).Clear();
        Sha3(cnt.AsSpan(0, 0xFE0)).CopyTo(cnt.AsSpan(0xFE0));
        WriteAt(stream, cntOffset, cnt);
        MarkTouched(cntOffset, cnt.Length);
        var wrap = ProsperoPublisherRsa.BuildCntHeaderWrap(cnt);
        if (wrap.Length != CntHeaderWrapBytes)
        {
            throw new InvalidDataException("invalid CNT header wrap length");
        }

        WriteAt(stream, cntOffset + 0x1000, wrap);
        MarkTouched(cntOffset + 0x1000, wrap.Length);
        stream.Flush();
        progress?.Invoke(75, "Rebuilding CNT rollups and RSA-3072 header wrap");

        table = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x18));
        bodyOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(0x20));
        bodySize = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(0x28));
        var cntEnd = Math.Max(0x5A0L, Math.Max(table + entries.Count * (long)EntryMetaSize, bodyOffset + bodySize));
        foreach (var entry in entries)
        {
            cntEnd = Math.Max(cntEnd, (long)entry.Offset + entry.Size);
        }

        cntEnd = (cntEnd + 15) & ~15L;
        var supplementOffset = cntOffset + cntEnd;
        RequireRange(fileSize, supplementOffset, fileSize - supplementOffset, "SI archive");
        var contentIdBytes = cnt.AsSpan(0x40, 0x30);
        var terminator = contentIdBytes.IndexOf((byte)0);
        var contentId = Encoding.ASCII.GetString(terminator < 0 ? contentIdBytes : contentIdBytes[..terminator]);
        var repaired = RepairPlayGoCrc(stream, fileSize, supplementOffset, touched, contentId);
        stream.Flush();
        progress?.Invoke(100, "Repairing SI PlayGo CRC32C and ZIP metadata");
        return new SonySdkConversionReport(cleared.Count, outerSize / BlockSize, wrap.Length, repaired);
    }

    private static int RepairPlayGoCrc(FileStream stream, long fileSize, long supplementOffset, SortedSet<long> touched, string contentId)
    {
        var archive = ReadExact(stream, supplementOffset, checked((int)(fileSize - supplementOffset)));
        var crcName = $"config/{contentId}/playgo-chunk.crc";
        var member = FindStoredMember(archive, crcName);
        var current = archive.AsSpan(member.DataOffset, member.Size).ToArray();
        var blockCount = (supplementOffset + BlockSize - 1) / BlockSize;
        if (current.Length != blockCount * 4)
        {
            throw new InvalidDataException($"SI PlayGo CRC table has {current.Length} bytes; expected {blockCount * 4}");
        }

        var repaired = 0;
        foreach (var blockIndex in touched)
        {
            if (blockIndex >= blockCount)
            {
                continue;
            }

            var blockOffset = blockIndex * BlockSize;
            var size = (int)Math.Min(BlockSize, supplementOffset - blockOffset);
            var block = ReadExact(stream, blockOffset, size);
            BinaryPrimitives.WriteUInt32LittleEndian(current.AsSpan((int)blockIndex * 4), ProsperoCrc32C.Compute(block));
            repaired++;
        }

        ReplaceStoredMember(archive, member, current);
        WriteAt(stream, supplementOffset, archive);
        return repaired;
    }

    private readonly record struct ZipMember(int LocalHeader, int DataOffset, int Size, int CentralRecord);

    /// <summary>Tìm đúng một mục ZIP lưu thẳng (STORED, không data-descriptor) theo tên, đọc từ thư mục trung tâm.</summary>
    private static ZipMember FindStoredMember(byte[] archive, string name)
    {
        var eocd = LastIndexOf(archive, "PK\x05\x06"u8, Math.Max(0, archive.Length - 0x10016));
        if (eocd < 0 || eocd + 22 > archive.Length)
        {
            throw new InvalidDataException("SI end-of-central-directory record is missing");
        }

        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(eocd + 12));
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(eocd + 16));
        var centralEnd = (long)centralOffset + centralSize;
        if (centralEnd > eocd)
        {
            throw new InvalidDataException("SI central directory is outside the archive");
        }

        ZipMember? found = null;
        var cursor = (int)centralOffset;
        while (cursor < centralEnd)
        {
            if (!archive.AsSpan(cursor, 4).SequenceEqual("PK\x01\x02"u8))
            {
                throw new InvalidDataException("invalid SI central-directory record");
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(cursor + 8));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(cursor + 10));
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(cursor + 20));
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(cursor + 24));
            var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(cursor + 28));
            var extraSize = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(cursor + 30));
            var commentSize = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(cursor + 32));
            var localHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(cursor + 42));
            var entryName = Encoding.UTF8.GetString(archive, cursor + 46, nameSize);
            if (entryName == name)
            {
                if (found != null)
                {
                    throw new InvalidDataException($"SI archive must contain exactly one {name}");
                }

                if (method != 0 || (flags & 0x08) != 0)
                {
                    throw new InvalidDataException($"SI member is not a directly patchable STORED entry: {name}");
                }

                if (compressedSize != uncompressedSize)
                {
                    throw new InvalidDataException($"SI member size mismatch for {name}");
                }

                if (!archive.AsSpan(localHeader, 4).SequenceEqual("PK\x03\x04"u8))
                {
                    throw new InvalidDataException($"invalid SI local header for {name}");
                }

                var localNameSize = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(localHeader + 26));
                var localExtraSize = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(localHeader + 28));
                found = new ZipMember(localHeader, localHeader + 30 + localNameSize + localExtraSize, (int)uncompressedSize, cursor);
            }

            cursor += 46 + nameSize + extraSize + commentSize;
        }

        return found ?? throw new InvalidDataException($"SI archive must contain exactly one {name}");
    }

    /// <summary>Ghi dữ liệu mới (cùng kích thước) vào mục ZIP và cập nhật CRC-32 ở header cục bộ lẫn thư mục trung tâm.</summary>
    private static void ReplaceStoredMember(byte[] archive, ZipMember member, byte[] payload)
    {
        if (payload.Length != member.Size)
        {
            throw new InvalidDataException("SI member size mismatch");
        }

        payload.CopyTo(archive, member.DataOffset);
        var crc = ZipCrc32(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(member.LocalHeader + 14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(member.CentralRecord + 16), crc);
    }

    private static List<Entry> ParseEntries(byte[] header)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x10));
        var table = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18));
        if (count == 0 || count > 0x10000 || table + (long)count * EntryMetaSize > header.Length)
        {
            throw new InvalidDataException("invalid CNT entry table");
        }

        var entries = new List<Entry>((int)count);
        for (var index = 0; index < count; index++)
        {
            var record = (int)table + index * EntryMetaSize;
            entries.Add(new Entry(
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(record)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(record + 8)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(record + 0x10)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(record + 0x14))));
        }

        return entries;
    }

    private static void RequireRange(long fileSize, long offset, long size, string name)
    {
        if (offset < 0 || size < 0 || offset > fileSize || size > fileSize - offset)
        {
            throw new InvalidDataException($"{name} is outside the package");
        }
    }

    private static byte[] ReadExact(FileStream stream, long offset, int size)
    {
        var buffer = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void WriteAt(FileStream stream, long offset, ReadOnlySpan<byte> data)
    {
        stream.Position = offset;
        stream.Write(data);
    }

    private static byte[] Sha3(ReadOnlySpan<byte> data)
    {
        var digest = new byte[32];
        ProsperoSha3.HashData(data, digest);
        return digest;
    }

    /// <summary>SHA3-256 của một vùng tệp, băm theo luồng (imagedigs.dat của game lớn dài vài chục MB).</summary>
    private static byte[] HashRange(FileStream stream, long offset, long size)
    {
        if (size == 0)
        {
            return Sha3(ReadOnlySpan<byte>.Empty);
        }

        return Crypto.Sha3_256(stream, offset, size);
    }

    private static byte[] HashRanges(FileStream stream, IReadOnlyList<(long Offset, long Size)> ranges)
    {
        // Chỉ dùng cho bốn mục SC ngắn ở đầu CNT (vài KB) nên gom vào bộ nhớ rồi băm một lần.
        using var buffer = new MemoryStream();
        var chunk = new byte[1 << 20];
        foreach (var (offset, size) in ranges)
        {
            stream.Position = offset;
            var remaining = size;
            while (remaining > 0)
            {
                var read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                if (read <= 0)
                {
                    throw new InvalidDataException($"truncated package at 0x{offset:X}");
                }

                buffer.Write(chunk, 0, read);
                remaining -= read;
            }
        }

        return Sha3(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static int LastIndexOf(byte[] data, ReadOnlySpan<byte> pattern, int minimum)
    {
        var index = data.AsSpan(minimum).LastIndexOf(pattern);
        return index < 0 ? -1 : minimum + index;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    /// <summary>CRC-32 của ZIP (zlib.crc32).</summary>
    public static uint ZipCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }
}
