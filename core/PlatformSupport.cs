namespace ZenLoop.Core;

/// <summary>
/// Platform messaging + multi-vendor capability summary. AMD is the primary Optimize path;
/// Intel/NVIDIA are detect + honest CanApply flags (never fake Apply success).
/// </summary>
public sealed record PlatformSupport(
    bool AmdGpuLikely,
    bool AmdCpuLikely,
    bool IntelCpuLikely,
    bool NvidiaGpuLikely,
    string Message)
{
    public bool FullySupported => AmdGpuLikely && AmdCpuLikely && !IntelCpuLikely && !NvidiaGpuLikely;
    public bool HasUnsupportedHint => !FullySupported;
    /// <summary>True when the one-click AMD Optimize path is appropriate.</summary>
    public bool OptimizePathSupported => AmdGpuLikely && AmdCpuLikely;

    public static PlatformSupport FromNames(string? gpuName, string? cpuName = null)
    {
        bool nvidia = ProductIdentity.LooksNvidia(gpuName);
        bool intel = ProductIdentity.LooksIntel(cpuName);
        bool amdGpu = ProductIdentity.LooksAmdGpu(gpuName);
        bool amdCpu = ProductIdentity.LooksAmdCpu(cpuName);

        // Empty names: assume AMD path until proven otherwise (helpers only talk AMD APIs).
        if (string.IsNullOrWhiteSpace(gpuName)) amdGpu = !nvidia;
        if (string.IsNullOrWhiteSpace(cpuName)) amdCpu = !intel;
        if (nvidia) amdGpu = false;
        if (intel) amdCpu = false;
        if (!amdGpu && !nvidia && !string.IsNullOrWhiteSpace(gpuName) && ProductIdentity.LooksIntel(gpuName))
            amdGpu = false;

        string msg;
        if (amdGpu && amdCpu && !intel && !nvidia)
            msg = "Supported path: AMD Ryzen + Radeon (ADLX + Ryzen Master).";
        else
            msg = ProductIdentity.UnsupportedPlatformMessage(
                intel ? (cpuName ?? "Intel") : null,
                nvidia ? (gpuName ?? "NVIDIA") : null);

        return new PlatformSupport(amdGpu, amdCpu, intel, nvidia, msg);
    }
}
