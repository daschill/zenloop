using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class PboApplyTests
{
    [Fact]
    public void Apply_then_read_back_returns_same_pbo_and_per_core_co()
    {
        var backend = new LoopbackSmuBackend();
        var smu = new SmuService(backend);

        var profile = CurveOptimizerSearch.Search(
            logicalCores: 8,
            coreStableAtSignedOffset: (core, off) => -off <= (core < 2 ? 20 : 35),
            pptWatts: 142,
            tdcAmps: 110,
            edcAmps: 170,
            boostOverrideMhz: 200,
            scalar: 1);

        var applied = smu.Apply(profile, PersistMode.Bios);
        Assert.True(applied.SessionApplied);
        Assert.True(applied.BiosPersisted);
        Assert.Equal("loopback", applied.Backend);
        Assert.Null(applied.Error);

        var read = smu.Read();
        Assert.NotNull(read);
        Assert.Equal(profile.PptWatts, read!.PptWatts);
        Assert.Equal(profile.TdcAmps, read.TdcAmps);
        Assert.Equal(profile.EdcAmps, read.EdcAmps);
        Assert.Equal(profile.BoostOverrideMhz, read.BoostOverrideMhz);
        Assert.Equal(profile.Cores.Count, read.Cores.Count);
        for (int i = 0; i < profile.Cores.Count; i++)
        {
            Assert.Equal(profile.Cores[i].Core, read.Cores[i].Core);
            Assert.Equal(profile.Cores[i].Sign, read.Cores[i].Sign);
            Assert.Equal(profile.Cores[i].Magnitude, read.Cores[i].Magnitude);
        }
    }

    [Fact]
    public void Session_persist_does_not_claim_bios_commit()
    {
        var backend = new LoopbackSmuBackend();
        var smu = new SmuService(backend);
        var profile = new CpuPboProfile
        {
            PptWatts = 120,
            TdcAmps = 95,
            EdcAmps = 140,
            BoostOverrideMhz = 200,
            Cores = [CurveOptimizerCore.FromSigned(0, -15)],
        };
        var r = smu.Apply(profile, PersistMode.Session);
        Assert.True(r.SessionApplied);
        Assert.False(r.BiosPersisted);
        Assert.Equal(PersistMode.Session, backend.LastPersist);
        Assert.Equal(-15, smu.Read()!.Cores[0].SignedOffset);
    }

    [Fact]
    public void Amd_backend_apply_invokes_helper_apply_not_a_copy_then_read_matches()
    {
        var profile = CurveOptimizerSearch.Search(
            8, (core, off) => -off <= 20,
            pptWatts: 142, tdcAmps: 110, edcAmps: 170, boostOverrideMhz: 200);

        string? lastCmd = null;
        CpuPboProfile? stored = null;
        var backend = new AmdRyzenMasterBackend(args =>
        {
            lastCmd = args[0];
            if (args[0] == "apply")
            {
                Assert.Equal("apply", args[0]);
                Assert.Contains("--ppt", args);
                Assert.Equal("142", args[args.ToList().IndexOf("--ppt") + 1]);
                Assert.Equal("110", args[args.ToList().IndexOf("--tdc") + 1]);
                Assert.Equal("170", args[args.ToList().IndexOf("--edc") + 1]);
                Assert.Equal("200", args[args.ToList().IndexOf("--boost") + 1]);
                Assert.Contains("--co", args);
                Assert.Equal("bios", args[args.ToList().IndexOf("--persist") + 1]);
                stored = profile.Clone();
                return """{"ok":true,"session_applied":true,"bios_persisted":true,"co_written":true,"backend":"amd-ryzen-master","ppt_watts":142,"tdc_amps":110,"edc_amps":170,"boost_override_mhz":200,"scalar":1,"cores":[{"core":0,"sign":"Negative","magnitude":20,"signed_offset":-20},{"core":1,"sign":"Negative","magnitude":20,"signed_offset":-20},{"core":2,"sign":"Negative","magnitude":20,"signed_offset":-20},{"core":3,"sign":"Negative","magnitude":20,"signed_offset":-20},{"core":4,"sign":"Negative","magnitude":20,"signed_offset":-20},{"core":5,"sign":"Negative","magnitude":20,"signed_offset":-20},{"core":6,"sign":"Negative","magnitude":20,"signed_offset":-20},{"core":7,"sign":"Negative","magnitude":20,"signed_offset":-20}]}""";
            }
            if (args[0] == "read")
            {
                Assert.NotNull(stored);
                return stored!.ToJson();
            }
            throw new InvalidOperationException("unexpected " + args[0]);
        });

        var smu = new SmuService(backend);
        var applied = smu.Apply(profile, PersistMode.Bios);
        Assert.Equal("apply", lastCmd);
        Assert.True(applied.SessionApplied);
        Assert.True(applied.BiosPersisted);
        Assert.Equal("amd-ryzen-master", applied.Backend);
        Assert.Null(applied.Error);

        var read = smu.Read();
        Assert.NotNull(read);
        Assert.Equal(142, read!.PptWatts);
        Assert.Equal(110, read.TdcAmps);
        Assert.Equal(170, read.EdcAmps);
        Assert.Equal(200, read.BoostOverrideMhz);
        Assert.Equal(8, read.Cores.Count);
        Assert.Equal(-20, read.Cores[0].SignedOffset);
    }

    [Fact]
    public void Bios_persist_without_bios_export_is_concrete_error_not_silent_ok()
    {
        var backend = new AmdRyzenMasterBackend(_ =>
            """{"ok":false,"session_applied":true,"bios_persisted":false,"backend":"amd-ryzen-master","error":"BIOS persist failed: Platform.dll has no SetPPTLimit_BIOS C export; CDefaultBIOS::SetPPTLimit_BIOS returned 4"}""");
        var smu = new SmuService(backend);
        var r = smu.Apply(new CpuPboProfile { PptWatts = 142, TdcAmps = 110, EdcAmps = 170, BoostOverrideMhz = 200 }, PersistMode.Bios);
        Assert.True(r.SessionApplied);
        Assert.False(r.BiosPersisted);
        Assert.False(r.Ok);
        Assert.Contains("BIOS persist failed", r.Error);
        Assert.DoesNotContain("winring", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CpuSmuProtocol_apply_args_encode_pbo_and_per_core_co()
    {
        var p = new CpuPboProfile
        {
            PptWatts = 142,
            TdcAmps = 110,
            EdcAmps = 170,
            BoostOverrideMhz = 200,
            Scalar = 1,
            Cores = [CurveOptimizerCore.FromSigned(0, -25), CurveOptimizerCore.FromSigned(1, -10)],
        };
        var args = CpuSmuProtocol.ApplyArgs(p, PersistMode.Session);
        Assert.Equal("apply", args[0]);
        Assert.Contains("--ppt", args);
        Assert.Contains("142", args);
        Assert.Contains("--tdc", args);
        Assert.Contains("110", args);
        Assert.Contains("--edc", args);
        Assert.Contains("170", args);
        Assert.Contains("--boost", args);
        Assert.Contains("200", args);
        Assert.Equal("session", args[args.ToList().IndexOf("--persist") + 1]);
        Assert.Equal("-25,-10", args[args.ToList().IndexOf("--co") + 1]);
    }

    [Fact]
    public void WithOut_appends_out_path_for_elevated_helper()
    {
        var path = @"C:\tmp\zenloop cpu.json";
        var args = CpuSmuProtocol.WithOut(CpuSmuProtocol.ApplyArgs(
            new CpuPboProfile { PptWatts = 142, TdcAmps = 110, EdcAmps = 170, BoostOverrideMhz = 200 },
            PersistMode.Bios), path);
        Assert.Equal("apply", args[0]);
        Assert.Equal("bios", args[args.ToList().IndexOf("--persist") + 1]);
        Assert.Equal("--out", args[^2]);
        Assert.Equal(path, args[^1]);
        var quoted = AmdRyzenMasterBackend.QuoteArgs(args);
        Assert.Contains("--persist bios", quoted);
        Assert.Contains("--out", quoted);
        Assert.Contains("\"C:\\tmp\\zenloop cpu.json\"", quoted);
    }

    [Fact]
    public void AppSettings_round_trips_windows_app_flags()
    {
        var path = Path.Combine(Path.GetTempPath(), "zenloop-app-settings-test.json");
        try
        {
            var s = new AppSettings { ApplyProfilesOnStart = true, StartWithWindows = true, MinimizeToTray = false };
            s.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.True(loaded.ApplyProfilesOnStart);
            Assert.True(loaded.StartWithWindows);
            Assert.False(loaded.MinimizeToTray);
            Assert.Equal(WindowsElevation.NoElevateArg, "--no-elevate");
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void NeedsElevation_when_helper_not_bound_or_not_elevated()
    {
        Assert.True(CpuSmuProtocol.NeedsElevation(
            """{"ok":false,"elevated":false,"error":"GetRmCpuParameters returned -1 (empty SMU buffer; limits not invented); CGraniteCPU not bound"}"""));
        Assert.True(CpuSmuProtocol.NeedsElevation("""{"ok":false,"device_error":5,"elevated":false}"""));
        Assert.False(CpuSmuProtocol.NeedsElevation(
            """{"ok":true,"elevated":true,"session_applied":true,"co_written":true}"""));
    }

    [Fact]
    public void Read_returns_null_when_helper_ok_false_even_if_ppt_fields_present()
    {
        var backend = new AmdRyzenMasterBackend(_ =>
            """{"ok":false,"error":"GetRmCpuParameters returned -1 (empty SMU buffer; limits not invented)","rm_status":-1,"ppt_watts":142,"tdc_amps":110,"edc_amps":170,"backend":"amd-ryzen-master"}""");
        var smu = new SmuService(backend);
        Assert.Null(smu.Read());
        Assert.Null(CpuSmuProtocol.TryParseProfile(
            """{"ok":false,"error":"GetRmCpuParameters returned -1 (empty SMU buffer; limits not invented)","ppt_watts":142,"tdc_amps":110,"edc_amps":170}"""));
    }

    [Fact]
    public void Session_apply_is_false_when_co_not_written()
    {
        var backend = new AmdRyzenMasterBackend(_ =>
            """{"ok":false,"session_applied":false,"bios_persisted":false,"co_written":false,"backend":"amd-ryzen-master","error":"per-core Curve Optimizer not written: CGraniteCPU not bound","ppt_watts":142,"tdc_amps":110,"edc_amps":170}""");
        var smu = new SmuService(backend);
        var r = smu.Apply(new CpuPboProfile
        {
            PptWatts = 142,
            TdcAmps = 110,
            EdcAmps = 170,
            Cores = [CurveOptimizerCore.FromSigned(0, -20)],
        }, PersistMode.Session);
        Assert.False(r.SessionApplied);
        Assert.False(r.CoWritten);
        Assert.False(r.Ok);
        Assert.Contains("Curve Optimizer not written", r.Error);
    }

    [Fact]
    public void ParseApply_does_not_trust_session_true_when_co_written_false()
    {
        var r = CpuSmuProtocol.ParseApply(
            """{"ok":true,"session_applied":true,"bios_persisted":true,"co_written":false,"backend":"amd-ryzen-master"}""");
        Assert.False(r.SessionApplied);
        Assert.False(r.BiosPersisted);
        Assert.False(r.CoWritten);
    }

    [Fact]
    public void Bios_persist_false_when_co_offsets_not_committed()
    {
        var backend = new AmdRyzenMasterBackend(_ =>
            """{"ok":false,"session_applied":true,"bios_persisted":false,"co_written":true,"backend":"amd-ryzen-master","error":"BIOS persist failed: SetCurveOptimizer core0=4 core1=4"}""");
        var smu = new SmuService(backend);
        var r = smu.Apply(new CpuPboProfile
        {
            PptWatts = 142,
            TdcAmps = 110,
            EdcAmps = 170,
            Cores = [CurveOptimizerCore.FromSigned(0, -20), CurveOptimizerCore.FromSigned(1, -10)],
        }, PersistMode.Bios);
        Assert.True(r.SessionApplied);
        Assert.True(r.CoWritten);
        Assert.False(r.BiosPersisted);
        Assert.False(r.Ok);
        Assert.Contains("BIOS persist failed", r.Error);
        Assert.Contains("SetCurveOptimizer", r.Error);
    }
}
