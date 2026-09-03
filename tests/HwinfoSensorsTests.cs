using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class HwinfoSensorsTests
{
    [Fact]
    public void Picks_amd_tctl_and_package_power_not_gpu_or_limits()
    {
        var readings = new List<HwinfoReading>
        {
            new(1, "GPU Hot Spot", "°C", 78),
            new(1, "CPU (Tctl/Tdie)", "°C", 64.5),
            new(1, "CPU CCD1 (Tdie)", "°C", 62),
            new(5, "CPU PPT Limit", "W", 142),
            new(5, "CPU Package Power", "W", 88.2),
            new(5, "GPU Power", "W", 310),
            new(2, "CPU Core Voltage (SVI3 TFN)", "V", 1.18),
            new(6, "Core 0 T0 Effective Clock", "MHz", 5150),
            new(6, "Core 1 T0 Effective Clock", "MHz", 5050),
            new(6, "FCLK", "MHz", 2000),
        };

        var snap = HwinfoSensors.FromReadings(readings);
        Assert.True(snap.Available);
        Assert.Equal(64.5, snap.CpuTempC);
        Assert.Equal(88.2, snap.CpuPowerW);
        Assert.Equal(1180, snap.CpuVoltageMv);
        Assert.Equal(5100, snap.CpuClockMhz); // average of effective clocks
    }

    [Fact]
    public void Empty_readings_are_fail_closed()
    {
        var snap = HwinfoSensors.FromReadings(Array.Empty<HwinfoReading>());
        Assert.False(snap.Available);
        Assert.Null(snap.CpuTempC);
        Assert.Null(snap.CpuPowerW);
    }

    [Fact]
    public void MergeInto_writes_cpu_keys_used_by_FromSamples()
    {
        var dest = new Dictionary<string, double?> { ["board_power_w"] = 40 };
        HwinfoSensors.MergeInto(dest, new HwinfoSnapshot
        {
            Available = true,
            CpuTempC = 70,
            CpuPowerW = 100,
            CpuClockMhz = 5000,
            CpuVoltageMv = 1150,
        });
        Assert.Equal(70, dest["cpu_temp_c"]);
        Assert.Equal(100, dest["cpu_power_w"]);
        Assert.Equal(100, dest["ppt_w"]);
        Assert.Equal(5000, dest["cpu_clock_mhz"]);
        Assert.Equal(40, dest["board_power_w"]);
    }

    [Fact]
    public void ScoreTemp_rejects_gpu_and_tjmax_distance()
    {
        Assert.Equal(0, HwinfoSensors.ScoreTemp("GPU Temperature"));
        Assert.Equal(0, HwinfoSensors.ScoreTemp("Core Max Distance to TjMax"));
        Assert.True(HwinfoSensors.ScoreTemp("CPU (Tctl/Tdie)") > HwinfoSensors.ScoreTemp("CPU CCD1 (Tdie)"));
    }
}
