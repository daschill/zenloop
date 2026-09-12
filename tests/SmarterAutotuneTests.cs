using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class SmarterAutotuneTests
{
    [Fact]
    public void FaultClassifier_maps_reasons_to_kinds()
    {
        Assert.Equal(FaultKind.Whea, FaultClassifier.ClassifyReason("Microsoft-Windows-WHEA-Logger id=18: corrected hardware"));
        Assert.Equal(FaultKind.Tdr, FaultClassifier.ClassifyReason("Display id=4101: Display driver amdkmdag stopped responding and has recovered."));
        Assert.Equal(FaultKind.Thermal, FaultClassifier.ClassifyReason("GPU hotspot 106.0 >= 105"));
        Assert.Equal(FaultKind.Instability, FaultClassifier.ClassifyReason("stress failed"));
        Assert.Equal(FaultKind.None, FaultClassifier.ClassifyReason("ok"));
        Assert.Equal(FaultKind.Tdr, FaultClassifier.Classify(new FaultEvent("Display", 4101, "Display driver stopped responding")));
        Assert.Equal(FaultKind.Whea, FaultClassifier.MostSevere(new[] { FaultKind.Thermal, FaultKind.Whea, FaultKind.Tdr }));
    }

    [Fact]
    public void AdaptiveSearch_widens_steps_after_tdr_and_shrinks_near_cliff()
    {
        var afterTdr = AdaptiveSearch.FromFault(FaultKind.Tdr);
        Assert.True(afterTdr.VoltageMarginMv >= 25);
        Assert.True(afterTdr.PreferConservativeClock);
        Assert.True(afterTdr.ConfirmBackoffSteps >= 2);

        Assert.Equal(25, AdaptiveSearch.NextClockStep(25, FaultKind.None, nearCliff: false));
        Assert.True(AdaptiveSearch.NextClockStep(25, FaultKind.None, nearCliff: true) < 25);
        Assert.True(AdaptiveSearch.NextClockStep(25, FaultKind.Thermal, nearCliff: false) >= 50);

        int fine = AdaptiveSearch.NextVoltageStep(15, lastPassMv: 1085, lastFailMv: 1070);
        Assert.True(fine <= 15);
        Assert.True(fine >= 5);
    }

    [Fact]
    public void Voltage_search_floor_uses_prior_fail_cliff()
    {
        int floor = VoltageSearch.SearchFloorFromMemory(700, 15, priorDailyMv: 1085, priorFailMv: 1000);
        Assert.Equal(985, floor);
        Assert.True(VoltageSearch.MarginForFault(FaultKind.Whea) >= 25);
    }

    [Fact]
    public void TuneScore_efficiency_prefers_work_per_watt()
    {
        var hotFast = new TuneCandidate(2800, Throughput: 1000, PowerW: 300, TempC: 90);
        var coolEff = new TuneCandidate(2500, Throughput: 920, PowerW: 200, TempC: 70);
        var best = TuneScore.PickBest("efficiency", new[] { hotFast, coolEff }, referenceThroughput: 900);
        Assert.Equal(2500, best);

        var perfBest = TuneScore.PickBest("performance", new[] { hotFast, coolEff }, 900);
        Assert.Equal(2800, perfBest);
    }

    [Fact]
    public void SearchConvergence_detects_plateau_and_prune()
    {
        Assert.True(SearchConvergence.IsPlateau(new[] { 100.0, 100.5, 100.8 }, window: 3, epsilonPct: 1.5));
        Assert.False(SearchConvergence.IsPlateau(new[] { 100.0, 110.0, 120.0 }, window: 3, epsilonPct: 1.5));
        Assert.True(SearchConvergence.ShouldPruneClimb(new[] { 10.0, 12.0, 11.5, 11.0 }));
        Assert.False(SearchConvergence.ShouldPruneClimb(new[] { 10.0, 11.0, 12.0 }));
        Assert.Equal(new[] { 2500, 2550 }, SearchConvergence.PruneAfterFail(new[] { 2500, 2550, 2700, 2800 }, 2700, climbing: true));
    }

    [Fact]
    public async Task ClockSearch_walk_stops_on_fail_and_picks_scored_best_for_balanced()
    {
        // Throughput peaks at 2600 then declines; fail at 2700.
        async Task<(bool, double, double?, double?, string?)> Probe(int mhz)
        {
            await Task.Yield();
            if (mhz >= 2700) return (false, 0, null, null, "stress failed");
            double thr = mhz switch { 2550 => 100, 2600 => 112, 2650 => 111, _ => 90 };
            double power = 150 + (mhz - 2500) * 0.2;
            return (true, thr, power, 75, null);
        }

        var cands = new[] { 2550, 2600, 2650, 2700, 2750 };
        var walk = await ClockSearch.WalkAsync("balanced", cands, Probe);
        Assert.Equal(2600, walk.BestMhz);
        Assert.DoesNotContain(2750, walk.Evaluated); // pruned after fail
        Assert.Equal(FaultKind.Instability, walk.LastFault);
    }

    [Fact]
    public async Task CurveOptimizer_seeded_prior_uses_fewer_probes()
    {
        bool Oracle(int core, int signedOffset)
        {
            int mag = -signedOffset;
            int floor = core == 0 ? 20 : 10;
            return mag <= floor;
        }

        var cold = await CurveOptimizerSearch.SearchDetailedAsync(
            2, (c, o) => Task.FromResult(Oracle(c, o)),
            142, 110, 170, 200);

        var seeded = await CurveOptimizerSearch.SearchDetailedAsync(
            2, (c, o) => Task.FromResult(Oracle(c, o)),
            142, 110, 170, 200,
            priorMagnitudes: new[] { 20, 10 });

        Assert.Equal(20, seeded.Profile.Cores[0].Magnitude);
        Assert.Equal(10, seeded.Profile.Cores[1].Magnitude);
        Assert.True(seeded.ProbeCount <= cold.ProbeCount,
            $"seeded {seeded.ProbeCount} should be <= cold {cold.ProbeCount}");
    }

    [Fact]
    public async Task ConfirmAllCores_fault_backoff_skips_more_steps()
    {
        var profile = new CpuPboProfile
        {
            Cores =
            [
                CurveOptimizerCore.FromSigned(0, -30),
                CurveOptimizerCore.FromSigned(1, -10),
            ],
        };

        var confirmed = await CurveOptimizerSearch.ConfirmAllCoresAsync(
            profile,
            offsets => Task.FromResult(-offsets[0] <= 20),
            step: 5,
            backoffSteps: 2);

        Assert.Equal(-20, confirmed.Cores[0].SignedOffset);
    }

    [Fact]
    public void MachineTuneMemory_round_trips_and_fingerprint_gate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-mem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "machine-tune-memory.json");
        try
        {
            var mem = new MachineTuneMemory
            {
                Goal = "balanced",
                StockVoltMv = 1150,
                StockClockMhz = 2500,
                StockVramMhz = 2500,
                DailyVoltageMv = 1085,
                FailVoltageMv = 1000,
                LastGoodClockMhz = 2650,
                LastFaultKind = "Tdr",
                CoMagnitudes = [20, 15, 10],
            };
            mem.Save(path);

            var loaded = MachineTuneMemory.TryLoadMatching(path, 1150, 2500, 2500, "balanced");
            Assert.NotNull(loaded);
            Assert.Equal(1085, loaded!.DailyVoltageMv);
            Assert.Equal(FaultKind.Tdr, loaded.LastFault);
            Assert.Equal(3, loaded.CoMagnitudes!.Count);

            Assert.Null(MachineTuneMemory.TryLoadMatching(path, 1150, 2400, 2500, "balanced"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ClockSearch_candidates_honor_conservative_hint_and_prior()
    {
        var hints = AdaptiveSearch.FromFault(FaultKind.Thermal, AdaptiveSearch.Defaults(25));
        var bal = ClockSearch.Candidates("balanced", 2500, 2500, 500, 3000, 25, hints, priorGoodMhz: 2600);
        Assert.True(bal.Count > 0);
        // Thermal hint doubles clock step (50) and prefers conservative mild OC.
        Assert.True(hints.PreferConservativeClock);
        Assert.True(bal.Max() <= 2500 + Math.Max(hints.ClockStepMhz * 4, 100));
        Assert.Contains(2600, bal);
        Assert.Contains("mild", ClockSearch.DescribeGoal("balanced"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoSearchWindow_narrows_around_prior()
    {
        var (lo, hi) = AdaptiveSearch.CoSearchWindow(20, 5, 40);
        Assert.True(lo >= 10);
        Assert.True(hi <= 25);
        var full = AdaptiveSearch.CoSearchWindow(null, 5, 40);
        Assert.Equal(0, full.LoMag);
        Assert.Equal(40, full.HiMag);
    }
}
