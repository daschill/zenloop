using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class MarketLeadershipTests
{
    [Fact]
    public void OptimizeCheckpoint_schema2_round_trips_forensics_fields()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "optimize-checkpoint.json");
            var ck = new OptimizeCheckpoint
            {
                Goal = "balanced",
                Status = OptimizeCheckpoint.StatusRunning,
                LastCompletedPhase = "baseline",
                ActivePhase = "gpu",
                LastProbeName = "uv-1100mV",
            };
            ck.EnsureRunId();
            ck.SetGpuCandidate(1100, 2700, 2500, false);
            ck.Heartbeat("gpu", "uv-1100mV");
            ck.Save(path);

            var loaded = OptimizeCheckpoint.TryLoadIncomplete(path, "balanced");
            Assert.NotNull(loaded);
            Assert.Equal(OptimizeCheckpoint.SchemaVersion, loaded!.Schema);
            Assert.False(string.IsNullOrWhiteSpace(loaded.RunId));
            Assert.Equal(1100, loaded.GpuCandidateVoltMv);
            Assert.Equal(2700, loaded.GpuCandidateClockMhz);
            Assert.Contains(loaded.RunId, loaded.DescribeResume(), StringComparison.Ordinal);

            ck.MarkAborted("user", "stopped by user", FaultKind.Tdr, "Display TDR");
            ck.Save(path);
            Assert.True(ck.IsAborted);
            Assert.Null(OptimizeCheckpoint.TryLoadIncomplete(path)); // aborted is not resume-as-crash
            Assert.Contains("Aborted", ck.DescribeResume(), StringComparison.Ordinal);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void OptimizeForensics_writes_probes_faults_abort_and_crash_context()
    {
        var dir = TempDir();
        try
        {
            var logs = Path.Combine(dir, "logs");
            var ck = new OptimizeCheckpoint { Goal = "performance", LastCompletedPhase = "gpu" };
            ck.EnsureRunId();
            ck.SetGpuCandidate(1050, 2800, 2600);
            var f = OptimizeForensics.Create(logs, ck.RunId);
            f.MirrorCheckpoint(ck);
            f.AppendProbe(new OptimizeProbeRecord
            {
                Phase = "gpu",
                Name = "uv-1050mV",
                Ok = false,
                Reason = "Display id=4101",
                FaultKind = FaultKind.Tdr.ToString(),
                VoltageMv = 1050,
                ClockMhz = 2500,
            });
            f.WriteFaults(new[] { new FaultEvent("Display", 4101, "Display driver stopped responding") }, FaultKind.Tdr);
            f.WriteAbort(OptimizeSummary.FormatAbort("stopped by user"));

            Assert.True(File.Exists(f.CheckpointCopyPath));
            Assert.True(File.Exists(f.ProbesPath));
            Assert.True(File.Exists(f.FaultsPath));
            Assert.True(File.Exists(f.AbortPath));
            Assert.Contains("uv-1050mV", File.ReadAllText(f.ProbesPath), StringComparison.Ordinal);

            var crash = OptimizeForensics.WriteContextualCrashLog(
                logs, new InvalidOperationException("boom"), ck, Path.Combine(dir, "bin"));
            Assert.True(File.Exists(crash));
            Assert.Contains(ck.RunId, File.ReadAllText(crash), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dir, "bin", "crash.log")));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task VoltageSearch_clock_conditioned_raises_daily_for_higher_band()
    {
        // Coarse daily 1000 works at stock; at 2700 needs 1030; at 2800 fails even with bumps → keep 1030.
        async Task<bool> Stable(int mv, int clock)
        {
            await Task.Yield();
            if (clock <= 2500) return mv >= 1000;
            if (clock <= 2700) return mv >= 1030;
            return mv >= 1100; // unreachable with maxBumpSteps=2 from 1000
        }

        var bands = new[] { 2700, 2800 };
        var r = await VoltageSearch.FindClockConditionedDailyAsync(
            stockMv: 1200,
            coarseDailyMv: 1000,
            stockClockMhz: 2500,
            targetClockMhzBands: bands,
            stepMv: 15,
            legalMin: 900,
            legalMax: 1200,
            isStableAtClock: Stable,
            marginMv: 15,
            maxBumpSteps: 2);

        Assert.True(r.DailyMv >= 1030);
        Assert.Equal(2700, r.HighestStableClockMhz);
        Assert.NotEmpty(r.Evaluated);

        var defaults = VoltageSearch.DefaultClockBands(2500, 2800, 50);
        Assert.Contains(2800, defaults);
        Assert.Contains(defaults, b => b > 2500 && b < 2800);
    }

    [Fact]
    public async Task ClockSearch_voltage_bump_recovers_higher_clock()
    {
        // At 1050 mV fail at 2700; at 1065 pass 2700 then fail 2750.
        async Task<(bool, double, double?, double?, string?)> Probe(int mhz, int mv)
        {
            await Task.Yield();
            if (mhz >= 2750) return (false, 0, null, null, "stress failed");
            if (mhz >= 2700 && mv < 1065) return (false, 0, null, null, "stress failed");
            return (true, 100 + (mhz - 2500) * 0.1, 200, 70, null);
        }

        var walk = await ClockSearch.WalkWithVoltageBumpAsync(
            "performance",
            new[] { 2650, 2700, 2750 },
            dailyVoltMv: 1050,
            stockVoltMv: 1200,
            voltageStepMv: 15,
            maxVoltageBumps: 2,
            Probe);

        Assert.Equal(2700, walk.BestMhz);
        Assert.True(walk.VoltageBumps >= 1);
        Assert.Equal(1065, walk.RefinedDailyMv);
        Assert.Contains(2750, walk.Evaluated); // attempted then pruned
        Assert.Equal(FaultKind.Instability, walk.LastFault);
    }

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "zenloop-ml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }
}
