using System.Text;
using System.Text.Json;
using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class RtssOsdBridgeTests
{
    [Fact]
    public void FormatOsdText_includes_result_score_and_live_sensors()
    {
        var snap = MetricsSnapshotExport.FromOptimize(
            baseline: new BenchRun
            {
                Cpu = new BenchMetrics { Throughput = 100, PeakTempC = 70, AvgPowerW = 100 },
                Gpu = new BenchMetrics { Throughput = 100, PeakTempC = 80, AvgPowerW = 200 },
                Ram = new BenchMetrics { Throughput = 100 },
            },
            tuned: new BenchRun
            {
                Cpu = new BenchMetrics { Throughput = 110, PeakTempC = 68, AvgPowerW = 90 },
                Gpu = new BenchMetrics { Throughput = 110, PeakTempC = 78, AvgPowerW = 180 },
                Ram = new BenchMetrics { Throughput = 110 },
            },
            delta: null,
            liveMetrics: new Dictionary<string, double?>
            {
                ["cpu_temp_c"] = 61.0,
                ["hotspot_c"] = 77.0,
                ["board_power_w"] = 200.0,
            },
            goal: "balanced",
            pass: true);

        var text = RtssOsdBridge.FormatOsdText(snap, multiLine: true);
        Assert.Contains("ZenLoop", text, StringComparison.Ordinal);
        Assert.Contains("Pass", text, StringComparison.Ordinal);
        Assert.Contains("balanced", text, StringComparison.Ordinal);
        Assert.Contains("CPU", text, StringComparison.Ordinal);
        Assert.Contains("HS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WinRing0", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildZenLoopPayload_has_signature_and_stable_size()
    {
        var snap = MetricsSnapshotExport.FromMetrics(
            new Dictionary<string, double?>
            {
                ["cpu_temp_c"] = 55.5,
                ["board_power_w"] = 180.0,
            },
            source: "telemetry",
            goal: "efficiency",
            pass: null);
        var text = RtssOsdBridge.FormatOsdText(snap);
        var payload = RtssOsdBridge.BuildZenLoopPayload(snap, text);
        Assert.Equal(628, payload.Length);
        Assert.Equal(RtssOsdBridge.ZenLoopSignature, BitConverter.ToUInt32(payload, 0));
        Assert.Equal(RtssOsdBridge.ZenLoopLayoutVersion, BitConverter.ToUInt32(payload, 4));
        Assert.Equal(55.5, BitConverter.ToDouble(payload, 20), 3);
        Assert.Equal(180.0, BitConverter.ToDouble(payload, 36), 3);
        var result = Encoding.UTF8.GetString(payload, 68, 16).TrimEnd('\0');
        Assert.Equal("Unknown", result);
    }

    [Fact]
    public void TryWriteOsdTextFile_atomic_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-osd-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, RtssOsdBridge.OsdTextFileName);
        try
        {
            Assert.True(RtssOsdBridge.TryWriteOsdTextFile("ZenLoop\nPass", path));
            Assert.True(File.Exists(path));
            Assert.Equal("ZenLoop\nPass", File.ReadAllText(path));
            Assert.Contains("RTSS", RtssOsdBridge.InstallHelp, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(RtssOsdBridge.ZenLoopMapName, RtssOsdBridge.InstallHelp, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Publish_without_rtss_still_writes_osd_text_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-pub-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, "osd.txt");
        try
        {
            var snap = MetricsSnapshotExport.FromMetrics(
                new Dictionary<string, double?> { ["cpu_temp_c"] = 50 },
                source: "about");
            var result = RtssOsdBridge.Publish(snap, path);
            Assert.True(result.OsdTextFile);
            Assert.True(File.Exists(path));
            Assert.Contains("ZenLoop", result.Text, StringComparison.Ordinal);
            // RTSS slot requires Windows + running RTSS — not asserted true here.
            Assert.DoesNotContain("WinRing0", result.Describe(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}

public class AdrenalinUvBakeOffTests
{
    static BenchRun MakeBench(string label, double thr, double temp, double power) => new()
    {
        Label = label,
        Cpu = new BenchMetrics { Throughput = thr, PeakTempC = temp - 5, AvgPowerW = power * 0.4 },
        Gpu = new BenchMetrics { Throughput = thr, PeakTempC = temp, AvgPowerW = power * 0.6 },
        Ram = new BenchMetrics { Throughput = thr },
    };

    [Fact]
    public void Build_report_compares_zenloop_vs_stock_and_writes_markdown()
    {
        var stock = MakeBench("stock", 100, 85, 300);
        var zen = MakeBench("zen", 110, 80, 270);
        var report = AdrenalinUvBakeOff.Build(
            new[]
            {
                new BakeOffLegInput
                {
                    Kind = nameof(BakeOffLegKind.Stock),
                    Bench = stock,
                    Profile = new GpuProfile { VoltageMv = 1200, MaxMhz = 2500 },
                },
                new BakeOffLegInput
                {
                    Kind = nameof(BakeOffLegKind.ZenLoopUv),
                    Bench = zen,
                    Profile = new GpuProfile { VoltageMv = 1050, MaxMhz = 2700 },
                },
            },
            goal: "balanced");

        Assert.Equal(1, report.Schema);
        Assert.Equal(2, report.Legs.Count);
        Assert.NotNull(report.ZenLoopVsStock);
        Assert.True(report.ZenLoopVsStock!.SpeedPct > 0);
        Assert.Equal("ZenLoop UV", report.WinnerByScore);
        Assert.Contains("ZenLoop UV", report.WinnerByEfficiency!, StringComparison.Ordinal);

        var dir = Path.Combine(Path.GetTempPath(), "zenloop-bakeoff-" + Guid.NewGuid().ToString("n"));
        try
        {
            var jsonPath = AdrenalinUvBakeOff.Write(report, dir);
            Assert.True(File.Exists(jsonPath));
            var mdPath = Path.Combine(dir, AdrenalinUvBakeOff.MarkdownFileName);
            Assert.True(File.Exists(mdPath));
            var md = File.ReadAllText(mdPath);
            Assert.Contains("ZenLoop vs stock", md, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No WinRing0", md, StringComparison.Ordinal);

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
            Assert.True(doc.RootElement.TryGetProperty("zenloop_vs_stock", out _));
            Assert.True(doc.RootElement.TryGetProperty("legs", out _));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryImportAdrenalinProfile_reads_zenloop_and_flexible_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-adr-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var zl = Path.Combine(dir, "zl.json");
            File.WriteAllText(zl, new GpuProfile { VoltageMv = 1040, MaxMhz = 2650, VramMhz = 2500, PowerPct = 110 }.ToJson());
            var imported = AdrenalinUvBakeOff.TryImportAdrenalinProfile(zl);
            Assert.NotNull(imported);
            Assert.Equal(1040, imported!.VoltageMv);
            Assert.Equal(2650, imported.MaxMhz);

            var flex = Path.Combine(dir, "adr.json");
            File.WriteAllText(flex, """
                { "Tuning": { "Voltage": 1.05, "MaxFreq": 2600, "MemClock": 2400, "PowerLimit": 100 } }
                """);
            var flexProf = AdrenalinUvBakeOff.TryImportAdrenalinProfile(flex);
            Assert.NotNull(flexProf);
            Assert.Equal(1050, flexProf!.VoltageMv);
            Assert.Equal(2600, flexProf.MaxMhz);
            Assert.Equal(2400, flexProf.VramMhz);

            var xml = Path.Combine(dir, "adr.xml");
            File.WriteAllText(xml, """
                <Profile><Voltage>1.02</Voltage><MaxFreq>2550</MaxFreq></Profile>
                """);
            var xmlProf = AdrenalinUvBakeOff.TryImportAdrenalinProfile(xml);
            Assert.NotNull(xmlProf);
            Assert.Equal(1020, xmlProf!.VoltageMv);
            Assert.Equal(2550, xmlProf.MaxMhz);

            Assert.Null(AdrenalinUvBakeOff.TryImportAdrenalinProfile(Path.Combine(dir, "missing.json")));
            File.WriteAllText(Path.Combine(dir, "empty.json"), "{ \"hello\": true }");
            Assert.Null(AdrenalinUvBakeOff.TryImportAdrenalinProfile(Path.Combine(dir, "empty.json")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Build_with_adrenalin_leg_sets_zenloop_vs_adrenalin()
    {
        var report = AdrenalinUvBakeOff.Build(new[]
        {
            new BakeOffLegInput { Kind = "Stock", Bench = MakeBench("s", 100, 85, 300) },
            new BakeOffLegInput { Kind = "ZenLoopUv", Bench = MakeBench("z", 112, 78, 260) },
            new BakeOffLegInput
            {
                Kind = "AdrenalinProfile",
                Bench = MakeBench("a", 108, 80, 275),
                Profile = new GpuProfile { VoltageMv = 1100, MaxMhz = 2600 },
            },
        });
        Assert.NotNull(report.ZenLoopVsAdrenalin);
        Assert.True(report.ZenLoopVsAdrenalin!.SpeedPct > 0);
        Assert.Equal(3, report.Legs.Count);
    }

    [Fact]
    public void BuildFromBenchFiles_round_trips_offline()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-bf-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var stockPath = Path.Combine(dir, "stock.json");
            var zenPath = Path.Combine(dir, "zen.json");
            MakeBench("stock", 100, 85, 300).SaveFile(stockPath);
            MakeBench("zen", 110, 80, 270).SaveFile(zenPath);
            var report = AdrenalinUvBakeOff.BuildFromBenchFiles(stockPath, zenPath, goal: "performance");
            Assert.Equal("performance", report.Goal);
            Assert.NotNull(report.ZenLoopVsStock);
            Assert.Equal(2, report.Legs.Count);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
