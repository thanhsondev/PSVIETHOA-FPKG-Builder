using System.Buffers.Binary;
using PsViethoa.FpkgBuilder.Core.Localization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace PsViethoa.FpkgBuilder.Core.Zstd;

/// <summary>
/// Vá ZSTD: thay / thêm file (vd. Việt hoá) vào game đã đóng ZSTD mà không giải cả game.
/// Runtime ampr-emu-ng đòi các khối nằm liền nhau theo thứ tự băm và luôn đọc file trong gói trước file rời, nên mỗi file đi một trong 4 đường:
///  • Rời (đã rời / file mới): chép đè hoặc thêm, ghi lại ampr_emu.index.
///  • Tại chỗ: cùng số khối, mỗi khối mới vừa chỗ khối cũ — thô nếu đúng cỡ, hoặc frame ZSTD "độn" cho bằng đúng cỡ cũ (block rỗng 3 byte,
///    trường content size 4/8 byte, window descriptor) — ghi đè đúng vị trí, không dời khối nào.
///  • Viết lại đuôi: từ file đầu tiên không vừa chỗ trở đi, khối của file không đổi được chép nguyên byte (không giải / nén lại),
///    file mới được nén; volume phía trước giữ nguyên.
/// Mọi thứ bị thay được giữ trong thư mục hoàn tác cạnh game (&lt;game&gt;_zstd_undo_&lt;thời gian&gt;).
/// </summary>
public static partial class AmprZstd
{
    public enum PatchKind { LooseReplace, LooseNew, InPlace, Rewrite }
    public sealed record PatchItem(string Path, string Source, long Size, PatchKind Kind, string Note);
    public sealed record PatchPlan(Info Info, string PatchDir, int Level, IReadOnlyList<PatchItem> Items, int RewriteFromFile, long RewriteCopyBytes, IReadOnlyList<string> Skipped)
    {
        public int Count(PatchKind k) => Items.Count(i => i.Kind == k);
    }
    public sealed record PatchResult(string Folder, string UndoDir, int Files, int InPlace, int Rewritten, int Loose, long CopiedBytes, TimeSpan Elapsed);

    private const string UndoManifest = "undo.json";
    private static readonly int[] FitLevels = [19, 22];

    // ───── frame ZSTD "độn" đúng cỡ ─────

