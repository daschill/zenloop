using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class DepthUpgradeTests
{
    [Fact]
    public void OptimizePhases_resume_skips_completed_and_continues()
    {
        var planned = OptimizePhases.Default;
        var afterGpu = ResumePlanner.RemainingSteps(planned, "gpu");
        Assert.Equal(new[] { "cpu", "current" }, afterGpu);
        Assert.Equal(planned, ResumePlanner.RemainingSteps(planned, null));
        Assert.Empty(ResumePlanner.RemainingSteps(planned, "current"));
    }

    [Fact]
    public void OptimizeCheckpoint_round_trips_and_describe_resume()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "optimize-checkpoint.json");
            var ck = new OptimizeCheckpoint
            {
                Goal = "balanced",
                LastCompletedPhase = "baseline",
                SkipVram = true,
                DailyMv = 1100,
                ClockMhz = 2700,
            };
            ck.Save(path);
            var loaded = OptimizeCheckpoint.TryLoadIncomplete(path, "balanced");
            Assert.NotNull(loaded);
            Assert.True(loaded!.IsIncomplete);
            Assert.Equal("baseline", loaded.LastCompletedPhase);
            Assert.True(loaded.SkipVram);
            Assert.Contains("baseline", loaded.DescribeResume(), StringComparison.Ordinal);
            Assert.Contains("gpu", loaded.DescribeResume(), StringComparison.Ordinal);

            Assert.Null(OptimizeCheckpoint.TryLoadIncomplete(path, "performance"));
            ck.LastCompletedPhase = "done";
            ck.Save(path);
            Assert.Null(OptimizeCheckpoint.TryLoadIncomplete(path));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void OptimizeHistory_keeps_last_n_with_before_after()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "optimize-history.json");
            var hist = new OptimizeHistory();
            for (int i = 0; i < 25; i++)
            {
                hist.Add(new OptimizeHistoryEntry
                {
                    Goal = "balanced",
                    Passed = i % 2 == 0,
                    SpeedPct = i,
                    BaselineScore = 100,
                    TunedScore = 100 + i,
                    DailyMv = 1050 + i,
                    ClockMhz = 2600,
                    Summary = $"run {i}",
                }, maxEntries: 20);
            }
            Assert.Equal(20, hist.Entries.Count);
            Assert.Equal(24, hist.Entries[0].SpeedPct); // newest first
            hist.Save(path);
            var loaded = OptimizeHistory.Load(path);
            Assert.Equal(20, loaded.Entries.Count);
            var text = loaded.FormatRecent(3);
            Assert.Contains("before score", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PASS", text, StringComparison.Ordinal);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void OptimizeHistoryEntry_from_result_captures_delta()
    {
        var baseline = new BenchRun
        {
            Label = "baseline",
            Cpu = new BenchMetrics { Throughput = 100 },
            Gpu = new BenchMetrics { Throughput = 100 },
            Ram = new BenchMetrics { Throughput = 100 },
        };
        var tuned = new BenchRun
        {
            Label = "current",
            Cpu = new BenchMetrics { Throughput = 110 },
            Gpu = new BenchMetrics { Throughput = 110 },
            Ram = new BenchMetrics { Throughput = 110 },
        };
        var delta = BenchCompare.Compare(baseline, tuned);
        var cpu = new CpuPboProfile
        {
            Cores = { CurveOptimizerCore.FromSigned(0, -20), CurveOptimizerCore.FromSigned(1, -10) },
        };
        var entry = OptimizeHistoryEntry.FromResult(
            "balanced", true, delta, baseline, tuned,
            gpuOk: true, dailyMv: 1080, clockMhz: 2750, vramMhz: 2500, cpu, resumed: true);
        Assert.True(entry.Passed);
        Assert.True(entry.Resumed);
        Assert.Equal(1080, entry.DailyMv);
        Assert.Equal(15, entry.CpuCoAvg);
        Assert.Contains("balanced", entry.OneLine(), StringComparison.OrdinalIgnoreCase);
        Assert.True(entry.SpeedPct is > 0);
    }

    [Fact]
    public void AppProfileStore_match_and_upsert()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "app-profiles.json");
            var store = new AppProfileStore { Enabled = true };
            store.Upsert("game.exe", Path.Combine(dir, "game.zenloop.json"), "Cool Game");
            store.Upsert("editor.exe", Path.Combine(dir, "ed.zenloop.json"));
            Assert.Equal(2, store.Rules.Count);
            Assert.Equal("Cool Game", store.Match(@"C:\Games\game.exe")!.DisplayName);
            Assert.Equal("editor.exe", store.Match("EDITOR.EXE")!.Match);
            Assert.Null(store.Match("other.exe"));
            store.Enabled = false;
            Assert.Null(store.Match("game.exe"));
            store.Enabled = true;
            store.Save(path);
            var loaded = AppProfileStore.Load(path);
            Assert.Equal(2, loaded.Rules.Count);
            Assert.False(loaded.AutoApply);
            Assert.Contains("auto-apply off", loaded.StatusLine(), StringComparison.OrdinalIgnoreCase);
            loaded.AutoApply = true;
            Assert.Contains("auto-apply on", loaded.StatusLine(), StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void AppProfileMatcher_finds_rule_for_running_process_name()
    {
        var self = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "dotnet");
        var store = new AppProfileStore { Enabled = true };
        store.Upsert(self, "pack.json", "self");
        // May or may not find depending on process enumeration permissions; Match alone is authoritative.
        Assert.NotNull(store.Match(self));
        Assert.NotNull(store.Match(self + ".exe"));
        Assert.NotNull(AppProfileMatcher.MatchForeground(store, @"C:\Games\" + self + ".exe"));
        Assert.Null(AppProfileMatcher.MatchForeground(store, "other.exe"));
    }

    [Fact]
    public void AppProfileHotApply_gating_respects_toggle_busy_debounce_and_pack()
    {
        var store = new AppProfileStore { Enabled = true, AutoApply = true };
        var pack = Path.Combine(Path.GetTempPath(), "zenloop-hot-" + Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(pack, "{}");
        try
        {
            var rule = store.Upsert("game.exe", pack, "Game");
            var now = DateTime.UtcNow;
            bool Exists(string p) => File.Exists(p);

            Assert.Equal(AppProfileApplyDecision.SkipAutoApplyOff,
                AppProfileHotApply.Decide(store, autoApplyEnabled: false, optimizeBusy: false, rule,
                    null, now, now - TimeSpan.FromSeconds(5), packExists: Exists));
            Assert.Equal(AppProfileApplyDecision.SkipBusy,
                AppProfileHotApply.Decide(store, true, optimizeBusy: true, rule,
                    null, now, now - TimeSpan.FromSeconds(5), packExists: Exists));
            Assert.Equal(AppProfileApplyDecision.SkipNoMatch,
                AppProfileHotApply.Decide(store, true, false, matched: null,
                    null, now, now - TimeSpan.FromSeconds(5), packExists: Exists));
            Assert.Equal(AppProfileApplyDecision.SkipDebounce,
                AppProfileHotApply.Decide(store, true, false, rule,
                    null, now, focusChangedUtc: now, debounce: TimeSpan.FromSeconds(2), packExists: Exists));
            Assert.Equal(AppProfileApplyDecision.SkipAlreadyApplied,
                AppProfileHotApply.Decide(store, true, false, rule,
                    lastAppliedPackPath: pack, now, now - TimeSpan.FromSeconds(5), packExists: Exists));
            Assert.Equal(AppProfileApplyDecision.SkipMissingPack,
                AppProfileHotApply.Decide(store, true, false, rule,
                    null, now, now - TimeSpan.FromSeconds(5), packExists: _ => false));
            Assert.Equal(AppProfileApplyDecision.Apply,
                AppProfileHotApply.Decide(store, true, false, rule,
                    null, now, now - TimeSpan.FromSeconds(5), packExists: Exists));
        }
        finally { try { File.Delete(pack); } catch { /* ignore */ } }
    }

    [Fact]
    public void AppProfileHotApplySession_debounces_then_applies_once_per_focus()
    {
        var store = new AppProfileStore { Enabled = true };
        var pack = Path.Combine(Path.GetTempPath(), "zenloop-hot2-" + Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(pack, "{}");
        try
        {
            store.Upsert("game.exe", pack);
            var session = new AppProfileHotApplySession();
            var t0 = DateTime.UtcNow;
            var (d0, m0) = session.Tick(store, autoApplyEnabled: true, optimizeBusy: false, "game.exe", t0,
                debounce: TimeSpan.FromSeconds(2), packExists: File.Exists);
            Assert.Equal(AppProfileApplyDecision.SkipDebounce, d0);
            Assert.NotNull(m0);

            var (d1, _) = session.Tick(store, true, false, "game.exe", t0.AddSeconds(3),
                debounce: TimeSpan.FromSeconds(2), packExists: File.Exists);
            Assert.Equal(AppProfileApplyDecision.Apply, d1);
            session.MarkApplied(pack);

            var (d2, _) = session.Tick(store, true, false, "game.exe", t0.AddSeconds(4),
                debounce: TimeSpan.FromSeconds(2), packExists: File.Exists);
            Assert.Equal(AppProfileApplyDecision.SkipAlreadyApplied, d2);

            // Busy Optimize must not apply even after debounce.
            session.ClearApplied();
            var (dBusy, _) = session.Tick(store, true, optimizeBusy: true, "game.exe", t0.AddSeconds(10),
                debounce: TimeSpan.FromSeconds(2), packExists: File.Exists);
            Assert.Equal(AppProfileApplyDecision.SkipBusy, dBusy);
        }
        finally { try { File.Delete(pack); } catch { /* ignore */ } }
    }

    [Fact]
    public void OptimizeSummary_end_banner_includes_pass_fail_scores_and_abort()
    {
        var planned = OptimizePhases.Default;
        var text = OptimizeSummary.PhaseChecklist(planned, "baseline", "gpu");
        Assert.Contains("✓ baseline", text);
        Assert.Contains("… gpu", text);
        Assert.Contains("· cpu", text);

        var cpu = new CpuPboProfile
        {
            Cores = { CurveOptimizerCore.FromSigned(0, -20), CurveOptimizerCore.FromSigned(1, -10) },
        };
        var baseline = new BenchRun
        {
            Label = "baseline",
            Cpu = new BenchMetrics { Throughput = 100, PeakTempC = 70, AvgPowerW = 100 },
            Gpu = new BenchMetrics { Throughput = 100, PeakTempC = 80, AvgPowerW = 200 },
            Ram = new BenchMetrics { Throughput = 100 },
        };
        var tuned = new BenchRun
        {
            Label = "current",
            Cpu = new BenchMetrics { Throughput = 110, PeakTempC = 68, AvgPowerW = 90 },
            Gpu = new BenchMetrics { Throughput = 110, PeakTempC = 78, AvgPowerW = 180 },
            Ram = new BenchMetrics { Throughput = 110 },
        };
        var delta = BenchCompare.Compare(baseline, tuned);
        var pass = OptimizeSummary.FormatEndSummary(
            true, delta, gpuOk: true, gpuReason: null,
            dailyMv: 1080, clockMhz: 2700, vramMhz: 2500, cpu,
            baselineScore: baseline.SystemScore, tunedScore: tuned.SystemScore);
        Assert.Contains("=== Optimize PASS ===", pass);
        Assert.Contains("Score:", pass);
        Assert.Contains("GPU tune: PASS", pass);
        Assert.Contains("1080 mV", pass);

        var fail = OptimizeSummary.FormatEndSummary(
            false, null, gpuOk: false, gpuReason: "temp trip",
            dailyMv: null, clockMhz: null, vramMhz: null, cpu: null);
        Assert.Contains("=== Optimize FAIL ===", fail);
        Assert.Contains("temp trip", fail);

        var abort = OptimizeSummary.FormatAbort("stopped by user");
        Assert.Contains("=== Optimize ABORT ===", abort);
        Assert.Contains("stopped by user", abort);
    }

    [Fact]
    public void StartupReapply_plans_and_retries_once()
    {
        Assert.Null(StartupReapply.BuildPlan(false, false, false));
        var fromSaved = StartupReapply.BuildPlan(true, true, false)!;
        Assert.Equal("saved profiles", fromSaved.Source);
        Assert.Contains("GPU=yes", StartupReapply.Describe(fromSaved));
        var fromPack = StartupReapply.BuildPlan(false, false, true)!;
        Assert.Equal("profile pack fallback", fromPack.Source);
        Assert.True(StartupReapply.ShouldRetry(0, succeeded: false));
        Assert.False(StartupReapply.ShouldRetry(1, succeeded: false));
        Assert.False(StartupReapply.ShouldRetry(0, succeeded: true));
    }

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-depth-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }
}
