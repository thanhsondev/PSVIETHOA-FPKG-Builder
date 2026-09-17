using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Kết quả kiểm tra bản mới trên GitHub Releases.</summary>
public sealed record UpdateInfo(
    string LatestVersion,
    string TagName,
    string ReleaseUrl,
    string? AssetName,
    string? AssetUrl,
    long AssetSize,
    DateTimeOffset? PublishedAt,
    bool IsNewer);

/// <summary>
/// Kiểm tra bản mới qua GitHub API (releases/latest). Không tự tải hay cài — chỉ báo phiên bản mới và đường dẫn tải
/// cho đúng nền tảng (macOS Apple Silicon / Intel, Windows x64).
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "thanhsondev/PSVIETHOA-FPKG-Builder";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    public const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PSVIETHOA-FPKG-Builder", "2"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>Đoạn tên tệp phát hành cho nền tảng hiện tại (khớp tên zip trên Releases).</summary>
    public static string PlatformAssetHint()
    {
        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "macOS-AppleSilicon" : "macOS-Intel";
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Linux-arm64" : "Linux-x64";
        }

        return OperatingSystem.IsWindows() ? "Windows-x64" : string.Empty;
    }

    /// <summary>
    /// Đoạn tên tệp ưu tiên trong số các tệp khớp nền tảng: bản cài bằng Setup.exe (có Uninstall.exe cạnh ứng dụng) thì lấy
    /// "-Setup.exe" để cập nhật cũng qua bộ cài; bản portable/macOS lấy ".zip".
    /// </summary>
    public static string PreferredAssetToken()
    {
        if (OperatingSystem.IsWindows() && IsInstalledViaSetup)
        {
            return "Setup";
        }

        return OperatingSystem.IsLinux() ? ".tar.gz" : ".zip";
    }

    /// <summary>Ứng dụng được cài bằng bộ cài Windows (Uninstall.exe nằm cạnh tệp thực thi).</summary>
    public static bool IsInstalledViaSetup
    {
        get
        {
            try
            {
                return OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "Uninstall.exe"));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static async Task<UpdateInfo> CheckAsync(string currentVersion, string platformAssetHint, CancellationToken cancellationToken, string? preferredToken = null)
    {
        string json;
        try
        {
            json = await Http.GetStringAsync(LatestApi, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(Loc.F("Update.Network", ex.Message), ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(Loc.F("Update.Network", Loc.T("Update.Timeout")), ex);
        }

        return Parse(json, currentVersion, platformAssetHint, preferredToken);
    }

    /// <summary>
    /// Đọc JSON của GitHub releases/latest (tách ra để kiểm thử không cần mạng). Trong các tệp khớp <paramref name="platformAssetHint"/>,
    /// ưu tiên tệp chứa <paramref name="preferredToken"/> (Setup.exe hay .zip); không có thì lấy tệp khớp đầu tiên.
    /// </summary>
    public static UpdateInfo Parse(string json, string currentVersion, string platformAssetHint, string? preferredToken = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(Loc.T("Update.BadResponse"));
        }

        var tag = tagElement.GetString() ?? string.Empty;
        var latest = NormalizeVersion(tag);
        var url = root.TryGetProperty("html_url", out var htmlUrl) && htmlUrl.ValueKind == JsonValueKind.String ? htmlUrl.GetString()! : ReleasesPage;
        DateTimeOffset? published = root.TryGetProperty("published_at", out var publishedAt) && publishedAt.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(publishedAt.GetString(), out var stamp)
            ? stamp
            : null;

        string? assetName = null, assetUrl = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
                if (platformAssetHint.Length == 0 || !name.Contains(platformAssetHint, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var preferred = !string.IsNullOrEmpty(preferredToken) && name.Contains(preferredToken, StringComparison.OrdinalIgnoreCase);
                if (assetName != null && !preferred)
                {
                    continue;
                }

                assetName = name;
                assetUrl = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                assetSize = asset.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var value) ? value : 0;
                if (preferred || string.IsNullOrEmpty(preferredToken))
                {
                    break;
                }
            }
        }

        return new UpdateInfo(latest, tag, url, assetName, assetUrl, assetSize, published, IsNewer(latest, currentVersion));
    }

    /// <summary>"v2.1.4" / "2.1.4+abc" → "2.1.4".</summary>
    public static string NormalizeVersion(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var cut = trimmed.IndexOfAny(['+', '-', ' ']);
        return cut >= 0 ? trimmed[..cut] : trimmed;
    }

    /// <summary>
    /// Bản mới hơn khi số phiên bản lớn hơn; cùng số thì bản chính thức (không hậu tố) mới hơn bản thử ("2.2.0-test3" → "2.2.0"),
    /// và giữa hai bản thử so hậu tố theo thứ tự chữ/số tự nhiên ("test2" < "test3" < "test10").
    /// </summary>
    public static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(NormalizeVersion(latest), out var a) || !Version.TryParse(NormalizeVersion(current), out var b))
        {
            return false;
        }

        var comparison = Pad(a).CompareTo(Pad(b));
        if (comparison != 0)
        {
            return comparison > 0;
        }

        var latestSuffix = PrereleaseSuffix(latest);
        var currentSuffix = PrereleaseSuffix(current);
        if (latestSuffix.Length == 0)
        {
            return currentSuffix.Length > 0;
        }

        return currentSuffix.Length > 0 && CompareNatural(latestSuffix, currentSuffix) > 0;
    }

    /// <summary>Phần sau dấu '-' của "v2.2.0-test3" ("test3"); rỗng với bản chính thức. Metadata sau '+' bị bỏ.</summary>
    private static string PrereleaseSuffix(string text)
    {
        var trimmed = text.Trim();
        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        var dash = trimmed.IndexOf('-');
        return dash >= 0 ? trimmed[(dash + 1)..].Trim().ToLowerInvariant() : string.Empty;
    }

    private static int CompareNatural(string left, string right)
    {
        var i = 0;
        var j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
            {
                var startI = i;
                var startJ = j;
                while (i < left.Length && char.IsDigit(left[i])) i++;
                while (j < right.Length && char.IsDigit(right[j])) j++;
                var numberComparison = long.Parse(left[startI..i]).CompareTo(long.Parse(right[startJ..j]));
                if (numberComparison != 0)
                {
                    return numberComparison;
                }

                continue;
            }

            var charComparison = left[i].CompareTo(right[j]);
            if (charComparison != 0)
            {
                return charComparison;
            }

            i++;
            j++;
        }

        return (left.Length - i).CompareTo(right.Length - j);
    }

    private static Version Pad(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
