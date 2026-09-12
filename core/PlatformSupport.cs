namespace ZenLoop.Core;

/// <summary>Honest AMD-only support messaging (no Intel/NVIDIA control in this product generation).</summary>
public sealed record PlatformSupport(bool AmdGpuLikely, bool AmdCpuLikely, string Message)
{
    public bool FullySupported => AmdGpuLikely && AmdCpuLikely;
    public bool HasUnsupportedHint => !FullySupported;

    public static PlatformSupport FromNames(string? gpuName, string? cpuName = null)
    {
        bool nvidia = ProductIdentity.LooksNvidia(gpuName);
        bool intel = ProductIdentity.LooksIntel(cpuName);
        bool amdGpu = !nvidia && (ProductIdentity.LooksAmdGpu(gpuName) || string.IsNullOrWhiteSpace(gpuName));
        bool amdCpu = !intel && (ProductIdentity.LooksAmdCpu(cpuName) || string.IsNullOrWhiteSpace(cpuName));

        // Empty names: assume AMD path until proven otherwise (helpers only talk AMD APIs).
        if (string.IsNullOrWhiteSpace(gpuName)) amdGpu = true;
        if (string.IsNullOrWhiteSpace(cpuName)) amdCpu = true;
        if (nvidia) amdGpu = false;
        if (intel) amdCpu = false;

        string msg;
        if (amdGpu && amdCpu)
            msg = "Supported path: AMD Ryzen + Radeon (ADLX + Ryzen Master).";
        else
            msg = ProductIdentity.UnsupportedPlatformMessage(
                intel ? (cpuName ?? "Intel") : null,
                nvidia ? (gpuName ?? "NVIDIA") : null);

        return new PlatformSupport(amdGpu, amdCpu, msg);
    }
}
