using System.Buffers.Binary;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Cách sửa một tệp SELF: đổi magic PS4 sang Prospero, vị trí đoạn <c>.sceversion</c> và số byte 0 chèn trước nó.</summary>
public readonly record struct SelfRepairPlan(bool RewriteMagic, long TrailerOffset, int Padding)
{
    /// <summary>Mô tả như dòng "Repairing SELF … (…)" của script.</summary>
    public string Description
    {
        get
        {
            var repairs = new List<string>(2);
            if (RewriteMagic)
            {
                repairs.Add("Prospero magic");
            }

            if (Padding > 0)
            {
                repairs.Add($".sceversion +{Padding}-byte alignment");
            }

            return string.Join(", ", repairs);
        }
    }
}

/// <summary>
/// Sửa header SELF như script fix12 (<c>self_repair_plan</c>, <c>write_repaired_self</c>): bản dump backport đôi khi mang tệp SELF
/// với magic PS4 (<c>4F 15 3D 1D</c>) hoặc đoạn <c>.sceversion</c> cuối tệp lệch khỏi ranh giới ghi ở header (@0x10). Bản sửa ghi vào
/// <c>.gp5-assets/&lt;tên&gt;/normalized-self/</c> cạnh GP5; tệp nguồn không bao giờ bị sửa.
/// </summary>
public static class SonySdkSelfRepair
{
    /// <summary>Thư mục con trong .gp5-assets/&lt;tên&gt; (<c>NORMALIZED_SELF_DIRECTORY</c>).</summary>
    public const string FolderName = "normalized-self";

    private static ReadOnlySpan<byte> ProsperoMagic => [0x54, 0x14, 0xF5, 0xEE];

    private static ReadOnlySpan<byte> LegacyMagic => [0x4F, 0x15, 0x3D, 0x1D];

    private const int MaxMetadataSize = 64 * 1024 * 1024;

