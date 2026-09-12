using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class CommercialProductTests
{
    [Fact]
    public void ProductIdentity_exposes_eula_and_branding()
    {
        Assert.Equal("ZenLoop", ProductIdentity.Name);
        Assert.True(ProductIdentity.EulaVersion >= 1);
        Assert.Contains("AS IS", ProductIdentity.EulaSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("own risk", ProductIdentity.ShortDisclaimer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not affiliated", ProductIdentity.NotAffiliated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CLR_CMOS", ProductIdentity.RecoverySummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Adrenalin", ProductIdentity.AboutText(), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(ProductIdentity.Version));
        Assert.StartsWith("ZenLoop", ProductIdentity.WindowTitle);
    }

    [Fact]
    public void UnsupportedPlatformMessage_calls_out_intel_and_nvidia()
    {
        var msg = ProductIdentity.UnsupportedPlatformMessage("Intel(R) Core", "NVIDIA GeForce RTX");
        Assert.Contains("Intel", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NVIDIA", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AMD", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlatformSupport_marks_nvidia_gpu_unsupported()
    {
        var s = PlatformSupport.FromNames("NVIDIA GeForce RTX 4080", "AMD Ryzen 7 9800X3D");
        Assert.False(s.AmdGpuLikely);
        Assert.True(s.AmdCpuLikely);
        Assert.True(s.HasUnsupportedHint);
        Assert.Contains("NVIDIA", s.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlatformSupport_accepts_radeon_path()
    {
        var s = PlatformSupport.FromNames("AMD Radeon RX 7900 XTX", "AMD Ryzen 7 9800X3D");
        Assert.True(s.FullySupported);
        Assert.False(s.HasUnsupportedHint);
    }

    [Fact]
    public void AppSettings_tracks_eula_acceptance()
    {
        var s = new AppSettings();
        Assert.False(s.HasAcceptedCurrentEula);
        s.AcceptedEulaVersion = ProductIdentity.EulaVersion;
        Assert.True(s.HasAcceptedCurrentEula);

        var dir = Path.Combine(Path.GetTempPath(), "zenloop-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "app-settings.json");
        try
        {
            s.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal(ProductIdentity.EulaVersion, loaded.AcceptedEulaVersion);
            Assert.True(loaded.HasAcceptedCurrentEula);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ProfilePack_round_trips_gpu_cpu_ram()
    {
        var pack = ProfilePack.FromParts(
            new GpuProfile { MaxMhz = 2800, VoltageMv = 1050, VramMhz = 2500, PowerPct = 10 },
            new CpuPboProfile
            {
                PptWatts = 120,
                TdcAmps = 100,
                EdcAmps = 150,
                Cores = { CurveOptimizerCore.FromSigned(0, -20), CurveOptimizerCore.FromSigned(1, -18) },
            },
            new RamTimingProfile { MemClockMhz = 3000, Tcl = 30, Trcd = 38, Trp = 38, Tras = 68, Trfc = 500, VddioMv = 1200 },
            goal: "balanced",
            notes: "unit test");

        Assert.Contains("GPU", pack.Summary(), StringComparison.Ordinal);
        Assert.Contains("CPU", pack.Summary(), StringComparison.Ordinal);
        Assert.Contains("RAM", pack.Summary(), StringComparison.Ordinal);

        var parsed = ProfilePack.Parse(pack.ToJson());
        Assert.Equal(ProfilePack.SchemaVersion, parsed.Schema);
        Assert.Equal(1050, parsed.Gpu!.VoltageMv);
        Assert.Equal(2800, parsed.Gpu.MaxMhz);
        Assert.Equal(120, parsed.Cpu!.PptWatts);
        Assert.Equal(2, parsed.Cpu.Cores.Count);
        Assert.Equal(30, parsed.Ram!.Tcl);
        Assert.Equal(6000, parsed.Ram.DataRateMts);

        var dir = Path.Combine(Path.GetTempPath(), "zenloop-pack-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tune.zenloop.json");
        try
        {
            parsed.SaveFile(path);
            var fromDisk = ProfilePack.TryLoadFile(path);
            Assert.NotNull(fromDisk);
            Assert.Equal(1050, fromDisk!.Gpu!.VoltageMv);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ProfilePack_rejects_empty_and_future_schema()
    {
        Assert.Throws<InvalidOperationException>(() => ProfilePack.Parse("""{"schema":1,"product":"ZenLoop"}"""));
        Assert.Throws<InvalidOperationException>(() =>
            ProfilePack.Parse("""{"schema":99,"gpu":{"max_mhz":2000,"voltage_mv":1100,"vram_mhz":2500,"power_pct":0}}"""));
    }

    [Fact]
    public void RamTimingGuidance_formats_and_warns()
    {
        var p = new RamTimingProfile
        {
            MemClockMhz = 3000,
            Tcl = 30,
            Trcd = 38,
            Trp = 38,
            Tras = 40,
            Trfc = 500,
            VddioMv = 1200,
            Expo = true,
        };
        Assert.Contains("DDR5-6000", RamTimingGuidance.FormatPrimaryLine(p));
        Assert.Contains("EXPO", RamTimingGuidance.Guidance(p), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZenTimings", RamTimingGuidance.Guidance(), StringComparison.OrdinalIgnoreCase);
        var warns = RamTimingGuidance.SoftWarnings(p);
        Assert.Contains(warns, w => w.Contains("tRAS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BiosWriteGuard_still_refuses_unprivileged_bios()
    {
        var refused = BiosWriteGuard.RefuseIfCannotPersistBios(PersistMode.Bios, isAdministrator: false, allowElevate: false);
        Assert.NotNull(refused);
        Assert.False(refused!.BiosPersisted);
    }
}
