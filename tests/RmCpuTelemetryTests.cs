using System.Runtime.InteropServices;
using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class RmCpuTelemetryTests
{
    [Fact]
    public void Parses_live_ppt_temp_voltage_clock_from_rm_full_stats()
    {
        var s = new RmFullStats
        {
            Init = 1,
            CoreCount = 8,
            PptLimit = 162,
            PptValue = 88.5f,
            VddcrVddPower = 70,
            CclkFmax = 5200,
            PeakSpeed = 5050,
            AvgCoreVoltage = 1.18,
            PeakCoreVoltage = 1.22,
            Temperature = 64.25,
        };
        var buf = StructBytes(s);
        var live = RmCpuTelemetry.Parse(buf);
        Assert.True(live.Ok);
        Assert.Equal(88.5, live.CpuPowerW!.Value, 3);
        Assert.Equal(64.25, live.CpuTempC!.Value, 3);
        Assert.Equal(1180, live.CpuVoltageMv!.Value, 3);
        Assert.Equal(5050, live.CpuClockMhz!.Value, 3);
    }

    [Fact]
    public void Empty_buffer_is_fail_closed()
    {
        var live = RmCpuTelemetry.Parse(new byte[256]);
        Assert.False(live.Ok);
        Assert.Null(live.CpuPowerW);
        Assert.Null(live.CpuTempC);
    }

    [Fact]
    public void MergeJsonTick_lifts_cpu_keys()
    {
        var d = new Dictionary<string, double?>();
        RmCpuTelemetry.MergeJsonTick(d, """{"type":"tick","ok":true,"metrics":{"cpu_power_w":91.2,"cpu_temp_c":70}}""");
        Assert.Equal(91.2, d["cpu_power_w"]);
        Assert.Equal(70, d["cpu_temp_c"]);
    }

    static byte[] StructBytes<T>(T value) where T : unmanaged
    {
        var buf = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(buf, in value);
        return buf;
    }
}
