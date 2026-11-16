using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Threading;
using PsViethoa.FpkgBuilder.App.Services;

namespace PsViethoa.FpkgBuilder.App.Xmas;

/// <summary>
/// Nhạc nền Giáng sinh (port XmasMusic của Studio 2.1.0): mp3 nhúng trong ứng dụng (PsViethoa.Xmas.mp3), tự phát chậm ~1,5 giây sau
/// khi mở và lặp liên tục.
/// <para>Windows: KHÔNG dùng MCI "mpegvideo" / MediaPlayer / DirectShow — các đường đó kéo bộ giải mã LAV (K-Lite) lên và hiện icon LAV ở
/// khay. Lần đầu giải mp3 → PCM bằng Media Foundation (bộ giải MP3 có sẵn của Windows), lưu WAV đệm ở thư mục dữ liệu của app
/// (…/music); sau đó phát bằng PlaySound lặp (winmm) — nhẹ nhất, chế độ chia sẻ nên app khác vẫn phát nhạc bình thường. Âm lượng là
/// âm lượng phiên của riêng app (waveOutSetVolume trên thiết bị 0), không đụng âm lượng hệ thống.</para>
/// <para>macOS: <c>afplay</c> chạy nền (phát thẳng mp3), hết bài thì phát lại; đổi âm lượng thì khởi động lại afplay với <c>-v</c> mới.
/// Dừng khi tắt nhạc / thoát app.</para>
/// <para>Linux: chưa hỗ trợ (bỏ qua).</para>
/// </summary>
internal static class XmasMusic
{
    private const string ResourceName = "PsViethoa.Xmas.mp3";

    private static bool wanted;
    private static int session;
    private static double volume = 0.35;

    // Windows
    private static IntPtr wavMemory;
    private static bool winPlaying;

    // macOS
    private static Process? afplay;
    private static string? mp3Path;
    private static DispatcherTimer? volumeRestart;

    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static bool Playing => wanted && (OperatingSystem.IsWindows() ? winPlaying : afplay != null);

    public static string LastError { get; private set; } = "";

    public static event Action? StateChanged;

