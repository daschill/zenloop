namespace ZenLoop.Core;

/// <summary>
/// Coordinated startup re-apply of saved GPU + CPU packs with one retry.
/// Pure decision helpers; actual ADLX/SMU calls stay in the app layer.
/// </summary>
public static class StartupReapply
{
    public sealed record Plan(bool HasGpu, bool HasCpu, string Source);

    /// <summary>
    /// Prefer discrete startup.json + cpu-pbo.json; fall back to a profile pack file if provided.
    /// </summary>
    public static Plan? BuildPlan(bool hasGpuProfile, bool hasCpuProfile, bool hasPackFallback)
    {
        if (hasGpuProfile || hasCpuProfile)
            return new Plan(hasGpuProfile, hasCpuProfile, "saved profiles");
        if (hasPackFallback)
            return new Plan(true, true, "profile pack fallback");
        return null;
    }

    public static string Describe(Plan plan)
        => $"Startup re-apply from {plan.Source}: GPU={(plan.HasGpu ? "yes" : "no")} CPU={(plan.HasCpu ? "yes" : "no")}";

    /// <summary>Whether a failed apply should be retried once (transient helper/driver hiccup).</summary>
    public static bool ShouldRetry(int attempt, bool succeeded)
        => !succeeded && attempt == 0;
}
