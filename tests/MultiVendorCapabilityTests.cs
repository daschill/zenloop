using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class MultiVendorCapabilityTests
{
    [Fact]
    public void Matrix_amd_path_exposes_real_apply_flags_from_caps()
    {
        var caps = WindowsControlSurface.FromCpuInfoJson(
            """
            {"ok":true,"elevated":true,"driver_running":true,"supported_processor":true,
             "co_bind":true,"bios_bind":true,"backend":"amd-ryzen-master",
             "capabilities":{"session_pbo":true,"session_co":true,"bios_pbo":true,"bios_co":true,
               "bios_ram":true,"boost_override":false,"curve_shaper":false,"gpu_bios_persist":false}}
            """,
            GpuControlProbe.FromRanges(true, true));

        var matrix = MultiVendorCapabilityMatrix.Build(
            "AMD Radeon RX 7900 XTX",
            "AMD Ryzen 7 9800X3D",
            caps);

        Assert.True(matrix.AmdCpu.Present);
        Assert.True(matrix.AmdGpu.Present);
        Assert.False(matrix.IntelCpu.Present);
        Assert.False(matrix.NvidiaGpu.Present);
        Assert.Contains(matrix.AmdCpu.Features, f => f.Feature == "SessionPBO" && f.CanApply);
        Assert.Contains(matrix.AmdGpu.Features, f => f.Feature == "SessionClockVoltage" && f.CanApply);
        Assert.Contains(matrix.AmdCpu.Features, f => f.Feature == "BoostOverride" && !f.CanApply);
        Assert.Contains(matrix.AmdCpu.Features, f => f.Feature == "CurveShaper" && !f.CanApply && !f.Detected);
        Assert.Contains("no-apply", matrix.FormatForUi());
        Assert.Contains("AMD CPU", matrix.OneLineSummary());
    }

    [Fact]
    public void Matrix_curve_shaper_export_found_without_abi_is_detect_only()
    {
        var caps = WindowsControlSurface.FromCpuInfoJson(
            """
            {"ok":true,"elevated":true,"driver_running":true,"supported_processor":true,
             "capabilities":{"session_pbo":true,"session_co":true,"bios_pbo":true,"bios_co":true,
               "bios_ram":true,"curve_shaper":false,"curve_shaper_export_found":true,
               "curve_shaper_note":"Curve Shaper C export found: GetCurveShaper (ABI not published)",
               "curve_shaper_probe":{"found":true,"platform_exports":10,"device_exports":10,"named_hits":1,"pe_hits":0,
                 "match":"GetCurveShaper","match_dll":"Platform.dll","abi_published":false}}}
            """);

        Assert.True(caps.CurveShaperExportFound);
        Assert.False(caps.CurveShaper);

        var matrix = MultiVendorCapabilityMatrix.Build(
            "AMD Radeon RX 7900 XTX",
            "AMD Ryzen 7 9800X3D",
            caps);

        var cs = Assert.Single(matrix.AmdCpu.Features, f => f.Feature == "CurveShaper");
        Assert.True(cs.Detected);
        Assert.False(cs.CanApply);
        Assert.Contains("ABI", cs.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_nvidia_detect_without_nvapi_refuses_power_apply()
    {
        var nvidia = NvidiaNvapiBackend.Probe(
            gpuName: "NVIDIA GeForce RTX 4080",
            fileExists: _ => false);

        Assert.True(nvidia.Detected);
        Assert.False(nvidia.NvapiPresent);
        Assert.False(nvidia.PowerLimitApplyAvailable);

        var matrix = MultiVendorCapabilityMatrix.Build(
            "NVIDIA GeForce RTX 4080",
            "AMD Ryzen 7 9800X3D",
            amdCaps: WindowsControlCapabilities.Unavailable("no amd gpu"),
            nvidia: nvidia);

        Assert.True(matrix.NvidiaGpu.Present);
        Assert.False(matrix.AmdGpu.Present);
        Assert.Contains(matrix.NvidiaGpu.Features, f => f.Feature == "SessionPowerLimit" && !f.CanApply);
        Assert.Contains(matrix.NvidiaGpu.Features, f => f.Feature == "SessionVoltageCurve" && !f.CanApply);
    }

    [Fact]
    public void Matrix_nvidia_power_apply_only_when_session_allows()
    {
        using var session = NvidiaNvapiSession.CreateForTests(powerLimitSetAvailable: true);
        var nvidia = new NvidiaGpuProbeResult
        {
            Detected = true,
            GpuName = "NVIDIA GeForce RTX 4080",
            NvapiPresent = true,
            NvapiInitialized = true,
            PowerLimitApplyAvailable = true,
        };

        var matrix = MultiVendorCapabilityMatrix.Build(
            "NVIDIA GeForce RTX 4080",
            "AMD Ryzen 9 7950X",
            nvidia: nvidia);

        Assert.Contains(matrix.NvidiaGpu.Features, f => f.Feature == "SessionPowerLimit" && f.CanApply);

        var ok = VendorApplyGuard.EnsureHonest(
            NvidiaNvapiBackend.ApplyPowerLimitPercent(10, session),
            canApply: true,
            "SessionPowerLimit");
        Assert.True(ok.SessionApplied);
        Assert.Equal(NvidiaNvapiBackend.BackendName, ok.Backend);
        Assert.Null(ok.Error);

        var refusedVoltage = NvidiaNvapiBackend.RefuseVoltageCurve();
        Assert.False(refusedVoltage.SessionApplied);
        Assert.Contains("voltage", refusedVoltage.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nvidia_apply_refuses_when_power_api_missing()
    {
        using var session = NvidiaNvapiSession.CreateForTests(powerLimitSetAvailable: false);
        var result = NvidiaNvapiBackend.ApplyPowerLimitPercent(5, session);
        Assert.False(result.SessionApplied);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Intel_cpu_lane_detect_only_refuse_apply()
    {
        var intel = IntelPlatformBackend.Probe(cpuName: "Intel(R) Core(TM) i9-14900K");
        Assert.True(intel.CpuDetected);
        Assert.Contains("WinRing0", IntelPlatformBackend.NoPublicUndervoltReason, StringComparison.OrdinalIgnoreCase);

        var refused = IntelPlatformBackend.RefuseApply("SessionUndervolt");
        Assert.False(refused.SessionApplied);
        Assert.False(refused.BiosPersisted);
        Assert.Equal(IntelPlatformBackend.BackendName, refused.Backend);

        var matrix = MultiVendorCapabilityMatrix.Build(
            "AMD Radeon RX 7800 XT",
            "Intel(R) Core(TM) i9-14900K",
            intel: intel);
        Assert.True(matrix.IntelCpu.Present);
        Assert.False(matrix.AmdCpu.Present);
        Assert.All(matrix.IntelCpu.Features.Where(f => f.Feature != "Detect"), f => Assert.False(f.CanApply));
    }

    [Fact]
    public void Intel_igcl_present_still_no_fake_apply()
    {
        var intel = IntelPlatformBackend.Probe(
            cpuName: "Intel(R) Core(TM) Ultra 7",
            gpuName: "Intel(R) Arc(TM) A770 Graphics",
            fileExists: p => p.Contains("ControlLib", StringComparison.OrdinalIgnoreCase));

        Assert.True(intel.IgclPresent);
        Assert.True(intel.GpuDetected);

        var matrix = MultiVendorCapabilityMatrix.Build(
            "Intel(R) Arc(TM) A770 Graphics",
            "Intel(R) Core(TM) Ultra 7",
            intel: intel);
        Assert.True(matrix.IntelGpu.Present);
        Assert.Contains(matrix.IntelGpu.Features, f => f.Feature == "SessionTune" && !f.CanApply);
    }

    [Fact]
    public void VendorApplyGuard_never_allows_disallowed_backends()
    {
        Assert.True(VendorApplyGuard.IsDisallowedBackend("WinRing0x64"));
        Assert.True(VendorApplyGuard.IsDisallowedBackend("raw-smu"));
        Assert.False(VendorApplyGuard.IsDisallowedBackend("amd-ryzen-master"));
        Assert.False(VendorApplyGuard.IsDisallowedBackend("nvapi64"));

        var cleared = VendorApplyGuard.EnsureHonest(
            new ApplyResult { SessionApplied = true, Error = "failed", Backend = "nvapi64" },
            canApply: true,
            "SessionPowerLimit");
        Assert.False(cleared.SessionApplied);

        var gated = VendorApplyGuard.EnsureHonest(
            new ApplyResult { SessionApplied = true, Backend = "nvapi64" },
            canApply: false,
            "SessionVoltageCurve");
        Assert.False(gated.SessionApplied);
        Assert.NotNull(gated.Error);
    }

    [Fact]
    public void PlatformSupport_nvidia_still_blocks_full_optimize_path()
    {
        var s = PlatformSupport.FromNames("NVIDIA GeForce RTX 4080", "AMD Ryzen 7 9800X3D");
        Assert.False(s.AmdGpuLikely);
        Assert.True(s.NvidiaGpuLikely);
        Assert.False(s.FullySupported);
        Assert.False(s.OptimizePathSupported);
        Assert.Contains("NVIDIA", s.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capability matrix", s.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemHardwareNames_registry_seam()
    {
        var cpu = SystemHardwareNames.TryReadProcessorName((_, _) => "AMD Ryzen 7 9800X3D");
        Assert.Equal("AMD Ryzen 7 9800X3D", cpu);

        var adapters = SystemHardwareNames.ListDisplayAdapterNames(
            () => new[] { "AMD Radeon RX 7900 XTX", "NVIDIA GeForce RTX 4080" });
        Assert.Equal(2, adapters.Count);
        Assert.Contains(adapters, ProductIdentity.LooksNvidia);
    }

    [Fact]
    public void ProductIdentity_about_mentions_matrix_not_blanket_ban()
    {
        var about = ProductIdentity.AboutText();
        Assert.Contains("WinRing0", about, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capability matrix", about, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No Intel/NVIDIA hardware control in this release", about);
    }
}