    /// <summary><c>is_complete_sceversion</c>: chuỗi bản ghi (00 00, u16 cỡ, 08, tên ASCII kết thúc ':', 2 × 8 byte phiên bản bằng nhau) kéo tới hết.</summary>
    public static bool IsCompleteSceVersion(ReadOnlySpan<byte> records)
    {
        if (records.IsEmpty)
        {
            return false;
        }

        var offset = 0;
        var count = 0;
        while (offset < records.Length)
        {
            if (records.Length - offset < 5 || records[offset] != 0 || records[offset + 1] != 0)
            {
                return false;
            }

            int payloadSize = BinaryPrimitives.ReadUInt16LittleEndian(records[(offset + 2)..]);
            var recordSize = payloadSize + 4;
            if (payloadSize < 18 || recordSize > records.Length - offset || records[offset + 4] != 8)
            {
                return false;
            }

            var nameSize = payloadSize - 17;
            var name = records.Slice(offset + 5, nameSize);
            var versionOffset = offset + 5 + nameSize;
            if (name.IsEmpty || name[^1] != (byte)':')
            {
                return false;
            }

            foreach (var value in name)
            {
                if (value is < 0x20 or > 0x7E)
                {
                    return false;
                }
            }

            // Python cắt lát ngoài phạm vi trả về ngắn hơn, không lỗi: so đúng như vậy.
            if (!Slice(records, versionOffset, 8).SequenceEqual(Slice(records, versionOffset + 8, 8)))
            {
                return false;
            }

            offset += recordSize;
            count++;
        }

        return count != 0;
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int start, int length)
    {
        if (start >= data.Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return data.Slice(start, Math.Min(length, data.Length - start));
    }

    /// <summary><c>self_repair_plan</c>: null = không phải SELF hoặc không cần sửa.</summary>
    public static SelfRepairPlan? Plan(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
        var size = stream.Length;
        if (size < 0x20)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[0x20];
        stream.ReadExactly(header);
        var rewrite = header[..4].SequenceEqual(LegacyMagic);
        if (!rewrite && !header[..4].SequenceEqual(ProsperoMagic))
        {
            return null;
        }

        SelfRepairPlan? Fallback() => rewrite ? new SelfRepairPlan(true, size, 0) : null;
        var boundary = BinaryPrimitives.ReadUInt64LittleEndian(header[0x10..]);
        if (boundary < 0x20 || boundary > (ulong)size)
        {
            return Fallback();
        }

        var start = Math.Max(0L, (long)boundary - 0x0F);
        var tailLength = size - start;
        if (tailLength is <= 0 or > MaxMetadataSize)
        {
            return Fallback();
        }

        var tail = new byte[tailLength];
        stream.Position = start;
        stream.ReadExactly(tail);
        var localBoundary = (int)((long)boundary - start);
        for (var backtrack = 0; backtrack <= Math.Min(0x0F, localBoundary); backtrack++)
        {
            if (IsCompleteSceVersion(tail.AsSpan(localBoundary - backtrack)))
            {
                return rewrite || backtrack > 0 ? new SelfRepairPlan(rewrite, (long)boundary - backtrack, backtrack) : null;
            }
        }

        return Fallback();
    }

    /// <summary>
    /// <c>executable_sdk_version</c> (fix15): phiên bản SDK (major/minor, 16 bit cao) lớn nhất trong đoạn <c>.sceversion</c> cuối tệp SELF —
    /// mỗi bản ghi mang 8 byte phiên bản big-endian. Null khi không phải SELF hoặc không tìm thấy đoạn hợp lệ.
    /// </summary>
    public static ulong? ExecutableSdkVersion(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
        var size = stream.Length;
        if (size < 0x20)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[0x20];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual(LegacyMagic) && !header[..4].SequenceEqual(ProsperoMagic))
        {
            return null;
        }

        var boundary = BinaryPrimitives.ReadUInt64LittleEndian(header[0x10..]);
        if (boundary < 0x20 || boundary > (ulong)size)
        {
            return null;
        }

        var start = Math.Max(0L, (long)boundary - 0x0F);
        if (size - start is <= 0 or > MaxMetadataSize)
        {
            return null;
        }

        var tail = new byte[size - start];
        stream.Position = start;
        stream.ReadExactly(tail);
        var localBoundary = (int)((long)boundary - start);
        for (var backtrack = 0; backtrack <= Math.Min(0x0F, localBoundary); backtrack++)
        {
            var records = tail.AsSpan(localBoundary - backtrack);
            if (!IsCompleteSceVersion(records))
            {
                continue;
            }

            ulong highest = 0;
            var offset = 0;
            while (offset < records.Length)
            {
                int payloadSize = BinaryPrimitives.ReadUInt16LittleEndian(records[(offset + 2)..]);
                var versionOffset = offset + 5 + payloadSize - 17;
                highest = Math.Max(highest, BinaryPrimitives.ReadUInt64BigEndian(records[versionOffset..]));
                offset += payloadSize + 4;
            }

            return highest & 0xFFFF000000000000UL;
        }

        return null;
    }

    /// <summary><c>write_repaired_self</c>: chép phần trước đoạn cuối (đổi magic nếu cần), chèn byte 0, chép phần còn lại, giữ thời gian tệp.</summary>
    public static void Write(string source, string destination, SelfRepairPlan plan, CancellationToken cancellationToken = default)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"generated normalized SELF already exists: {destination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            var remaining = plan.TrailerOffset;
            var position = 0L;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new EndOfStreamException($"unexpected EOF while normalizing {source}");
                }

                if (plan.RewriteMagic && position == 0)
                {
                    ProsperoMagic.CopyTo(buffer);
                }

                output.Write(buffer, 0, read);
                position += read;
                remaining -= read;
            }

            if (plan.Padding > 0)
            {
                output.Write(new byte[plan.Padding]);
            }

            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
        }

        // shutil.copystat: thời gian sửa/truy cập của tệp nguồn.
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        File.SetLastAccessTimeUtc(destination, File.GetLastAccessTimeUtc(source));
    }
}
