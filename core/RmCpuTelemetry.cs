using System.Runtime.InteropServices;
using System.Text.Json;

namespace ZenLoop.Core;

/// <summary>
/// Live CPU PPT / temperature / voltage / peak clock from AMD GetRmCpuParameters
/// (Ryzen Master Monitoring SDK layout). Fail-closed: garbage buffers yield nulls.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RmFullStats
{
    public int Init;
    public int CoreCount;
    public int CorePark;
    public int L1DataCache;
    public int L1InstructionCache;
    public int L3Cache;
    public float PptLimit;
    public float PptValue;
    public float EdcLimitVdd;
    public float EdcValueVdd;
    public float TdcLimitVdd;
    public float TdcValueVdd;
    public float EdcLimitSoc;
    public float EdcValueSoc;
    public float TdcLimitSoc;
    public float TdcValueSoc;
    public float HtcLimit;
    public float FclkP0;
    public float VddcrVddPower;
    public float VddcrSocPower;
    public float CclkFmax;
    public int Mode;
    public double PeakSpeed;
    public double PeakCoreVoltage;
    public double AvgCoreVoltage;
    public double SocVoltage;
    public double Temperature;
}

public sealed class RmLiveTelemetry
{
    public bool Ok { get; init; }
    public double? CpuPowerW { get; init; }
    public double? CpuTempC { get; init; }
    public double? CpuVoltageMv { get; init; }
    public double? CpuClockMhz { get; init; }
}

public static class RmCpuTelemetry
{
    public static RmLiveTelemetry Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 128) return new RmLiveTelemetry();
        var s = MemoryMarshal.Read<RmFullStats>(buf);
        double? power = SanePower(s.PptValue) ? s.PptValue
            : SanePower(s.VddcrVddPower) ? s.VddcrVddPower : null;
        double? temp = SaneTemp(s.Temperature) ? s.Temperature : ScanTemp(buf);
        double? voltV = SaneVoltV(s.AvgCoreVoltage) ? s.AvgCoreVoltage
            : SaneVoltV(s.PeakCoreVoltage) ? s.PeakCoreVoltage : null;
        double? clock = SaneMhz(s.PeakSpeed) ? s.PeakSpeed
            : SaneMhz(s.CclkFmax) ? s.CclkFmax : null;
        return new RmLiveTelemetry
        {
            Ok = power is not null || temp is not null,
            CpuPowerW = power,
            CpuTempC = temp,
            CpuVoltageMv = voltV is double v ? v * 1000.0 : null,
            CpuClockMhz = clock,
        };
    }

    public static void MergeInto(IDictionary<string, double?> dest, RmLiveTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(dest);
        if (t.CpuTempC is double c) dest["cpu_temp_c"] = c;
        if (t.CpuPowerW is double w)
        {
            dest["cpu_power_w"] = w;
            dest["ppt_w"] = w;
        }
        if (t.CpuVoltageMv is double v) dest["cpu_voltage_mv"] = v;
        if (t.CpuClockMhz is double m) dest["cpu_clock_mhz"] = m;
    }

    public static void MergeJsonTick(IDictionary<string, double?> dest, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in m.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Number)
                        dest[p.Name] = p.Value.GetDouble();
                }
            }
        }
        catch (JsonException)
        {
            /* ignore truncated line */
        }
    }

    static double? ScanTemp(ReadOnlySpan<byte> buf)
    {
        int n = Math.Min(buf.Length, 248);
        for (int off = 0; off + 8 <= n; off += 8)
        {
            double t = BitConverter.ToDouble(buf.Slice(off, 8));
            if (SaneTemp(t)) return t;
        }
        return null;
    }

    static bool SanePower(float w) => float.IsFinite(w) && w is > 1 and < 500;
    static bool SaneTemp(double t) => double.IsFinite(t) && t is > 10 and < 110;
    static bool SaneVoltV(double v) => double.IsFinite(v) && v is > 0.5 and < 1.65;
    static bool SaneMhz(double m) => double.IsFinite(m) && m is > 800 and < 7500;
}
