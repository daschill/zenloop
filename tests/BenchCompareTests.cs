using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class BenchCompareTests
{
    [Fact]
    public void PercentChange_is_current_minus_baseline_over_baseline()
    {
        Assert.Equal(10, BenchCompare.PercentChange(1000, 1100), 6);
        Assert.Equal(-10, BenchCompare.PercentChange(100, 90), 6);
        Assert.Equal(0, BenchCompare.PercentChange(0, 50));
    }

    [Fact]
    public void Compare_reports_faster_cooler_and_less_power()
    {
        var baseline = new BenchRun
        {
            Label = "baseline",
            Cpu = new BenchMetrics { Throughput = 1000 },
            Gpu = new BenchMetrics { Throughput = 2000, AvgPowerW = 300, PeakTempC = 80, AvgTempC = 75 },
            Ram = new BenchMetrics { Throughput = 400 },
        };
        var current = new BenchRun
        {
            Label = "tuned",
            Cpu = new BenchMetrics { Throughput = 1100 },
            Gpu = new BenchMetrics { Throughput = 2200, AvgPowerW = 270, PeakTempC = 72, AvgTempC = 68 },
            Ram = new BenchMetrics { Throughput = 420 },
        };

        var d = BenchCompare.Compare(baseline, current);

        Assert.Equal(10, d.CpuSpeedPct, 6);
        Assert.Equal(10, d.GpuSpeedPct, 6);
        Assert.True(d.SpeedPct > 0);
        Assert.Equal(-8, d.TempDeltaC, 6);
        Assert.Equal(-30, d.PowerDeltaW, 6);
        Assert.True(d.HasPower);
        Assert.True(d.HasTemp);
        Assert.True(d.EfficiencyPct > 0, "same-or-more work at fewer watts must raise efficiency");
        Assert.Contains("Faster", d.Summary);
        Assert.Contains("C", d.Summary);
        Assert.Contains("W", d.Summary);
        Assert.Contains("efficiency", d.Summary);
        Assert.Contains("FASTER", d.Report(baseline, current));
        Assert.Contains("GPU", d.Report());
    }

    [Fact]
    public void FromSamples_uses_real_tick_keys_for_power_and_hotspot()
    {
        var ticks = new List<IReadOnlyDictionary<string, double?>>
        {
            new Dictionary<string, double?> { ["board_power_w"] = 280, ["hotspot_c"] = 70, ["clock_mhz"] = 2500 },
            new Dictionary<string, double?> { ["board_power_w"] = 320, ["hotspot_c"] = 82, ["clock_mhz"] = 2700 },
        };
        var m = BenchCompare.FromSamples(ticks, throughput: 1234);
        Assert.Equal(1234, m.Throughput);
        Assert.Equal(2, m.Samples);
        Assert.Equal(300, m.AvgPowerW);
        Assert.Equal(82, m.PeakTempC);
        Assert.Equal(76, m.AvgTempC);
        Assert.Equal(2600, m.AvgClockMhz);
        Assert.Equal(600, m.EnergyJ); // 300 W * 2 s
    }

    [Fact]
    public void FromSamples_cpu_kind_does_not_steal_idle_gpu_board_power()
    {
        var ticks = new List<IReadOnlyDictionary<string, double?>>
        {
            new Dictionary<string, double?>
            {
                ["board_power_w"] = 45, ["hotspot_c"] = 40,
                ["cpu_power_w"] = 88, ["cpu_temp_c"] = 71, ["cpu_clock_mhz"] = 5050,
            },
        };
        var cpu = BenchCompare.FromSamples(ticks, 900, BenchKind.Cpu, 12);
        Assert.Equal(88, cpu.AvgPowerW);
        Assert.Equal(71, cpu.PeakTempC);
        Assert.Equal(5050, cpu.AvgClockMhz);
        Assert.Equal(88 * 12, cpu.EnergyJ);

        var gpu = BenchCompare.FromSamples(ticks, 50, BenchKind.Gpu, 12);
        Assert.Equal(45, gpu.AvgPowerW);
        Assert.Equal(40, gpu.PeakTempC);
    }

    [Fact]
    public void Missing_telemetry_is_not_invented_as_a_delta()
    {
        var baseline = new BenchRun
        {
            Cpu = new BenchMetrics { Throughput = 1000 },
            Gpu = new BenchMetrics { Throughput = 2000 },
        };
        var current = new BenchRun
        {
            Cpu = new BenchMetrics { Throughput = 1100 },
            Gpu = new BenchMetrics { Throughput = 2200 },
        };
        var d = BenchCompare.Compare(baseline, current);
        Assert.False(d.HasPower);
        Assert.False(d.HasTemp);
        Assert.Equal(0, d.PowerDeltaW);
        Assert.Equal(0, d.TempDeltaC);
        Assert.Equal(0, d.EfficiencyPct);
        Assert.Null(d.CpuPowerDeltaW);
        Assert.Null(d.GpuTempDeltaC);
        Assert.Contains("not measured", d.Report());
    }

    [Fact]
    public void Combined_load_power_is_cpu_ppt_plus_gpu_board()
    {
        var baseline = new BenchRun
        {
            Cpu = new BenchMetrics { Throughput = 1000, AvgPowerW = 120, PeakTempC = 80, DurationS = 10, EnergyJ = 1200 },
            Gpu = new BenchMetrics { Throughput = 2000, AvgPowerW = 300, PeakTempC = 85, DurationS = 10, EnergyJ = 3000 },
            Ram = new BenchMetrics { Throughput = 400 },
        };
        var current = new BenchRun
        {
            Cpu = new BenchMetrics { Throughput = 1080, AvgPowerW = 95, PeakTempC = 72, DurationS = 10, EnergyJ = 950 },
            Gpu = new BenchMetrics { Throughput = 2100, AvgPowerW = 250, PeakTempC = 76, DurationS = 10, EnergyJ = 2500 },
            Ram = new BenchMetrics { Throughput = 420 },
        };
        var d = BenchCompare.Compare(baseline, current);
        Assert.Equal(-75, d.PowerDeltaW, 6); // (95+250) - (120+300)
        Assert.Equal(-25, d.CpuPowerDeltaW);
        Assert.Equal(-50, d.GpuPowerDeltaW);
        Assert.Equal(-8, d.CpuTempDeltaC);
        Assert.Equal(-9, d.GpuTempDeltaC);
        Assert.Equal(-8.5, d.TempDeltaC, 6);
        Assert.Equal(-750, d.EnergyDeltaJ); // (950+2500) - (1200+3000)
        Assert.True(d.EfficiencyPct > 0);
        Assert.True(d.HarmonicSpeedPct > 0);
    }

    [Fact]
    public void Harmonic_mean_matches_3dmark_style_combined_score()
    {
        // 3DMark Time Spy uses a weighted harmonic mean of sub-scores.
        Assert.Equal(2, BenchCompare.HarmonicMean(new[] { 2.0, 2.0, 2.0 }), 6);
        var h = BenchCompare.HarmonicMean(new[] { 100.0, 200.0 });
        Assert.Equal(400.0 / 3.0, h, 6);
    }

    [Fact]
    public void Geomean_of_ten_percent_gains_is_ten_percent()
    {
        Assert.Equal(1.1, BenchCompare.Geomean(new[] { 1.1, 1.1, 1.1 }), 6);
    }

    [Fact]
    public void BenchRun_json_round_trips()
    {
        var run = new BenchRun
        {
            Label = "baseline",
            Gpu = new BenchMetrics { Throughput = 99, AvgPowerW = 250, PeakTempC = 71, EnergyJ = 2500 },
            Hwinfo = true,
        };
        var parsed = BenchRun.Parse(run.ToJson());
        Assert.Equal("baseline", parsed.Label);
        Assert.Equal(99, parsed.Gpu.Throughput);
        Assert.Equal(250, parsed.Gpu.AvgPowerW);
        Assert.Equal(71, parsed.Gpu.PeakTempC);
        Assert.Equal(2500, parsed.Gpu.EnergyJ);
        Assert.True(parsed.Hwinfo);
    }
}
