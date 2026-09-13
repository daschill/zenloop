using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class AmdPrerequisitesTests
{
    [Fact]
    public void Probe_reports_both_present_when_dlls_exist()
    {
        var systemDir = Path.Combine("sysroot", "System32");
        var programFiles = Path.Combine("pf");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(systemDir, "amdadlx64.dll"),
            Path.Combine(programFiles, "AMD", "RyzenMaster", "bin", "Platform.dll"),
            Path.Combine(programFiles, "AMD", "RyzenMaster", "bin", "Device.dll"),
        };
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(programFiles, "AMD"),
            Path.Combine(programFiles, "AMD", "RyzenMaster", "bin"),
        };
        var status = AmdPrerequisites.Probe(files.Contains, dirs.Contains, programFiles, systemDir);
        Assert.True(status.AllReady);
        Assert.Contains("look installed", status.UserGuidance(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_missing_adrenalin_and_ryzen_master_gives_install_guidance()
    {
        var status = AmdPrerequisites.Probe(
            _ => false,
            _ => false,
            @"C:\Program Files",
            @"C:\Windows\System32");
        Assert.False(status.AdrenalinPresent);
        Assert.False(status.RyzenMasterPresent);
        var guide = status.UserGuidance();
        Assert.Contains("Adrenalin", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ryzen Master", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("amd.com", guide, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatHelperError_wraps_adlx_failure_with_guidance()
    {
        var msg = AmdPrerequisites.FormatHelperError(
            "ADLX initialize failed. Is AMD Software (Adrenalin) installed?",
            new PrerequisiteStatus(false, true, "missing", @"C:\rm"));
        Assert.Contains("Adrenalin", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("amdadlx", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LooksLikeMissingRyzenMaster_detects_platform_dll()
    {
        Assert.True(AmdPrerequisites.LooksLikeMissingRyzenMaster("Ryzen Master Platform.dll not found"));
        Assert.True(AmdPrerequisites.LooksLikeMissingRyzenMaster("AMDRyzenMasterDriver not installed"));
        Assert.False(AmdPrerequisites.LooksLikeMissingRyzenMaster("GPU hotspot 90 C"));
    }

    [Fact]
    public void OptimizePreflight_hard_blocks_without_adrenalin_or_rm()
    {
        var missing = new PrerequisiteStatus(false, false, "no adlx", "no rm");
        var blocked = OptimizePreflight.Evaluate(missing);
        Assert.True(blocked.HardBlock);
        Assert.False(blocked.CanProceed);
        Assert.Contains("Adrenalin", blocked.Message, StringComparison.OrdinalIgnoreCase);

        var ready = new PrerequisiteStatus(true, true, @"C:\adlx", @"C:\rm");
        var caps = WindowsControlSurface.FromCpuInfoJson(
            """
            {"ok":true,"elevated":true,"driver_running":false,"supported_processor":true,
             "co_bind":false,"bios_bind":true,"backend":"amd-ryzen-master",
             "capabilities":{"session_pbo":false,"session_co":false,"bios_pbo":true,"bios_co":true,
               "bios_ram":true,"curve_shaper":false,"gpu_bios_persist":false}}
            """,
            GpuControlProbe.FromRanges(false, false));
        var ok = OptimizePreflight.Evaluate(ready, caps);
        Assert.True(ok.CanProceed);
        Assert.False(ok.HardBlock);
        Assert.Contains(ok.Warnings, w => w.Contains("driver", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ok.Warnings, w => w.Contains("Curve Shaper", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OptimizePreflight_pack_cs_refuse_is_honest()
    {
        var cs = new CurveShaperProfile { Enabled = true, Bands = { CurveShaperBand.FromSigned(0, -5) } };
        var msg = OptimizePreflight.FormatCurveShaperPackRefuse(cs);
        Assert.Contains("refusing", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("applied Curve Shaper", msg, StringComparison.OrdinalIgnoreCase);
    }
}

public class BiosWriteGuardTests
{
    [Fact]
    public void Refuse_bios_when_not_admin_and_elevation_disabled()
    {
        var refused = BiosWriteGuard.RefuseIfCannotPersistBios(PersistMode.Bios, isAdministrator: false, allowElevate: false);
        Assert.NotNull(refused);
        Assert.False(refused!.BiosPersisted);
        Assert.False(refused.SessionApplied);
        Assert.Contains("refused", refused.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allows_bios_when_elevation_permitted()
    {
        Assert.Null(BiosWriteGuard.RefuseIfCannotPersistBios(PersistMode.Bios, isAdministrator: false, allowElevate: true));
        Assert.Null(BiosWriteGuard.RefuseIfCannotPersistBios(PersistMode.Bios, isAdministrator: true, allowElevate: false));
        Assert.Null(BiosWriteGuard.RefuseIfCannotPersistBios(PersistMode.Session, isAdministrator: false, allowElevate: false));
    }

    [Fact]
    public void EnsureNoSilentBiosSuccess_clears_persisted_when_error_present()
    {
        var r = new ApplyResult
        {
            SessionApplied = true,
            BiosPersisted = true,
            CoWritten = true,
            Error = "BIOS persist failed: export returned 4",
            Backend = "amd-ryzen-master",
        };
        var fixedUp = BiosWriteGuard.EnsureNoSilentBiosSuccess(r, PersistMode.Bios);
        Assert.False(fixedUp.BiosPersisted);
        Assert.Contains("BIOS persist failed", fixedUp.Error);
    }

    [Fact]
    public void EnsureNoSilentBiosSuccess_refuses_bios_true_without_co()
    {
        var r = new ApplyResult
        {
            SessionApplied = true,
            BiosPersisted = true,
            CoWritten = false,
            Backend = "amd-ryzen-master",
        };
        var fixedUp = BiosWriteGuard.EnsureNoSilentBiosSuccess(r, PersistMode.Bios);
        Assert.False(fixedUp.BiosPersisted);
        Assert.Contains("refusing silent BIOS success", fixedUp.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureNoSilentBiosSuccess_adds_error_when_bios_not_persisted()
    {
        var r = new ApplyResult
        {
            SessionApplied = true,
            BiosPersisted = false,
            CoWritten = true,
            Backend = "amd-ryzen-master",
        };
        var fixedUp = BiosWriteGuard.EnsureNoSilentBiosSuccess(r, PersistMode.Bios);
        Assert.False(fixedUp.BiosPersisted);
        Assert.Contains("no silent success", fixedUp.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureNoSilentBiosSuccess_leaves_session_mode_alone()
    {
        var r = new ApplyResult
        {
            SessionApplied = true,
            BiosPersisted = false,
            CoWritten = true,
            Backend = "amd-ryzen-master",
        };
        var fixedUp = BiosWriteGuard.EnsureNoSilentBiosSuccess(r, PersistMode.Session);
        Assert.True(fixedUp.SessionApplied);
        Assert.Null(fixedUp.Error);
    }

    [Fact]
    public void Amd_backend_default_allow_elevate_applies_bios_via_fake_helper()
    {
        var backend = new AmdRyzenMasterBackend(
            _ => """{"ok":true,"session_applied":true,"bios_persisted":true,"co_written":true,"backend":"amd-ryzen-master"}""",
            allowElevate: true);
        var r = backend.Apply(new CpuPboProfile { PptWatts = 100, TdcAmps = 80, EdcAmps = 120, Cores = [CurveOptimizerCore.FromSigned(0, -10)] }, PersistMode.Bios);
        Assert.True(r.BiosPersisted);
        Assert.True(r.CoWritten);
        Assert.Null(r.Error);
    }
}

public class FaultClassifierTests
{
    [Fact]
    public void Classifies_whea_and_tdr()
    {
        Assert.True(FaultClassifier.IsFault(new FaultEvent("Microsoft-Windows-WHEA-Logger", 18, "corrected hardware error")));
        Assert.True(FaultClassifier.IsFault(new FaultEvent("Display", 4101, "Display driver nvlddmkm stopped responding and has successfully recovered.")));
        Assert.False(FaultClassifier.IsFault(new FaultEvent("Application", 1000, " benigntimeout ")));
    }
}

/// <summary>
/// Live ADLX / Ryzen Master stress — skipped in CI via Category=LiveHardware filter.
/// Manual checklist on a Windows AMD Ryzen + Radeon PC (Adrenalin + Ryzen Master installed):
/// 1. zenloop-cpu.exe caps → curve_shaper false on stock RM; probe export counts &gt; 0; abi_published false
/// 2. zenloop-cpu.exe cs-apply --enabled 1 → refuses (no symbol or no published ABI); never invents bands
/// 3. Optimize this PC with both products installed → GPU ADLX + CO path; blocked MessageBox if either missing
/// 4. UV bake-off writes %LocalAppData%\ZenLoop\bakeoff report
/// 5. DRAM lab Read RAM + Export; RTSS OSD when RTSS running
/// Policy: no WinRing0 / raw SMU.
/// </summary>
public class LiveHardwareTests
{
    [Fact(Skip = "Requires live AMD Adrenalin + Ryzen Master on a Windows PC")]
    [Trait("Category", "LiveHardware")]
    public void Live_adlx_and_ryzen_master_smoke()
    {
        Assert.Fail("Not executed in CI.");
    }

    [Fact(Skip = "Requires live AMD Ryzen Master Platform/Device on a Windows PC")]
    [Trait("Category", "LiveHardware")]
    public void Live_curve_shaper_caps_and_cs_apply_refuse()
    {
        Assert.Fail("Not executed in CI — verify caps probe + cs-apply refuse on physical AMD box.");
    }
}
