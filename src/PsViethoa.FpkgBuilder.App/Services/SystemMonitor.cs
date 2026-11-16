// Port từ PSVIETHOA STUDIO 2.1.0 (SystemMonitor.cs) — chỉ chạy trên Windows; macOS / Linux: không khởi động (khung Hệ thống ẩn).
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Timer = System.Threading.Timer;

namespace PsViethoa.FpkgBuilder.App.Services;

/// <summary>One physical drive as shown in the sidebar.</summary>
public sealed record DiskSample(int Number, string Model, string Kind, string Letters, double ReadBps, double WriteBps, double BusyPercent, int? TempC, int? WarnC);

/// <summary>One reading of the whole machine. <paramref name="CpuTempSource"/>: "ASUS", "ACPI", or "ACPI-static"
/// (the firmware zone never moves, so it is not a real CPU reading and <paramref name="CpuTempC"/> is null).</summary>
public sealed record SystemSample(double CpuPercent, int? CpuTempC, string CpuTempSource, IReadOnlyList<DiskSample> Disks);

/// <summary>
/// CPU load, CPU temperature, per-drive read/write speed and drive temperatures, sampled once per second on a
/// background thread. Everything comes from Windows itself, no admin rights and no kernel driver:
/// <list type="bullet">
/// <item>CPU % and disk speeds: PDH performance counters (the same numbers Task Manager shows);</item>
/// <item>CPU temperature: the ASUS ATKACPI sensor on ASUS laptops (the value Armoury Crate shows), otherwise ACPI thermal
/// zones. Many laptops expose a fake zone fixed at ~28 °C; a zone that never moves is detected and hidden ("—");</item>
/// <item>drive temperature: IOCTL_STORAGE_QUERY_PROPERTY / StorageDeviceTemperatureProperty (NVMe and most SATA SSDs).</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemMonitor : IDisposable
{
    private readonly Timer timer;
    private IntPtr query;
    private IntPtr cpuCounter, readCounter, writeCounter, idleCounter, thermalCounter;
    private readonly Dictionary<int, (string Model, string Kind)> identity = [];
    private readonly Dictionary<int, (int? Temp, int? Warn)> temps = [];
    private DateTime lastIdentity = DateTime.MinValue, lastTemps = DateTime.MinValue;
    private int busy;
    private SafeFileHandle? atk;
    private bool atkTried;
    private double acpiMin = double.MaxValue, acpiMax = double.MinValue;
    private int acpiSamples;

    public SystemSample? Latest { get; private set; }
    public int Version { get; private set; }

    public SystemMonitor()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) == 0)
            {
                if (PdhAddEnglishCounter(query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out cpuCounter) != 0)
                    PdhAddEnglishCounter(query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out cpuCounter);
                PdhAddEnglishCounter(query, @"\PhysicalDisk(*)\Disk Read Bytes/sec", IntPtr.Zero, out readCounter);
                PdhAddEnglishCounter(query, @"\PhysicalDisk(*)\Disk Write Bytes/sec", IntPtr.Zero, out writeCounter);
                PdhAddEnglishCounter(query, @"\PhysicalDisk(*)\% Idle Time", IntPtr.Zero, out idleCounter);
                if (PdhAddEnglishCounter(query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out thermalCounter) != 0) thermalCounter = IntPtr.Zero;
                PdhCollectQueryData(query);    // rates need two samples
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { query = IntPtr.Zero; }
        timer = new Timer(_ => Sample(), null, 1000, 1000);
    }

    private void Sample()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            double cpu = 0; int? cpuTemp = null; var cpuTempSource = "";
            double? acpi = null;
            var read = new Dictionary<int, (string Letters, double Value)>();
            var write = new Dictionary<int, double>();
            var busyPct = new Dictionary<int, double>();
            if (query != IntPtr.Zero && PdhCollectQueryData(query) == 0)
            {
                if (cpuCounter != IntPtr.Zero && PdhGetFormattedCounterValue(cpuCounter, PdhFmtDouble | PdhFmtNoCap100, out _, out var v) == 0 && v.CStatus is 0 or 1)
                    cpu = Math.Clamp(v.DoubleValue, 0, 100);
                foreach (var (name, value) in ReadArray(readCounter))
                    if (ParseDisk(name) is { } d) read[d.Number] = (d.Letters, value);
                foreach (var (name, value) in ReadArray(writeCounter))
                    if (ParseDisk(name) is { } d) write[d.Number] = value;
                foreach (var (name, value) in ReadArray(idleCounter))
                    if (ParseDisk(name) is { } d) busyPct[d.Number] = Math.Clamp(100 - value, 0, 100);
                var zones = ReadArray(thermalCounter).Select(z => z.Value - 273.15).Where(c => c is > 5 and < 125).ToList();
                if (zones.Count > 0) acpi = zones.Max();
            }
            if (AsusCpuTemp() is { } asus) { cpuTemp = asus; cpuTempSource = "ASUS"; }
            else if (acpi is { } zone)
            {
                // A real sensor moves at least a little within half a minute of 1 s samples; the fixed firmware value does not.
                acpiMin = Math.Min(acpiMin, zone); acpiMax = Math.Max(acpiMax, zone); acpiSamples++;
                if (acpiSamples >= 30 && acpiMax - acpiMin < 0.05) cpuTempSource = "ACPI-static";
                else { cpuTemp = (int)Math.Round(zone); cpuTempSource = "ACPI"; }
            }

            var now = DateTime.UtcNow;
            if (now - lastIdentity > TimeSpan.FromSeconds(30))
            {
                lastIdentity = now;
                foreach (var n in read.Keys) if (!identity.ContainsKey(n)) identity[n] = Identify(n);
            }
            if (now - lastTemps > TimeSpan.FromSeconds(3))
            {
                lastTemps = now;
                foreach (var n in read.Keys) temps[n] = Temperature(n);
            }

            var disks = read.OrderBy(k => k.Key).Select(k =>
            {
                (string Model, string Kind) id = identity.TryGetValue(k.Key, out var i) ? i : (PsViethoa.FpkgBuilder.Core.Localization.Loc.F("Sys.Disk", k.Key), "");
                (int? Temp, int? Warn) t = temps.TryGetValue(k.Key, out var tt) ? tt : (null, null);
                return new DiskSample(k.Key, id.Model, id.Kind, k.Value.Letters, Math.Max(0, k.Value.Value), Math.Max(0, write.GetValueOrDefault(k.Key)), busyPct.GetValueOrDefault(k.Key), t.Temp, t.Warn);
            }).ToList();
            Latest = new SystemSample(cpu, cpuTemp, cpuTempSource, disks);
            Version++;
        }
        catch (Exception) { /* monitoring is best effort; never disturb the app */ }
        finally { Volatile.Write(ref busy, 0); }
    }

    /// <summary>PhysicalDisk instance names look like "0 C:" or "1 D: E:"; "_Total" is skipped.</summary>
    private static (int Number, string Letters)? ParseDisk(string name)
    {
        var space = name.IndexOf(' ');
        var head = space < 0 ? name : name[..space];
        if (!int.TryParse(head, out var n)) return null;
        return (n, space < 0 ? "" : name[(space + 1)..].Trim());
    }

    private static List<(string Name, double Value)> ReadArray(IntPtr counter)
    {
        var result = new List<(string, double)>();
        if (counter == IntPtr.Zero) return result;
        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, buffer) != 0) return result;
            var stride = IntPtr.Size + 16;          // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer + {CStatus, pad, double}
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * stride;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                var cstatus = Marshal.ReadInt32(item + IntPtr.Size);
                if (cstatus is not (0 or 1)) continue;
                result.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8))));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    /// <summary>
    /// ASUS laptops: the ATK ACPI driver (installed with the ASUS System Control Interface) answers DSTS for device
    /// 0x00120094 = CPU temperature in °C, offset by 0x10000 ("present" bit). Read only, no admin rights needed.
    /// Returns null on any other machine.
    /// </summary>
    private int? AsusCpuTemp()
    {
        if (!atkTried)
        {
            atkTried = true;
            var h = CreateFile(@"\\.\ATKACPI", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) h.Dispose(); else atk = h;
        }
        if (atk == null) return null;
        var input = new byte[16];
        BitConverter.GetBytes(0x53545344u).CopyTo(input, 0);   // "DSTS" = read device status
        BitConverter.GetBytes(8u).CopyTo(input, 4);
        BitConverter.GetBytes(0x00120094u).CopyTo(input, 8);
        var output = new byte[16];
        if (!DeviceIoControl(atk, AtkControlCode, input, input.Length, output, output.Length, out _, IntPtr.Zero)) return null;
        var value = BitConverter.ToInt32(output, 0) - 0x10000;
        return value is > 5 and < 125 ? value : null;
    }

    // ── Drive identity and temperature (IOCTL_STORAGE_QUERY_PROPERTY, opened with no access rights: works without admin) ──
    private static SafeFileHandle OpenDisk(int n)
        => CreateFile($@"\\.\PhysicalDrive{n}", 0, 3 /* share read|write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

    private static byte[]? QueryProperty(SafeFileHandle h, int propertyId)
    {
        var q = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(q, 0);           // QueryType 0 = PropertyStandardQuery
        var output = new byte[1024];
        return DeviceIoControl(h, IoctlStorageQueryProperty, q, q.Length, output, output.Length, out var got, IntPtr.Zero) && got >= 8 ? output[..got] : null;
    }

    private static (string Model, string Kind) Identify(int n)
    {
        using var h = OpenDisk(n);
        if (h.IsInvalid) return (PsViethoa.FpkgBuilder.Core.Localization.Loc.F("Sys.Disk", n), "");
        var model = PsViethoa.FpkgBuilder.Core.Localization.Loc.F("Sys.Disk", n);
        var bus = 0;
        if (QueryProperty(h, 0) is { Length: >= 32 } d)                          // StorageDeviceProperty
        {
            var productOffset = BitConverter.ToInt32(d, 16);
            bus = BitConverter.ToInt32(d, 28);
            if (productOffset > 0 && productOffset < d.Length)
            {
                var end = Array.IndexOf(d, (byte)0, productOffset);
                var text = Encoding.ASCII.GetString(d, productOffset, (end < 0 ? d.Length : end) - productOffset).Trim();
                if (text.Length > 0) model = text;
            }
        }
        var seek = QueryProperty(h, 7) is { Length: >= 9 } s ? s[8] != 0 : (bool?)null;   // StorageDeviceSeekPenaltyProperty
        var kind = bus switch { 17 => "NVMe", 7 => "USB", 11 => seek == true ? "HDD" : "SATA SSD", _ => seek == true ? "HDD" : seek == false ? "SSD" : "" };
        return (model, kind);
    }

    private static (int? Temp, int? Warn) Temperature(int n)
    {
        using var h = OpenDisk(n);
        if (h.IsInvalid) return (null, null);
        // StorageDeviceTemperatureProperty (52): STORAGE_TEMPERATURE_DATA_DESCRIPTOR, first STORAGE_TEMPERATURE_INFO at offset 24.
        if (QueryProperty(h, 52) is not { Length: >= 28 } t) return (null, null);
        var warning = BitConverter.ToInt16(t, 10);
        var count = BitConverter.ToUInt16(t, 12);
        if (count == 0) return (null, null);
        var temp = BitConverter.ToInt16(t, 26);
        return temp is > 0 and < 150 ? (temp, warning is > 30 and < 150 ? warning : null) : (null, null);
    }

    public void Dispose()
    {
        timer.Dispose();
        atk?.Dispose();
        if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────
    private const uint PdhFmtDouble = 0x00000200, PdhFmtNoCap100 = 0x00008000;
    private const int PdhMoreData = unchecked((int)0x800007D2);
    private const int IoctlStorageQueryProperty = 0x002D1400;
    private const int AtkControlCode = 0x0022240C;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue { public int CStatus; private int pad; public double DoubleValue; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, int code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
