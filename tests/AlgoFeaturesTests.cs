using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class AlgoFeaturesTests
{
    [Fact]
    public void Voltage_confirm_walks_up_when_margin_voltage_fails()
    {
        // Binary cliff at 1000; margin pushes daily to 1015, which still fails until 1030.
        bool SearchOracle(int v) => v >= 1000;
        bool ConfirmOracle(int v) => v >= 1030;

        var found = VoltageSearch.FindMinStable(1150, 700, 15, 700, 1150, SearchOracle);
        Assert.True(found.DailyMv >= 1000);
        int confirmed = VoltageSearch.ConfirmDaily(found.DailyMv, 1150, 15, 700, 1150, ConfirmOracle);
        Assert.Equal(1030, confirmed);
        Assert.True(confirmed <= 1150);
    }

    [Fact]
    public void Voltage_non_monotonic_oracle_falls_back_to_linear()
    {
        // Pass in mid-band but fail at stock — violates UV monotonicity (higher V must be safer).
        bool Oracle(int v) => v is >= 1000 and < 1100;

        var found = VoltageSearch.FindMinStable(1150, 700, 15, 700, 1150, Oracle);
        Assert.True(found.UsedLinearFallback);
        Assert.NotNull(found.FailVoltageMv);
        Assert.True(found.Evaluated.Count >= 2);
    }

    [Fact]
    public async Task CurveOptimizer_binary_uses_fewer_probes_than_linear_bound()
    {
        bool Oracle(int core, int signedOffset)
        {
            int mag = -signedOffset;
            int floor = core switch { 0 => 20, 1 => 10, _ => 30 };
            return mag <= floor;
        }

        var detailed = await CurveOptimizerSearch.SearchDetailedAsync(
            8,
            (c, o) => Task.FromResult(Oracle(c, o)),
            142, 110, 170, 200);

        Assert.Equal(20, detailed.Profile.Cores[0].Magnitude);
        Assert.Equal(10, detailed.Profile.Cores[1].Magnitude);
        Assert.Equal(30, detailed.Profile.Cores[2].Magnitude);
        int linearBound = CurveOptimizerSearch.MaxLinearProbes(8, 40, 5);
        Assert.True(detailed.ProbeCount < linearBound,
            $"binary probes {detailed.ProbeCount} should be < linear bound {linearBound}");
    }

    [Fact]
    public async Task CurveOptimizer_all_core_confirm_backs_off_worst_core()
    {
        var profile = new CpuPboProfile
        {
            PptWatts = 142,
            TdcAmps = 110,
            EdcAmps = 170,
            BoostOverrideMhz = 200,
            Cores =
            [
                CurveOptimizerCore.FromSigned(0, -30),
                CurveOptimizerCore.FromSigned(1, -10),
                CurveOptimizerCore.FromSigned(2, -20),
            ],
        };

        var confirmed = await CurveOptimizerSearch.ConfirmAllCoresAsync(
            profile,
            offsets =>
            {
                bool ok = -offsets[0] <= 20;
                return Task.FromResult(ok);
            });

        Assert.Equal(-20, confirmed.Cores[0].SignedOffset);
        Assert.Equal(-10, confirmed.Cores[1].SignedOffset);
        Assert.Equal(-20, confirmed.Cores[2].SignedOffset);
    }

    [Fact]
    public void Resume_invalidates_when_stock_fingerprint_changes()
    {
        var planned = AutotunePhases.Default;
        var remaining = ResumePlanner.RemainingStepsIfStockMatches(
            planned, "voltage",
            stockVolt: 1150, stockClock: 2500, stockVram: 2500,
            savedStockVolt: 1150, savedStockClock: 2400, savedStockVram: 2500);
        Assert.Equal(planned, remaining);

        var ok = ResumePlanner.RemainingStepsIfStockMatches(
            planned, "voltage",
            1150, 2500, 2500,
            1150, 2500, 2500);
        Assert.Equal("clock", ok[0]);
    }

    [Fact]
    public void ShouldSkip_matches_remaining_cursor()
    {
        var planned = AutotunePhases.Default;
        Assert.True(ResumePlanner.ShouldSkip("baseline", "voltage", planned));
        Assert.False(ResumePlanner.ShouldSkip("clock", "voltage", planned));
        Assert.True(ResumePlanner.ShouldSkip("clock", null, planned)); // next is baseline
    }

    [Fact]
    public void ClockSearch_balanced_caps_mild_oc_performance_goes_higher()
    {
        var bal = ClockSearch.Candidates("balanced", 2500, 2500, 500, 3000, 25);
        var perf = ClockSearch.Candidates("performance", 2500, 2500, 500, 3000, 25);
        Assert.True(bal.Count > 0);
        Assert.True(bal.Max() <= 2500 + Math.Max(150, 25 * 6));
        Assert.True(perf.Max() > bal.Max());
        Assert.Contains(3000, perf);

        var eff = ClockSearch.Candidates("efficiency", 2500, 2500, 500, 3000, 25);
        Assert.Equal(new[] { 2500, 2400, 2300 }, eff);
        Assert.Contains("mild", ClockSearch.DescribeGoal("balanced"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BenchSummary_omits_power_and_temp_when_missing()
    {
        var baseline = new BenchRun
        {
            Cpu = new BenchMetrics { Throughput = 1000 },
            Gpu = new BenchMetrics { Throughput = 2000 },
        };
        var current = new BenchRun
        {
            Cpu = new BenchMetrics { Throughput = 1100 },
            Gpu = new BenchMetrics { Throughput = 2200 },
        };
        var d = BenchCompare.Compare(baseline, current);
        Assert.False(d.HasPower);
        Assert.False(d.HasTemp);
        Assert.Contains("Faster", d.Summary);
        Assert.DoesNotContain(" W", d.Summary);
        Assert.DoesNotContain(" C", d.Summary);
        Assert.DoesNotContain("efficiency", d.Summary);
    }

    [Fact]
    public void FaultClassifier_ignores_bare_4101_without_display_context()
    {
        var noise = new FaultEvent("Service Control Manager", 4101, "Something unrelated.");
        Assert.False(FaultClassifier.IsFault(noise));

        var tdr = new FaultEvent("Display", 4101, "Display driver amdkmdag stopped responding and has recovered.");
        Assert.True(FaultClassifier.IsFault(tdr));
    }

    [Fact]
    public void OptimizeSummary_phase_checklist_and_post_tune()
    {
        var planned = AutotunePhases.Default;
        var check = OptimizeSummary.PhaseChecklist(planned, "voltage", "clock");
        Assert.Contains("✓ baseline", check);
        Assert.Contains("✓ voltage", check);
        Assert.Contains("… clock", check);

        var cpu = new CpuPboProfile
        {
            Cores =
            [
                CurveOptimizerCore.FromSigned(0, -20),
                CurveOptimizerCore.FromSigned(1, -10),
            ],
        };
        var text = OptimizeSummary.FormatPostTune(
            delta: null, gpuOk: true, gpuReason: null,
            dailyMv: 1085, clockMhz: 2700, vramMhz: 2600, cpu: cpu);
        Assert.Contains("GPU tune: PASS", text);
        Assert.Contains("1085 mV", text);
        Assert.Contains("Curve Optimizer", text);
        Assert.Contains("incomplete", text);
    }
}
