using System.Text.Json;
using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class MetricsSnapshotExportTests
{
    [Fact]
    public void Schema_v2_serialize_includes_stable_session_and_live_fields()
    {
        var baseline = new BenchRun
        {
            Cpu = new BenchMetrics { Throughput = 100, PeakTempC = 70, AvgPowerW = 100 },
            Gpu = new BenchMetrics { Throughput = 100, PeakTempC = 80, AvgPowerW = 200 },
            Ram = new BenchMetrics { Throughput = 100 },
        };
        var tuned = new BenchRun
        {
            Cpu = new BenchMetrics { Throughput = 110, PeakTempC = 68, AvgPowerW = 90 },
            Gpu = new BenchMetrics { Throughput = 110, PeakTempC = 78, AvgPowerW = 180 },
            Ram = new BenchMetrics { Throughput = 110 },
        };
        var delta = BenchCompare.Compare(baseline, tuned);

        var snap = MetricsSnapshotExport.FromOptimize(
            baseline,
            tuned,
            delta,
            liveMetrics: new Dictionary<string, double?>
            {
                ["cpu_temp_c"] = 61.5,
                ["cpu_power_w"] = 85.0,
                ["board_power_w"] = 205.0,
                ["hotspot_c"] = 76.0,
            },
            goal: "balanced",
            summary: delta.Summary,
            pass: true,
            profilePackId: "pack-demo-1");

        Assert.Equal(2, snap.Schema);
        Assert.Equal("Pass", snap.Result);
        Assert.Equal("optimize", snap.Source);
        Assert.Equal("pack-demo-1", snap.ProfilePackId);
        Assert.NotNull(snap.Session);
        Assert.NotNull(snap.Live);
        Assert.Equal(snap.Session!.ScoreBefore, snap.ScoreBefore);
        Assert.Equal(snap.Session.ScoreAfter, snap.ScoreAfter);
        Assert.Equal(61.5, snap.Live!.CpuTempC);
        Assert.Equal(205.0, snap.Live.BoardPowerW);

        var json = MetricsSnapshotExport.Serialize(snap);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("schema").GetInt32());
        Assert.Equal("Pass", root.GetProperty("result").GetString());
        Assert.True(root.TryGetProperty("timestamp_utc", out _));
        Assert.True(root.TryGetProperty("captured_utc", out _));
        Assert.True(root.TryGetProperty("score_before", out _));
        Assert.True(root.TryGetProperty("score_after", out _));
        Assert.True(root.TryGetProperty("temp_before_c", out _));
        Assert.True(root.TryGetProperty("temp_after_c", out _));
        Assert.True(root.TryGetProperty("power_before_w", out _));
        Assert.True(root.TryGetProperty("power_after_w", out _));
        Assert.True(root.TryGetProperty("profile_pack_id", out _));
        Assert.True(root.TryGetProperty("session", out var session));
        Assert.True(session.TryGetProperty("speed_pct", out _));
        Assert.True(root.TryGetProperty("live", out var live));
        Assert.Equal(61.5, live.GetProperty("cpu_temp_c").GetDouble());
        Assert.DoesNotContain("WinRing0", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Atomic_write_round_trips_and_path_help_documents_location()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-metrics-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, MetricsSnapshotExport.FileName);
        try
        {
            var snap = MetricsSnapshotExport.FromMetrics(
                new Dictionary<string, double?>
                {
                    ["cpu_temp_c"] = 62.5,
                    ["board_power_w"] = 210.0,
                },
                source: "about",
                goal: "balanced",
                summary: "telemetry sample",
                pass: null);
            Assert.Equal("Unknown", snap.Result);
            Assert.Equal(MetricsSnapshotExport.SchemaVersion, snap.Schema);

            var written = MetricsSnapshotExport.Write(snap, path, atomic: true);
            Assert.Equal(path, written);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));

            var loaded = MetricsSnapshotExport.TryLoad(path);
            Assert.NotNull(loaded);
            Assert.Equal(62.5, loaded!.Live!.CpuTempC);
            Assert.Equal(210.0, loaded.Live.BoardPowerW);
            Assert.Equal("about", loaded.Source);

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
    public void NormalizeResult_maps_pass_fail_aborted()
    {
        Assert.Equal("Pass", MetricsSnapshotExport.NormalizeResult(true));
        Assert.Equal("Fail", MetricsSnapshotExport.NormalizeResult(false));
        Assert.Equal("Aborted", MetricsSnapshotExport.NormalizeResult(false, "user stop"));
        Assert.Equal("Unknown", MetricsSnapshotExport.NormalizeResult(null));
    }

    [Fact]
    public void Aborted_optimize_sets_result_without_inventing_scores()
    {
        var snap = MetricsSnapshotExport.FromOptimize(
            baseline: null,
            tuned: null,
            delta: null,
            liveMetrics: new Dictionary<string, double?> { ["cpu_temp_c"] = 55 },
            goal: "balanced",
            pass: false,
            abortReason: "stopped by user");
        Assert.Equal("Aborted", snap.Result);
        Assert.False(snap.Pass);
        Assert.Null(snap.Session);
        Assert.Null(snap.ScoreBefore);
        Assert.Equal(55, snap.Live!.CpuTempC);
    }
}
