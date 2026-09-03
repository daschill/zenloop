namespace ZenLoop.App.Services;

public sealed class Limits
{
    public double GpuTempC { get; set; } = 88;
    public double HotspotC { get; set; } = 105;
    public double MemTempC { get; set; } = 96;
    public double CpuTempC { get; set; } = 92;
    public int MinVoltageMv { get; set; } = 1025;
    public int MaxClockMhz { get; set; } = 3000;
    public int MaxVramMhz { get; set; } = 2700;
    public int MinClockMhz { get; set; } = 2200;
}

public static class Safety
{
    public static string? Trip(IReadOnlyDictionary<string, double?> metrics, Limits limits)
    {
        return Over(metrics, "gpu_temp_c", limits.GpuTempC, "GPU temp")
            ?? Over(metrics, "hotspot_c", limits.HotspotC, "GPU hotspot")
            ?? Over(metrics, "mem_temp_c", limits.MemTempC, "VRAM temp")
            ?? Over(metrics, "cpu_temp_c", limits.CpuTempC, "CPU package");
    }

    static string? Over(IReadOnlyDictionary<string, double?> metrics, string key, double limit, string label)
    {
        if (!metrics.TryGetValue(key, out var v) || v is null) return null;
        return v.Value >= limit ? $"{label} {v.Value:0.0} >= {limit:0}" : null;
    }
}
