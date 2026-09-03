using System.Runtime.InteropServices;
using System.Text;

namespace ZenLoop.Core;

/// <summary>
/// HWiNFO shared-memory snapshot. Official layout (hwisenssm2.h): pack(1),
/// poll_time is 64-bit, reading type 1=temp 2=volt 5=power 6=clock.
/// Fail-closed: missing HWiNFO returns an empty snapshot, never invented watts/temps.
/// </summary>
public sealed class HwinfoSnapshot
{
    public bool Available { get; init; }
    public double? CpuTempC { get; init; }
    public double? CpuPowerW { get; init; }
    public double? CpuVoltageMv { get; init; }
    public double? CpuClockMhz { get; init; }
    public int Readings { get; init; }
}

public readonly record struct HwinfoReading(int Type, string Label, string Unit, double Value);

public static class HwinfoSensors
{
    public const uint SignatureHwiS = 0x53695748; // 'HWiS'
    public const uint SignatureDead = 0x44414544; // 'DEAD'

    public static HwinfoSnapshot TryRead()
    {
        try
        {
            var readings = ReadLive();
            if (readings is null) return new HwinfoSnapshot();
            return FromReadings(readings);
        }
        catch
        {
            return new HwinfoSnapshot();
        }
    }

    public static HwinfoSnapshot FromReadings(IReadOnlyList<HwinfoReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        return new HwinfoSnapshot
        {
            Available = readings.Count > 0,
            CpuTempC = PickTemp(readings),
            CpuPowerW = PickPower(readings),
            CpuVoltageMv = PickVoltageMv(readings),
            CpuClockMhz = PickClock(readings),
            Readings = readings.Count,
        };
    }

    public static void MergeInto(IDictionary<string, double?> dest, HwinfoSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(dest);
        if (snap.CpuTempC is double t) dest["cpu_temp_c"] = t;
        if (snap.CpuPowerW is double w)
        {
            dest["cpu_power_w"] = w;
            dest["ppt_w"] = w;
        }
        if (snap.CpuVoltageMv is double v) dest["cpu_voltage_mv"] = v;
        if (snap.CpuClockMhz is double c) dest["cpu_clock_mhz"] = c;
    }

    public static int ScoreTemp(string label)
    {
        var s = Norm(label);
        if (s.Length == 0) return 0;
        if (s.Contains("distance to tj") || s.Contains("tjmax") || s.Contains("delta")) return 0;
        if (s.Contains("gpu") || s.Contains("vram") || s.Contains("motherboard") || s.Contains("chipset")) return 0;
        if (s.Contains("tctl/tdie") || s.Contains("tctl / tdie")) return 100;
        if (s is "cpu package" || s.Contains("cpu package")) return 95;
        if (s.Contains("cpu die (average)") || s.Contains("cpu die average")) return 90;
        if (s.Contains("cpu tctl") || s.Contains("tctl")) return 88;
        if (s.Contains("ccd1") && s.Contains("tdie")) return 80;
        if (s.Contains("ccd") && s.Contains("tdie")) return 70;
        if (s.Contains("core (tctl")) return 85;
        return 0;
    }

    public static int ScorePower(string label)
    {
        var s = Norm(label);
        if (s.Length == 0) return 0;
        if (s.Contains("limit") || s.Contains("tdp") || s.Contains("peak") || s.Contains("max ppt")) return 0;
        if (s.Contains("gpu") || s.Contains("soc ") || s.Contains("vddcr soc") || s.Contains("memory")) return 0;
        if (s.Contains("cpu package power") || s is "package power") return 100;
        if (s.Contains("cpu ppt") || s is "ppt") return 95;
        if (s.Contains("package power")) return 90;
        if (s.Contains("cpu power") && !s.Contains("core power")) return 80;
        if (s.Contains("cpu core power")) return 60;
        return 0;
    }

    public static int ScoreClock(string label)
    {
        var s = Norm(label);
        if (s.Length == 0) return 0;
        if (s.Contains("gpu") || s.Contains("fclk") || s.Contains("uclk") || s.Contains("mclk") || s.Contains("memory")) return 0;
        if (s.Contains("effective clock")) return 100;
        if (s.Contains("core") && s.Contains("clock") && !s.Contains("uncore")) return 70;
        if (s is "cpu clock" || s.Contains("average clock")) return 60;
        return 0;
    }

    public static int ScoreVoltage(string label)
    {
        var s = Norm(label);
        if (s.Length == 0) return 0;
        if (s.Contains("gpu") || s.Contains("soc") || s.Contains("dram") || s.Contains("memory") || s.Contains("vddio")) return 0;
        if (s.Contains("svi3") && s.Contains("tfn")) return 100;
        if (s.Contains("svi2") && s.Contains("tfn")) return 95;
        if (s.Contains("core voltage") && s.Contains("cpu")) return 80;
        if (s.Contains("cpu vid") || (s.Contains("vid") && s.Contains("cpu"))) return 50;
        return 0;
    }

    public static double? PickTemp(IEnumerable<HwinfoReading> readings)
        => Best(readings, r => r.Type is 0 or 1, ScoreTemp, v => v is > 0 and < 125);

    public static double? PickPower(IEnumerable<HwinfoReading> readings)
        => Best(readings, r => r.Type is 0 or 5, ScorePower, v => v is > 1 and < 900);

