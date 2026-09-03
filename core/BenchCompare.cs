using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

public enum BenchKind
{
    Cpu,
    Gpu,
    Ram,
}

public sealed class BenchMetrics
{
    [JsonPropertyName("throughput")]
    public double Throughput { get; set; }

    [JsonPropertyName("avg_power_w")]
    public double? AvgPowerW { get; set; }

    [JsonPropertyName("peak_temp_c")]
    public double? PeakTempC { get; set; }

    [JsonPropertyName("avg_temp_c")]
    public double? AvgTempC { get; set; }

    [JsonPropertyName("avg_clock_mhz")]
    public double? AvgClockMhz { get; set; }

    [JsonPropertyName("avg_voltage_mv")]
    public double? AvgVoltageMv { get; set; }

    [JsonPropertyName("duration_s")]
    public double DurationS { get; set; }

    [JsonPropertyName("energy_j")]
    public double? EnergyJ { get; set; }

    [JsonPropertyName("samples")]
    public int Samples { get; set; }
}

public sealed class BenchRun
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "current";

    [JsonPropertyName("utc")]
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("cpu")]
    public BenchMetrics Cpu { get; set; } = new();

    [JsonPropertyName("gpu")]
    public BenchMetrics Gpu { get; set; } = new();

    [JsonPropertyName("ram")]
    public BenchMetrics Ram { get; set; } = new();

    [JsonPropertyName("hwinfo")]
    public bool Hwinfo { get; set; }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Equal-weight harmonic mean of CPU/GPU/RAM throughput (3DMark Time Spy style).</summary>
    [JsonIgnore]
    public double SystemScore => BenchCompare.HarmonicMean(Throughputs());

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static BenchRun Parse(string json)
        => JsonSerializer.Deserialize<BenchRun>(json, JsonOpts)
           ?? throw new InvalidOperationException("bench JSON empty");

    public void SaveFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    public static BenchRun? TryLoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return Parse(File.ReadAllText(path));
    }

    IEnumerable<double> Throughputs()
    {
        if (Cpu.Throughput > 0) yield return Cpu.Throughput;
        if (Gpu.Throughput > 0) yield return Gpu.Throughput;
        if (Ram.Throughput > 0) yield return Ram.Throughput;
    }
}

public sealed class BenchDelta
{
    public double CpuSpeedPct { get; set; }
    public double GpuSpeedPct { get; set; }
    public double RamSpeedPct { get; set; }
    /// <summary>Geometric mean of CPU/GPU/RAM speed ratios, as percent vs baseline (SkatterBencher).</summary>
    public double SpeedPct { get; set; }
    /// <summary>Percent change of harmonic-mean system score (3DMark-style combined).</summary>
    public double HarmonicSpeedPct { get; set; }
    /// <summary>Current minus baseline. Negative = cooler. Null if either side lacked temp.</summary>
    public double TempDeltaC { get; set; }
    public double? CpuTempDeltaC { get; set; }
    public double? GpuTempDeltaC { get; set; }
    /// <summary>Current minus baseline. Negative = less power. Null-safe: missing telemetry is 0 here for Summary.</summary>
    public double PowerDeltaW { get; set; }
    public double? CpuPowerDeltaW { get; set; }
    public double? GpuPowerDeltaW { get; set; }
    public double? EnergyDeltaJ { get; set; }
    /// <summary>Change in throughput-per-watt. Positive = more work per watt (CTR energy efficiency).</summary>
    public double EfficiencyPct { get; set; }
    public bool HasTemp { get; set; }
    public bool HasPower { get; set; }

    public string Summary => FormatSummary(this);

    public static string FormatSummary(BenchDelta d)
        => $"Faster {d.SpeedPct:+0.0;-0.0}%  ·  {d.TempDeltaC:+0.0;-0.0} C  ·  {d.PowerDeltaW:+0.0;-0.0} W  ·  efficiency {d.EfficiencyPct:+0.0;-0.0}%";

