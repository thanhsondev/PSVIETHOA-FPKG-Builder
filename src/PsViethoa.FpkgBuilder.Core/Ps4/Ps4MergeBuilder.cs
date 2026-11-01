using LibOrbisPkg.SFO;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Ps4;

/// <summary>
/// Gộp base + update thành một PKG base đầy đủ theo cách PS4-AiO: tệp update thay tệp base cùng đường dẫn, keystone giữ của base,
/// param.sfo lấy của update với CATEGORY=gd, APP_VER=01.00 và VERSION = bản update. Nguồn là PKG thì xả vào thư mục tác vụ trước.
/// </summary>
public static class Ps4MergeBuilder
{
    public static Ps4PackageInfo InspectInput(string input, bool update, string passcode = Ps4Defaults.Passcode)
    {
        if (File.Exists(input))
        {
            return Ps4PackageReader.Inspect(input, passcode);
        }

        var image = Ps4ProjectBuilder.ResolveImage(input);
        var detected = Ps4ProjectBuilder.DetectVersion(image, update ? Ps4JobKind.Patch : Ps4JobKind.Base);
        var sfo = ParamSfo.Deserialize(File.ReadAllBytes(detected.SfoPath));
        string Value(string key) => sfo[key]?.ToString() ?? string.Empty;
        return new Ps4PackageInfo(image, Value("TITLE"), Value("TITLE_ID"), Value("CONTENT_ID"), detected.Version, Value("CATEGORY"), 0, true, false, Value("VERSION"));
    }

