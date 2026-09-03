using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class AdvancedTunerTests
{
    const int Stock = 1150;
    const int Min = 700;
    const int Step = VoltageSearch.DefaultStepMv;
    const int Margin = VoltageSearch.DefaultMarginMv;

    [Fact]
    public void DailyVoltage_after_fail_is_fail_plus_positive_margin_inside_legal_range()
    {
        const int failV = 1000;
        int daily = VoltageSearch.DailyVoltageAfterFail(failV, Margin, Min, Stock);

        Assert.True(Margin > 0);
        Assert.Equal(failV + Margin, daily);
        Assert.InRange(daily, Min, Stock);
        Assert.True(daily > failV);
    }

    [Fact]
    public void DailyVoltage_clamps_to_legal_max()
    {
        int daily = VoltageSearch.DailyVoltageAfterFail(1140, Margin, Min, Stock);
        Assert.Equal(Stock, daily);
        Assert.InRange(daily, Min, Stock);
    }

    [Fact]
    public void Binary_search_evaluates_fewer_voltages_than_linear_on_midrange_fail()
    {
        const int vStar = 1000;
        bool Oracle(int v) => v >= vStar;

        int linear = VoltageSearch.CountLinearUntilFail(Stock, Min, Step, Oracle);
        var found = VoltageSearch.FindMinStable(Stock, Min, Step, Min, Stock, Oracle, Margin);

        Assert.True(linear > 2, $"linear count {linear} too small for a meaningful comparison");
        Assert.True(
            found.Evaluated.Count < linear,
            $"binary evaluated {found.Evaluated.Count} voltages, linear {linear}");
        Assert.True(found.MinStableMv is int p && p >= vStar);
        Assert.True(found.FailVoltageMv is int f && f < vStar);
        Assert.True(found.DailyMv >= vStar);
        Assert.InRange(found.DailyMv, Min, Stock);
        Assert.True(found.DailyMv >= VoltageSearch.DailyVoltageAfterFail(found.FailVoltageMv!.Value, Margin, Min, Stock));
    }

    [Fact]
    public void Resume_skips_last_completed_step_and_starts_at_the_next()
    {
        var planned = AutotunePhases.Default;
        const string lastCompleted = "voltage";
        var remaining = ResumePlanner.RemainingSteps(planned, lastCompleted);

        Assert.DoesNotContain(lastCompleted, remaining);
        Assert.Equal("clock", remaining[0]);
        Assert.Equal(new[] { "clock", "vram", "fast-timing", "final-soak" }, remaining);
    }

    [Fact]
    public void Resume_from_empty_last_step_runs_full_plan()
    {
        var planned = AutotunePhases.Default;
        var remaining = ResumePlanner.RemainingSteps(planned, null);
        Assert.Equal(planned, remaining);
    }

    [Fact]
    public void Whea_and_display_timeout_records_are_faults()
    {
        var whea = new FaultEvent("Microsoft-Windows-WHEA-Logger", 18, "A corrected hardware error has occurred.");
        var tdr = new FaultEvent("Display", 4101, "Display driver amdkmdag stopped responding and has recovered.");
        var timeout = new FaultEvent(
            "Microsoft-Windows-Kernel-PnP",
            4101,
            "Display driver timeout detection and recovery (TDR) occurred.");
        var benign = new FaultEvent("Service Control Manager", 7036, "The Windows Update service entered the running state.");

        Assert.True(FaultClassifier.IsFault(whea));
        Assert.True(FaultClassifier.IsFault(tdr));
        Assert.True(FaultClassifier.IsFault(timeout));
        Assert.False(FaultClassifier.IsFault(benign));
    }

    [Fact]
    public void GpuProfile_round_trips_clocks_voltage_vram_power_and_fan_curve()
    {
        var original = new GpuProfile
        {
            MaxMhz = 2800,
            MinMhz = 500,
            VoltageMv = 1100,
            VramMhz = 2614,
            PowerPct = 5,
            FastTiming = true,
            Fan = new FanCurve
            {
                ZeroRpm = false,
                Points =
                [
                    new FanPoint { Temp = 40, Speed = 30 },
                    new FanPoint { Temp = 55, Speed = 45 },
                    new FanPoint { Temp = 70, Speed = 65 },
                    new FanPoint { Temp = 85, Speed = 85 },
                    new FanPoint { Temp = 95, Speed = 100 },
                ],
            },
        };

        var json = original.ToJson();
        var wrapped = """{"ok":true,"profile":""" + json + "}";
        var a = GpuProfile.Parse(json);
        var b = GpuProfile.Parse(wrapped);

        Assert.Equal(original.MaxMhz, a.MaxMhz);
        Assert.Equal(original.VoltageMv, a.VoltageMv);
        Assert.Equal(original.VramMhz, a.VramMhz);
        Assert.Equal(original.PowerPct, a.PowerPct);
        Assert.Equal(original.FastTiming, a.FastTiming);
        Assert.Equal(original.Fan!.ZeroRpm, a.Fan!.ZeroRpm);
        Assert.Equal(5, a.Fan.Points.Count);
        Assert.Equal(70, a.Fan.Points[2].Temp);
        Assert.Equal(65, a.Fan.Points[2].Speed);
        Assert.Equal(a.MaxMhz, b.MaxMhz);
        Assert.Equal(a.VoltageMv, b.VoltageMv);
        Assert.Equal(a.Fan.Points.Count, b.Fan!.Points.Count);
    }

    [Fact]
    public void Startup_profile_TryLoadFile_returns_profile_when_present_and_null_when_missing()
    {
        Assert.Null(GpuProfile.TryLoadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".json")));

        var path = Path.Combine(Path.GetTempPath(), "zenloop-startup-" + Guid.NewGuid().ToString("n") + ".json");
        var saved = new GpuProfile
        {
            MaxMhz = 2750,
            VoltageMv = 1085,
            VramMhz = 2600,
            PowerPct = 0,
            FastTiming = false,
            Fan = new FanCurve { ZeroRpm = false, Points = [new FanPoint { Temp = 50, Speed = 40 }] },
        };
        File.WriteAllText(path, saved.ToJson());
        try
        {
            var loaded = GpuProfile.TryLoadFile(path);
            Assert.NotNull(loaded);
            Assert.Equal(2750, loaded!.MaxMhz);
            Assert.Equal(1085, loaded.VoltageMv);
            Assert.Equal(2600, loaded.VramMhz);
            Assert.False(loaded.Fan!.ZeroRpm);
            Assert.Equal(50, loaded.Fan.Points[0].Temp);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PerCoreGuide_reports_failed_cores_and_helper_args_do_not_write_smu()
    {
        var results = new PerCoreOutcome[]
        {
            new(0, true, "ok"),
            new(1, false, "stress failed"),
            new(2, true, "ok"),
            new(3, false, "WHEA"),
        };
        Assert.Equal(new[] { 1, 3 }, PerCoreGuide.FailedCores(results));
        var summary = PerCoreGuide.FormatSummary(results);
        Assert.Contains("Failed cores: 1, 3", summary);
        Assert.DoesNotContain("SMU", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WinRing0", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("write PBO", summary, StringComparison.OrdinalIgnoreCase);

        var allPass = PerCoreGuide.FormatSummary([new PerCoreOutcome(0, true, "ok"), new PerCoreOutcome(1, true, "ok")]);
        Assert.Contains("All 2 logical cores passed", allPass);

        var args = PerCoreGuide.HelperArgs(20, "core", 3);
        Assert.Equal("stress-cpu", args[0]);
        Assert.Contains("--mode", args);
        Assert.Contains("core", args);
        Assert.Contains("--core", args);
        Assert.Contains("3", args);
        var joined = string.Join(' ', args);
        Assert.DoesNotContain("smu", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("winring", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pbo", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mailbox", joined, StringComparison.OrdinalIgnoreCase);
    }
}