    public string Report(BenchRun? baseline = null, BenchRun? current = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"System  FASTER {SpeedPct:+0.0;-0.0}%  (geomean CPU/GPU/RAM)  harmonic {HarmonicSpeedPct:+0.0;-0.0}%");
        sb.AppendLine($"CPU     {CpuSpeedPct:+0.0;-0.0}%   {FmtTemp(CpuTempDeltaC)}   {FmtPower(CpuPowerDeltaW)}");
        sb.AppendLine($"GPU     {GpuSpeedPct:+0.0;-0.0}%   {FmtTemp(GpuTempDeltaC)}   {FmtPower(GpuPowerDeltaW)}");
        sb.AppendLine($"RAM     {RamSpeedPct:+0.0;-0.0}%");
        if (HasPower)
            sb.AppendLine($"Power   {PowerDeltaW:+0.0;-0.0} W combined  ·  efficiency {EfficiencyPct:+0.0;-0.0}% work/W");
        else
            sb.AppendLine("Power   not measured (CPU PPT from Ryzen Master when the app is Administrator; GPU uses ADLX board power)");
        if (HasTemp)
            sb.AppendLine($"Temp    {TempDeltaC:+0.0;-0.0} C  (negative = cooler)");
        else
            sb.AppendLine("Temp    not measured");
        if (EnergyDeltaJ is double e)
            sb.AppendLine($"Energy  {e:+0.0;-0.0} J  (integral of sampled watts)");
        if (baseline is not null && current is not null)
        {
            sb.AppendLine($"Saved   {baseline.Label} {baseline.Utc:u}  →  {current.Label} {current.Utc:u}");
        }
        return sb.ToString().TrimEnd();
    }

    static string FmtTemp(double? d) => d is double v ? $"{v:+0.0;-0.0} C" : "temp —";
    static string FmtPower(double? d) => d is double v ? $"{v:+0.0;-0.0} W" : "W —";
}

public static class BenchCompare
{
    public static double PercentChange(double baseline, double current)
    {
        if (Math.Abs(baseline) < 1e-12) return 0;
        return (current - baseline) / baseline * 100.0;
    }

    public static double Geomean(IEnumerable<double> values)
    {
        var xs = values.Where(v => v > 0).ToList();
        if (xs.Count == 0) return 0;
        return Math.Exp(xs.Average(Math.Log));
    }

    public static double HarmonicMean(IEnumerable<double> values)
    {
        var xs = values.Where(v => v > 0).ToList();
        if (xs.Count == 0) return 0;
        return xs.Count / xs.Sum(v => 1.0 / v);
    }

    public static double? DeltaBoth(double? baseline, double? current)
    {
        if (baseline is double a && current is double b) return b - a;
        return null;
    }

    public static double? LoadPowerW(BenchRun r)
    {
        double sum = 0;
        int n = 0;
        if (r.Cpu.AvgPowerW is double c && c > 0) { sum += c; n++; }
        if (r.Gpu.AvgPowerW is double g && g > 0) { sum += g; n++; }
        return n == 0 ? null : sum;
    }

    public static double? LoadEnergyJ(BenchRun r)
    {
        double sum = 0;
        int n = 0;
        foreach (var m in new[] { r.Cpu, r.Gpu, r.Ram })
        {
            var e = m.EnergyJ ?? EnergyOf(m);
            if (e is double j && j > 0) { sum += j; n++; }
        }
        return n == 0 ? null : sum;
    }

    public static double? EnergyOf(BenchMetrics m)
    {
        if (m.EnergyJ is double e && e > 0) return e;
        if (m.AvgPowerW is double w && w > 0 && m.DurationS > 0) return w * m.DurationS;
        return null;
    }

    public static double? PeakTemp(BenchMetrics m) => m.PeakTempC ?? m.AvgTempC;

    public static BenchMetrics FromSamples(IReadOnlyList<IReadOnlyDictionary<string, double?>> ticks, double throughput)
        => FromSamples(ticks, throughput, BenchKind.Gpu, durationS: ticks.Count);

    public static BenchMetrics FromSamples(
        IReadOnlyList<IReadOnlyDictionary<string, double?>> ticks,
        double throughput,
        BenchKind kind,
        double durationS)
    {
        var m = new BenchMetrics
        {
            Throughput = throughput,
            Samples = ticks.Count,
            DurationS = durationS > 0 ? durationS : ticks.Count,
        };
        List<double> powers, temps, clocks, volts;
        switch (kind)
        {
            case BenchKind.Cpu:
            case BenchKind.Ram:
                powers = Vals(ticks, "cpu_power_w", "ppt_w", "package_power_w");
                temps = Vals(ticks, "cpu_temp_c", "tctl_c", "package_temp_c");
                clocks = Vals(ticks, "cpu_clock_mhz", "effective_clock_mhz");
                volts = Vals(ticks, "cpu_voltage_mv");
                break;
            default:
                powers = Vals(ticks, "board_power_w", "power_w");
                temps = Vals(ticks, "hotspot_c", "gpu_temp_c");
                clocks = Vals(ticks, "clock_mhz");
                volts = Vals(ticks, "voltage_mv");
                break;
        }
        if (powers.Count > 0) m.AvgPowerW = powers.Average();
        if (temps.Count > 0)
        {
            m.PeakTempC = temps.Max();
            m.AvgTempC = temps.Average();
        }
        if (clocks.Count > 0) m.AvgClockMhz = clocks.Average();
        if (volts.Count > 0) m.AvgVoltageMv = volts.Average();
        if (m.AvgPowerW is double w && w > 0 && m.DurationS > 0)
            m.EnergyJ = w * m.DurationS;
        return m;
    }