    internal static Ps4PreparedProject Prepare(Ps4JobRequest request, Ps4JobContext job)
    {
        Ps4ProjectBuilder.ValidatePasscode(request.Passcode, Loc.T("Ps4.Merge.PasscodeLabel"));
        if (string.IsNullOrWhiteSpace(request.BasePackage))
        {
            throw new ArgumentException(Loc.T("Ps4.Merge.PickBase"));
        }

        var baseInfo = InspectInput(request.BasePackage, false, request.Passcode);
        var updateInfo = InspectInput(request.Input, true, request.Passcode);
        if (baseInfo.Path.Equals(updateInfo.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Loc.T("Ps4.Merge.SameSource"));
        }

        if (baseInfo.Category is not ("gd" or "gda"))
        {
            throw new InvalidDataException(Loc.T("Ps4.Merge.BaseNotBase"));
        }

        if (updateInfo.Category != "gp")
        {
            throw new InvalidDataException(Loc.T("Ps4.Merge.UpdateNotUpdate"));
        }

        if (baseInfo.ContentId != updateInfo.ContentId || baseInfo.TitleId != updateInfo.TitleId)
        {
            throw new InvalidDataException(Loc.F("Ps4.Merge.OtherGame", baseInfo.ContentId, updateInfo.ContentId));
        }

        if (string.CompareOrdinal(updateInfo.Version, baseInfo.Version) < 0)
        {
            throw new InvalidDataException(Loc.F("Ps4.Merge.UpdateLower", updateInfo.Version, baseInfo.Version));
        }

        foreach (var source in new[] { baseInfo, updateInfo })
        {
            if (Directory.Exists(source.Path) && Ps4Files.IsInside(request.Output, source.Path))
            {
                throw new IOException(Loc.T("Ps4.Merge.OutputInside"));
            }
        }

        var version = Ps4AppVersion.Normalize(string.IsNullOrWhiteSpace(request.Version) ? updateInfo.Version : request.Version);
        job.Save("merge_inputs.json", new
        {
            base_source = baseInfo,
            update_source = updateInfo,
            output_version = version,
            rule = "Update replaces matching paths; preserve base keystone; output is standalone base PKG.",
        });
        job.Log(Loc.F("Ps4.Merge.Start", baseInfo.TitleId, baseInfo.Version, updateInfo.Version, version), Ps4LogLevel.Ok);
        job.Log(Loc.T("Ps4.Merge.Rule"));

        string ExtractInput(string input, string label, double start, double end, ISet<string>? skip = null)
        {
            if (Directory.Exists(input))
            {
                return Ps4ProjectBuilder.ResolveImage(input);
            }

            job.Token.ThrowIfCancellationRequested();
            var phasePrefix = Loc.T(label == "update" ? "Ps4.Merge.ExtractUpdate" : "Ps4.Merge.ExtractBase");
            using var child = new Ps4JobContext(Path.Combine(job.Folder, "inputs"), label, job.Token,
                (line, level) => job.Log($"[{label.ToUpperInvariant()}] {line}", level),
                p => job.Progress(p with { Phase = $"{phasePrefix}: {p.Phase}", Percent = start + (end - start) * p.Percent / 100 }));
            var result = Ps4PackageReader.Extract(new Ps4JobRequest(Ps4JobKind.Extract, input, child.Folder, Passcode: request.Passcode, Verify: false), child, skip);
            child.Save("result.json", result);
            return Path.Combine(child.Folder, "image0");
        }

        var fromPackages = File.Exists(request.Input) || File.Exists(request.BasePackage);
        var updateImage = ExtractInput(request.Input, "update", 2, 25);
        var updatePaths = Ps4ProjectBuilder.CollectPaths(updateImage, Ps4JobKind.Patch, job.Token);
        var skipBase = new HashSet<string>(updatePaths.Keys, StringComparer.OrdinalIgnoreCase);
        skipBase.Remove("sce_sys/keystone");
        var baseImage = ExtractInput(request.BasePackage, "base", 25, 49, skipBase);
        var basePaths = Ps4ProjectBuilder.CollectPaths(baseImage, Ps4JobKind.Base, job.Token);
        if (!basePaths.TryGetValue("sce_sys/keystone", out var keystone))
        {
            throw new FileNotFoundException(Loc.T("Ps4.Merge.NoKeystone"));
        }

        var updateSfo = Ps4ProjectBuilder.DetectVersion(updateImage, Ps4JobKind.Patch).SfoPath;
        var paths = new SortedDictionary<string, string>(basePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var item in updatePaths)
        {
            paths[item.Key] = item.Value;
        }

        paths["sce_sys/keystone"] = keystone;
        paths["sce_sys/param.sfo"] = updateSfo;
        foreach (var target in paths.Keys)
        {
            job.Token.ThrowIfCancellationRequested();
            var parent = target;
            while (parent.LastIndexOf('/') is var index && index >= 0)
            {
                parent = parent[..index];
                if (paths.ContainsKey(parent))
                {
                    throw new InvalidDataException(Loc.F("Ps4.Merge.Conflict", parent));
                }
            }
        }

        job.Save("merge_map.json", paths.Select(p => new
        {
            target = p.Key,
            source = p.Value,
            origin = p.Key.Equals("sce_sys/keystone", StringComparison.OrdinalIgnoreCase) ? "base-keystone"
                : p.Key.Equals("sce_sys/param.sfo", StringComparison.OrdinalIgnoreCase) || updatePaths.ContainsKey(p.Key) ? "update" : "base",
        }));
        job.Save("merge_summary.json", new
        {
            content_id = updateInfo.ContentId,
            title_id = updateInfo.TitleId,
            base_version = baseInfo.Version,
            update_version = updateInfo.Version,
            merged_version = version,
            output_app_ver = "01.00",
            update_files = updatePaths.Count,
            base_files_retained = paths.Keys.Count(k => !updatePaths.ContainsKey(k)),
            total_files = paths.Count,
            base_keystone_sha256 = Ps4Files.Hash(keystone),
            update_metadata = updateSfo,
            source_folders_copied = false,
            source_packages_retained = true,
            output_type = "pkg_ps4_app",
        });
        job.Log(Loc.F("Ps4.Merge.MapReady", paths.Count));
        if (fromPackages)
        {
            job.BuildProgressStart = 50;
        }

        var buildRequest = request with { Kind = Ps4JobKind.Base, Input = updateImage, Version = "01.00" };
        return Ps4ProjectBuilder.Prepare(buildRequest, job, paths, updateSfo, true, version);
    }
}
