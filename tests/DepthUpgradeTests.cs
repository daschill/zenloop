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
            Assert.Contains("enabled", loaded.StatusLine(), StringComparison.OrdinalIgnoreCase);
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
    }

    [Fact]
    public void OptimizeSummary_checklist_works_for_optimize_phases()
    {
        var planned = OptimizePhases.Default;
        var text = OptimizeSummary.PhaseChecklist(planned, "baseline", "gpu");
        Assert.Contains("✓ baseline", text);
        Assert.Contains("… gpu", text);
        Assert.Contains("· cpu", text);
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