    public static double Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0, 1);
            ApplyVolume();
        }
    }

    private static string MusicDir => Path.Combine(SettingsService.Directory, "music");

    public static void StartDelayed(double vol, int delayMs = 1500)
    {
        volume = Math.Clamp(vol, 0, 1);
        wanted = true;
        DispatcherTimer.RunOnce(() =>
        {
            if (wanted)
            {
                _ = PlayAsync();
            }
        }, TimeSpan.FromMilliseconds(delayMs), DispatcherPriority.ApplicationIdle);
    }

    public static async Task PlayAsync()
    {
        wanted = true;
        int my = ++session;
        if (!Supported)
        {
            LastError = PsViethoa.FpkgBuilder.Core.Localization.Loc.T("Xmas.MusicUnsupported");
            StateChanged?.Invoke();
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (wavMemory == IntPtr.Zero)
                {
                    var mem = await Task.Run(LoadWavToMemory);   // giải mã / nạp trên luồng nền, không khựng giao diện
                    if (my != session || !wanted)
                    {
                        if (mem != IntPtr.Zero && wavMemory == IntPtr.Zero)
                        {
                            wavMemory = mem;
                        }

                        StateChanged?.Invoke();
                        return;
                    }

                    wavMemory = mem;
                }

                if (wavMemory == IntPtr.Zero)
                {
                    StateChanged?.Invoke();
                    return;
                }

                ApplyVolume();
                winPlaying = PlaySound(wavMemory, IntPtr.Zero, SndAsync | SndNoDefault | SndMemory | SndLoop);
                if (!winPlaying)
                {
                    LastError = "PlaySound không phát được";
                }
            }
            else
            {
                mp3Path ??= await Task.Run(EnsureMp3);
                if (my != session || !wanted)
                {
                    StateChanged?.Invoke();
                    return;
                }

                StartAfplay();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DebugLog.Write("XmasMusic: " + ex);
        }

        StateChanged?.Invoke();
    }

    public static void Stop()
    {
        wanted = false;
        session++;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (winPlaying)
                {
                    PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                }
            }
            catch
            {
            }

            winPlaying = false;
        }
        else
        {
            KillAfplay();
        }

        StateChanged?.Invoke();
    }

    /// <summary>Thoát app: dừng hẳn (macOS không để lại tiến trình afplay mồ côi).</summary>
    public static void Shutdown()
    {
        wanted = false;
        session++;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (winPlaying)
                {
                    PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                }
            }
            catch
            {
            }

            winPlaying = false;
        }
        else
        {
            KillAfplay();
        }
    }

    private static void ApplyVolume()
    {
        if (OperatingSystem.IsWindows())
        {
            uint v = (uint)Math.Round(volume * 0xFFFF);
            try
            {
                waveOutSetVolume(IntPtr.Zero, v | (v << 16));
            }
            catch
            {
            }
        }
        else if (OperatingSystem.IsMacOS() && afplay != null)
        {
            // afplay không đổi âm lượng khi đang phát: gom các lần kéo / lăn chuột rồi khởi động lại một lần.
            if (volumeRestart == null)
            {
                volumeRestart = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                volumeRestart.Tick += (_, _) =>
                {
                    volumeRestart.Stop();
                    if (wanted && afplay != null)
                    {
                        StartAfplay();
                    }
                };
            }

            volumeRestart.Stop();
            volumeRestart.Start();
        }
    }

    // ───────────────────────── macOS: afplay ─────────────────────────

    private static void StartAfplay()
    {
        KillAfplay();
        if (mp3Path == null)
        {
            return;
        }

        var info = new ProcessStartInfo("/usr/bin/afplay") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-v");
        info.ArgumentList.Add(volume.ToString("0.00", CultureInfo.InvariantCulture));
        info.ArgumentList.Add(mp3Path);
        var p = new Process { StartInfo = info, EnableRaisingEvents = true };
        int my = session;
        p.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(afplay, p))
            {
                return;
            }

            afplay = null;
            p.Dispose();
            // Hết bài: phát lại (lặp liên tục) nếu người dùng vẫn bật nhạc.
            if (wanted && my == session)
            {
                StartAfplay();
            }

            StateChanged?.Invoke();
        });
        if (!p.Start())
        {
            LastError = "Không chạy được afplay";
            p.Dispose();
            return;
        }

        afplay = p;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private static void OnProcessExit(object? sender, EventArgs e) => KillAfplay();

    private static void KillAfplay()
    {
        var p = afplay;
        afplay = null;
        if (p == null)
        {
            return;
        }

        try
        {
            if (!p.HasExited)
            {
                p.Kill();
            }
        }
        catch
        {
        }

        p.Dispose();
    }

    private static byte[]? ReadResource(out string hash)
    {
        hash = "";
        using var res = typeof(XmasMusic).Assembly.GetManifestResourceStream(ResourceName);
        if (res == null)
        {
            return null;
        }

        var bytes = new byte[res.Length];
        res.ReadExactly(bytes);
        hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        return bytes;
    }

    private static string? EnsureMp3()
    {
        var bytes = ReadResource(out var hash);
        if (bytes == null)
        {
            return null;
        }

        Directory.CreateDirectory(MusicDir);
        var mp3 = Path.Combine(MusicDir, $"xmas-{hash}.mp3");
        if (!File.Exists(mp3) || new FileInfo(mp3).Length != bytes.Length)
        {
            File.WriteAllBytes(mp3, bytes);
        }

        return mp3;
    }

    // ───────────────────────── Windows: Media Foundation → WAV → PlaySound ─────────────────────────

    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004, SndLoop = 0x0008;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutSetVolume(IntPtr device, uint volume);

    /// <summary>Nạp WAV đệm vào bộ nhớ không quản lý (PlaySound SND_MEMORY cần vùng nhớ cố định suốt lúc phát).</summary>
    private static IntPtr LoadWavToMemory()
    {
        var file = EnsureWav();
        if (file == null)
        {
            return IntPtr.Zero;
        }

        var bytes = File.ReadAllBytes(file);
        var mem = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, mem, bytes.Length);
        return mem;
    }

    /// <summary>WAV đệm theo SHA-256 của mp3 nhúng; có rồi thì dùng lại.</summary>
    internal static string? EnsureWav()
    {
        var bytes = ReadResource(out var hash);
        if (bytes == null)
        {
            LastError = "Không tìm thấy nhạc nhúng";
            return null;
        }

        Directory.CreateDirectory(MusicDir);
        var wav = Path.Combine(MusicDir, $"xmas-{hash}.wav");
        if (File.Exists(wav) && new FileInfo(wav).Length > 44)
        {
            return wav;
        }

        foreach (var old in Directory.EnumerateFiles(MusicDir, "xmas-*.*"))
        {
            try
            {
                File.Delete(old);
            }
            catch
            {
            }
        }

        var mp3 = Path.Combine(MusicDir, $"xmas-{hash}.mp3");
        File.WriteAllBytes(mp3, bytes);
        try
        {
            var tmp = wav + ".tmp";
            DecodeToWav(mp3, tmp);
            File.Move(tmp, wav, true);
            return wav;
        }
        finally
        {
            try
            {
                File.Delete(mp3);
            }
            catch
            {
            }
        }
    }

    // ── Media Foundation Source Reader (gọi vtable trực tiếp, không cần thư viện ngoài) ──
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] private static extern int MFCreateWaveFormatExFromMFMediaType(IntPtr type, out IntPtr wfx, out uint size, uint flags);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

    private static readonly Guid MtMajor = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00AA00389B71"), Pcm = new("00000001-0000-0010-8000-00AA00389B71");
    private const uint FirstAudio = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE;

    private static unsafe void** V(IntPtr p) => *(void***)p;
    private static unsafe void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(p)[2])(p); }
    private static void Check(int hr, string what) { if (hr < 0) throw new IOException($"Media Foundation {what}: 0x{hr:X8}"); }

    private static unsafe void DecodeToWav(string mp3, string wavPath)
    {
        Check(MFStartup(0x00020070, 0), "MFStartup");
        IntPtr reader = IntPtr.Zero, type = IntPtr.Zero, cur = IntPtr.Zero;
        try
        {
            Check(MFCreateSourceReaderFromURL(mp3, IntPtr.Zero, out reader), "SourceReader");
            var r = V(reader);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)r[4])(reader, AllStreams, 0);       // SetStreamSelection
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)r[4])(reader, FirstAudio, 1);
            Check(MFCreateMediaType(out type), "MediaType");
            Guid k1 = MtMajor, v1 = Audio, k2 = MtSubtype, v2 = Pcm;
            var setGuid = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)V(type)[24];            // IMFAttributes::SetGUID
            Check(setGuid(type, &k1, &v1), "SetGUID");
            Check(setGuid(type, &k2, &v2), "SetGUID");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)r[7])(reader, FirstAudio, IntPtr.Zero, type), "SetCurrentMediaType");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)r[6])(reader, FirstAudio, &cur), "GetCurrentMediaType");
            Check(MFCreateWaveFormatExFromMFMediaType(cur, out var wfx, out var wfxSize, 0), "WaveFormat");
            byte[] fmt = new byte[16];
            Marshal.Copy(wfx, fmt, 0, (int)Math.Min(16u, wfxSize));
            Marshal.FreeCoTaskMem(wfx);
            fmt[0] = 1; fmt[1] = 0;            // WAVE_FORMAT_PCM

            using var fs = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            using var bw = new BinaryWriter(fs);
            bw.Write("RIFF"u8); bw.Write(0); bw.Write("WAVE"u8);
            bw.Write("fmt "u8); bw.Write(16); bw.Write(fmt);
            bw.Write("data"u8); bw.Write(0);
            long data = 0;
            var readSample = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)r[9];
            while (true)
            {
                uint actual, flags; long ts; IntPtr sample = IntPtr.Zero;
                Check(readSample(reader, FirstAudio, 0, &actual, &flags, &ts, &sample), "ReadSample");
                if (sample != IntPtr.Zero)
                {
                    IntPtr buf = IntPtr.Zero;
                    try
                    {
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(sample)[41])(sample, &buf), "ConvertToContiguousBuffer");
                        byte* ptr; uint max, len;
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)V(buf)[3])(buf, &ptr, &max, &len), "Lock");
                        bw.Write(new ReadOnlySpan<byte>(ptr, (int)len));
                        data += len;
                        ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(buf)[4])(buf);                     // Unlock
                    }
                    finally { Release(buf); Release(sample); }
                }
                if ((flags & 2) != 0) break;   // MF_SOURCE_READERF_ENDOFSTREAM
            }
            bw.Flush();
            fs.Position = 4; bw.Write((int)(36 + data));
            fs.Position = 40; bw.Write((int)data);
        }
        finally { Release(cur); Release(type); Release(reader); MFShutdown(); }
    }
}
