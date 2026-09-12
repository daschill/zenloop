using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class CurveDramMetricsTests
{
    [Fact]
    public void Probe_loopback_reports_curve_shaper_unavailable()
    {
        var caps = new SmuService(new LoopbackSmuBackend()).ProbeCapabilities();
        Assert.False(caps.CurveShaper);
        Assert.Contains("Curve Shaper", caps.CurveShaperReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CS=False", caps.StatusLine(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(caps.Limitations, l => l.Contains("Curve Shaper", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Probe_from_info_json_honors_curve_shaper_flag()
    {
        const string json = """
            {"ok":true,"elevated":true,"driver_running":true,"supported_processor":true,
             "co_bind":true,"bios_bind":true,"backend":"amd-ryzen-master",
             "capabilities":{"session_pbo":true,"session_co":true,"bios_pbo":true,"bios_co":true,
               "bios_ram":true,"curve_shaper":false,"boost_override":false,"manual_all_core_oc":false,
               "curve_shaper_note":"no Platform.dll/Device.dll Curve Shaper C export"}}
            """;
        var caps = WindowsControlSurface.FromCpuInfoJson(json);
        Assert.False(caps.CurveShaper);
        Assert.Contains("no Platform", caps.CurveShaperReason, StringComparison.OrdinalIgnoreCase);

        const string yes = """
            {"ok":true,"capabilities":{"session_pbo":true,"session_co":true,"bios_pbo":true,"bios_co":true,
               "bios_ram":true,"curve_shaper":true,"curve_shaper_note":"Curve Shaper C export found: GetCurveShaper"}}
            """;
        var on = WindowsControlSurface.FromCpuInfoJson(yes);
        Assert.True(on.CurveShaper);
        Assert.Contains("GetCurveShaper", on.CurveShaperReason);
    }

    [Fact]
    public void Apply_curve_shaper_refuses_when_unavailable()
    {
        var backend = new AmdRyzenMasterBackend(_ => throw new InvalidOperationException("should not run"));
        var r = backend.ApplyCurveShaper(new CurveShaperProfile { Enabled = true }, available: false);
        Assert.False(r.SessionApplied);
        Assert.False(r.BiosPersisted);
        Assert.Contains("Curve Shaper", r.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(backend.ReadCurveShaper(available: false));
    }

    [Fact]
    public void Surface_apply_curve_shaper_uses_capability_gate()
    {
        var surface = new WindowsControlSurface(new SmuService(new LoopbackSmuBackend()));
        var caps = surface.Probe();
        Assert.False(caps.CurveShaper);
        var r = surface.ApplyCurveShaper(new CurveShaperProfile { Enabled = true }, caps);
        Assert.False(r.Ok);
        Assert.Contains("Curve Shaper", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfilePack_round_trips_optional_curve_shaper()
    {
        var pack = ProfilePack.FromParts(
            gpu: null,
            cpu: null,
            ram: null,
            goal: "test",
            curveShaper: new CurveShaperProfile
            {
                Enabled = true,
                Bands = { CurveShaperBand.FromSigned(0, -10), CurveShaperBand.FromSigned(1, -5) },
            });
        Assert.Contains("CS×2", pack.Summary());
        var parsed = ProfilePack.Parse(pack.ToJson());
        Assert.NotNull(parsed.CurveShaper);
        Assert.True(parsed.CurveShaper!.Enabled);
        Assert.Equal(2, parsed.CurveShaper.Bands.Count);
        Assert.Equal(-10, parsed.CurveShaper.Bands[0].SignedOffset);
    }

    [Fact]
    public void RamTimingGuidance_expo_first_and_fabric_clocks()
    {
        var p = new RamTimingProfile
        {
            MemClockMhz = 3000,
            Tcl = 30,
            Trcd = 38,
            Trp = 36,
            Tras = 40,
            Trfc = 500,
            VddioMv = 1400,
            Expo = false,
            FclkMhz = 2000,
            MclkMhz = 3000,
        };
        var line = RamTimingGuidance.FormatPrimaryLine(p);
        Assert.Contains("DDR5-6000", line);
        Assert.Contains("FCLK 2000", line);
        Assert.Contains("MCLK 3000", line);

        var detail = RamTimingGuidance.FormatDetailBlock(p);
        Assert.Contains("EXPO: off", detail);
        Assert.Contains("FCLK: 2000", detail);

        var guide = RamTimingGuidance.Guidance(p);
        Assert.Contains("EXPO-first", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a fabricated DDR5", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe table of", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, RamTimingGuidance.ExpoFirstSteps().Count);

        var warns = RamTimingGuidance.SoftWarnings(p);
        Assert.Contains(warns, w => w.Contains("EXPO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warns, w => w.Contains("tRCD", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warns, w => w.Contains("tRAS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warns, w => w.Contains("VDDIO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warns, w => w.Contains("FCLK", StringComparison.OrdinalIgnoreCase) && w.Contains("MCLK"));
    }

    [Fact]
    public void RamTimingProtocol_parses_fclk_mclk_without_inventing_uclk()
    {
        var p = RamTimingProtocol.TryParse(
            """{"ok":true,"ram":{"mem_clock_mhz":3000,"vddio_mv":1200,"tcl":30,"trcd":36,"trp":36,"tras":76,"trfc":560,"expo":true,"fclk_mhz":2000,"mclk_mhz":3000}}""");
        Assert.NotNull(p);
        Assert.Equal(2000, p!.FclkMhz);
        Assert.Equal(3000, p.MclkMhz);
        Assert.Null(p.UclkMhz);
    }

    [Fact]
    public void MetricsSnapshotExport_writes_documented_json_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-metrics-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, MetricsSnapshotExport.FileName);
        try
        {
            var snap = MetricsSnapshotExport.FromMetrics(
                new Dictionary<string, double?>
                {
                    ["cpu_temp_c"] = 62.5,
                    ["cpu_power_w"] = 88.0,
                    ["board_power_w"] = 210.0,
                    ["hotspot_c"] = 78.0,
                },
                source: "optimize",
                goal: "balanced",
                summary: "faster +2.1%",
                pass: true);
            Assert.Equal("optimize", snap.Source);
            Assert.Equal(MetricsSnapshotExport.SchemaVersion, snap.Schema);
            Assert.True(snap.Schema >= 2);
            Assert.Equal("Pass", snap.Result);
            Assert.NotNull(snap.Live);
            Assert.Equal(62.5, snap.Live!.CpuTempC);

            var written = MetricsSnapshotExport.Write(snap, path, atomic: true);
            Assert.Equal(path, written);
            Assert.True(File.Exists(path));

            var loaded = MetricsSnapshotExport.TryLoad(path);
            Assert.NotNull(loaded);
            Assert.Equal(62.5, loaded!.Metrics["cpu_temp_c"]);
            Assert.Equal("balanced", loaded.Goal);
            Assert.True(loaded.Pass);
            Assert.Equal("Pass", loaded.Result);
            Assert.Contains(MetricsSnapshotExport.FileName, MetricsSnapshotExport.PathHelp);
            Assert.EndsWith(MetricsSnapshotExport.FileName, MetricsSnapshotExport.DefaultPath);
            Assert.Contains("ZenLoop", MetricsSnapshotExport.DefaultPath);
            Assert.Contains("Schema 2", MetricsSnapshotExport.PathHelp);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BiosWriteGuard_still_intact_for_curve_shaper_slice()
    {
        Assert.Contains("CLR_CMOS", BiosWriteGuard.RamBiosWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Curve Shaper", BiosWriteGuard.CurveShaperUnavailableNote, StringComparison.OrdinalIgnoreCase);
        var refused = BiosWriteGuard.RefuseIfCannotPersistBios(PersistMode.Bios, false, false);
        Assert.NotNull(refused);
        Assert.False(refused!.BiosPersisted);
    }
}
