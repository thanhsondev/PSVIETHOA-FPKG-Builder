using System.Globalization;
using System.Text.RegularExpressions;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Một giai đoạn của quá trình tạo gói (tên lấy từ bảng chuỗi) với trọng số thời gian ước lượng.</summary>
public sealed record BuildPhase(string Key, double Weight, int Order)
{
    public string Name => Localization.Loc.T("Phase." + Key);
}

/// <summary>
/// Nhận diện giai đoạn và phần trăm từ các dòng nhật ký của LibProsperoPkg 1.2.
/// Trọng số được hiệu chỉnh từ đo đạc thực tế (game 21 GB, Kraken 7): nén ảnh trong chiếm ~98% thời gian,
/// ghi PFS ngoài + SHA3 ~1%, phần còn lại là khoá/CNT/ghi tệp.
/// </summary>
public static partial class PhaseCatalog
{
    public static readonly BuildPhase Mount = new("mount", 0.5, -2);
    public static readonly BuildPhase Extract = new("extract", 8, -1);
    public static readonly BuildPhase Prepare = new("prepare", 1, 0);
    public static readonly BuildPhase InnerRead = new("inner-read", 1.5, 1);
    public static readonly BuildPhase InnerData = new("inner-data", 85, 2);
    public static readonly BuildPhase Layout = new("layout", 0.5, 3);
    public static readonly BuildPhase Naps = new("naps", 0.5, 3);
    public static readonly BuildPhase Outer = new("outer", 5, 4);
    public static readonly BuildPhase Keys = new("keys", 1.5, 5);
    public static readonly BuildPhase Cnt = new("cnt", 0.5, 6);
    public static readonly BuildPhase Finalize = new("finalize", 3, 7);
    public static readonly BuildPhase Verify = new("verify", 1, 8);
    public static readonly BuildPhase Sha256 = new("sha256", 6, 9);

    /// <summary>Kiểm tra đầy đủ: giải mã + giải nén thử mọi tệp (engine VerifyPackageFull) — lâu ngang một lượt đọc hết gói.</summary>
    public static readonly BuildPhase VerifyFull = new("verify-full", 8, 10);

    public static IReadOnlyList<BuildPhase> LibraryPhases { get; } =
        [Prepare, InnerRead, InnerData, Layout, Naps, Outer, Keys, Cnt, Finalize];

    /// <summary>Chuỗi giai đoạn đầy đủ cho một lần tạo gói (kèm bước chuẩn bị ảnh exFAT và kiểm tra của ứng dụng).</summary>
    public static IReadOnlyList<BuildPhase> Sequence(bool computeSha256, BuildPhase? exFatPhase = null, bool fullVerify = false)
    {
        var list = new List<BuildPhase>(LibraryPhases.Count + 3);
        if (exFatPhase != null)
        {
            list.Add(exFatPhase);
        }

        list.AddRange(LibraryPhases);
        if (computeSha256)
        {
            list.Add(Sha256);
        }

        list.Add(Verify);
        if (fullVerify)
        {
            list.Add(VerifyFull);
        }

        return list;
    }

