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
               "bios_ram":true,"curve_shaper":false,"curve_shaper_export_found":false,"boost_override":false,"manual_all_core_oc":false,
               "curve_shaper_note":"no Platform.dll/Device.dll Curve Shaper C export",
               "curve_shaper_probe":{"found":false,"platform_exports":120,"device_exports":80,"named_hits":0,"pe_hits":0,
                 "match":null,"match_dll":null,"abi_published":false,"alternative":"Use signed PBO + Curve Optimizer"}}}
            """;
        var caps = WindowsControlSurface.FromCpuInfoJson(json);
        Assert.False(caps.CurveShaper);
        Assert.False(caps.CurveShaperExportFound);
        Assert.False(caps.CurveShaperAbiPublished);
        Assert.Contains("no Platform", caps.CurveShaperReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(120, caps.CurveShaperProbe.PlatformExports);
        Assert.Contains("platform_exports=120", caps.CurveShaperProbe.SummaryLine());

        // Legacy/export-found without abi_published: Detect yes, CanApply no.
        const string exportOnly = """
            {"ok":true,"capabilities":{"session_pbo":true,"session_co":true,"bios_pbo":true,"bios_co":true,
               "bios_ram":true,"curve_shaper":true,"curve_shaper_export_found":true,
               "curve_shaper_note":"Curve Shaper C export found: GetCurveShaper in Platform.dll (ABI not published — ZenLoop will not invent band writes)",
               "curve_shaper_probe":{"found":true,"platform_exports":100,"device_exports":50,"named_hits":1,"pe_hits":0,
                 "match":"GetCurveShaper","match_dll":"Platform.dll","abi_published":false}}}
            """;
        var on = WindowsControlSurface.FromCpuInfoJson(exportOnly);
        Assert.False(on.CurveShaper); // CanApply requires abi_published
        Assert.True(on.CurveShaperExportFound);
        Assert.False(on.CurveShaperAbiPublished);
        Assert.Contains("GetCurveShaper", on.CurveShaperReason);
        Assert.True(CurveShaperSupport.LooksLikeExportFound(on.CurveShaperReason));
        Assert.Equal("GetCurveShaper", on.CurveShaperProbe.Match);

        // Future: published ABI unlocks CanApply.
        const string ready = """
            {"ok":true,"capabilities":{"session_pbo":true,"session_co":true,"bios_pbo":true,"bios_co":true,
               "bios_ram":true,"curve_shaper":true,"curve_shaper_export_found":true,
               "curve_shaper_note":"Curve Shaper C export ready: GetCurveShaper in Platform.dll",
               "curve_shaper_probe":{"found":true,"platform_exports":100,"device_exports":50,"named_hits":1,"pe_hits":0,
                 "match":"GetCurveShaper","match_dll":"Platform.dll","abi_published":true}}}
            """;
        var apply = WindowsControlSurface.FromCpuInfoJson(ready);
        Assert.True(apply.CurveShaper);
        Assert.True(apply.CurveShaperExportFound);
        Assert.True(apply.CurveShaperAbiPublished);
    }

    [Fact]
    public void Apply_curve_shaper_refuses_when_unavailable()
    {
        var backend = new AmdRyzenMasterBackend(_ => throw new InvalidOperationException("should not run"));
        var r = backend.ApplyCurveShaper(new CurveShaperProfile { Enabled = true }, available: false);
        Assert.False(r.SessionApplied);
        Assert.False(r.BiosPersisted);
        Assert.Contains("Curve Shaper", r.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PBO", r.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(backend.ReadCurveShaper(available: false));
    }

    [Fact]
    public void Apply_curve_shaper_refuses_empty_bands_even_when_available()
    {
        var backend = new AmdRyzenMasterBackend(_ => throw new InvalidOperationException("should not run"));
        var r = backend.ApplyCurveShaper(new CurveShaperProfile { Enabled = true }, available: true);
        Assert.False(r.SessionApplied);
        Assert.Contains("no band", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_curve_shaper_helper_abi_refuse_never_fakes_success()
    {
        var backend = new AmdRyzenMasterBackend(_ =>
            """{"ok":false,"session_applied":false,"bios_persisted":false,"error":"Curve Shaper export matched (GetCurveShaper in Platform.dll) but no published C ABI — refusing cs-apply"}""");
        var r = backend.ApplyCurveShaper(
            new CurveShaperProfile
            {
                Enabled = true,
                Bands = { CurveShaperBand.FromSigned(0, -10) },
            },
            available: true);
        Assert.False(r.SessionApplied);
        Assert.False(r.BiosPersisted);
        Assert.Contains("ABI", r.Error!, StringComparison.OrdinalIgnoreCase);
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
    public void CurveShaperAlternative_never_invents_bands()
    {
        Assert.Contains("will not invent", CurveShaperAlternative.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(CurveShaperAlternative.Steps().Count >= 4);
        Assert.DoesNotContain("band 0 =", CurveShaperAlternative.Guidance(), StringComparison.OrdinalIgnoreCase);
        var cpu = new CpuPboProfile
        {
            PptWatts = 120,
            Cores =
            {
                CurveOptimizerCore.FromSigned(0, -15),
                CurveOptimizerCore.FromSigned(1, -10),
            },
        };
        var summary = CurveShaperAlternative.FormatCoSummary(cpu);
        Assert.Contains("PPT 120", summary);
        Assert.Contains("Curve Optimizer", summary);
        Assert.Contains("not Curve Shaper", summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(CurveShaperSupport.ProbedExportNames.Count >= 18);
        Assert.Contains(CurveShaperSupport.ProbedExportNames, n => n.StartsWith('?'));
    }

    [Fact]
    public void ResolveCanApply_requires_abi_published()
    {
        Assert.False(CurveShaperSupport.ResolveCanApply(
            curveShaperFlag: true,
            exportFoundFlag: true,
            abiPublished: false,
            note: "Curve Shaper C export found: GetCurveShaper (ABI not published)"));
        Assert.True(CurveShaperSupport.ResolveCanApply(
            curveShaperFlag: true,
            exportFoundFlag: true,
            abiPublished: true,
            note: "ready"));
        Assert.False(CurveShaperSupport.ResolveCanApply(
            curveShaperFlag: true,
            exportFoundFlag: true,
            abiPublished: null,
            note: "export found"));
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

        var refuse = OptimizePreflight.FormatCurveShaperPackRefuse(parsed.CurveShaper, caps: null);
        Assert.Contains("refusing live CS apply", refuse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PBO", refuse, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("Stress after reboot", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe table of", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, RamTimingGuidance.ExpoFirstSteps().Count);
        Assert.True(RamTimingGuidance.StressAdvice().Count >= 4);

        var warns = RamTimingGuidance.SoftWarnings(p);
        Assert.Contains(warns, w => w.Contains("EXPO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warns, w => w.Contains("tRCD", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warns, w => w.Contains("tRAS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warns, w => w.Contains("VDDIO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warns, w => w.Contains("FCLK", StringComparison.OrdinalIgnoreCase) && w.Contains("MCLK"));

        Assert.False(p.HasAnySecondary);
        Assert.Contains("unread", RamTimingGuidance.FormatSecondaryLine(p), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZenTimings", RamTimingGuidance.FormatSecondaryBlock(p), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DramLab_shows_secondaries_only_when_readable()
    {
        var p = new RamTimingProfile
        {
            MemClockMhz = 3000,
            Tcl = 30,
            Trcd = 36,
            Trp = 36,
            Tras = 76,
            Trfc = 560,
            VddioMv = 1200,
            Expo = true,
            FclkMhz = 2000,
            UclkMhz = 3000,
            MclkMhz = 3000,
            Trc = 114,
            Tfaw = 32,
            TrrdS = 8,
            TwtrL = 16,
        };
        Assert.True(p.HasAnySecondary);
        var sec = RamTimingGuidance.FormatSecondaryBlock(p);
        Assert.Contains("tRC 114", sec);
        Assert.Contains("tFAW 32", sec);
        Assert.DoesNotContain("unread", sec, StringComparison.OrdinalIgnoreCase);
        var lab = RamTimingGuidance.FormatLabBlock(p);
        Assert.Contains("UCLK: 3000", lab);
        Assert.Contains("tRC 114", lab);
    }

    [Fact]
    public void DramLabExport_writes_json_without_inventing_secondaries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-dram-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, DramLabExport.FileName);
        try
        {
            var p = new RamTimingProfile
            {
                MemClockMhz = 3000,
                Tcl = 30,
                Trcd = 36,
                Trp = 36,
                Tras = 76,
                Trfc = 560,
                VddioMv = 1200,
                Expo = true,
                FclkMhz = 2000,
            };
            var report = DramLabExport.FromProfile(p, source: "test");
            Assert.False(report.SecondariesReadable);
            Assert.Contains("unread", report.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.True(report.StressAdvice.Count >= 4);
            Assert.Contains("WinRing0", report.Note, StringComparison.OrdinalIgnoreCase);

            var written = DramLabExport.Write(report, path, atomic: true);
            Assert.Equal(path, written);
            var loaded = DramLabExport.TryLoad(path);
            Assert.NotNull(loaded);
            Assert.Equal(2000, loaded!.Ram!.FclkMhz);
            Assert.Null(loaded.Ram.UclkMhz);
            Assert.Null(loaded.Ram.Trc);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
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
    public void RamTimingProtocol_parses_optional_secondaries_when_present()
    {
        var p = RamTimingProtocol.TryParse(
            """{"ok":true,"ram":{"mem_clock_mhz":3000,"vddio_mv":1200,"tcl":30,"trcd":36,"trp":36,"tras":76,"trfc":560,"expo":true,"trc":110,"tfaw":32,"uclk_mhz":3000}}""");
        Assert.NotNull(p);
        Assert.Equal(110, p!.Trc);
        Assert.Equal(32, p.Tfaw);
        Assert.Equal(3000, p.UclkMhz);
        Assert.True(p.HasAnySecondary);
    }

    [Fact]
    public void Hwinfo_fabric_merge_fills_unread_only()
    {
        var p = new RamTimingProfile { MemClockMhz = 3000, FclkMhz = 1800 };
        var readings = new List<HwinfoReading>
        {
            new(6, "FCLK", "MHz", 2000),
            new(6, "UCLK", "MHz", 3000),
            new(6, "MCLK", "MHz", 3000),
        };
        RamTimingGuidance.MergeFabricFromHwinfo(p, readings);
        Assert.Equal(1800, p.FclkMhz); // RM value preserved
        Assert.Equal(3000, p.UclkMhz);
        Assert.Equal(3000, p.MclkMhz);
        var picked = HwinfoSensors.PickFabricClocks(readings);
        Assert.Equal(2000, picked.FclkMhz);
        Assert.Equal(3000, picked.UclkMhz);
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
        Assert.Contains("never invents", BiosWriteGuard.CurveShaperWarning, StringComparison.OrdinalIgnoreCase);
        var refused = BiosWriteGuard.RefuseIfCannotPersistBios(PersistMode.Bios, false, false);
        Assert.NotNull(refused);
        Assert.False(refused!.BiosPersisted);
    }
}
