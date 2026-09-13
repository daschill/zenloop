namespace ZenLoop.Core;

/// <summary>CPU or GPU vendor lanes shown in the multi-vendor capability matrix.</summary>
public enum HardwareVendor
{
    Unknown = 0,
    Amd = 1,
    Intel = 2,
    Nvidia = 3,
}

/// <summary>One row in the vendor × feature capability matrix.</summary>
public sealed class VendorFeatureCapability
{
    public required string Feature { get; init; }
    public bool Detected { get; init; }
    /// <summary>True only when a real public/signed API path can perform the write.</summary>
    public bool CanApply { get; init; }
    public string Api { get; init; } = "none";
    public string? Reason { get; init; }

    public string FormatLine()
    {
        string apply = CanApply ? "APPLY" : "no-apply";
        string why = string.IsNullOrWhiteSpace(Reason) ? "" : $" — {Reason}";
        return $"{Feature}: detect={Detected} {apply} api={Api}{why}";
    }
}

/// <summary>One vendor domain (e.g. AMD CPU, NVIDIA GPU) with honest feature flags.</summary>
public sealed class VendorLane
{
    public required HardwareVendor Vendor { get; init; }
    public required string Domain { get; init; }
    public bool Present { get; init; }
    public string? DeviceName { get; init; }
    public string Backend { get; init; } = "none";
    public IReadOnlyList<VendorFeatureCapability> Features { get; init; } = Array.Empty<VendorFeatureCapability>();

    public string Title => $"{VendorLabel(Vendor)} {Domain}";

    public static string VendorLabel(HardwareVendor v) => v switch
    {
        HardwareVendor.Amd => "AMD",
        HardwareVendor.Intel => "Intel",
        HardwareVendor.Nvidia => "NVIDIA",
        _ => "Unknown",
    };

    public string StatusBlock()
    {
        if (!Present)
            return $"{Title}: not detected";
        var lines = new List<string> { $"{Title}: {DeviceName ?? "(present)"}  backend={Backend}" };
        foreach (var f in Features)
            lines.Add("  · " + f.FormatLine());
        return string.Join(Environment.NewLine, lines);
    }

    public bool AnyCanApply => Features.Any(f => f.CanApply);
}

