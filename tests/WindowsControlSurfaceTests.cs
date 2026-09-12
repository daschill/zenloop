using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class WindowsControlSurfaceTests
{
    [Fact]
    public void Probe_loopback_reports_session_and_bios_but_not_boost_or_gpu_bios()
    {
        var smu = new SmuService(new LoopbackSmuBackend());
        var caps = smu.ProbeCapabilities(gpu: GpuControlProbe.FromRanges(true, true));
        Assert.True(caps.HelperAvailable);
        Assert.True(caps.SessionPbo);
        Assert.True(caps.SessionCo);
        Assert.True(caps.BiosPbo);
        Assert.True(caps.BiosCo);
        Assert.True(caps.BiosRam);
        Assert.True(caps.BiosStockWrite);
        Assert.False(caps.BoostOverride);
        Assert.False(caps.ManualAllCoreOc);
        Assert.True(caps.GpuManual);
        Assert.True(caps.GpuFan);
        Assert.False(caps.GpuBiosPersist);
        Assert.Contains(caps.Limitations, l => l.Contains("boost", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("boost=False", caps.StatusLine(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_from_info_json_honors_bind_flags_and_nested_capabilities()
    {
        const string json = """
            {"ok":true,"elevated":true,"driver_running":true,"supported_processor":true,
             "co_bind":true,"bios_bind":false,"backend":"amd-ryzen-master",
             "capabilities":{"session_pbo":true,"session_co":true,"bios_pbo":false,"bios_co":false,
               "bios_ram":false,"boost_override":false,"manual_all_core_oc":false,"gpu_bios_persist":false,
               "bios_stock_write":false,"requires_reboot_after_bios":true}}
            """;
        var caps = WindowsControlSurface.FromCpuInfoJson(json, GpuControlProbe.FromRanges(true, false));
        Assert.True(caps.SessionPbo);
        Assert.True(caps.SessionCo);
        Assert.False(caps.BiosPbo);
        Assert.False(caps.BiosRam);
        Assert.False(caps.BiosStockWrite);
        Assert.False(caps.BoostOverride);
        Assert.True(caps.GpuManual);
        Assert.False(caps.GpuFan);
        Assert.False(caps.GpuBiosPersist);
    }

    [Fact]
    public void Probe_unavailable_backend_is_honest()
    {
        var smu = new SmuService(new UnavailableSmuBackend("no helper"));
        var caps = smu.ProbeCapabilities();
        Assert.False(caps.HelperAvailable);
        Assert.False(caps.SessionPbo);
        Assert.False(caps.BiosPbo);
        Assert.Contains("no helper", caps.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restore_session_stock_zeros_all_cores_without_bios()
    {
        var backend = new LoopbackSmuBackend();
        var surface = new WindowsControlSurface(new SmuService(backend));
        var seed = new CpuPboProfile
        {
            PptWatts = 142,
            TdcAmps = 110,
            EdcAmps = 170,
            BoostOverrideMhz = 200,
            Cores =
            [
                CurveOptimizerCore.FromSigned(0, -25),
                CurveOptimizerCore.FromSigned(1, -20),
                CurveOptimizerCore.FromSigned(2, -15),
            ],
        };
        var r = surface.RestoreSessionStock(seed, logicalCores: 3);
        Assert.True(r.SessionApplied);
        Assert.False(r.BiosPersisted);
        Assert.False(r.RequiresReboot);
        Assert.False(r.BoostOverrideApplied);
        Assert.Equal(PersistMode.Session, backend.LastPersist);
        var read = surface.Smu.Read()!;
        Assert.Equal(3, read.Cores.Count);
        Assert.All(read.Cores, c => Assert.Equal(0, c.SignedOffset));
        Assert.Equal(142, read.PptWatts);
    }

    [Fact]
    public void Write_stock_to_bios_persists_zero_co_and_requires_reboot()
    {
        var backend = new LoopbackSmuBackend();
        var surface = new WindowsControlSurface(new SmuService(backend));
        var seed = new CpuPboProfile
        {
            PptWatts = 120,
            TdcAmps = 95,
            EdcAmps = 140,
            Cores = [CurveOptimizerCore.FromSigned(0, -30), CurveOptimizerCore.FromSigned(1, -10)],
        };
        var r = surface.WriteStockToBios(seed, logicalCores: 2);
        Assert.True(r.SessionApplied);
        Assert.True(r.BiosPersisted);
        Assert.True(r.RequiresReboot);
        Assert.Equal(PersistMode.Bios, backend.LastPersist);
        Assert.All(surface.Smu.Read()!.Cores, c => Assert.Equal(0, c.SignedOffset));
    }

    [Fact]
    public void ParseApply_sets_requires_reboot_and_boost_false()
    {
        var r = CpuSmuProtocol.ParseApply(
            """{"ok":true,"session_applied":true,"bios_persisted":true,"requires_reboot":true,"co_written":true,"boost_override_applied":false,"backend":"amd-ryzen-master","boost_override_note":"no Platform.dll C export"}""");
        Assert.True(r.RequiresReboot);
        Assert.False(r.BoostOverrideApplied);
        Assert.Contains("no Platform", r.Note!);
    }

    [Fact]
    public void CapsArgs_and_guard_stock_warning_exist()
    {
        Assert.Equal("caps", CpuSmuProtocol.CapsArgs()[0]);
        Assert.Contains("CLR_CMOS", BiosWriteGuard.StockBiosWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be applied", BiosWriteGuard.BoostUnavailableNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureNoSilentBiosSuccess_sets_requires_reboot_on_success()
    {
        var r = BiosWriteGuard.EnsureNoSilentBiosSuccess(new ApplyResult
        {
            SessionApplied = true,
            BiosPersisted = true,
            CoWritten = true,
            Backend = "loopback",
        }, PersistMode.Bios);
        Assert.True(r.RequiresReboot);
        Assert.True(r.BiosPersisted);
    }
}
