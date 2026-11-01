using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using LibOrbisPkg.SFO;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Ps4;

/// <summary>GP4 đã dựng: đường dẫn, thư mục image, CONTENT_ID, phiên bản, CATEGORY, tổng dung lượng và danh sách tệp.</summary>
public sealed record Ps4PreparedProject(
    string Path,
    string ImageRoot,
    string ContentId,
    string Version,
    string Category,
    long TotalBytes,
    IReadOnlyList<Ps4BuildFile> Files,
    string MasterVersion);

/// <summary>
/// Dựng GP4 đúng loại (base / update / DLC / gộp) từ thư mục game như bản Studio: param.sfo dùng bản sao riêng chỉ sửa CATEGORY /
/// APP_VER (VERSION) / CONTENT_ID; metadata do engine tự tạo bị loại; PlayGo giữ theo GP4 hoặc manifest của nguồn; nén PFS bật cho
/// mọi tệp. Nguồn không bị sửa.
/// </summary>
public static class Ps4ProjectBuilder
{
    private const string ContentIdPattern = @"^[A-Z]{2}[0-9]{4}-[A-Z0-9]{9}_[0-9]{2}-[A-Z0-9_]{16}$";

    internal static void ValidatePasscode(string value, string label)
    {
        if (!Regex.IsMatch(value, @"^[0-9A-Za-z_-]{32}$"))
        {
            throw new ArgumentException(Loc.F("Ps4.Project.BadPasscode", label));
        }
    }

    /// <summary>Thư mục image (có eboot.bin hoặc sce_sys/param.sfo), hoặc image0 trong thư mục đã xả. Tên thư mục không quyết định loại gói.</summary>
    public static string ResolveImage(string selected)
    {
        var root = Path.GetFullPath(selected);
        if (File.Exists(root))
        {
            // Đóng base / update / DLC đi từ thư mục đã xả (như Studio); PKG chỉ dùng được ở chế độ Gộp.
            throw new InvalidDataException(Loc.F("Ps4.Project.SourceIsPackage", root));
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(Loc.F("Ps4.Project.SourceMissing", root));
        }

        if (File.Exists(Path.Combine(root, "eboot.bin")) || File.Exists(Path.Combine(root, "sce_sys", "param.sfo")))
        {
            return root;
        }

        var image = Directory.EnumerateDirectories(root).FirstOrDefault(d => Path.GetFileName(d).Equals("image0", StringComparison.OrdinalIgnoreCase));
        if (image != null && (File.Exists(Path.Combine(image, "eboot.bin")) || File.Exists(Path.Combine(image, "sce_sys", "param.sfo")) || File.Exists(Path.Combine(root, "Sc0", "param.sfo"))))
        {
            return image;
        }

        if (Path.GetFileName(root).Equals("image0", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "Sc0", "param.sfo")))
        {
            return root;
        }