/// <summary>
/// Complete multi-vendor capability matrix. AMD-first product path; Intel/NVIDIA lanes are
/// detect + honest apply flags only — never invent Apply success.
/// </summary>
public sealed class MultiVendorCapabilityMatrix
{
    public required VendorLane AmdCpu { get; init; }
    public required VendorLane AmdGpu { get; init; }
    public required VendorLane IntelCpu { get; init; }
    public required VendorLane IntelGpu { get; init; }
    public required VendorLane NvidiaGpu { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public IEnumerable<VendorLane> AllLanes()
    {
        yield return AmdCpu;
        yield return AmdGpu;
        yield return IntelCpu;
        yield return IntelGpu;
        yield return NvidiaGpu;
    }

    public string FormatForUi()
    {
        var blocks = AllLanes().Select(l => l.StatusBlock()).ToList();
        if (Notes.Count > 0)
        {
            blocks.Add("Notes:");
            blocks.AddRange(Notes.Select(n => "  · " + n));
        }
        return string.Join(Environment.NewLine, blocks);
    }

    public string OneLineSummary()
    {
        var parts = new List<string>();
        foreach (var lane in AllLanes().Where(l => l.Present))
        {
            int apply = lane.Features.Count(f => f.CanApply);
            parts.Add($"{lane.Title}:{(apply > 0 ? $"{apply}-apply" : "detect-only")}");
        }
        return parts.Count == 0 ? "Multi-vendor: no adapters reported" : "Multi-vendor  " + string.Join("  ", parts);
    }

    /// <summary>Build matrix from names + AMD control caps + optional NVIDIA/Intel probes.</summary>
    public static MultiVendorCapabilityMatrix Build(
        string? gpuName,
        string? cpuName,
        WindowsControlCapabilities? amdCaps = null,
        NvidiaGpuProbeResult? nvidia = null,
        IntelPlatformProbeResult? intel = null,
        IReadOnlyList<string>? displayAdapters = null)
    {
        bool intelCpuName = ProductIdentity.LooksIntel(cpuName) || intel?.CpuDetected == true;
        bool amdCpuName = ProductIdentity.LooksAmdCpu(cpuName);
        if (string.IsNullOrWhiteSpace(cpuName))
            amdCpuName = !intelCpuName;
        if (intelCpuName)
            amdCpuName = false;

        bool nvidiaName = ProductIdentity.LooksNvidia(gpuName) ||
                          (displayAdapters?.Any(ProductIdentity.LooksNvidia) ?? false) ||
                          nvidia?.Detected == true;
        bool intelGpuName = ProductIdentity.LooksIntel(gpuName) ||
                            (displayAdapters?.Any(n => ProductIdentity.LooksIntel(n) &&
                                                       (n.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                                                        n.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                                                        n.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
                                                        n.Contains("Iris", StringComparison.OrdinalIgnoreCase))) ?? false) ||
                            intel?.GpuDetected == true;
        bool amdGpuName = ProductIdentity.LooksAmdGpu(gpuName);
        if (string.IsNullOrWhiteSpace(gpuName) && !nvidiaName && !intelGpuName)
            amdGpuName = true; // helpers are AMD ADLX; assume until proven otherwise
        if (nvidiaName || intelGpuName)
            amdGpuName = false;

        var caps = amdCaps ?? WindowsControlCapabilities.Unavailable("not probed");

        var amdCpu = new VendorLane
        {
            Vendor = HardwareVendor.Amd,
            Domain = "CPU",
            Present = amdCpuName || caps.HelperAvailable && caps.SupportedProcessor,
            DeviceName = string.IsNullOrWhiteSpace(cpuName) ? (amdCpuName ? "AMD Ryzen (assumed)" : null) : cpuName,
            Backend = caps.HelperAvailable ? (caps.Backend.Length > 0 ? caps.Backend : "amd-ryzen-master") : "none",
            Features =
            [
                Feat("Detect", amdCpuName || caps.SupportedProcessor, false, caps.Backend, null),
                Feat("SessionPBO", caps.SessionPbo, caps.SessionPbo, "Ryzen Master Platform.dll",
                    caps.SessionPbo ? null : "Session PBO unavailable"),
                Feat("SessionCO", caps.SessionCo, caps.SessionCo, "Ryzen Master CGraniteCPU",
                    caps.SessionCo ? null : "Curve Optimizer bind unavailable"),
                Feat("BiosPBO", caps.BiosPbo, caps.BiosPbo, "Ryzen Master CDefaultBIOS",
                    caps.BiosPbo ? null : "BIOS mailbox not bound"),
                Feat("BiosCO", caps.BiosCo, caps.BiosCo, "Ryzen Master CDefaultBIOS",
                    caps.BiosCo ? null : "BIOS CO unavailable"),
                Feat("BiosRAM", caps.BiosRam, caps.BiosRam, "Ryzen Master CDefaultBIOS",
                    caps.BiosRam ? null : "BIOS RAM unavailable"),
                Feat("CurveShaper",
                    caps.CurveShaper || caps.CurveShaperExportFound,
                    caps.CurveShaper, // CanApply only with published ABI
                    "Ryzen Master C export",
                    caps.CurveShaper
                        ? null
                        : (caps.CurveShaperExportFound
                            ? CurveShaperSupport.SignatureUnknownNote
                            : caps.CurveShaperReason)),
                Feat("BoostOverride", caps.BoostOverride, false, "none",
                    "No Platform.dll C export for PBO boost override"),
            ],
        };

        var amdGpu = new VendorLane
        {
            Vendor = HardwareVendor.Amd,
            Domain = "GPU",
            Present = amdGpuName,
            DeviceName = string.IsNullOrWhiteSpace(gpuName) ? (amdGpuName ? "Radeon (ADLX)" : null) : gpuName,
            Backend = amdGpuName && caps.GpuManual ? "adlx" : (amdGpuName ? "adlx-probe" : "none"),
            Features =
            [
                Feat("Detect", amdGpuName, false, "ADLX", null),
                Feat("SessionClockVoltage", caps.GpuManual, caps.GpuManual, "ADLX ManualGraphicsTuning",
                    caps.GpuManual ? null : "ADLX manual tuning ranges unavailable"),
                Feat("SessionFan", caps.GpuFan, caps.GpuFan, "ADLX FanTuning",
                    caps.GpuFan ? null : "ADLX fan API unavailable"),
                Feat("GpuBiosPersist", false, false, "none", "ADLX cannot write motherboard BIOS"),
            ],
        };

        bool intelCpu = intelCpuName;
        var intelCpuLane = new VendorLane
        {
            Vendor = HardwareVendor.Intel,
            Domain = "CPU",
            Present = intelCpu,
            DeviceName = intelCpu ? (cpuName ?? intel?.CpuName ?? "Intel CPU") : null,
            Backend = "none",
            Features =
            [
                Feat("Detect", intelCpu, false, "CPUID/name", null),
                Feat("SessionUndervolt", intelCpu, false, "none",
                    IntelPlatformBackend.NoPublicUndervoltReason),
                Feat("PowerLimits", intelCpu, false, "none",
                    "No redistributable signed third-party PL1/PL2 API (use Intel XTU or BIOS)"),
                Feat("BiosPersist", intelCpu, false, "none", "No ZenLoop UEFI path for Intel"),
            ],
        };

        bool intelGpu = intelGpuName && !amdGpuName && !nvidiaName;
        bool igcl = intel?.IgclPresent == true;
        var intelGpuLane = new VendorLane
        {
            Vendor = HardwareVendor.Intel,
            Domain = "GPU",
            Present = intelGpu || igcl,
            DeviceName = intelGpu ? (gpuName ?? intel?.GpuName ?? "Intel Graphics") : (igcl ? "Intel GPU (IGCL DLL present)" : null),
            Backend = igcl ? "igcl-detect" : "none",
            Features =
            [
                Feat("Detect", intelGpu || igcl, false, igcl ? "IGCL ControlLib.dll" : "name", null),
                Feat("SessionTune", igcl, false, "IGCL",
                    "IGCL detected but not vendored/wired for Apply in this build — no fake success"),
            ],
        };

        bool nvPresent = nvidiaName || nvidia?.Detected == true;
        bool nvapi = nvidia?.NvapiPresent == true;
        bool nvPower = nvidia?.PowerLimitApplyAvailable == true;
        var nvidiaLane = new VendorLane
        {
            Vendor = HardwareVendor.Nvidia,
            Domain = "GPU",
            Present = nvPresent,
            DeviceName = nvPresent
                ? (nvidia?.GpuName ?? gpuName ?? displayAdapters?.FirstOrDefault(ProductIdentity.LooksNvidia) ?? "NVIDIA GPU")
                : null,
            Backend = nvapi ? "nvapi64" : (nvPresent ? "detect-only" : "none"),
            Features =
            [
                Feat("Detect", nvPresent, false, nvapi ? "nvapi64.dll" : "name/PnP", null),
                Feat("SessionPowerLimit", nvPresent, nvPower, nvPower ? "NVAPI ClientPowerPolicies" : "nvapi64.dll",
                    nvPower
                        ? null
                        : (nvapi
                            ? "NVAPI loaded but ClientPowerPolicies SetStatus not resolved — refuse Apply"
                            : "nvapi64.dll not present (install NVIDIA driver)")),
                Feat("SessionVoltageCurve", nvPresent, false, "none",
                    "Voltage/clock curve needs proprietary tooling; ZenLoop will not fake Apply"),
                Feat("GpuBiosPersist", nvPresent, false, "none", "No motherboard BIOS path for NVIDIA from Windows"),
            ],
        };

        var notes = new List<string>
        {
            "AMD remains the primary Optimize path (ADLX + Ryzen Master).",
            "Apply succeeds only when CanApply=true for that feature — never a fake OK.",
            "No WinRing0, no raw SMU IOCTL, no unsigned kernel drivers.",
        };
        if (intelCpu)
            notes.Add(IntelPlatformBackend.NoPublicUndervoltReason);
        if (nvPresent && !nvPower)
            notes.Add("NVIDIA: use NVIDIA App / Afterburner for voltage curves; ZenLoop only applies when NVAPI power policies resolve.");

        return new MultiVendorCapabilityMatrix
        {
            AmdCpu = amdCpu,
            AmdGpu = amdGpu,
            IntelCpu = intelCpuLane,
            IntelGpu = intelGpuLane,
            NvidiaGpu = nvidiaLane,
            Notes = notes,
        };
    }

    static VendorFeatureCapability Feat(string feature, bool detected, bool canApply, string api, string? reason)
        => new()
        {
            Feature = feature,
            Detected = detected,
            CanApply = canApply && detected,
            Api = api,
            Reason = reason,
        };
}