    public static BenchDelta Compare(BenchRun baseline, BenchRun current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        var cpu = PercentChange(baseline.Cpu.Throughput, current.Cpu.Throughput);
        var gpu = PercentChange(baseline.Gpu.Throughput, current.Gpu.Throughput);
        var ram = PercentChange(baseline.Ram.Throughput, current.Ram.Throughput);
        var ratios = new List<double>();
        if (baseline.Cpu.Throughput > 0 && current.Cpu.Throughput > 0)
            ratios.Add(current.Cpu.Throughput / baseline.Cpu.Throughput);
        if (baseline.Gpu.Throughput > 0 && current.Gpu.Throughput > 0)
            ratios.Add(current.Gpu.Throughput / baseline.Gpu.Throughput);
        if (baseline.Ram.Throughput > 0 && current.Ram.Throughput > 0)
            ratios.Add(current.Ram.Throughput / baseline.Ram.Throughput);
        double speedPct = ratios.Count == 0 ? 0 : (Geomean(ratios) - 1) * 100.0;
        double harmPct = PercentChange(baseline.SystemScore, current.SystemScore);

        var cpuTemp = DeltaBoth(PeakTemp(baseline.Cpu), PeakTemp(current.Cpu));
        var gpuTemp = DeltaBoth(PeakTemp(baseline.Gpu), PeakTemp(current.Gpu));
        var temps = new List<double>();
        if (cpuTemp is double ct) temps.Add(ct);
        if (gpuTemp is double gt) temps.Add(gt);
        bool hasTemp = temps.Count > 0;
        double tempDelta = hasTemp ? temps.Average() : 0;

        var cpuP = DeltaBoth(baseline.Cpu.AvgPowerW, current.Cpu.AvgPowerW);
        var gpuP = DeltaBoth(baseline.Gpu.AvgPowerW, current.Gpu.AvgPowerW);
        var baseLoad = LoadPowerW(baseline);
        var curLoad = LoadPowerW(current);
        var powerDeltaN = DeltaBoth(baseLoad, curLoad);
        bool hasPower = powerDeltaN is not null;

        var baseEff = Eff(baseline);
        var curEff = Eff(current);
        double effPct = baseEff is double be && curEff is double ce && be > 0
            ? PercentChange(be, ce)
            : 0;

        return new BenchDelta
        {
            CpuSpeedPct = cpu,
            GpuSpeedPct = gpu,
            RamSpeedPct = ram,
            SpeedPct = speedPct,
            HarmonicSpeedPct = harmPct,
            TempDeltaC = tempDelta,
            CpuTempDeltaC = cpuTemp,
            GpuTempDeltaC = gpuTemp,
            PowerDeltaW = powerDeltaN ?? 0,
            CpuPowerDeltaW = cpuP,
            GpuPowerDeltaW = gpuP,
            EnergyDeltaJ = DeltaBoth(LoadEnergyJ(baseline), LoadEnergyJ(current)),
            EfficiencyPct = effPct,
            HasTemp = hasTemp,
            HasPower = hasPower,
        };
    }

    static double? Eff(BenchRun r)
    {
        double thr = r.Cpu.Throughput + r.Gpu.Throughput + r.Ram.Throughput;
        var w = LoadPowerW(r);
        if (w is not double watts || watts <= 0 || thr <= 0) return null;
        return thr / watts;
    }

    static List<double> Vals(IReadOnlyList<IReadOnlyDictionary<string, double?>> ticks, params string[] keys)
    {
        var list = new List<double>();
        foreach (var t in ticks)
        {
            foreach (var k in keys)
            {
                if (t.TryGetValue(k, out var v) && v is double d && !double.IsNaN(d) && !double.IsInfinity(d))
                {
                    list.Add(d);
                    break;
                }
            }
        }
        return list;
    }
}
