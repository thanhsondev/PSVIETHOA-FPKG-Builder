using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LibOrbisPkg.PKG;
using LibOrbisPkg.Rif;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Ps4;

/// <summary>Thông tin lưu khi xả DLC để đóng lại: CONTENT_ID, TITLE_ID, entitlement key và gói nguồn.</summary>
public sealed record Ps4DlcRebuildInfo(string ContentId, string TitleId, string EntitlementKey, string SourcePackage);

/// <summary>Entitlement key của DLC: đọc từ license debug của gói, dlc_rebuild.json, GP4 cũ, license.dat hoặc người dùng nhập.</summary>
public static class Ps4DlcMetadata
{
    public static string? KeyFromPackage(Pkg pkg)
    {
        if (pkg.Header.content_type != ContentType.AC || pkg.LicenseDat == null ||
            pkg.LicenseDat.LicenseType is not (LicenseType.Debug_0 or LicenseType.Debug_1))
        {
            return null;
        }

        var license = pkg.LicenseDat;
        license.DecryptSecretWithDebugKey();
        try
        {
            return Convert.ToHexString(license.Secret.AsSpan(0x70, 16)).ToLowerInvariant();
        }
        finally
        {
            license.EncryptSecretWithDebugKey();
        }
    }

    public static string ResolveKey(string image, string contentId, string explicitKey = "")
    {
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return ValidateKey(explicitKey);
        }

        var parent = Path.GetDirectoryName(image)!;
        foreach (var path in new[] { Path.Combine(parent, "dlc_rebuild.json"), Path.Combine(image, "dlc_rebuild.json") })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var info = JsonSerializer.Deserialize<Ps4DlcRebuildInfo>(File.ReadAllText(path)) ?? throw new InvalidDataException(Loc.T("Ps4.Dlc.BadRebuildJson"));
            if (info.ContentId != contentId)
            {
                throw new InvalidDataException(Loc.T("Ps4.Dlc.RebuildJsonOtherId"));
            }

            return ValidateKey(info.EntitlementKey);
        }

        foreach (var gp4 in Directory.EnumerateFiles(image, "*.gp4").Concat(Directory.EnumerateFiles(parent, "*.gp4")))
        {
            var document = XDocument.Load(gp4);
            var package = document.Root?.Element("volume")?.Element("package");
            if ((string?)package?.Attribute("content_id") != contentId)
            {
                continue;
            }

            var key = (string?)package?.Attribute("entitlement_key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                return ValidateKey(key);
            }
        }

        foreach (var path in new[] { Path.Combine(image, "sce_sys", "license.dat"), Path.Combine(parent, "Sc0", "license.dat") })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = File.OpenRead(path);
            var license = new LicenseDatReader(stream).Read();
            if (license.ContentId != contentId || license.LicenseType is not (LicenseType.Debug_0 or LicenseType.Debug_1))
            {
                continue;
            }

            license.DecryptSecretWithDebugKey();
            try
            {
                return Convert.ToHexString(license.Secret.AsSpan(0x70, 16)).ToLowerInvariant();
            }
            finally
            {
                license.EncryptSecretWithDebugKey();
            }
        }

        throw new InvalidDataException(Loc.T("Ps4.Dlc.KeyMissing"));
    }

    private static string ValidateKey(string key)
    {
        key = key.Trim();
        if (!Regex.IsMatch(key, "^[0-9a-fA-F]{32}$"))
        {
            throw new InvalidDataException(Loc.T("Ps4.Dlc.KeyFormat"));
        }

        return key.ToLowerInvariant();
    }
}
