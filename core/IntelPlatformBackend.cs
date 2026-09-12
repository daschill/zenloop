namespace ZenLoop.Core;

/// <summary>
/// Intel platform probe — detect only. There is no redistributable signed third-party
/// undervolt/power API ZenLoop can call without WinRing0/MSR hacks (explicitly out of scope).
/// </summary>
public sealed class IntelPlatformProbeResult
{
    public bool CpuDetected { get; init; }
    public string? CpuName { get; init; }
    public bool GpuDetected { get; init; }
    public string? GpuName { get; init; }
    /// <summary>Intel Graphics Control Library DLL present on disk (not wired for Apply yet).</summary>
    public bool IgclPresent { get; init; }
    public string? Note { get; init; }
}

public static class IntelPlatformBackend
{
    public const string NoPublicUndervoltReason =
        "Intel CPU: no public signed third-party undervolt API (post-Plundervolt). Use Intel XTU or BIOS — ZenLoop will not use WinRing0/MSR.";

    public const string BackendName = "intel-detect";

    /// <summary>Probe Intel CPU/GPU presence. File-system and name seams are injectable for tests.</summary>
    public static IntelPlatformProbeResult Probe(
        string? cpuName = null,
        string? gpuName = null,
        IReadOnlyList<string>? displayAdapters = null,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        bool cpu = ProductIdentity.LooksIntel(cpuName);
        string? resolvedCpu = cpu ? cpuName : null;

        var adapters = displayAdapters ?? Array.Empty<string>();
        string? intelGpu = null;
        if (ProductIdentity.LooksIntel(gpuName))
            intelGpu = gpuName;
        else
            intelGpu = adapters.FirstOrDefault(n =>
                ProductIdentity.LooksIntel(n) &&
                (n.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("Iris", StringComparison.OrdinalIgnoreCase)));

        bool igcl = IgclDllCandidates().Any(fileExists);

        return new IntelPlatformProbeResult
        {
            CpuDetected = cpu,
            CpuName = resolvedCpu,
            GpuDetected = intelGpu is not null,
            GpuName = intelGpu,
            IgclPresent = igcl,
            Note = cpu ? NoPublicUndervoltReason : (igcl ? "IGCL DLL present; Apply not wired (no fake success)." : null),
        };
    }

    /// <summary>Always refuses Apply — never sets <see cref="ApplyResult.SessionApplied"/>.</summary>
    public static ApplyResult RefuseApply(string feature)
    {
        return VendorApplyGuard.Refuse(
            BackendName,
            $"{feature}: {NoPublicUndervoltReason}");
    }

    public static IEnumerable<string> IgclDllCandidates()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string sys = Environment.SystemDirectory;
        yield return Path.Combine(sys, "ControlLib.dll");
        if (!string.IsNullOrEmpty(pf))
            yield return Path.Combine(pf, "Intel", "Intel Graphics Software", "ControlLib.dll");
        if (!string.IsNullOrEmpty(pfx86))
            yield return Path.Combine(pfx86, "Intel", "Intel Graphics Software", "ControlLib.dll");
    }
}
