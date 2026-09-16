using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LibProsperoPkg.PKG;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Kiểm tra cấu trúc tệp PKG vừa tạo (header FIH, superblock PFS ngoài, dấu PLAINTEXT_NOAUTH, SHA-256 tuỳ chọn).</summary>
public static class PackageVerifier
{
    private const int HashBufferSize = 4 * 1024 * 1024;

    // "\x7FFIH" – 4 byte magic của ảnh FIH.
    private static ReadOnlySpan<byte> FihMagic => [0x7F, (byte)'F', (byte)'I', (byte)'H'];

    /// <summary>Dấu 16 byte tại superblock+880 của lớp ngoài PLAINTEXT_NOAUTH.</summary>
    public const string PlaintextMarker = "PPRPLAIN-NOAUTH!";

    /// <summary>Các trường tối thiểu đọc từ header FIH (chưa kiểm tra giá trị) — dùng chung cho kiểm tra sau build và chế độ giải nén.</summary>
    public sealed record FihSummary(byte SignedByte, long OuterSuperblockOffset, ushort OuterMode, string Marker);

    /// <summary>Đọc magic FIH, signed byte, offset superblock PFS ngoài, mode và dấu 16 byte của lớp ngoài.</summary>
    public static FihSummary ReadFihSummary(Stream stream)
    {
        if (stream.Length < 4096)
        {
            throw new InvalidDataException(Localization.Loc.T("Verify.TooSmall"));
        }

        stream.Position = 0;
        Span<byte> header = stackalloc byte[48];
        stream.ReadExactly(header);

        if (!header[..4].SequenceEqual(FihMagic))
        {
            throw new InvalidDataException(Localization.Loc.T("Verify.NoFih"));
        }

        var signedByte = header[5];
        var superblockOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(32, 8)));
        if (superblockOffset < 0 || superblockOffset + 896 > stream.Length)
        {
            throw new InvalidDataException(Localization.Loc.T("Verify.Superblock"));
        }

        stream.Position = superblockOffset + 28;
        Span<byte> modeBytes = stackalloc byte[2];
        stream.ReadExactly(modeBytes);
        var outerMode = BinaryPrimitives.ReadUInt16LittleEndian(modeBytes);

        stream.Position = superblockOffset + 880;
        Span<byte> markerBytes = stackalloc byte[16];
        stream.ReadExactly(markerBytes);
        return new FihSummary(signedByte, superblockOffset, outerMode, Encoding.ASCII.GetString(markerBytes));
    }

    public static PackageVerification Verify(
        string packagePath,
        OuterImageMode expectedMode,
        bool calculateSha256,
        CancellationToken cancellationToken,
        Action<double>? hashProgress = null)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException(Localization.Loc.T("Verify.NotFound"), packagePath);
        }

        var detectedType = ProsperoPkgReader.DetectType(packagePath);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferSize, FileOptions.SequentialScan);
        if (stream.Length < 4096)
        {
            throw new InvalidDataException(Localization.Loc.T("Verify.TooSmall"));
        }

        var summary = ReadFihSummary(stream);
        var signedByte = summary.SignedByte;
        if (signedByte != 0)
        {
            throw new InvalidDataException(Localization.Loc.F("Verify.Signed", signedByte.ToString("X2")));
        }

        var outerMode = summary.OuterMode;
        if (outerMode != 13)
        {
            throw new InvalidDataException(Localization.Loc.F("Verify.OuterMode", outerMode.ToString("X4")));
        }

        string? marker = null;
        if (expectedMode == OuterImageMode.PlaintextNoAuth)
        {
            marker = summary.Marker;
            if (!string.Equals(marker, PlaintextMarker, StringComparison.Ordinal))
            {
                throw new InvalidDataException(Localization.Loc.T("Verify.Marker"));
            }
        }

        string? sha256 = null;
        if (calculateSha256)
        {
            sha256 = ComputeSha256(stream, cancellationToken, hashProgress);
        }

        string? contentId = null;
        var entryCount = 0;
        var isOfficial = false;
        try
        {
            var package = ProsperoPkgReader.Read(packagePath);
            contentId = package.Header?.ContentId;
            entryCount = package.Entries?.Count ?? 0;
            isOfficial = package.Fih?.IsOfficial ?? false;
        }
        catch (Exception)
        {
            // Thông tin chi tiết chỉ là phần bổ sung; các kiểm tra cấu trúc phía trên mới là bắt buộc.
        }

        return new PackageVerification(
            detectedType?.ToString() ?? "Unknown",
            stream.Length,
            signedByte,
            outerMode,
            marker,
            sha256,
            contentId,
            entryCount,
            isOfficial);
    }

    /// <summary>
    /// Kiểm tra nội dung gói bằng engine: <paramref name="full"/> = false chỉ đọc metadata (nhanh, vài mili giây tới vài giây);
    /// true giải mã và giải nén thử toàn bộ trong bộ nhớ (lâu ngang một lượt đọc hết gói). Không ghi gì ra đĩa.
    /// </summary>
    public static ContentVerification VerifyContents(
        string packagePath,
        string passcode,
        bool full,
        CancellationToken cancellationToken,
        Action<int>? progress = null,
        Action<string>? report = null)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException(Localization.Loc.T("Verify.NotFound"), packagePath);
        }

        var result = full
            ? ProsperoPackageArchive.VerifyPackageFull(packagePath, passcode, cancellationToken, progress, report)
            : ProsperoPackageArchive.VerifyPackageQuick(packagePath, passcode, cancellationToken, progress, report);
        cancellationToken.ThrowIfCancellationRequested();
        return new ContentVerification(
            result.IsFull,
            result.Elapsed,
            result.Checks.ToArray(),
            result.Issues.Select(issue => new ContentVerificationIssue(issue.Stage, issue.Message)).ToArray(),
            result.FileCount,
            result.FilesDecoded,
            result.OuterBlockCount,
            result.OuterBlocksVerified,
            result.UnpackedBytes);
    }

    /// <summary>Tính SHA-256 của toàn bộ tệp với bộ đệm lớn, báo tiến độ 0..100.</summary>
    public static string ComputeSha256(Stream stream, CancellationToken cancellationToken, Action<double>? progress = null)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = GC.AllocateUninitializedArray<byte>(HashBufferSize);
        long done = 0;
        var total = Math.Max(1, stream.Length);
        var lastReported = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            done += read;
            var percent = (int)(done * 100 / total);
            if (percent != lastReported && progress != null)
            {
                lastReported = percent;
                progress(percent);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