    public static double? PickClock(IEnumerable<HwinfoReading> readings)
    {
        var hits = readings
            .Where(r => r.Type is 0 or 6 && ScoreClock(r.Label) >= 100 && r.Value is > 200 and < 8000)
            .Select(r => r.Value)
            .ToList();
        if (hits.Count > 0) return hits.Average();
        return Best(readings, r => r.Type is 0 or 6, ScoreClock, v => v is > 200 and < 8000);
    }

    public static double? PickVoltageMv(IEnumerable<HwinfoReading> readings)
    {
        var v = Best(readings, r => r.Type is 0 or 2, ScoreVoltage, x => x is > 0.4 and < 3.0 || x is > 400 and < 2000);
        if (v is null) return null;
        return v.Value < 20 ? v.Value * 1000.0 : v.Value;
    }

    static double? Best(
        IEnumerable<HwinfoReading> readings,
        Func<HwinfoReading, bool> typeOk,
        Func<string, int> score,
        Func<double, bool> sane)
    {
        int best = 0;
        double? val = null;
        foreach (var r in readings)
        {
            if (!typeOk(r) || !sane(r.Value)) continue;
            int sc = score(r.Label);
            if (sc > best)
            {
                best = sc;
                val = r.Value;
            }
        }
        return best > 0 ? val : null;
    }

    static string Norm(string label) => (label ?? "").Trim().ToLowerInvariant();

    static List<HwinfoReading>? ReadLive()
    {
        var map = Native.OpenFileMappingW(Native.FileMapRead, false, Native.MapNameGlobal);
        if (map == IntPtr.Zero)
            map = Native.OpenFileMappingW(Native.FileMapRead, false, Native.MapNameLocal);
        if (map == IntPtr.Zero) return null;

        IntPtr mutex = Native.OpenMutexW(Native.MutexModify, false, Native.MutexName);
        bool locked = false;
        if (mutex != IntPtr.Zero)
        {
            uint wait = Native.WaitForSingleObject(mutex, 80);
            locked = wait is 0 or 0x00000080;
        }

        try
        {
            var view = Native.MapViewOfFile(map, Native.FileMapRead, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero) return null;
            try
            {
                var hdr = Marshal.PtrToStructure<Native.Header>(view);
                if (hdr.Signature == SignatureDead) return null;
                if (hdr.Signature != SignatureHwiS && hdr.Signature != 0x53776948 && hdr.Signature != 0x53697748)
                    return null;
                uint n = hdr.NumReadingElements;
                uint size = hdr.SizeOfReadingSection;
                if (n == 0 || n > 8000 || size < 160 || size > 1024) return null;
                var list = new List<HwinfoReading>((int)Math.Min(n, 8000));
                for (uint i = 0; i < n; i++)
                {
                    var addr = IntPtr.Add(view, (int)(hdr.OffsetOfReadingSection + i * size));
                    var rec = Marshal.PtrToStructure<Native.Reading>(addr);
                    if (double.IsNaN(rec.Value) || double.IsInfinity(rec.Value)) continue;
                    list.Add(new HwinfoReading(
                        rec.Type,
                        Native.Z(rec.LabelUser, rec.LabelOrig),
                        Native.Z(rec.Unit, null),
                        rec.Value));
                }
                return list;
            }
            finally
            {
                Native.UnmapViewOfFile(view);
            }
        }
        finally
        {
            if (locked) Native.ReleaseMutex(mutex);
            if (mutex != IntPtr.Zero) Native.CloseHandle(mutex);
            Native.CloseHandle(map);
        }
    }

    static class Native
    {
        public const uint FileMapRead = 0x0004;
        public const uint MutexModify = 0x00100001; // SYNCHRONIZE | MUTEX_MODIFY_STATE
        public const string MapNameGlobal = @"Global\HWiNFO_SENS_SM2";
        public const string MapNameLocal = "HWiNFO_SENS_SM2";
        public const string MutexName = @"Global\HWiNFO_SM2_MUTEX";

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Header
        {
            public uint Signature;
            public uint Version;
            public uint Revision;
            public long PollTime;
            public uint OffsetOfSensorSection;
            public uint SizeOfSensorElement;
            public uint NumSensorElements;
            public uint OffsetOfReadingSection;
            public uint SizeOfReadingSection;
            public uint NumReadingElements;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        public struct Reading
        {
            public int Type;
            public uint SensorIndex;
            public uint ReadingId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] LabelOrig;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] LabelUser;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Unit;
            public double Value;
            public double ValueMin;
            public double ValueMax;
            public double ValueAvg;
        }

        public static string Z(byte[]? a, byte[]? b)
        {
            var s = Decode(a);
            return s.Length > 0 ? s : Decode(b);
        }

        static string Decode(byte[]? raw)
        {
            if (raw is null || raw.Length == 0) return "";
            int n = Array.IndexOf(raw, (byte)0);
            if (n < 0) n = raw.Length;
            if (n <= 0) return "";
            return Encoding.Latin1.GetString(raw, 0, n).Trim();
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);

        [DllImport("kernel32", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHi, uint offLo, UIntPtr size);

        [DllImport("kernel32", SetLastError = true)]
        public static extern bool UnmapViewOfFile(IntPtr view);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenMutexW(uint access, bool inherit, string name);

        [DllImport("kernel32", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32", SetLastError = true)]
        public static extern bool ReleaseMutex(IntPtr handle);

        [DllImport("kernel32", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
