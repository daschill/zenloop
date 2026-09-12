using System.Reflection;

namespace ZenLoop.Core;

/// <summary>Branding, versioning, and legal copy shared by UI and packaging.</summary>
public static class ProductIdentity
{
    public const string Name = "ZenLoop";
    public const string Tagline = "One-click Optimize for AMD Ryzen + Radeon — faster, cooler, less power.";
    public const string Copyright = "Copyright (c) ZenLoop contributors. MIT License.";
    public const string NotAffiliated = "Not affiliated with Advanced Micro Devices, Inc. AMD, Ryzen, and Radeon are trademarks of Advanced Micro Devices, Inc.";

    /// <summary>Bump when EULA/disclaimer text that users must re-accept changes.</summary>
    public const int EulaVersion = 1;

    public const string ShortDisclaimer =
        "Overclocking and undervolting can crash Windows, corrupt data, or damage hardware. " +
        "You use ZenLoop at your own risk. Keep Adrenalin Default and CLR_CMOS recovery paths ready. " +
        "ZenLoop is not a substitute for BIOS Optimized Defaults.";

    public const string EulaSummary =
        "By continuing you agree that: (1) ZenLoop is provided AS IS with no warranty; " +
        "(2) you accept all risk of instability or hardware damage from voltage/clock/BIOS changes; " +
        "(3) ZenLoop requires Administrator rights and AMD Adrenalin + Ryzen Master; " +
        "(4) ZenLoop is not affiliated with AMD; (5) BIOS and RAM writes need your explicit confirmation each time.";

    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    public static string WindowTitle => $"{Name} {Version}";

    public static string AboutText()
    {
        return
            $"{Name} {Version}\n{Tagline}\n\n{Copyright}\n{NotAffiliated}\n\n" +
            "GPU: AMD ADLX (Adrenalin).\nCPU/BIOS/RAM mailbox: AMD Ryzen Master Platform/Device.\n" +
            "Curve Shaper: gated on a real RM C export (otherwise UI disabled).\n" +
            $"Metrics JSON: {MetricsSnapshotExport.DefaultPath}\n" +
            "No WinRing0. No Intel/NVIDIA hardware control in this release.\n\n" +
            ShortDisclaimer + "\n\n" +
            RecoverySummary;
    }

    /// <summary>Short recovery path shown in About; full guide is docs/RECOVERY.md (shipped next to the exe).</summary>
    public const string RecoverySummary =
        "Recovery: Adrenalin → Performance → Tuning → Default (or in-app Reset factory). " +
        "Clear session Curve Optimizer (CO=0 this boot). " +
        "BIOS Write stock is not UEFI Optimized Defaults. " +
        "If the PC will not POST after RAM/BIOS writes: CLR_CMOS / jumper, then Optimized Defaults — " +
        "see RECOVERY.md next to ZenLoop.exe.";

    public static string UnsupportedPlatformMessage(string? cpuVendor, string? gpuVendor)
    {
        var parts = new List<string>();
        if (LooksIntel(cpuVendor))
            parts.Add("Intel CPUs are not controlled by ZenLoop (use Intel XTU / ThrottleStop or BIOS).");
        if (LooksNvidia(gpuVendor))
            parts.Add("NVIDIA GPUs are not controlled by ZenLoop (use NVIDIA App / Afterburner).");
        if (parts.Count == 0)
            return "ZenLoop targets AMD Ryzen CPUs and AMD Radeon GPUs only.";
        parts.Add("AMD Ryzen + Radeon remain the supported path. Optimize and SMU writes stay AMD-only.");
        return string.Join(" ", parts);
    }

    public static bool LooksIntel(string? vendor)
        => !string.IsNullOrWhiteSpace(vendor) &&
           vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    public static bool LooksNvidia(string? vendor)
        => !string.IsNullOrWhiteSpace(vendor) &&
           (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("GeForce", StringComparison.OrdinalIgnoreCase));

    public static bool LooksAmdCpu(string? vendorOrName)
        => !string.IsNullOrWhiteSpace(vendorOrName) &&
           (vendorOrName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            vendorOrName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase));

    public static bool LooksAmdGpu(string? vendorOrName)
        => !string.IsNullOrWhiteSpace(vendorOrName) &&
           (vendorOrName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            vendorOrName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            vendorOrName.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase));
}
