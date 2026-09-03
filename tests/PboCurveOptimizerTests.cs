using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class PboCurveOptimizerTests
{
    [Fact]
    public void Search_from_monotonic_per_core_oracle_matches_floors_and_pbo_fields()
    {
        // Core 0 fails beyond -20, core 1 beyond -10, cores 2–7 pass through -30.
        bool Oracle(int core, int signedOffset)
        {
            int mag = -signedOffset;
            int floor = core switch
            {
                0 => 20,
                1 => 10,
                _ => 30,
            };
            return mag <= floor;
        }

        var profile = CurveOptimizerSearch.Search(
            logicalCores: 8,
            coreStableAtSignedOffset: Oracle,
            pptWatts: 142,
            tdcAmps: 110,
            edcAmps: 170,
            boostOverrideMhz: 200,
            scalar: 1);

        Assert.True(profile.PboEnabled);
        Assert.Equal(142, profile.PptWatts);
        Assert.Equal(110, profile.TdcAmps);
        Assert.Equal(170, profile.EdcAmps);
        Assert.Equal(200, profile.BoostOverrideMhz);
        Assert.Equal(8, profile.Cores.Count);
        Assert.Equal(20, profile.Cores[0].Magnitude);
        Assert.Equal("Negative", profile.Cores[0].Sign);
        Assert.Equal(-20, profile.Cores[0].SignedOffset);
        Assert.Equal(10, profile.Cores[1].Magnitude);
        Assert.Equal(-10, profile.Cores[1].SignedOffset);
        for (int i = 2; i < 8; i++)
        {
            Assert.Equal(i, profile.Cores[i].Core);
            Assert.Equal(30, profile.Cores[i].Magnitude);
            Assert.Equal("Negative", profile.Cores[i].Sign);
        }
    }

    [Fact]
    public void CpuPboProfile_round_trips_pbo_and_per_core_curve_optimizer()
    {
        var original = CurveOptimizerSearch.Search(
            8,
            (core, off) => -off <= (core == 0 ? 15 : 25),
            720, 480, 640, 200, 10);

        var json = original.ToJson();
        var parsed = CpuPboProfile.Parse(json);

        Assert.Equal(original.PptWatts, parsed.PptWatts);
        Assert.Equal(original.TdcAmps, parsed.TdcAmps);
        Assert.Equal(original.EdcAmps, parsed.EdcAmps);
        Assert.Equal(original.BoostOverrideMhz, parsed.BoostOverrideMhz);
        Assert.Equal(original.Scalar, parsed.Scalar);
        Assert.Equal(original.Cores.Count, parsed.Cores.Count);
        for (int i = 0; i < original.Cores.Count; i++)
        {
            Assert.Equal(original.Cores[i].Core, parsed.Cores[i].Core);
            Assert.Equal(original.Cores[i].Sign, parsed.Cores[i].Sign);
            Assert.Equal(original.Cores[i].Magnitude, parsed.Cores[i].Magnitude);
            Assert.Equal(original.Cores[i].SignedOffset, parsed.Cores[i].SignedOffset);
        }
    }

    [Fact]
    public void CpuSmuProtocol_parse_profile_round_trips_helper_json()
    {
        const string json = """
            {"ok":true,"pbo_enabled":true,"ppt_watts":720,"tdc_amps":480,"edc_amps":640,"boost_override_mhz":200,"scalar":1,
             "cores":[
               {"core":0,"sign":"Negative","magnitude":20,"signed_offset":-20},
               {"core":1,"sign":"Negative","magnitude":10,"signed_offset":-10}
             ]}
            """;
        var p = CpuSmuProtocol.ParseProfile(json);
        Assert.Equal(720, p.PptWatts);
        Assert.Equal(480, p.TdcAmps);
        Assert.Equal(640, p.EdcAmps);
        Assert.Equal(200, p.BoostOverrideMhz);
        Assert.Equal(-20, p.Cores[0].SignedOffset);
        Assert.Equal(-10, p.Cores[1].SignedOffset);
        var again = CpuPboProfile.Parse(p.ToJson());
        Assert.Equal(720, again.PptWatts);
        Assert.Equal(-20, again.Cores[0].SignedOffset);
    }
}