    private static int FrameHeaderLen(ReadOnlySpan<byte> f)
    {
        byte fhd = f[4];
        bool single = (fhd & 0x20) != 0;
        int n = 5 + (single ? 0 : 1);
        n += (fhd >> 6) switch { 0 => single ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        return n;
    }

    /// <summary>Viết lại <paramref name="frame"/> thành frame hợp lệ dài đúng <paramref name="dst"/>.Length: header (single-segment hoặc window 256 KiB,
    /// content size 4 hoặc 8 byte, checksum) + block raw rỗng (3 byte mỗi block) + các block cũ. False nếu không độn được đúng cỡ.</summary>
    private static bool PadFrame(ReadOnlySpan<byte> frame, int contentLen, Span<byte> dst)
    {
        var body = frame[FrameHeaderLen(frame)..];
        foreach (var (fcs8, wd) in new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            int h = 5 + (wd ? 1 : 0) + (fcs8 ? 8 : 4);
            int extra = dst.Length - h - body.Length;
            if (extra < 0 || extra % 3 != 0) continue;
            dst.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(dst, 0xFD2FB528u);
            dst[4] = (byte)(0x04 | (fcs8 ? 0xC0 : 0x80) | (wd ? 0 : 0x20));
            int at = 5;
            if (wd) dst[at++] = 8 << 3;                       // cửa sổ 2^(10+8) = 256 KiB
            if (fcs8) { BinaryPrimitives.WriteUInt64LittleEndian(dst[at..], (ulong)contentLen); at += 8; }
            else { BinaryPrimitives.WriteUInt32LittleEndian(dst[at..], (uint)contentLen); at += 4; }
            at += extra;                                        // block raw rỗng, không phải block cuối: header 3 byte = 0
            body.CopyTo(dst[at..]);
            return true;
        }
        return false;
    }

    /// <summary>Mã hoá khối <paramref name="raw"/> để chiếm đúng <paramref name="target"/> byte (thô nếu bằng cỡ, hoặc frame độn). Null nếu không vừa.</summary>
    private static byte[]? FitChunk(ReadOnlySpan<byte> raw, int target, Func<int, Compressor> compressor, out bool zstd)
    {
        zstd = false;
        if (target == raw.Length) return raw.ToArray();
        if (target > raw.Length || raw.Length <= 1) return null;
        var tmp = new byte[raw.Length];
        foreach (var lv in FitLevels)
        {
            if (!compressor(lv).TryWrap(raw, tmp, out int n) || n >= raw.Length) continue;
            if (n > target) continue;
            var outp = new byte[target];
            if (PadFrame(tmp.AsSpan(0, n), raw.Length, outp)) { zstd = true; return outp; }
        }
        return null;
    }

    // ───── lập kế hoạch ─────

    /// <summary>Đọc thư mục vá (cùng cấu trúc game) và quyết định đường đi của từng file. Nén thử các file trong gói để biết có vừa chỗ không.</summary>
    public static PatchPlan PlanPatch(Info info, string patchDir, int level, CancellationToken ct, IProgress<Progress>? progress = null)
    {
        if (info.Index == null) throw new InvalidOperationException(Loc.T("Zstd.Core.088"));
        if (info.Problem.Length > 0) throw new InvalidDataException(info.Problem);
        patchDir = Path.GetFullPath(patchDir).TrimEnd('\\', '/');
        if (!Directory.Exists(patchDir)) throw new DirectoryNotFoundException(patchDir);
        if (string.Equals(patchDir, info.Folder, StringComparison.OrdinalIgnoreCase)) throw new IOException(Loc.T("Zstd.Core.094"));
        var idx = info.Index;
        var packed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < idx.Files.Count; i++) packed[idx.Files[i].Path] = i;
        var loose = info.Loose.ToDictionary(e => e.Path, e => e, StringComparer.OrdinalIgnoreCase);
        var items = new List<PatchItem>(); var skipped = new List<string>();
        var fits = new Dictionary<int, bool>();
        var src = Walk(patchDir);
        long total = src.Sum(f => f.Size), done = 0;
        using var comps = new ThreadLocal<Dictionary<int, Compressor>>(() => [], true);
        Compressor C(int lv) { var d = comps.Value!; return d.TryGetValue(lv, out var c) ? c : d[lv] = NewCompressor(lv); }
        try
        {
            foreach (var f in src)
            {
                ct.ThrowIfCancellationRequested();
                if (IsPackName(f.Rel) || f.Rel.Equals(EmuIndexName, StringComparison.OrdinalIgnoreCase)) { skipped.Add(f.Rel + Loc.T("Zstd.Core.095")); continue; }
                if (!ValidPath(f.Rel)) { skipped.Add(f.Rel + Loc.T("Zstd.Core.096")); continue; }
                if (packed.TryGetValue(f.Rel, out int fi))
                {
                    var rec = idx.Files[fi];
                    bool ok = FitsInPlace(rec, idx, f.Full, f.Size, C, ct, out string why);
                    fits[fi] = ok;
                    items.Add(new PatchItem(rec.Path, f.Full, f.Size, ok ? PatchKind.InPlace : PatchKind.Rewrite, ok ? Loc.T("Zstd.Core.097") : why));
                }
                else if (loose.TryGetValue(f.Rel, out var le)) items.Add(new PatchItem(le.Path, f.Full, f.Size, PatchKind.LooseReplace, Loc.T("Zstd.Core.098")));
                else items.Add(new PatchItem(f.Rel, f.Full, f.Size, PatchKind.LooseNew, Loc.T("Zstd.Core.099")));
                done += f.Size;
                progress?.Report(new Progress(done * 100.0 / Math.Max(1, total), 0, Loc.T("Zstd.Core.100") + f.Rel, done, total));
            }
        }
        finally { foreach (var d in comps.Values) foreach (var c in d.Values) c.Dispose(); }
        // Từ file đầu tiên (theo thứ tự trong gói) không vừa chỗ trở đi phải viết lại; file vá đứng sau nó cũng nén lại trong phần viết lại.
        int from = fits.Where(kv => !kv.Value).Select(kv => kv.Key).DefaultIfEmpty(-1).Min();
        long copy = 0;
        if (from >= 0)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Kind == PatchKind.InPlace && packed[items[i].Path] > from) items[i] = items[i] with { Kind = PatchKind.Rewrite, Note = Loc.T("Zstd.Core.101") };
            var replaced = items.Where(i => i.Kind == PatchKind.Rewrite).Select(i => packed[i.Path]).ToHashSet();
            for (int i = from; i < idx.Files.Count; i++)
                if (!replaced.Contains(i)) { var r = idx.Files[i]; for (int k = 0; k < r.ChunkCount; k++) copy += idx.Chunks[r.FirstChunk + k].Stored; }
        }
        items.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.CompareOrdinal(a.Path, b.Path));
        return new PatchPlan(info, patchDir, level, items, from, copy, skipped);
    }

    private static bool FitsInPlace(FileRec rec, Index idx, string path, long size, Func<int, Compressor> comp, CancellationToken ct, out string why)
    {
        int count = (int)((size + ChunkSize - 1) >> ChunkShift);
        if (count != rec.ChunkCount) { why = Loc.F("Zstd.Core.102", rec.ChunkCount, count); return false; }
        if (count == 0) { why = ""; return true; }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buf = new byte[ChunkSize];
        for (int k = 0; k < count; k++)
        {
            ct.ThrowIfCancellationRequested();
            int len = (int)Math.Min(ChunkSize, size - (long)k * ChunkSize);
            fs.ReadExactly(buf, 0, len);
            var old = idx.Chunks[rec.FirstChunk + k];
            if (FitChunk(buf.AsSpan(0, len), old.Stored, comp, out _) == null)
            { why = Loc.F("Zstd.Core.103", k + 1, count, FormatSize(old.Stored)); return false; }
        }
        why = "";
        return true;
    }

    // ───── thực hiện ─────

    private sealed class UndoInfo
    {
        public string Game { get; set; } = "";
        public string Time { get; set; } = "";
        public int OldVolumeCount { get; set; }
        public int FirstMovedVolume { get; set; } = -1;   // volume cũ từ đây trở đi đã chuyển vào thư mục hoàn tác
        public List<string> LooseReplaced { get; set; } = [];
        public List<string> LooseAdded { get; set; } = [];
        public List<string> DirsCreated { get; set; } = [];
        public int InPlaceChunks { get; set; }
    }

    public static PatchResult Patch(PatchPlan plan, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log = null, int threads = 0)
    {
        var sw = Stopwatch.StartNew();
        var info = Read(plan.Info.Folder);                         // đọc lại: gói có thể đã đổi từ lúc lập kế hoạch
        if (info.Index == null || info.Problem.Length > 0) throw new InvalidDataException(Loc.T("Zstd.Core.104") + info.Problem);
        if (!info.Index.ArchiveId.AsSpan().SequenceEqual(plan.Info.Index!.ArchiveId) || info.Index.Chunks.Count != plan.Info.Index.Chunks.Count)
            throw new IOException(Loc.T("Zstd.Core.105"));
        var idx = info.Index;
        string game = info.Folder;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var undo = Path.Combine(Path.GetDirectoryName(game)!, Path.GetFileName(game) + "_zstd_undo_" + stamp);
        Directory.CreateDirectory(undo);
        var u = new UndoInfo { Game = game, Time = stamp, OldVolumeCount = idx.VolumeSizes.Count };
        void SaveUndo() => File.WriteAllText(Path.Combine(undo, UndoManifest), JsonSerializer.Serialize(u, new JsonSerializerOptions { WriteIndented = true }));
        File.Copy(Path.Combine(game, IndexName), Path.Combine(undo, IndexName));
        if (File.Exists(Path.Combine(game, EmuIndexName))) File.Copy(Path.Combine(game, EmuIndexName), Path.Combine(undo, EmuIndexName));
        SaveUndo();
        log?.Invoke(Loc.F("Zstd.Core.106", undo));

        long total = plan.Items.Sum(i => i.Size) + plan.RewriteCopyBytes, done = 0;
        var last = Stopwatch.StartNew();
        void Report(string d, bool force = false)
        {
            if (!force && last.ElapsedMilliseconds < 200) return;
            last.Restart();
            progress?.Report(new Progress(Math.Min(100, done * 100.0 / Math.Max(1, total)), done / Math.Max(0.001, sw.Elapsed.TotalSeconds), d, done, total));
        }
        var buf = new byte[4 << 20];
        int nLoose = 0, nInPlace = 0, nRewrite = 0;

        // 1. File rời.
        foreach (var it in plan.Items.Where(i => i.Kind is PatchKind.LooseReplace or PatchKind.LooseNew))
        {
            ct.ThrowIfCancellationRequested();
            var dst = Path.Combine(game, it.Path.Replace('/', '\\'));
            if (it.Kind == PatchKind.LooseReplace && File.Exists(dst))
            {
                var keep = Path.Combine(undo, "loose", it.Path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                File.Copy(dst, keep, true);
                u.LooseReplaced.Add(it.Path);
            }
            else u.LooseAdded.Add(it.Path);
            for (var d = Path.GetDirectoryName(dst)!; d.Length > game.Length && !Directory.Exists(d); d = Path.GetDirectoryName(d)!)
                u.DirsCreated.Add(Path.GetRelativePath(game, d));
            SaveUndo();
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(it.Source, dst, true);
            done += it.Size; nLoose++; Report(Loc.T("Zstd.Core.077") + it.Path);
        }

        var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < idx.Files.Count; i++) byPath[idx.Files[i].Path] = i;
        using var comps = new ThreadLocal<Dictionary<int, Compressor>>(() => [], true);
        Compressor C(int lv) { var d = comps.Value!; return d.TryGetValue(lv, out var c) ? c : d[lv] = NewCompressor(lv); }
        try
        {
            // 2. Tại chỗ: lưu byte gốc vào chunks.bin rồi ghi đè.
            var inPlace = plan.Items.Where(i => i.Kind == PatchKind.InPlace).ToList();
            if (inPlace.Count > 0)
            {
                using var undoChunks = new FileStream(Path.Combine(undo, "chunks.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                var handles = new Dictionary<int, FileStream>();
                try
                {
                    var rb = new byte[ChunkSize];
                    foreach (var it in inPlace)
                    {
                        ct.ThrowIfCancellationRequested();
                        int fi = byPath[it.Path];
                        var rec = idx.Files[fi];
                        using var fs = new FileStream(it.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                        for (int k = 0; k < rec.ChunkCount; k++)
                        {
                            int len = (int)Math.Min(ChunkSize, it.Size - (long)k * ChunkSize);
                            fs.ReadExactly(rb, 0, len);
                            var old = idx.Chunks[rec.FirstChunk + k];
                            var enc = FitChunk(rb.AsSpan(0, len), old.Stored, C, out bool z) ?? throw new IOException(Loc.F("Zstd.Core.107", it.Path, k + 1));
                            if (!handles.TryGetValue(old.Volume, out var vf))
                                handles[old.Volume] = vf = new FileStream(Path.Combine(game, VolumeName(old.Volume)), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
                            var orig = new byte[old.Stored];
                            vf.Position = old.Offset; vf.ReadExactly(orig);
                            Span<byte> head = stackalloc byte[10];
                            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)old.Volume);
                            BinaryPrimitives.WriteUInt32LittleEndian(head[2..], old.Offset);
                            BinaryPrimitives.WriteUInt32LittleEndian(head[6..], (uint)old.Stored);
                            undoChunks.Write(head); undoChunks.Write(orig); undoChunks.Flush(true);
                            u.InPlaceChunks++;
                            vf.Position = old.Offset; vf.Write(enc);
                            idx.Chunks[rec.FirstChunk + k] = old with { Zstd = z };
                            done += len; Report(Loc.T("Zstd.Core.108") + it.Path);
                        }
                        idx.Files[fi] = rec with { Size = it.Size, StoreOnly = false };
                        nInPlace++;
                    }
                }
                finally { foreach (var h in handles.Values) { h.Flush(true); h.Dispose(); } }
                SaveUndo();
            }

            // 3. Viết lại đuôi.
            long copied = 0;
            if (plan.RewriteFromFile >= 0)
            {
                var rewrite = plan.Items.Where(i => i.Kind == PatchKind.Rewrite).ToDictionary(i => byPath[i.Path]);
                int f0 = plan.RewriteFromFile;
                int c0 = idx.Files[f0].FirstChunk;
                int v0; long o0;
                if (c0 < idx.Chunks.Count) { v0 = idx.Chunks[c0].Volume; o0 = idx.Chunks[c0].Offset; }
                else { v0 = idx.VolumeSizes.Count - 1; o0 = idx.VolumeSizes[v0]; }   // các file từ f0 đều rỗng
                log?.Invoke(Loc.F("Zstd.Core.109", VolumeName(v0), o0, FormatSize(plan.RewriteCopyBytes), rewrite.Count));
                var newChunks = new List<Chunk>(idx.Chunks.Take(c0));
                var newVolumes = new List<long>(idx.VolumeSizes.Take(v0));
                var newFiles = new List<FileRec>(idx.Files.Take(f0));
                var temp = new List<string>();
                var oldHandles = new Dictionary<int, SafeFileHandle>();
                SafeFileHandle H(int v) => oldHandles.TryGetValue(v, out var h) ? h : oldHandles[v] = File.OpenHandle(Path.Combine(game, VolumeName(v)), FileMode.Open, FileAccess.Read, FileShare.Read);
                FileStream? vol = null; long cursor = 0;
                void Open(int id)
                {
                    var tp = Path.Combine(game, VolumeName(id) + ".vhnew");
                    temp.Add(tp);
                    vol = new FileStream(tp, FileMode.Create, FileAccess.Write, FileShare.None, 4 << 20);
                    vol.Write(new byte[VolumeHeaderSize]);
                    cursor = VolumeHeaderSize;
                }
                void Close() { if (vol == null) return; vol.Flush(true); newVolumes.Add(cursor); vol.Dispose(); vol = null; }
                void Put(ReadOnlySpan<byte> stored, bool z)
                {
                    if (vol == null || cursor + stored.Length > VolumeLimit) { Close(); Open(newVolumes.Count); }
                    newChunks.Add(new Chunk((uint)cursor, newVolumes.Count, stored.Length, z));
                    vol!.Write(stored);
                    cursor += stored.Length;
                }
                try
                {
                    // Phần đầu volume v0 (trước khối c0) giữ nguyên byte.
                    Open(v0);
                    for (long p = VolumeHeaderSize; p < o0;)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(buf.Length, o0 - p);
                        int got = RandomAccess.Read(H(v0), buf.AsSpan(0, n), p);
                        if (got != n) throw Bad(VolumeName(v0) + Loc.T("Zstd.Core.110"));
                        vol!.Write(buf, 0, n); p += n; cursor = p;
                    }
                    var raw = new byte[ChunkSize]; var stored = new byte[ChunkSize]; var outb = new byte[ChunkSize];
                    for (int fi = f0; fi < idx.Files.Count; fi++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var rec = idx.Files[fi];
                        int first = newChunks.Count;
                        if (rewrite.TryGetValue(fi, out var it))
                        {
                            using var fs = new FileStream(it.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                            if (fs.Length != it.Size) throw new IOException(it.Path + Loc.T("Zstd.Core.111"));
                            for (long left = it.Size; left > 0;)
                            {
                                int len = (int)Math.Min(ChunkSize, left);
                                fs.ReadExactly(raw, 0, len);
                                int n = CompressChunk(C(plan.Level), raw.AsSpan(0, len), outb);
                                if (n > 0) Put(outb.AsSpan(0, n), true); else Put(raw.AsSpan(0, len), false);
                                left -= len; done += len; Report(Loc.T("Zstd.Core.112") + it.Path);
                            }
                            newFiles.Add(rec with { Size = it.Size, FirstChunk = first, StoreOnly = false });
                            nRewrite++;
                        }
                        else
                        {
                            for (int k = 0; k < rec.ChunkCount; k++)
                            {
                                var c = idx.Chunks[rec.FirstChunk + k];
                                int got = RandomAccess.Read(H(c.Volume), stored.AsSpan(0, c.Stored), c.Offset);
                                if (got != c.Stored) throw Bad(VolumeName(c.Volume) + Loc.T("Zstd.Core.110"));
                                Put(stored.AsSpan(0, c.Stored), c.Zstd);
                                done += c.Stored; copied += c.Stored;
                            }
                            Report(Loc.T("Zstd.Core.077") + rec.Path);
                            newFiles.Add(rec with { FirstChunk = first });
                        }
                    }
                    Close();
                }
                catch
                {
                    vol?.Dispose();
                    foreach (var h in oldHandles.Values) h.Dispose();
                    foreach (var t in temp) try { File.Delete(t); } catch { }   // file tạm của chính lần vá này
                    throw;
                }
                foreach (var h in oldHandles.Values) h.Dispose();
                // Volume trống ở cuối (khi mọi khối từ v0 biến mất) không được phép: bỏ.
                if (newVolumes.Count > 0 && newVolumes[^1] == VolumeHeaderSize && newChunks.All(c => c.Volume != newVolumes.Count - 1))
                { newVolumes.RemoveAt(newVolumes.Count - 1); File.Delete(temp[^1]); temp.RemoveAt(temp.Count - 1); }
                if (newVolumes.Count > MaxVolumes) throw new IOException(Loc.T("Zstd.Core.079"));
                // Đổi chỗ: volume cũ từ v0 → thư mục hoàn tác; volume mới vào chỗ.
                var moved = Path.Combine(undo, "volumes");
                Directory.CreateDirectory(moved);
                u.FirstMovedVolume = v0; SaveUndo();
                for (int v = v0; v < idx.VolumeSizes.Count; v++) MoveOrCopy(Path.Combine(game, VolumeName(v)), Path.Combine(moved, VolumeName(v)));
                for (int i = 0; i < temp.Count; i++) File.Move(temp[i], Path.Combine(game, VolumeName(v0 + i)));
                idx.Files.Clear(); idx.Files.AddRange(newFiles);
                idx.Chunks.Clear(); idx.Chunks.AddRange(newChunks);
                idx.VolumeSizes.Clear(); idx.VolumeSizes.AddRange(newVolumes);
            }

            // 4. Header mọi volume (số volume có thể đổi), index, ampr_emu.index.
            for (int v = 0; v < idx.VolumeSizes.Count; v++)
            {
                using var fs = new FileStream(Path.Combine(game, VolumeName(v)), FileMode.Open, FileAccess.Write, FileShare.None, 1);
                fs.Write(VolumeHeader(v, idx.VolumeSizes.Count, idx.ArchiveId, idx.VolumeSizes[v] - VolumeHeaderSize));
            }
            File.WriteAllBytes(Path.Combine(game, IndexName + ".vhnew"), idx.Encode());
            File.Move(Path.Combine(game, IndexName + ".vhnew"), Path.Combine(game, IndexName), true);
            WriteEmuIndex(game, idx);

            // 5. Kiểm: đọc lại như runtime, giải các file vá trong gói và so SHA-256 với file vá.
            var check = Read(game);
            if (check.Problem.Length > 0) throw new InvalidDataException(Loc.T("Zstd.Core.113") + check.Problem);
            using (var reader = new PackReader(check, ThreadCount(threads)))
                foreach (var it in plan.Items.Where(i => i.Kind is PatchKind.InPlace or PatchKind.Rewrite))
                {
                    var rec = check.Index!.Files.First(f => f.Path == it.Path);
                    using var h1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    reader.Decode(rec, d => h1.AppendData(d.Span), _ => { }, ct);
                    using var fs = File.OpenRead(it.Source);
                    if (!h1.GetHashAndReset().AsSpan().SequenceEqual(SHA256.HashData(fs))) throw new InvalidDataException(Loc.T("Zstd.Core.113") + it.Path + Loc.T("Zstd.Core.114"));
                }
            log?.Invoke(Loc.F("Zstd.Core.115", check.Index!.VolumeSizes.Count, nInPlace + nRewrite));
            done = total; Report(Loc.T("Zstd.Core.Done"), true);
            return new PatchResult(game, undo, plan.Items.Count, nInPlace, nRewrite, nLoose, copied, sw.Elapsed);
        }
        finally { foreach (var d in comps.Values) foreach (var c in d.Values) c.Dispose(); }
    }

    private static void MoveOrCopy(string from, string to)
    {
        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) File.Move(from, to);
        else { File.Copy(from, to); File.Delete(from); }
    }

    /// <summary>ampr_emu.index mới: mọi file rời (trừ file gói) + mọi file trong gói, kích thước hiện tại.</summary>
    private static void WriteEmuIndex(string game, Index idx, long? mtime = null)
    {
        var rows = Walk(game).Where(f => !IsPackName(f.Rel) && !f.Rel.Equals(EmuIndexName, StringComparison.OrdinalIgnoreCase)
                                        && !f.Rel.EndsWith(".vhnew", StringComparison.OrdinalIgnoreCase)).Select(f => (f.Rel, f.Size))
            .Concat(idx.Files.Select(f => (f.Path, f.Size))).ToList();
        // ghi file tạm rồi thay: không để index nửa vời, không ghi xuyên qua hard link
        var dst = Path.Combine(game, EmuIndexName);
        var tmp = dst + ".vhnew";
        File.WriteAllBytes(tmp, BuildEmuIndex(rows, mtime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        File.Move(tmp, dst, overwrite: true);
    }

    /// <summary>Thư mục hoàn tác đi kèm game (mới nhất trước).</summary>
    public static IReadOnlyList<string> UndoFolders(string game)
    {
        game = Path.GetFullPath(game).TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(game)!;
        return Directory.EnumerateDirectories(parent, Path.GetFileName(game) + "_zstd_undo_*")
            .Where(d => File.Exists(Path.Combine(d, UndoManifest))).OrderByDescending(d => d, StringComparer.Ordinal).ToList();
    }

    /// <summary>Hoàn tác một lần vá: trả volume cũ, byte gốc của khối ghi đè, file rời gốc, index cũ; xoá file rời đã thêm.</summary>
    public static void Undo(string undoDir, Action<string>? log = null)
    {
        var u = JsonSerializer.Deserialize<UndoInfo>(File.ReadAllText(Path.Combine(undoDir, UndoManifest))) ?? throw new InvalidDataException(Loc.T("Zstd.Core.116"));
        var game = u.Game;
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException(game);
        var after = Read(game).Index;
        if (u.FirstMovedVolume >= 0)
        {
            int nowCount = after?.VolumeSizes.Count ?? 0;
            for (int v = u.FirstMovedVolume; v < Math.Max(nowCount, u.OldVolumeCount); v++)
            {
                var p = Path.Combine(game, VolumeName(v));
                if (File.Exists(p)) File.Delete(p);                    // volume do lần vá này tạo ra
            }
            for (int v = u.FirstMovedVolume; v < u.OldVolumeCount; v++) MoveOrCopy(Path.Combine(undoDir, "volumes", VolumeName(v)), Path.Combine(game, VolumeName(v)));
        }
        var cb = Path.Combine(undoDir, "chunks.bin");
        if (File.Exists(cb))
        {
            using var r = File.OpenRead(cb);
            var head = new byte[10];
            var open = new Dictionary<int, FileStream>();
            try
            {
                for (int i = 0; i < u.InPlaceChunks; i++)
                {
                    r.ReadExactly(head);
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(head); uint off = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(2)); int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(6));
                    var data = new byte[len]; r.ReadExactly(data);
                    if (!open.TryGetValue(v, out var f)) open[v] = f = new FileStream(Path.Combine(game, VolumeName(v)), FileMode.Open, FileAccess.Write, FileShare.None, 1);
                    f.Position = off; f.Write(data);
                }
            }
            finally { foreach (var f in open.Values) { f.Flush(true); f.Dispose(); } }
        }
        foreach (var rel in u.LooseAdded) { var p = Path.Combine(game, rel.Replace('/', '\\')); if (File.Exists(p)) File.Delete(p); }
        foreach (var rel in u.DirsCreated.OrderByDescending(d => d.Length))
        { var d = Path.Combine(game, rel); if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
        foreach (var rel in u.LooseReplaced) File.Copy(Path.Combine(undoDir, "loose", rel.Replace('/', '\\')), Path.Combine(game, rel.Replace('/', '\\')), true);
        File.Copy(Path.Combine(undoDir, IndexName), Path.Combine(game, IndexName), true);
        if (File.Exists(Path.Combine(undoDir, EmuIndexName))) File.Copy(Path.Combine(undoDir, EmuIndexName), Path.Combine(game, EmuIndexName), true);
        var check = Read(game);
        if (check.Problem.Length > 0) throw new InvalidDataException(Loc.T("Zstd.Core.117") + check.Problem);
        File.Move(Path.Combine(undoDir, UndoManifest), Path.Combine(undoDir, UndoManifest + ".done"));
        log?.Invoke(Loc.F("Zstd.Core.118", u.Time, check.Index!.VolumeSizes.Count, undoDir));
    }
}