        throw new InvalidDataException(Loc.T("Ps4.Project.NotGameFolder"));
    }

    public static string LocateMetadata(string image, string name)
    {
        var direct = Path.Combine(image, "sce_sys", name);
        if (File.Exists(direct))
        {
            return direct;
        }

        var parent = Path.GetDirectoryName(image)!;
        var sc0 = Directory.EnumerateDirectories(parent).FirstOrDefault(d => Path.GetFileName(d).Equals("Sc0", StringComparison.OrdinalIgnoreCase));
        return sc0 != null ? Path.Combine(sc0, name) : direct;
    }

    public static Ps4DetectedVersion DetectVersion(string selected, Ps4JobKind kind)
    {
        var image = ResolveImage(selected);
        var primaryPath = LocateMetadata(image, "param.sfo");
        var primary = ParamSfo.Deserialize(File.ReadAllBytes(primaryPath));
        static string Value(ParamSfo sfo, string key) => sfo[key]?.ToString() ?? string.Empty;
        var chosenPath = primaryPath;
        var chosen = primary;
        if (kind is Ps4JobKind.Patch or Ps4JobKind.Merge && (Value(primary, "CATEGORY") != "gp" || !Ps4AppVersion.TryParse(Value(primary, "APP_VER"), out _)))
        {
            // PFS bên trong có thể mang SFO của base; Sc0 là metadata ngoài của gói đã xả.
            var parent = Path.GetDirectoryName(image)!;
            var sc0 = Directory.EnumerateDirectories(parent).FirstOrDefault(d => Path.GetFileName(d).Equals("Sc0", StringComparison.OrdinalIgnoreCase));
            var outerPath = sc0 == null ? string.Empty : Path.Combine(sc0, "param.sfo");
            if (File.Exists(outerPath) && !outerPath.Equals(primaryPath, StringComparison.OrdinalIgnoreCase))
            {
                var outer = ParamSfo.Deserialize(File.ReadAllBytes(outerPath));
                if (Value(outer, "CATEGORY") == "gp" && !string.IsNullOrEmpty(Value(primary, "CONTENT_ID")) &&
                    Value(outer, "CONTENT_ID") == Value(primary, "CONTENT_ID") && Value(outer, "TITLE_ID") == Value(primary, "TITLE_ID") &&
                    Ps4AppVersion.TryParse(Value(outer, "APP_VER"), out _))
                {
                    chosenPath = outerPath;
                    chosen = outer;
                }
            }
        }

        var versionKey = kind == Ps4JobKind.Dlc ? "VERSION" : "APP_VER";
        var version = Ps4AppVersion.Normalize(Value(chosen, versionKey));
        var note = chosenPath.Equals(primaryPath, StringComparison.OrdinalIgnoreCase)
            ? Loc.F("Ps4.Project.VersionFrom", versionKey, version, chosenPath)
            : Loc.F("Ps4.Project.VersionFromOuter", version, chosenPath, Value(primary, "CATEGORY"), Value(primary, "APP_VER"));
        return new Ps4DetectedVersion(version, chosenPath, note);
    }

    /// <summary>TITLE, TITLE_ID, CONTENT_ID, APP_VER, CATEGORY của thư mục game (APP_VER theo cách nhận phiên bản ở trên).</summary>
    public static Dictionary<string, string> InspectFolder(string selected, Ps4JobKind? kind = null)
    {
        var image = ResolveImage(selected);
        var sfo = ParamSfo.Deserialize(File.ReadAllBytes(LocateMetadata(image, "param.sfo")));
        var result = new[] { "TITLE", "TITLE_ID", "CONTENT_ID", "APP_VER", "CATEGORY" }.ToDictionary(k => k, k => sfo[k]?.ToString() ?? string.Empty);
        var detected = DetectVersion(selected, kind ?? Ps4JobKind.Base);
        result["APP_VER"] = detected.Version;
        result["VERSION_SOURCE"] = detected.SfoPath;
        result["VERSION_NOTE"] = detected.Note;
        return result;
    }

    /// <summary>Tệp không đưa vào GP4: dự án/báo cáo cũ, metadata engine tự tạo hoặc từ chối, keystone của update.</summary>
    internal static bool Excluded(string target, Ps4JobKind kind)
    {
        var lower = target.Replace('\\', '/').ToLowerInvariant();
        var parts = lower.Split('/');
        if (lower.EndsWith(".gp4", StringComparison.Ordinal) || lower is "extraction_manifest.tsv" or "extraction_manifest.json" or "sce_discmap.plt" or "sce_discmap_patch.plt")
        {
            return true;
        }

        if (parts[0] is ".pkgstudio" or "metadata_inner_original")
        {
            return true;
        }

        if (!lower.StartsWith("sce_sys/", StringComparison.Ordinal))
        {
            return false;
        }

        if (parts.Length > 1 && (parts[1].StartsWith('.') || parts[1] is "app" or "about" or "_unknown"))
        {
            return true;
        }

        if (lower.EndsWith(".encrypted", StringComparison.Ordinal) || lower.EndsWith(".dds", StringComparison.Ordinal))
        {
            return true;
        }

        // Engine tự tạo các mục này và từ chối GP4 có chúng ("cannot add origin-deltainfo.dat"). Update retail có kèm
        // origin-deltainfo.dat nên update đã xả mang nó theo.
        if (lower is "sce_sys/playgo-chunk.dat" or "sce_sys/playgo-chunk.sha" or "sce_sys/playgo-manifest.xml" or "sce_sys/psreserved.dat" or "sce_sys/license.dat" or "sce_sys/license.info"
            or "sce_sys/origin-deltainfo.dat" or "sce_sys/target-deltainfo.dat" or "sce_sys/pubtoolinfo.dat" or "sce_sys/selfinfo.dat" or "sce_sys/imageinfo.dat")
        {
            return true;
        }

        return kind == Ps4JobKind.Patch && lower == "sce_sys/keystone";
    }

    private static XDocument ReadXml(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    /// <summary>Tệp của image (và Sc0 → sce_sys/), đã lọc theo <see cref="Excluded"/>; khoá là đường dẫn trong gói.</summary>
    internal static SortedDictionary<string, string> CollectPaths(string image, Ps4JobKind kind, CancellationToken token)
    {
        var paths = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Ps4Files.Enumerate(image))
        {
            token.ThrowIfCancellationRequested();
            var target = Path.GetRelativePath(image, path).Replace('\\', '/');
            if (!Excluded(target, kind))
            {
                paths.Add(target, path);
            }
        }

        var parent = Path.GetDirectoryName(image)!;
        var sc0 = Directory.EnumerateDirectories(parent).FirstOrDefault(d => Path.GetFileName(d).Equals("Sc0", StringComparison.OrdinalIgnoreCase));
        if (sc0 != null)
        {
            foreach (var path in Ps4Files.Enumerate(sc0))
            {
                token.ThrowIfCancellationRequested();
                var target = "sce_sys/" + Path.GetRelativePath(sc0, path).Replace('\\', '/');
                if (!Excluded(target, kind))
                {
                    paths.TryAdd(target, path);
                }
            }
        }

        return paths;
    }

    internal static Ps4PreparedProject Prepare(
        Ps4JobRequest request,
        Ps4JobContext job,
        SortedDictionary<string, string>? overlay = null,
        string? sfoOverride = null,
        bool merged = false,
        string? masterVersionOverride = null)
    {
        if (request.Kind == Ps4JobKind.Extract)
        {
            throw new ArgumentException(Loc.T("Ps4.Project.PickKind"));
        }

        ValidatePasscode(request.Passcode, Loc.T("Ps4.Project.PasscodeLabel"));
        var image = ResolveImage(request.Input);
        if (Ps4Files.IsInside(request.Output, image))
        {
            throw new IOException(Loc.T("Ps4.Project.OutputInsideSource"));
        }

        var sfoPath = sfoOverride ?? LocateMetadata(image, "param.sfo");
        if (!File.Exists(sfoPath))
        {
            throw new FileNotFoundException(Loc.T("Ps4.Project.NoSfo"));
        }

        if (!string.IsNullOrWhiteSpace(request.CustomParamSfo))
        {
            // param.sfo người dùng sửa thay cho bản của nguồn (tên, cờ, thuộc tính…); CATEGORY/APP_VER vẫn được đặt ở dưới.
            var custom = Path.GetFullPath(request.CustomParamSfo.Trim().Trim('"'));
            if (!File.Exists(custom))
            {
                throw new FileNotFoundException(Loc.F("Ps4.Project.CustomSfoMissing", custom));
            }

            ParamSfo customSfo;
            try
            {
                customSfo = ParamSfo.Deserialize(File.ReadAllBytes(custom));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException(Loc.F("Ps4.Project.CustomSfoInvalid", ex.Message));
            }

            var sourceId = ParamSfo.Deserialize(File.ReadAllBytes(sfoPath))["CONTENT_ID"]?.ToString() ?? string.Empty;
            var customId = customSfo["CONTENT_ID"]?.ToString() ?? string.Empty;
            if (sourceId.Length > 0 && customId != sourceId)
            {
                throw new InvalidDataException(Loc.F("Ps4.Project.CustomSfoOtherId", customId, sourceId));
            }

            sfoPath = custom;
            job.Log(Loc.F("Ps4.Project.CustomSfoUsed", custom), Ps4LogLevel.Ok);
        }

        var originalSfo = File.ReadAllBytes(sfoPath);
        var sfo = ParamSfo.Deserialize(originalSfo);
        var contentId = sfo["CONTENT_ID"]?.ToString() ?? string.Empty;
        if (!Regex.IsMatch(contentId, ContentIdPattern))
        {
            throw new InvalidDataException(Loc.F("Ps4.Project.BadContentId", contentId));
        }

        var sourceContentId = contentId;
        string? retargetTitleId = null;
        string? patchBasePasscode = null;
        var category = request.Kind switch { Ps4JobKind.Base or Ps4JobKind.Merge => "gd", Ps4JobKind.Dlc => "ac", _ => "gp" };
        if (request.Kind == Ps4JobKind.Dlc && sfo["CATEGORY"]?.ToString() != "ac")
        {
            throw new InvalidDataException(Loc.T("Ps4.Project.NotDlc"));
        }

        var version = Ps4AppVersion.Normalize(string.IsNullOrWhiteSpace(request.Version) ? DetectVersion(image, request.Kind).Version : request.Version);
        if (request.Kind == Ps4JobKind.Patch)
        {
            if (!File.Exists(request.BasePackage))
            {
                throw new FileNotFoundException(Loc.T("Ps4.Project.PickBasePkg"));
            }

            var basePasscode = string.IsNullOrWhiteSpace(request.BasePasscode) ? Ps4Defaults.Passcode : request.BasePasscode;
            ValidatePasscode(basePasscode, Loc.T("Ps4.Project.BasePasscodeLabel"));
            patchBasePasscode = basePasscode;
            var baseInfo = Ps4PackageReader.Inspect(request.BasePackage, basePasscode);
            if (baseInfo.Category is not ("gd" or "gda"))
            {
                throw new InvalidDataException(Loc.T("Ps4.Project.RefNotBase"));
            }

            if (baseInfo.ContentId != contentId)
            {
                // Base khác vùng / khác CUSA: đổi update sang danh tính của base để cài lên được base đó.
                if (!Regex.IsMatch(baseInfo.ContentId, ContentIdPattern))
                {
                    throw new InvalidDataException(Loc.F("Ps4.Project.BaseBadContentId", baseInfo.ContentId));
                }

                retargetTitleId = Regex.IsMatch(baseInfo.TitleId, "^[A-Z]{4}[0-9]{5}$") ? baseInfo.TitleId : baseInfo.ContentId.Substring(7, 9);
                job.Log(Loc.F("Ps4.Project.Retarget", baseInfo.ContentId, contentId, retargetTitleId), Ps4LogLevel.Warn);
                contentId = baseInfo.ContentId;
            }

            if (!baseInfo.PasscodeMatches && !baseInfo.RecoverableFakePackage)
            {
                throw new InvalidDataException(Loc.T("Ps4.Project.BaseLocked"));
            }

            if (string.CompareOrdinal(version, baseInfo.Version) <= 0)
            {
                job.Log(Loc.F("Ps4.Project.UpdateNotHigher", version, baseInfo.Version), Ps4LogLevel.Warn);
            }

            job.Log(Loc.F(baseInfo.PasscodeMatches ? "Ps4.Project.BaseOk" : "Ps4.Project.BaseOkRecovered", baseInfo.ContentId, baseInfo.Version), Ps4LogLevel.Ok);
        }

        var stage = Path.Combine(job.Folder, "metadata");
        Directory.CreateDirectory(stage);
        File.WriteAllBytes(Path.Combine(stage, "param.original.sfo"), originalSfo);

        // Chỉ sửa vài chuỗi cố định; giữ mọi byte khác của nguồn và metadata riêng.
        var patchedSfo = (byte[])originalSfo.Clone();
        PatchSfoString(patchedSfo, "CATEGORY", category);
        if (retargetTitleId != null)
        {
            PatchSfoString(patchedSfo, "CONTENT_ID", contentId);
            PatchSfoString(patchedSfo, "TITLE_ID", retargetTitleId);
        }

        PatchSfoString(patchedSfo, request.Kind == Ps4JobKind.Dlc ? "VERSION" : "APP_VER", version);
        if (masterVersionOverride != null)
        {
            PatchSfoString(patchedSfo, "VERSION", masterVersionOverride);
        }

        var stagedSfo = Path.Combine(stage, "param.sfo");
        File.WriteAllBytes(stagedSfo, patchedSfo);
        var paths = overlay ?? CollectPaths(image, request.Kind, job.Token);
        paths["sce_sys/param.sfo"] = stagedSfo;
        var credit = Path.Combine(stage, "PSVIETHOA.COM.txt");
        File.WriteAllText(credit, "Bản quyền Việt hóa: Ngô Phi Phương x PSVIETHOA.COM\r\nhttps://psviethoa.com/\r\n", new UTF8Encoding(false));
        paths["PSVIETHOA.COM.txt"] = credit;
        if (request.Kind == Ps4JobKind.Patch && patchBasePasscode != null)
        {
            // Engine từ chối update có NP title khác base ("NP Title ID mismatch"): mượn của base khi update đổi CUSA hoặc thiếu tệp.
            foreach (var name in new[] { "nptitle.dat", "npbind.dat" })
            {
                var target = "sce_sys/" + name;
                var reason = retargetTitleId != null ? Loc.T("Ps4.Project.ReasonOtherCusa") : paths.ContainsKey(target) ? null : Loc.T("Ps4.Project.ReasonMissing");
                if (reason == null)
                {
                    continue;
                }

                var data = Ps4PackageReader.ReadMeta(request.BasePackage, name, patchBasePasscode);
                if (data == null)
                {
                    job.Log(Loc.F("Ps4.Project.BaseMetaUnreadable", name, reason), name == "nptitle.dat" ? Ps4LogLevel.Warn : Ps4LogLevel.Info);
                    continue;
                }

                var staged = Path.Combine(stage, name);
                File.WriteAllBytes(staged, data);
                paths[target] = staged;
                job.Log(Loc.F("Ps4.Project.BaseMetaUsed", name, reason), Ps4LogLevel.Ok);
            }
        }

        // Update có thể không kèm icon0.png: PS4 lấy ảnh từ base đã cài. Base/DLC vẫn cần icon riêng. Không tự tạo hay chép ảnh vào update.
        var requiredFiles = request.Kind switch
        {
            Ps4JobKind.Dlc => new[] { "sce_sys/param.sfo", "sce_sys/icon0.png" },
            Ps4JobKind.Patch => new[] { "sce_sys/param.sfo" },
            _ => new[] { "eboot.bin", "sce_sys/param.sfo", "sce_sys/icon0.png", "sce_sys/nptitle.dat", "sce_sys/npbind.dat" },
        };
        foreach (var required in requiredFiles)
        {
            if (!paths.ContainsKey(required))
            {
                throw new FileNotFoundException(Loc.F("Ps4.Project.RequiredMissing", required));
            }
        }

        if (request.Kind == Ps4JobKind.Patch && !paths.ContainsKey("eboot.bin"))
        {
            job.Log(Loc.T("Ps4.Project.UpdateNoEboot"));
        }

        if (request.Kind == Ps4JobKind.Base && !paths.ContainsKey("sce_sys/keystone"))
        {
            throw new FileNotFoundException(Loc.T("Ps4.Project.BaseNoKeystone"));
        }

        var records = paths.Select(pair =>
        {
            var info = new FileInfo(pair.Value);
            return new Ps4BuildFile(pair.Key, pair.Value, info.Length, info.LastWriteTimeUtc.Ticks);
        }).ToArray();
        var total = records.Sum(f => f.Bytes);
        if (!request.ProjectOnly)
        {
            Ps4Files.CheckSpace(request.Output, checked((long)(total * 2.1) + 512L * 1024 * 1024));
        }

        var (chunks, fileOptions) = request.Kind == Ps4JobKind.Dlc
            ? (null as XElement, new Dictionary<string, XAttribute[]>(StringComparer.OrdinalIgnoreCase))
            : ReadPlayGo(image, sourceContentId, job, merged ? Path.Combine(Path.GetDirectoryName(sfoPath)!, "playgo-manifest.xml") : null);
        var package = new XElement("package", new XAttribute("content_id", contentId), new XAttribute("passcode", request.Passcode));
        if (request.Kind == Ps4JobKind.Dlc)
        {
            var entitlementKey = Ps4DlcMetadata.ResolveKey(image, contentId, request.EntitlementKey);
            package.Add(new XAttribute("entitlement_key", entitlementKey));
            job.Log(Loc.T("Ps4.Project.DlcKeyKept"));
        }
        else
        {
            package.Add(new XAttribute("storage_type", request.Kind == Ps4JobKind.Patch ? "digital25" : "digital50"));
            package.Add(new XAttribute("app_type", "full"));
        }

        if (request.Kind == Ps4JobKind.Patch)
        {
            package.Add(new XAttribute("app_path", job.ToolPath(request.BasePackage)));
        }

        var fileNodes = new XElement("files", new XAttribute("img_no", "0"));
        var directories = new XElement("rootdir");
        var dirMap = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in records)
        {
            var node = new XElement("file", new XAttribute("targ_path", entry.Target), new XAttribute("orig_path", job.ToolPath(entry.Source)));
            if (fileOptions.TryGetValue(entry.Target, out var attributes))
            {
                foreach (var attribute in attributes)
                {
                    node.Add(new XAttribute(attribute));
                }
            }

            // Gói retail dùng PFS nén; không có thì PKG to bằng cây tệp thô (update Ninja Gaiden: 1485 MiB thay vì 876 MiB). Engine
            // không có cờ img_create cho việc này nên phải là thuộc tính từng tệp trong GP4. GP4 nguồn cạnh image giữ giá trị riêng.
            if (node.Attribute("pfs_compression") == null)
            {
                node.Add(new XAttribute("pfs_compression", "enable"));
            }

            fileNodes.Add(node);
            var parts = entry.Target.Split('/');
            var current = directories;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var key = string.Join('/', parts.Take(i + 1));
                if (!dirMap.TryGetValue(key, out var dir))
                {
                    dir = new XElement("dir", new XAttribute("targ_name", parts[i]));
                    current.Add(dir);
                    dirMap[key] = dir;
                }

                current = dir;
            }
        }

        var volumeType = request.Kind switch { Ps4JobKind.Base or Ps4JobKind.Merge => "pkg_ps4_app", Ps4JobKind.Dlc => "pkg_ps4_ac_data", _ => "pkg_ps4_patch" };
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("psproject",
            new XAttribute("fmt", "gp4"),
            new XAttribute("version", "0990"),
            new XElement("volume",
                new XElement("volume_type", volumeType),
                new XElement("volume_ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                package,
                chunks),
            fileNodes,
            directories));
        var project = Path.Combine(job.Folder, merged ? "Merged.gp4" : request.Kind == Ps4JobKind.Base ? "Base.gp4" : request.Kind == Ps4JobKind.Dlc ? "DLC.gp4" : "Update.gp4");
        document.Save(project);
        job.Save("source_files.json", records);
        job.Log(Loc.F("Ps4.Project.Gp4Ready", request.Kind.ToString().ToUpperInvariant(), records.Length, (total / 1e9).ToString("F2", CultureInfo.InvariantCulture)));
        job.Log(Loc.T("Ps4.Project.SourceKept"));
        job.Log(Loc.T("Ps4.Project.Compression"));
        return new Ps4PreparedProject(project, image, contentId, version, category, total, records,
            request.Kind == Ps4JobKind.Dlc ? version : masterVersionOverride ?? sfo["VERSION"]?.ToString() ?? string.Empty);
    }

    /// <summary>Sửa một chuỗi param.sfo tại chỗ (không đổi kích thước tệp).</summary>
    internal static void PatchSfoString(byte[] bytes, string key, string value)
    {
        int keys = BitConverter.ToInt32(bytes, 8);
        int values = BitConverter.ToInt32(bytes, 12);
        int count = BitConverter.ToInt32(bytes, 16);
        for (var i = 0; i < count; i++)
        {
            var entry = 20 + i * 16;
            var k = keys + BitConverter.ToUInt16(bytes, entry);
            var end = Array.IndexOf(bytes, (byte)0, k);
            if (end < k)
            {
                throw new InvalidDataException(Loc.T("Ps4.Project.SfoKeyEnd"));
            }

            if (Encoding.UTF8.GetString(bytes, k, end - k) != key)
            {
                continue;
            }

            var capacity = BitConverter.ToInt32(bytes, entry + 8);
            var offset = values + BitConverter.ToInt32(bytes, entry + 12);
            var encoded = Encoding.UTF8.GetBytes(value + "\0");
            if (encoded.Length > capacity || offset < 0 || (long)offset + capacity > bytes.Length)
            {
                throw new InvalidDataException(Loc.F("Ps4.Project.SfoFieldFull", key));
            }

            Array.Clear(bytes, offset, capacity);
            encoded.CopyTo(bytes, offset);
            BitConverter.GetBytes(encoded.Length).CopyTo(bytes, entry + 4);
            return;
        }

        throw new InvalidDataException(Loc.F("Ps4.Project.SfoFieldMissing", key));
    }

    /// <summary>Cấu hình PlayGo: GP4 cũ cùng CONTENT_ID cạnh image, hoặc số chunk của playgo-manifest.xml (mặc định 1).</summary>
    private static (XElement, Dictionary<string, XAttribute[]>) ReadPlayGo(string image, string contentId, Ps4JobContext job, string? preferredManifest = null)
    {
        var options = new Dictionary<string, XAttribute[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var gp4 in preferredManifest == null ? Directory.EnumerateFiles(image, "*.gp4") : [])
        {
            try
            {
                var document = ReadXml(gp4);
                var volume = document.Root?.Element("volume");
                if ((string?)volume?.Element("package")?.Attribute("content_id") != contentId)
                {
                    continue;
                }

                var chunks = volume?.Element("chunk_info");
                if (chunks?.Element("chunks") == null)
                {
                    continue;
                }

                foreach (var file in document.Root!.Element("files")?.Elements("file") ?? [])
                {
                    var target = (string?)file.Attribute("targ_path");
                    if (target != null)
                    {
                        options[target.Replace('\\', '/')] = file.Attributes()
                            .Where(a => a.Name.LocalName is "chunks" or "pfs_compression" or "align" or "layer_no")
                            .Select(a => new XAttribute(a))
                            .ToArray();
                    }
                }

                job.Log(Loc.F("Ps4.Project.PlayGoFromGp4", Path.GetFileName(gp4)));
                return (new XElement(chunks), options);
            }
            catch (Exception ex)
            {
                job.Log(Loc.F("Ps4.Project.OldGp4Skipped", ex.Message));
            }
        }

        var count = 1;
        var manifest = preferredManifest != null && File.Exists(preferredManifest) ? preferredManifest : LocateMetadata(image, "playgo-manifest.xml");
        if (File.Exists(manifest))
        {
            try
            {
                count = (int?)ReadXml(manifest).Root?.Element("volume")?.Element("chunk_info")?.Attribute("chunk_count") ?? 1;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(Loc.F("Ps4.Project.PlayGoBroken", ex.Message), ex);
            }
        }

        if (count is < 1 or > 8192)
        {
            throw new InvalidDataException(Loc.T("Ps4.Project.PlayGoBadCount"));
        }

        var info = new XElement("chunk_info", new XAttribute("chunk_count", count), new XAttribute("scenario_count", 1),
            new XElement("chunks", Enumerable.Range(0, count).Select(i => new XElement("chunk", new XAttribute("id", i), new XAttribute("label", "Chunk #" + i)))),
            new XElement("scenarios", new XAttribute("default_id", 0), new XElement("scenario", new XAttribute("id", 0), new XAttribute("type", "sp"),
                new XAttribute("initial_chunk_count", count), new XAttribute("label", "Scenario #0"), count == 1 ? "0" : $"0-{count - 1}")));
        job.Log(Loc.F("Ps4.Project.PlayGoChunks", count));
        return (info, options);
    }
}
