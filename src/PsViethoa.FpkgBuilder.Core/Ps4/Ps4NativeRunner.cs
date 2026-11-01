using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Ps4;

/// <summary>
/// Chạy orbis-pub-cmd và suy tiến độ như bản Studio: lúc ghi gói theo dung lượng PKG đang lớn dần, lúc digest / kiểm tra theo số
/// byte tiến trình đã đọc (Windows: GetProcessIoCounters, Linux: /proc/&lt;pid&gt;/io, macOS: proc_pid_rusage). PKG đầu ra luôn được giữ
/// nguyên kể cả khi engine báo lỗi.
/// </summary>
internal static class Ps4NativeRunner
{
    private enum Phase
    {
        Preparing,
        Writing,
        Digest,
        Rereading,
        ReferenceCompare,
        Integrity,
        Finalizing,
    }

    internal static async Task RunAsync(
        Ps4Runtime runtime,
        string tool,
        IEnumerable<string> arguments,
        Ps4JobContext job,
        long expectedBytes,
        bool verify,
        string passcode,
        bool fast = false,
        bool readsReferencePackage = false)
    {
        using var process = new Process { StartInfo = Ps4Engine.StartInfo(runtime, tool, arguments) };
        var phase = verify ? Phase.Integrity : Phase.Preparing;
        var detail = Loc.T("Ps4.Run.Engine");
        long phaseReadStart = 0;
        var errors = 0;
        var meter = new Ps4RateMeter();
        Phase? previousPhase = null;
        process.Start();
        job.Log(Loc.F("Ps4.Run.Pid", process.Id));

        async Task Drain(StreamReader stream)
        {
            while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var text = passcode.Length > 0 ? line.Replace(passcode, "[passcode]", StringComparison.Ordinal) : line;
                if (line.Contains("[Error]", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref errors);
                }

                job.Log(text, line.Contains("[Error]", StringComparison.Ordinal) ? Ps4LogLevel.Error : line.Contains("[Warn]", StringComparison.Ordinal) ? Ps4LogLevel.Warn : Ps4LogLevel.Engine);
                if (line.Contains("Writing internal image", StringComparison.OrdinalIgnoreCase))
                {
                    phase = Phase.Writing;
                    detail = Loc.T("Ps4.Run.WritingDetail");
                }

                if (line.Contains("Calculating image digest", StringComparison.OrdinalIgnoreCase))
                {
                    phaseReadStart = ReadBytes(process);
                    phase = readsReferencePackage ? Phase.ReferenceCompare : fast ? Phase.Rereading : Phase.Digest;
                    detail = Loc.T(readsReferencePackage ? "Ps4.Run.ReferenceDetail" : "Ps4.Run.DigestDetail");
                }

                if (line.Contains("Check Integrity started", StringComparison.OrdinalIgnoreCase))
                {
                    phaseReadStart = ReadBytes(process);
                    phase = Phase.Integrity;
                    detail = Loc.T(verify ? "Ps4.Run.IntegrityDetail" : "Ps4.Run.IntegrityReferenceDetail");
                }

                if (!verify && line.Contains("Check Integrity ended", StringComparison.OrdinalIgnoreCase))
                {
                    phaseReadStart = ReadBytes(process);
                    phase = Phase.Finalizing;
                    detail = Loc.T("Ps4.Run.FinalizingDetail");
                }

                var match = Regex.Match(line, @"Image0 Total Size\s*=\s*(\d+)MiB");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var mib))
                {
                    Interlocked.Exchange(ref expectedBytes, mib * 1024 * 1024);
                }
            }
        }

        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);
        try
        {
            while (!process.HasExited)
            {
                job.Token.ThrowIfCancellationRequested();
                var currentPhase = phase;
                if (currentPhase != previousPhase)
                {
                    meter = new Ps4RateMeter();
                    previousPhase = currentPhase;
                }

                long done;
                if (verify)
                {
                    done = ReadBytes(process);
                }
                else if (currentPhase is Phase.Digest or Phase.Rereading or Phase.ReferenceCompare or Phase.Finalizing or Phase.Integrity)
                {
                    done = Math.Max(0, ReadBytes(process) - phaseReadStart);
                }
                else
                {
                    done = 0;
                    foreach (var package in Directory.EnumerateFiles(job.Folder, "*.pkg", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            done = Math.Max(done, new FileInfo(package).Length);
                        }
                        catch (IOException)
                        {
                        }
                    }
                }

                var expected = Interlocked.Read(ref expectedBytes);
                var ratio = Math.Clamp(done / (double)Math.Max(1, expected), 0, 0.995);
                var percent = verify ? 94 + ratio * 4
                    : currentPhase == Phase.Integrity ? 93 + ratio * 3
                    : currentPhase is Phase.Finalizing or Phase.Rereading or Phase.ReferenceCompare ? 95 + ratio * 3.5
                    : currentPhase == Phase.Digest ? 79 + ratio * 14
                    : 4 + ratio * (fast ? 90 : 74);
                var rate = meter.Update(done, expected);
                if (currentPhase is Phase.Rereading or Phase.Digest or Phase.ReferenceCompare)
                {
                    var gigabytes = (done / 1e9).ToString("F2", CultureInfo.InvariantCulture);
                    detail = Loc.F(readsReferencePackage ? "Ps4.Run.ReferenceIo" : "Ps4.Run.RereadIo", gigabytes);
                }

                percent = job.BuildProgressStart + (100 - job.BuildProgressStart) * percent / 100;
                job.Progress(new Ps4Progress(PhaseText(currentPhase), detail, percent, Math.Min(done, expected), expected, rate.Speed, rate.Eta, true));
                await Task.Delay(200).ConfigureAwait(false);
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0 || errors > 0)
            {
                throw new InvalidOperationException(Loc.F("Ps4.Run.EngineFailed", process.ExitCode, errors, job.Folder));
            }
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
            }

            Ps4Engine.StopWineServer(runtime);
            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    private static string PhaseText(Phase phase) => Loc.T("Ps4.Phase." + phase);

    // ── Số byte tiến trình đã đọc ─────────────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOps;
        public ulong WriteOps;
        public ulong OtherOps;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("libproc", EntryPoint = "proc_pid_rusage")]
    private static extern int ProcPidRusage(int pid, int flavor, byte[] buffer);

    /// <summary>Byte tiến trình đã đọc; 0 nếu hệ điều hành không cho biết.</summary>
    private static long ReadBytes(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return GetProcessIoCounters(process.Handle, out var counters) ? checked((long)counters.ReadBytes) : 0;
            }

            if (OperatingSystem.IsLinux())
            {
                // rchar gồm cả đọc từ bộ nhớ đệm, giống ReadTransferCount của Windows.
                foreach (var line in File.ReadLines($"/proc/{process.Id}/io"))
                {
                    if (line.StartsWith("rchar:", StringComparison.Ordinal) && long.TryParse(line[6..].Trim(), out var value))
                    {
                        return value;
                    }
                }

                return 0;
            }

            if (OperatingSystem.IsMacOS())
            {
                // rusage_info_v2: 16 byte uuid rồi 18 số uint64; ri_diskio_bytesread là số thứ 17 (offset 144).
                var buffer = new byte[256];
                return ProcPidRusage(process.Id, 2, buffer) == 0 ? BitConverter.ToInt64(buffer, 16 + 16 * 8) : 0;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
        }

        return 0;
    }
}