    [GeneratedRegex(@"^\s*\[\+[0-9:.]{1,24}\]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrefix();

    [GeneratedRegex(@"^\[inner\]\s+read\s+\d+\s*/\s*\d+\s+\(\s*(?<pct>\d{1,3})%\)", RegexOptions.CultureInvariant)]
    private static partial Regex InnerReadPattern();

    [GeneratedRegex(@"^\[inner\]\s+data\s+(?<pct>\d{1,3})%", RegexOptions.CultureInvariant)]
    private static partial Regex InnerDataPattern();

    [GeneratedRegex(@"^\[stage\s+(?<stage>\d+)\s*/\s*\d+\]\s*(?<rest>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex StagePattern();

    [GeneratedRegex(@"^\[finalize\][^%]*?:\s*(?<pct>\d{1,3})%", RegexOptions.CultureInvariant)]
    private static partial Regex FinalizePattern();

    [GeneratedRegex(@":\s*(?<pct>\d{1,3})%", RegexOptions.CultureInvariant)]
    private static partial Regex GenericPercent();

    [GeneratedRegex(@"^\[inner\]\s+(?<what>Inner size planning|Inode and AFID planning|Inner layout planning|Inner layout validation|Inner data compression and write):\s*(?:(?<pct>\d{1,3})%|(?<state>started|complete))", RegexOptions.CultureInvariant)]
    private static partial Regex InnerSubPhasePattern();

    [GeneratedRegex(@";\s*(?<rate>\d+(?:\.\d+)?\s*(?:[KMG]i?B)/s)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ThroughputPattern();

    /// <summary>Đọc tốc độ "N MiB/s" nếu dòng nhật ký có ghi.</summary>
    public static string? ParseThroughput(string rawMessage)
    {
        var match = ThroughputPattern().Match(rawMessage);
        return match.Success ? match.Groups["rate"].Value.Replace("  ", " ") : null;
    }

    /// <summary>Bỏ tiền tố thời gian tương đối "[+00:00:04.543]" của thư viện.</summary>
    public static string StripTimestamp(string message)
    {
        var match = TimestampPrefix().Match(message);
        return match.Success ? message[match.Length..] : message;
    }

    /// <summary>Thử đọc giai đoạn và phần trăm từ một dòng nhật ký thư viện.</summary>
    public static bool TryParse(string rawMessage, out BuildPhase phase, out double? percent)
    {
        phase = Prepare;
        percent = null;
        var message = StripTimestamp(rawMessage).Trim();
        if (message.Length == 0)
        {
            return false;
        }

        if (message.StartsWith("[inner]", StringComparison.Ordinal))
        {
            var sub = InnerSubPhasePattern().Match(message);
            if (sub.Success)
            {
                var what = sub.Groups["what"].Value;
                double? pct = sub.Groups["pct"].Success ? ParsePercent(sub.Groups["pct"].Value)
                    : sub.Groups["state"].Value == "complete" ? 100 : 0;
                switch (what)
                {
                    case "Inner size planning":
                        phase = InnerRead;
                        percent = 33 + pct / 3;
                        return true;
                    case "Inode and AFID planning":
                        phase = InnerRead;
                        percent = 66 + pct / 3;
                        return true;
                    case "Inner data compression and write":
                        phase = InnerData;
                        percent = pct >= 100 ? 100 : 0;
                        return true;
                    default:
                        phase = Layout;
                        percent = what == "Inner layout validation" ? Math.Max(50, pct ?? 0) : pct;
                        return true;
                }
            }

            var read = InnerReadPattern().Match(message);
            if (read.Success)
            {
                phase = InnerRead;
                percent = ParsePercent(read.Groups["pct"].Value) / 3;
                return true;
            }

            if (message.StartsWith("[inner] Inner compression cache", StringComparison.Ordinal) ||
                message.StartsWith("[inner] Large combined writes", StringComparison.Ordinal))
            {
                phase = InnerData;
                percent = 100;
                return true;
            }

            var data = InnerDataPattern().Match(message);
            if (data.Success)
            {
                phase = InnerData;
                percent = ParsePercent(data.Groups["pct"].Value);
                return true;
            }

            if (message.Contains("Preparing", StringComparison.OrdinalIgnoreCase))
            {
                phase = InnerRead;
                percent = 0;
                return true;
            }

            if (message.Contains("Compressing", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("writing", StringComparison.OrdinalIgnoreCase))
            {
                phase = InnerData;
                percent = 0;
                return true;
            }

            return false;
        }

        if (message.StartsWith("[finalize]", StringComparison.Ordinal))
        {
            phase = Finalize;
            var finalize = FinalizePattern().Match(message);
            percent = finalize.Success ? ParsePercent(finalize.Groups["pct"].Value) : null;
            return true;
        }

        var stage = StagePattern().Match(message);
        if (stage.Success)
        {
            var number = int.Parse(stage.Groups["stage"].Value, CultureInfo.InvariantCulture);
            var rest = stage.Groups["rest"].Value;
            var complete = rest.Contains("complete", StringComparison.OrdinalIgnoreCase);
            var generic = GenericPercent().Match(rest);
            double? pct = generic.Success ? ParsePercent(generic.Groups["pct"].Value) : (complete ? 100 : 0);

            switch (number)
            {
                case 1:
                    phase = complete ? Layout : InnerRead;
                    percent = complete ? 100 : 0;
                    return true;
                case 2:
                    phase = Naps;
                    percent = pct;
                    return true;
                case 3:
                    if (complete)
                    {
                        // Sau khi PFS ngoài xong là khoảng lặng tính digest/bọc khoá RSA (không có dòng tiến trình).
                        phase = Keys;
                        percent = 0;
                        return true;
                    }

                    phase = Outer;
                    percent = rest.Contains("started", StringComparison.OrdinalIgnoreCase) ? 0 : pct;
                    return true;
                case 4:
                    phase = Cnt;
                    percent = pct;
                    return true;
                default:
                    phase = Finalize;
                    percent = complete ? 100 : 0;
                    return true;
            }
        }

        if (message.StartsWith("Source tree scan:", StringComparison.OrdinalIgnoreCase))
        {
            phase = Prepare;
            percent = message.Contains("complete", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
            return true;
        }

        if (message.StartsWith("Source scan:", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Building the PS5 package", StringComparison.OrdinalIgnoreCase))
        {
            phase = Prepare;
            percent = 100;
            return true;
        }

        if (message.StartsWith("Flushing finalized FIH image", StringComparison.OrdinalIgnoreCase))
        {
            phase = Finalize;
            percent = message.Contains("complete", StringComparison.OrdinalIgnoreCase) ? 100 : null;
            return true;
        }

        if (message.StartsWith("Calculating PFS image digests", StringComparison.OrdinalIgnoreCase))
        {
            phase = Keys;
            percent = 100;
            return true;
        }

        if (message.StartsWith("Finalizing the CNT", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Writing finalized", StringComparison.OrdinalIgnoreCase))
        {
            phase = Finalize;
            percent = 0;
            return true;
        }

        if (message.StartsWith("Build finished", StringComparison.OrdinalIgnoreCase))
        {
            phase = Finalize;
            percent = 100;
            return true;
        }

        return false;
    }

    private static double ParsePercent(string text) =>
        Math.Clamp(double.Parse(text, CultureInfo.InvariantCulture), 0, 100);
}
