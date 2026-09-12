using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ZenLoop.Core;

namespace ZenLoop.App.Services;

public sealed record TuneSettings(
    int MaxMhz,
    int MinMhz,
    int VoltageMv,
    int VramMhz,
    bool FastTiming,
    int PowerPct,
    FanCurve? Fan = null);

public sealed record StepResult(
    bool Ok,
    string Reason,
    TuneSettings? Settings,
    Dictionary<string, double?> Metrics,
    double Throughput,
    List<IReadOnlyDictionary<string, double?>>? Ticks = null);

public sealed record TuneProgress(
    string Message,
    double Fraction,
    Dictionary<string, double?>? Metrics,
    string? StepName,
    bool? Passed);

public sealed record CoreResult(int Core, bool Ok, string Reason);

public sealed class AutotuneProgressFile
{
    public string Goal { get; set; } = "balanced";
    public string? LastCompletedStep { get; set; }
    public List<string> PlannedSteps { get; set; } = [.. AutotunePhases.Default];
    public int? DailyVoltage { get; set; }
    public int? LastGoodClock { get; set; }
    public int? LastGoodVram { get; set; }
    public bool LastFast { get; set; }
    public int StockVolt { get; set; }
    public int StockClock { get; set; }
    public int StockVram { get; set; }
    public int StockMin { get; set; }
    public int StockPower { get; set; }
}

public sealed class AutotuneService
{
    public static readonly FanCurve PerformanceFan = new()
    {
        ZeroRpm = false,
        Points =
        [
            new FanPoint { Temp = 40, Speed = 30 },
            new FanPoint { Temp = 55, Speed = 45 },
            new FanPoint { Temp = 70, Speed = 65 },
            new FanPoint { Temp = 80, Speed = 80 },
            new FanPoint { Temp = 90, Speed = 100 },
        ],
    };

    readonly HwClient _hw;
    readonly string _root;
    readonly IFaultSource _faults;

    public AutotuneService(HwClient hw, IFaultSource? faults = null)
    {
        _hw = hw;
        _root = FindRoot();
        _faults = faults ?? new WindowsEventLogFaults();
    }

    public string ProgressPath => Path.Combine(_root, "logs", "autotune-progress.json");
    public string OptimizeCheckpointPath => Path.Combine(_root, "logs", "optimize-checkpoint.json");
    public string OptimizeHistoryPath => Path.Combine(_root, "profiles", "optimize-history.json");
    public string AppProfilesPath => Path.Combine(_root, "profiles", "app-profiles.json");
    public string MachineMemoryPath => Path.Combine(_root, "profiles", "machine-tune-memory.json");
    public string StartupProfilePath => Path.Combine(_root, "profiles", "startup.json");
    public string CpuPboProfilePath => Path.Combine(_root, "profiles", "cpu-pbo.json");
    public string RamProfilePath => Path.Combine(_root, "profiles", "ram-timings.json");
    public string ProfilePackExportDir => Path.Combine(_root, "profiles", "exports");

    public RamTimingProfile? LoadRamProfile() => RamTimingProfile.TryLoadFile(RamProfilePath);

    public void SaveRamProfile(RamTimingProfile p) => p.SaveFile(RamProfilePath);

    public ProfilePack BuildProfilePack(string? goal = null, string? notes = null)
        => ProfilePack.FromParts(LoadStartupProfile(), LoadCpuPboProfile(), LoadRamProfile(), goal, notes);

    public void ApplyProfilePackFiles(ProfilePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Directory.CreateDirectory(Path.Combine(_root, "profiles"));
        if (pack.Gpu is not null)
            File.WriteAllText(StartupProfilePath, pack.Gpu.ToJson());
        if (pack.Cpu is not null)
            SaveCpuPboProfile(pack.Cpu);
        if (pack.Ram is not null)
            SaveRamProfile(pack.Ram);
    }

    public string SettingsPath => Path.Combine(_root, "profiles", "app-settings.json");

    public AppSettings LoadSettings() => AppSettings.Load(SettingsPath);

    public void SaveSettings(AppSettings s) => s.Save(SettingsPath);

    public OptimizeCheckpoint? LoadIncompleteOptimize(string? goal = null)
        => OptimizeCheckpoint.TryLoadIncomplete(OptimizeCheckpointPath, goal);

    public bool HasIncompleteOptimize(string? goal = null)
        => LoadIncompleteOptimize(goal) is not null;

    public void ClearOptimizeCheckpoint() => OptimizeCheckpoint.Delete(OptimizeCheckpointPath);

    public OptimizeHistory LoadOptimizeHistory() => OptimizeHistory.Load(OptimizeHistoryPath);

    public AppProfileStore LoadAppProfiles() => AppProfileStore.Load(AppProfilesPath);

    public void SaveAppProfiles(AppProfileStore store) => store.Save(AppProfilesPath);

    public string BenchBaselinePath => Path.Combine(_root, "profiles", "bench-baseline.json");
    public string BenchCurrentPath => Path.Combine(_root, "profiles", "bench-current.json");

    public BenchRun? LoadBenchBaseline() => BenchRun.TryLoadFile(BenchBaselinePath);
    public BenchRun? LoadBenchCurrent() => BenchRun.TryLoadFile(BenchCurrentPath);

    public async Task<BenchRun> RunSystemBenchAsync(
        string label,
        int seconds,
        Limits limits,
        IProgress<TuneProgress>? progress,
        CancellationToken ct)
    {
        seconds = Math.Clamp(seconds, 8, 90);
        bool hwinfo = false;
        bool rmTel = false;
        var cpuLive = new Dictionary<string, double?>();
        using var telCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var telTask = PumpCpuTelemetryAsync(seconds * 4 + 8, cpuLive, () => rmTel = true, telCts.Token);
        async Task<BenchMetrics> One(string helperKind, string mode, BenchKind kind, string title)
        {
            var ticks = new List<IReadOnlyDictionary<string, double?>>();
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var poll = Task.Run(async () =>
            {
                while (!pollCts.IsCancellationRequested)
                {
                    try
                    {
                        var snap = _hw.Metrics();
                        var d = new Dictionary<string, double?>(snap.Metrics);
                        lock (cpuLive)
                        {
                            foreach (var kv in cpuLive)
                                d[kv.Key] = kv.Value;
                        }
                        var hi = HwinfoSensors.TryRead();
                        if (hi.Available)
                        {
                            hwinfo = true;
                            HwinfoSensors.MergeInto(d, hi);
                        }
                        lock (ticks) ticks.Add(d);
                    }
                    catch { /* ignore */ }
                    try { await Task.Delay(1000, pollCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }, pollCts.Token);

            progress?.Report(new TuneProgress(title, 0, null, label, null));
            var res = await StressAsync(helperKind, seconds, limits, progress, ct, mode);
            pollCts.Cancel();
            try { await poll; } catch { /* ignore */ }
            List<IReadOnlyDictionary<string, double?>> copy;
            lock (ticks) copy = ticks.ToList();
            if (res.Ticks is { Count: > 0 } stream)
            {
                foreach (var t in stream)
                {
                    var d = new Dictionary<string, double?>(t);
                    var hi = HwinfoSensors.TryRead();
                    if (hi.Available) hwinfo = true;
                    HwinfoSensors.MergeInto(d, hi);
                    copy.Add(d);
                }
            }
            else if (res.Metrics.Count > 0)
                copy.Add(res.Metrics);
            return BenchCompare.FromSamples(copy, res.Throughput, kind, seconds);
        }

        var cpu = await One("cpu", "all", BenchKind.Cpu, "CPU benchmark (all cores)…");
        var ram = await One("cpu", "ram", BenchKind.Ram, "RAM copy benchmark…");
        var gpu = await One("gpu", "all", BenchKind.Gpu, "GPU compute benchmark…");
        var run = new BenchRun
        {
            Label = label,
            Utc = DateTime.UtcNow,
            Cpu = cpu,
            Ram = ram,
            Gpu = gpu,
            Hwinfo = hwinfo || rmTel,
        };
        telCts.Cancel();
        try { await telTask; } catch { /* ignore */ }
        var path = string.Equals(label, "baseline", StringComparison.OrdinalIgnoreCase)
            ? BenchBaselinePath : BenchCurrentPath;
        run.SaveFile(path);
        AppendBenchHistory(run);
        return run;
    }

    public async Task<BenchDelta?> RunAutonomousAsync(
        string goal,
        int seconds,
        Limits limits,
        bool skipVram,
        SmuService? smu,
        CpuPboProfile seed,
        IProgress<TuneProgress>? progress,
        CancellationToken ct,
        bool resume = false)
    {
        IProgress<TuneProgress>? Scale(double lo, double hi)
        {
            if (progress is null) return null;
            return new Progress<TuneProgress>(p =>
                progress.Report(p with { Fraction = lo + Math.Clamp(p.Fraction, 0, 1) * (hi - lo) }));
        }

        var planned = OptimizePhases.Default.ToList();
        var ck = resume ? OptimizeCheckpoint.TryLoadIncomplete(OptimizeCheckpointPath, goal) : null;
        bool didResume = ck is not null;
        if (ck is null)
        {
            ck = new OptimizeCheckpoint
            {
                Goal = goal,
                PlannedPhases = planned,
                SkipVram = skipVram,
            };
        }
        else
        {
            planned = ck.PlannedPhases.Count > 0 ? ck.PlannedPhases : planned;
            skipVram = ck.SkipVram;
        }

        var remaining = ResumePlanner.RemainingSteps(planned, ck.LastCompletedPhase);
        bool Need(string phase) => remaining.Contains(phase);

        async Task CompletePhase(string phase)
        {
            ck.LastCompletedPhase = phase;
            ck.Save(OptimizeCheckpointPath);
            progress?.Report(new TuneProgress(
                OptimizeSummary.PhaseChecklist(planned, ck.LastCompletedPhase, null),
                0, null, phase, true));
        }

        if (didResume)
        {
            progress?.Report(new TuneProgress(ck.DescribeResume(), 0.01, null, "resume", null));
            progress?.Report(new TuneProgress(
                OptimizeSummary.PhaseChecklist(planned, ck.LastCompletedPhase,
                    remaining.Count > 0 ? remaining[0] : null), 0.015, null, "resume", null));
            await ReapplySavedTunesForResumeAsync(remaining, smu, ct);
        }

        bool gpuOk = ck.GpuOk ?? false;
        string? gpuReason = ck.GpuReason;
        TuneSettings? winner = null;
        if (ck.DailyMv is int dmv && ck.ClockMhz is int cmhz)
        {
            winner = new TuneSettings(cmhz, 500, dmv, ck.VramMhz ?? 2500, false, 0, PerformanceFan);
        }

        CpuPboProfile? cpuProfile = LoadCpuPboProfile();

        if (Need("stock"))
        {
            // Fresh Optimize invalidates mid-tune GPU progress from a prior interrupted GPU-only run.
            ClearProgress();
            progress?.Report(new TuneProgress("Restoring stock GPU for baseline…", 0.02, null, "stock", null));
            await Task.Run(() => _hw.Reset(), ct);

            if (smu is { IsAvailable: true })
            {
                progress?.Report(new TuneProgress("Session Curve Optimizer = 0 (stock CPU)…", 0.04, null, "cpu-stock", null));
                try
                {
                    ApplySessionStockCo(smu, seed);
                }
                catch (Exception ex)
                {
                    progress?.Report(new TuneProgress("CPU stock session skipped: " + ex.Message, 0.05, null, "cpu-stock", false));
                }
            }

            try
            {
                var info = await Task.Run(() => _hw.Info(), ct);
                ck.StockVoltMv = info.VoltageMv ?? info.VoltageRange?.Max ?? 0;
                ck.StockClockMhz = info.MaxMhz ?? info.DefaultMaxMhz ?? 0;
                ck.StockVramMhz = info.VramMhz ?? 0;
            }
            catch { /* fingerprint best-effort */ }

            await CompletePhase("stock");
        }

        if (Need("baseline"))
        {
            progress?.Report(new TuneProgress("Benchmarking stock…", 0.06, null, "baseline", null));
            await RunSystemBenchAsync("baseline", seconds, limits, Scale(0.06, 0.22), ct);
            await CompletePhase("baseline");
        }

        if (Need("gpu"))
        {
            progress?.Report(new TuneProgress("Auto GPU undervolt + overclock…", 0.22, null, "gpu", null));
            // Resume GPU search if a mid-GPU crash left autotune-progress.json
            bool gpuResume = didResume && HasProgress(goal);
            var gpu = await GpuAutotuneAsync(goal, seconds, limits, skipVram, Scale(0.22, 0.68), ct, resume: gpuResume);
            gpuOk = gpu.TryGetValue("ok", out var okObj) && okObj is true;
            gpuReason = gpu.GetValueOrDefault("reason")?.ToString();
            if (!gpuOk)
                progress?.Report(new TuneProgress("GPU tune: " + gpuReason, 0.68, null, "gpu", false));
            winner = gpu.TryGetValue("winner", out var wObj) && wObj is TuneSettings w ? w : winner;
            ck.GpuOk = gpuOk;
            ck.GpuReason = gpuReason;
            if (winner is not null)
            {
                ck.DailyMv = winner.VoltageMv;
                ck.ClockMhz = winner.MaxMhz;
                ck.VramMhz = winner.VramMhz;
            }
            await CompletePhase("gpu");
        }

        if (Need("cpu"))
        {
            if (smu is { IsAvailable: true })
            {
                progress?.Report(new TuneProgress("Auto-tune per-core Curve Optimizer…", 0.68, null, "cpu", null));
                try
                {
                    cpuProfile = await CpuPboAutotuneAsync(smu, seed, Math.Max(8, seconds / 2), limits, Scale(0.68, 0.86), ct);
                }
                catch (Exception ex)
                {
                    progress?.Report(new TuneProgress("CPU tune skipped: " + ex.Message, 0.86, null, "cpu", false));
                }
            }
            else
                progress?.Report(new TuneProgress("CPU tune skipped: SMU unavailable", 0.86, null, "cpu", false));
            await CompletePhase("cpu");
        }

        if (Need("current"))
        {
            progress?.Report(new TuneProgress("Benchmarking tuned system…", 0.86, null, "current", null));
            await RunSystemBenchAsync("current", seconds, limits, Scale(0.86, 1.0), ct);
            await CompletePhase("current");
        }

        var delta = CompareSavedBenches();
        var summary = OptimizeSummary.FormatPostTune(
            delta, gpuOk, gpuReason,
            winner?.VoltageMv ?? ck.DailyMv,
            winner?.MaxMhz ?? ck.ClockMhz,
            winner?.VramMhz ?? ck.VramMhz,
            cpuProfile);
        var pass = gpuOk && delta is not null;
        var entry = OptimizeHistoryEntry.FromResult(
            goal, pass, delta, LoadBenchBaseline(), LoadBenchCurrent(),
            gpuOk, ck.DailyMv ?? winner?.VoltageMv, ck.ClockMhz ?? winner?.MaxMhz,
            ck.VramMhz ?? winner?.VramMhz, cpuProfile, didResume, summary);
        var hist = LoadOptimizeHistory();
        hist.Add(entry);
        hist.Save(OptimizeHistoryPath);

        ck.LastCompletedPhase = "done";
        ClearOptimizeCheckpoint();
        progress?.Report(new TuneProgress(summary, 1, null, "done", pass));
        return delta;
    }

    async Task ReapplySavedTunesForResumeAsync(
        IReadOnlyList<string> remaining,
        SmuService? smu,
        CancellationToken ct)
    {
        // Past GPU: re-apply saved GPU profile so CPU / current bench see the tuned state.
        bool pastGpu = !remaining.Contains("gpu") && remaining.Count > 0;
        bool pastCpu = !remaining.Contains("cpu") && remaining.Contains("current");
        if (pastGpu)
        {
            var gpu = LoadStartupProfile();
            if (gpu is not null)
                await Task.Run(() => ApplyProfile(gpu), ct);
        }
        if (pastCpu && smu is { IsAvailable: true })
        {
            var cpu = LoadCpuPboProfile();
            if (cpu is not null)
                await Task.Run(() => smu.Apply(cpu, PersistMode.Session), ct);
        }
    }

    public BenchDelta? CompareSavedBenches()
    {
        var a = LoadBenchBaseline();
        var b = LoadBenchCurrent();
        if (a is null || b is null) return null;
        return BenchCompare.Compare(a, b);
    }

    public async Task<StepResult> StressAsync(
        string kind,
        int seconds,
        Limits limits,
        IProgress<TuneProgress>? progress,
        CancellationToken ct,
        string cpuMode = "all",
        int cpuCore = -1)
    {
        var mark = DateTime.UtcNow;
        var args = kind == "gpu"
            ? new[] { "stress-gpu", "--seconds", seconds.ToString() }
            : PerCoreGuide.HelperArgs(seconds, cpuMode, cpuCore).ToArray();

        var last = new Dictionary<string, double?>();
        var ticks = new List<IReadOnlyDictionary<string, double?>>();
        JsonElement? result = null;
        string trip = "";
        try
        {
            await foreach (var ev in _hw.Stream(args, ct))
            {
                var type = ev.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
                if (type == "tick")
                {
                    last = ReadMetrics(ev);
                    ticks.Add(last);
                    var reason = Safety.Trip(last, limits);
                    progress?.Report(new TuneProgress(
                        $"{kind} {Int(ev, "elapsed_s")}/{seconds}s",
                        Math.Clamp((Int(ev, "elapsed_s") ?? 0) / (double)Math.Max(1, seconds), 0, 1),
                        last, null, null));
                    if (reason is not null)
                    {
                        trip = reason;
                        break;
                    }
                }
                else if (type == "result")
                {
                    result = ev;
                    last = ReadMetrics(ev);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new StepResult(false, "cancelled", null, last, 0, ticks);
        }
        catch (HwException ex)
        {
            return new StepResult(false, ex.Message, null, last, 0, ticks);
        }

        var faults = _faults.Since(mark);
        if (faults.Count > 0)
        {
            var desc = string.Join(" | ", faults.Select(FaultClassifier.Describe));
            return new StepResult(false, desc, null, last, 0, ticks);
        }
        if (trip.Length > 0)
            return new StepResult(false, trip, null, last, 0, ticks);
        var passed = result is { } r && r.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.True;
        var thr = result is { } r2 && r2.TryGetProperty("throughput", out var th) && th.ValueKind == JsonValueKind.Number
            ? th.GetDouble() : 0;
        return new StepResult(passed, passed ? "ok" : "stress failed", null, last, thr, ticks);
    }

    public async Task<IReadOnlyList<CoreResult>> PerCoreStressAsync(
        int seconds,
        Limits limits,
        IProgress<TuneProgress>? progress,
        CancellationToken ct)
    {
        int n = Environment.ProcessorCount;
        var results = new List<CoreResult>();
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new TuneProgress($"Per-core {i}/{n - 1}", i / (double)Math.Max(1, n), null, $"core-{i}", null));
            var r = await StressAsync("cpu", Math.Max(12, seconds), limits, progress, ct, "core", i);
            results.Add(new CoreResult(i, r.Ok, r.Reason));
            progress?.Report(new TuneProgress(
                r.Ok ? $"Core {i} PASS" : $"Core {i} FAIL: {r.Reason}",
                (i + 1) / (double)n, r.Metrics, $"core-{i}", r.Ok));
        }
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "logs", "cpu-per-core.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }),
            ct);
        return results;
    }

    public bool HasProgress(string? goal = null)
    {
        var state = LoadProgress();
        if (state is null) return false;
        if (goal is null) return true;
        return string.Equals(state.Goal, goal, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrEmpty(state.LastCompletedStep);
    }

    public void ClearAutotuneProgress() => ClearProgress();

    public void RestoreSessionCpuStock(SmuService smu, CpuPboProfile seed)
        => ApplySessionStockCo(smu, seed);

    static void ApplySessionStockCo(SmuService smu, CpuPboProfile seed)
    {
        var stock = seed.Clone();
        int n = Math.Max(1, Environment.ProcessorCount);
        stock.Cores = Enumerable.Range(0, n).Select(i => CurveOptimizerCore.FromSigned(i, 0)).ToList();
        smu.Apply(stock, PersistMode.Session);
    }

    public async Task<Dictionary<string, object?>> GpuAutotuneAsync(
        string goal,
        int seconds,
        Limits limits,
        bool skipVram,
        IProgress<TuneProgress>? progress,
        CancellationToken ct,
        bool resume = true)
    {
        void Log(string msg, double frac, Dictionary<string, double?>? m = null, string? step = null, bool? ok = null)
            => progress?.Report(new TuneProgress(msg, frac, m, step, ok));

        Log("Reading stock GPU settings…", 0.02);
        var stock = await Task.Run(() => _hw.Info(), ct);
        if (stock.ClockRange is null || stock.VoltageRange is null)
            throw new HwException("Could not read GPU clock/voltage ranges. Is Adrenalin running?");

        var stockVolt = stock.VoltageMv ?? stock.VoltageRange.Value.Max;
        var stockClock = stock.MaxMhz ?? stock.DefaultMaxMhz ?? 2500;
        var stockVram = stock.VramMhz ?? 2500;
        var stockMin = stock.MinMhz ?? 500;
        var stockPower = stock.PowerPct ?? 0;
        var voltLo = Math.Max(stock.VoltageRange.Value.Min, limits.MinVoltageMv);
        var voltHi = stock.VoltageRange.Value.Max;
        var clockHi = Math.Min(stock.ClockRange.Value.Max, limits.MaxClockMhz);
        var clockLo = Math.Max(stock.ClockRange.Value.Min, limits.MinClockMhz);
        var stepC = Math.Max(25, stock.ClockRange.Value.Step >= 10 ? stock.ClockRange.Value.Step : 25);

        var planned = AutotunePhases.Default.ToList();
        if (skipVram)
            planned.RemoveAll(s => s is "vram" or "fast-timing");

        var state = resume ? LoadProgress() : null;
        if (state is null || !string.Equals(state.Goal, goal, StringComparison.OrdinalIgnoreCase))
        {
            state = new AutotuneProgressFile
            {
                Goal = goal,
                PlannedSteps = planned,
                StockVolt = stockVolt,
                StockClock = stockClock,
                StockVram = stockVram,
                StockMin = stockMin,
                StockPower = stockPower,
            };
        }
        else
            planned = state.PlannedSteps.Count > 0 ? state.PlannedSteps : planned;

        var remaining = ResumePlanner.RemainingStepsIfStockMatches(
            planned, state.LastCompletedStep,
            stockVolt, stockClock, stockVram,
            state.StockVolt, state.StockClock, state.StockVram);
        if (remaining.Count == planned.Count && !string.IsNullOrEmpty(state.LastCompletedStep))
        {
            // Stock fingerprint mismatch — discard stale resume state.
            state.LastCompletedStep = null;
            state.DailyVoltage = null;
            state.LastGoodClock = null;
            state.LastGoodVram = null;
            state.StockVolt = stockVolt;
            state.StockClock = stockClock;
            state.StockVram = stockVram;
            state.StockMin = stockMin;
            state.StockPower = stockPower;
            Log("Progress invalidated (stock GPU changed) — restarting plan", 0.03);
        }
        Log(remaining.Count == planned.Count
            ? $"Starting auto-tune ({ClockSearch.DescribeGoal(goal)})"
            : $"Resuming after '{state.LastCompletedStep}' → next '{(remaining.Count == 0 ? "done" : remaining[0])}'", 0.04);
        Log(OptimizeSummary.PhaseChecklist(planned, state.LastCompletedStep, remaining.Count > 0 ? remaining[0] : null), 0.045);

        TuneSettings BaseAt(int volt, int clock, int vram, bool fast = false) =>
            new(clock, stockMin, volt, vram, fast, stockPower, PerformanceFan);

        async Task<StepResult> ApplyAndTest(string name, TuneSettings s, double frac)
        {
            ct.ThrowIfCancellationRequested();
            Log($"Apply {s.VoltageMv} mV · {s.MaxMhz} MHz · VRAM {s.VramMhz}", frac, null, name, null);
            await Task.Run(() => _hw.SetGpu(s.MaxMhz, s.MinMhz, s.VoltageMv, s.VramMhz, s.PowerPct, s.FastTiming, s.Fan), ct);
            await Task.Delay(800, ct);
            var res = await StressAsync("gpu", seconds, limits, progress, ct);
            res = res with { Settings = s };
            if (!res.Ok)
                Log($"FAIL {name}: {res.Reason}", frac, res.Metrics, name, false);
            else
                Log($"PASS {name}", frac, res.Metrics, name, true);
            return res;
        }

        async Task Complete(string step)
        {
            state.LastCompletedStep = step;
            SaveProgress(state);
        }

        bool Need(string step) => remaining.Contains(step);

        if (Need("baseline"))
        {
            Log("Baseline at stock settings", 0.08);
            var baseline = await ApplyAndTest("baseline", BaseAt(stockVolt, stockClock, stockVram), 0.1);
            if (!baseline.Ok)
            {
                Log("Baseline already unstable. Restoring factory.", 1, baseline.Metrics, "baseline", false);
                await Task.Run(() => _hw.Reset(), CancellationToken.None);
                return new Dictionary<string, object?> { ["ok"] = false, ["reason"] = baseline.Reason };
            }
            await Complete("baseline");
        }

        var memory = MachineTuneMemory.TryLoadMatching(MachineMemoryPath, stockVolt, stockClock, stockVram, goal);
        var hints = AdaptiveSearch.FromFault(memory?.LastFault ?? FaultKind.None, AdaptiveSearch.Defaults(stepC));
        FaultKind sessionFault = memory?.LastFault ?? FaultKind.None;

        int dailyVolt = state.DailyVoltage ?? stockVolt;
        int? foundFailMv = memory?.FailVoltageMv;
        if (Need("voltage"))
        {
            int voltStep = hints.VoltageStepMv;
            int searchMin = VoltageSearch.SearchFloorFromMemory(
                voltLo, voltStep, memory?.DailyVoltageMv, memory?.FailVoltageMv);
            int margin = VoltageSearch.MarginForFault(sessionFault, hints.VoltageMarginMv);
            Log($"Binary voltage search… (floor {searchMin} mV, step {voltStep}, margin {margin})", 0.15);
            int probe = 0;
            int probeTotalHint = Math.Max(3, (int)Math.Ceiling(Math.Log2(Math.Max(2, VoltageSearch.LinearCandidates(stockVolt, searchMin, voltStep).Count))) + 1);
            var found = await VoltageSearch.FindMinStableAsync(
                stockVolt, searchMin, voltStep, voltLo, voltHi,
                async v =>
                {
                    probe++;
                    Log($"Voltage probe {probe}/≈{probeTotalHint}: {v} mV", 0.15 + 0.25 * Math.Min(1, probe / (double)Math.Max(1, probeTotalHint)));
                    var res = await ApplyAndTest($"uv-{v}mV", BaseAt(v, stockClock, stockVram), 0.2);
                    if (!res.Ok)
                        sessionFault = FaultClassifier.MostSevere(new[] { sessionFault, FaultClassifier.ClassifyReason(res.Reason) });
                    return res.Ok;
                },
                margin);

            dailyVolt = found.DailyMv;
            foundFailMv = found.FailVoltageMv ?? foundFailMv;
            if (found.UsedLinearFallback)
                Log("Voltage oracle non-monotonic — used linear fallback from stock.", 0.42);

            // Re-adapt margin if voltage phase saw a hard fault.
            hints = AdaptiveSearch.FromFault(sessionFault, hints);
            margin = VoltageSearch.MarginForFault(sessionFault, hints.VoltageMarginMv);
            if (found.FailVoltageMv is int failV)
                dailyVolt = Math.Max(dailyVolt, VoltageSearch.DailyVoltageAfterFail(failV, margin, voltLo, voltHi));

            Log($"Confirming daily {dailyVolt} mV…", 0.43);
            dailyVolt = await VoltageSearch.ConfirmDailyAsync(
                dailyVolt, stockVolt, voltStep, voltLo, voltHi,
                async v =>
                {
                    var res = await ApplyAndTest($"daily-{v}mV", BaseAt(v, stockClock, stockVram), 0.44);
                    if (!res.Ok)
                        sessionFault = FaultClassifier.MostSevere(new[] { sessionFault, FaultClassifier.ClassifyReason(res.Reason) });
                    return res.Ok;
                });

            state.DailyVoltage = dailyVolt;
            Log(
                $"Voltage search: {found.Evaluated.Count} probes{(found.UsedLinearFallback ? " (linear)" : " (binary)")}. Daily {dailyVolt} mV (margin {margin} mV, confirmed).",
                0.45);
            await Complete("voltage");
        }

        int lastGoodClock = state.LastGoodClock ?? stockClock;
        if (Need("clock"))
        {
            hints = AdaptiveSearch.FromFault(sessionFault, AdaptiveSearch.Defaults(
                AdaptiveSearch.NextClockStep(stepC, sessionFault, nearCliff: false)));
            var candidates = ClockSearch.Candidates(
                goal, stockClock, lastGoodClock, clockLo, clockHi, hints.ClockStepMhz, hints, memory?.LastGoodClockMhz);
            Log($"Clock search ({TuneScore.NormalizeGoal(goal)}): {candidates.Count} candidates, step {hints.ClockStepMhz} MHz", 0.5);
            double refThr = 0;
            var walk = await ClockSearch.WalkAsync(
                goal,
                candidates,
                async c =>
                {
                    var res = await ApplyAndTest(
                        $"{(TuneScore.NormalizeGoal(goal) == "efficiency" ? "eff" : "oc")}-{c}MHz@{dailyVolt}mV",
                        BaseAt(dailyVolt, c, stockVram),
                        0.55);
                    if (!res.Ok)
                        sessionFault = FaultClassifier.MostSevere(new[] { sessionFault, FaultClassifier.ClassifyReason(res.Reason) });
                    double thr = res.Throughput;
                    if (refThr <= 0 && res.Ok && thr > 0) refThr = thr;
                    double? power = Metric(res.Metrics, "board_power_w", "power_w");
                    double? temp = Metric(res.Metrics, "hotspot_c", "gpu_temp_c");
                    return (res.Ok, thr, power, temp, res.Ok ? null : res.Reason);
                },
                hints,
                refThr);
            if (walk.BestMhz is int best)
                lastGoodClock = best;
            else if (walk.LastStableMhz is int stable)
                lastGoodClock = stable;
            if (walk.LastFault != FaultKind.None)
                sessionFault = FaultClassifier.MostSevere(new[] { sessionFault, walk.LastFault });
            Log(
                $"Clock search: {walk.Evaluated.Count} probes, best {lastGoodClock} MHz"
                + (walk.LastFault != FaultKind.None ? $" (fault {walk.LastFault})" : "")
                + ".",
                0.65);
            state.LastGoodClock = lastGoodClock;
            await Complete("clock");
        }

        int lastGoodVram = state.LastGoodVram ?? stockVram;
        bool lastFast = state.LastFast;
        if (Need("vram") && stock.VramRange is { } vr)
        {
            var vramHi = Math.Min(vr.Max, limits.MaxVramMhz);
            var stepM = Math.Max(50, vr.Step >= 10 ? vr.Step : 50);
            var mem = lastGoodVram + stepM;
            while (mem <= vramHi)
            {
                var res = await ApplyAndTest($"vram-{mem}", BaseAt(dailyVolt, lastGoodClock, mem), 0.75);
                if (!res.Ok) break;
                lastGoodVram = mem;
                mem += stepM;
            }
            state.LastGoodVram = lastGoodVram;
            await Complete("vram");
        }

        if (Need("fast-timing"))
        {
            var ft = await ApplyAndTest("fast-timing", BaseAt(dailyVolt, lastGoodClock, lastGoodVram, true), 0.85);
            lastFast = ft.Ok;
            state.LastFast = lastFast;
            await Complete("fast-timing");
        }

        var winner = BaseAt(dailyVolt, lastGoodClock, lastGoodVram, lastFast);
        Log($"Winner: {winner.VoltageMv} mV @ {winner.MaxMhz} MHz, VRAM {winner.VramMhz}", 0.9);
        await Task.Run(() => _hw.SetGpu(winner.MaxMhz, winner.MinMhz, winner.VoltageMv, winner.VramMhz, winner.PowerPct, winner.FastTiming, winner.Fan), ct);

        if (Need("final-soak"))
        {
            Log("Final validation soak…", 0.92);
            var final = await StressAsync("gpu", Math.Max(seconds, 90), limits, progress, ct);
            if (!final.Ok)
            {
                Log("Final soak failed — restoring factory.", 1, final.Metrics, "final-soak", false);
                await Task.Run(() => _hw.Reset(), CancellationToken.None);
                return new Dictionary<string, object?> { ["ok"] = false, ["reason"] = final.Reason, ["winner"] = winner };
            }
            await Complete("final-soak");
        }

        var profile = new GpuProfile
        {
            MaxMhz = winner.MaxMhz,
            MinMhz = winner.MinMhz,
            VoltageMv = winner.VoltageMv,
            VramMhz = winner.VramMhz,
            PowerPct = winner.PowerPct,
            FastTiming = winner.FastTiming,
            Fan = winner.Fan,
        };
        var profiles = Path.Combine(_root, "profiles");
        Directory.CreateDirectory(profiles);
        var path = Path.Combine(profiles, $"gpu-{goal}.json");
        await File.WriteAllTextAsync(path, profile.ToJson(), ct);
        await File.WriteAllTextAsync(StartupProfilePath, profile.ToJson(), ct);
        SaveMachineMemory(new MachineTuneMemory
        {
            Goal = goal,
            StockVoltMv = stockVolt,
            StockClockMhz = stockClock,
            StockVramMhz = stockVram,
            DailyVoltageMv = dailyVolt,
            FailVoltageMv = foundFailMv,
            LastGoodClockMhz = lastGoodClock,
            LastGoodVramMhz = lastGoodVram,
            LastFaultKind = sessionFault == FaultKind.None ? null : sessionFault.ToString(),
        });
        ClearProgress();
        Log($"Saved {path}", 1, null, "final-soak", true);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["winner"] = winner,
            ["profile"] = path,
        };
    }

    public GpuProfile? LoadStartupProfile() => GpuProfile.TryLoadFile(StartupProfilePath);

    public HwSnapshot ApplyProfile(GpuProfile p) => _hw.ApplyProfile(p);

    public CpuPboProfile? LoadCpuPboProfile() => CpuPboProfile.TryLoadFile(CpuPboProfilePath);

    public void SaveCpuPboProfile(CpuPboProfile p) => p.SaveFile(CpuPboProfilePath);

    public async Task<CpuPboProfile> CpuPboAutotuneAsync(
        SmuService smu,
        CpuPboProfile seed,
        int seconds,
        Limits limits,
        IProgress<TuneProgress>? progress,
        CancellationToken ct)
    {
        int n = Math.Max(1, Environment.ProcessorCount);
        progress?.Report(new TuneProgress("Applying Precision Boost Overdrive limits (session SMU)…", 0.02, null, "pbo", null));
        var pboOnly = seed.Clone();
        pboOnly.Cores = Enumerable.Range(0, n).Select(i => CurveOptimizerCore.FromSigned(i, 0)).ToList();
        var applied = smu.Apply(pboOnly, PersistMode.Session);
        if (!applied.SessionApplied || !applied.CoWritten)
            throw new HwException(applied.Error ?? "PBO session apply failed (per-core Curve Optimizer not written)");

        var mem = MachineTuneMemory.Load(MachineMemoryPath);
        IReadOnlyList<int>? priorCo = mem.CoMagnitudes is { Count: > 0 } mags ? mags : null;
        var coHints = AdaptiveSearch.FromFault(mem.LastFault, AdaptiveSearch.Defaults());
        FaultKind coFault = mem.LastFault;

        var current = new int[n];
        int probesDone = 0;
        var detailed = await CurveOptimizerSearch.SearchDetailedAsync(
            n,
            async (core, signedOffset) =>
            {
                ct.ThrowIfCancellationRequested();
                var probeOff = (int[])current.Clone();
                probeOff[core] = signedOffset;
                var probe = seed.Clone();
                probe.Cores = probeOff.Select((off, i) => CurveOptimizerCore.FromSigned(i, off)).ToList();
                var r = smu.Apply(probe, PersistMode.Session);
                if (!r.SessionApplied || !r.CoWritten)
                    return false;
                probesDone++;
                progress?.Report(new TuneProgress(
                    $"Core {core + 1}/{n} Curve Optimizer {signedOffset} (probe {probesDone})",
                    0.05 + 0.7 * (core / (double)n),
                    null, $"co-{core}", null));
                var stress = await StressAsync("cpu", Math.Max(8, seconds), limits, progress, ct, "core", core);
                if (!stress.Ok)
                    coFault = FaultClassifier.MostSevere(new[] { coFault, FaultClassifier.ClassifyReason(stress.Reason) });
                if (stress.Ok) current[core] = signedOffset;
                return stress.Ok;
            },
            seed.PptWatts, seed.TdcAmps, seed.EdcAmps, seed.BoostOverrideMhz, seed.Scalar,
            step: coHints.CoStep,
            maxMagnitude: coHints.CoMaxMagnitude,
            priorMagnitudes: priorCo);

        var profile = detailed.Profile;
        progress?.Report(new TuneProgress(
            $"Per-core search done ({detailed.ProbeCount} probes). All-core confirmation…",
            0.78, null, "co-confirm", null));

        coHints = AdaptiveSearch.FromFault(coFault, coHints);
        profile = await CurveOptimizerSearch.ConfirmAllCoresAsync(
            profile,
            async offsets =>
            {
                ct.ThrowIfCancellationRequested();
                var probe = seed.Clone();
                probe.Cores = offsets.Select((off, i) => CurveOptimizerCore.FromSigned(i, off)).ToList();
                var r = smu.Apply(probe, PersistMode.Session);
                if (!r.SessionApplied || !r.CoWritten)
                    return false;
                progress?.Report(new TuneProgress(
                    "All-core Curve Optimizer soak…",
                    0.85, null, "co-all", null));
                var stress = await StressAsync("cpu", Math.Max(12, seconds), limits, progress, ct, "all");
                if (!stress.Ok)
                    coFault = FaultClassifier.MostSevere(new[] { coFault, FaultClassifier.ClassifyReason(stress.Reason) });
                return stress.Ok;
            },
            step: coHints.CoStep,
            backoffSteps: coHints.ConfirmBackoffSteps);

        var final = smu.Apply(profile, PersistMode.Session);
        if (!final.SessionApplied || !final.CoWritten)
            throw new HwException(final.Error ?? "Final per-core Curve Optimizer apply failed");
        SaveCpuPboProfile(profile);
        try
        {
            var merged = MachineTuneMemory.Load(MachineMemoryPath);
            merged.CoMagnitudes = profile.Cores.OrderBy(c => c.Core).Select(c => c.Magnitude).ToList();
            if (coFault != FaultKind.None) merged.RememberFault(coFault);
            merged.Save(MachineMemoryPath);
        }
        catch { /* memory is best-effort */ }
        progress?.Report(new TuneProgress("Per-core Curve Optimizer search finished.", 1, null, "co-done", true));
        return profile;
    }

    public void CpuUnderclock(int percent)
    {
        percent = Math.Clamp(percent, 50, 100);
        foreach (var which in new[] { "setacvalueindex", "setdcvalueindex" })
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = $"/{which} SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX {percent}",
                CreateNoWindow = true,
                UseShellExecute = false,
            })?.WaitForExit(8000);
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = "/setactive SCHEME_CURRENT",
            CreateNoWindow = true,
            UseShellExecute = false,
        })?.WaitForExit(8000);
    }

    static double? Metric(IReadOnlyDictionary<string, double?> metrics, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (metrics.TryGetValue(k, out var v) && v is double d && !double.IsNaN(d) && !double.IsInfinity(d))
                return d;
        }
        return null;
    }

    void SaveMachineMemory(MachineTuneMemory mem)
    {
        try
        {
            var existing = MachineTuneMemory.Load(MachineMemoryPath);
            if (existing.CoMagnitudes is { Count: > 0 } && mem.CoMagnitudes is null)
                mem.CoMagnitudes = existing.CoMagnitudes;
            mem.Save(MachineMemoryPath);
        }
        catch { /* best-effort */ }
    }

    AutotuneProgressFile? LoadProgress()
    {
        try
        {
            if (!File.Exists(ProgressPath)) return null;
            return JsonSerializer.Deserialize<AutotuneProgressFile>(File.ReadAllText(ProgressPath));
        }
        catch { return null; }
    }

    void SaveProgress(AutotuneProgressFile state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath)!);
        File.WriteAllText(ProgressPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    void ClearProgress()
    {
        try { if (File.Exists(ProgressPath)) File.Delete(ProgressPath); } catch { /* ignore */ }
    }

    async Task PumpCpuTelemetryAsync(
        int seconds,
        Dictionary<string, double?> latest,
        Action onLive,
        CancellationToken ct)
    {
        var exe = AmdRyzenMasterBackend.FindHelper();
        if (exe is null) return;
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in CpuSmuProtocol.TelemetryArgs(seconds))
                psi.ArgumentList.Add(a);
            proc = Process.Start(psi);
            if (proc is null) return;
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                line = line.Trim();
                if (line.Length == 0) continue;
                lock (latest)
                {
                    RmCpuTelemetry.MergeJsonTick(latest, line);
                    if (latest.ContainsKey("cpu_power_w") || latest.ContainsKey("cpu_temp_c"))
                        onLive();
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch { /* fail closed — GPU ADLX still records */ }
        finally
        {
            if (proc is not null)
            {
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch { /* ignore */ }
                proc.Dispose();
            }
        }
    }

    void AppendBenchHistory(BenchRun run)
    {
        try
        {
            var dir = Path.Combine(_root, "logs");
            Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(run) + Environment.NewLine;
            File.AppendAllText(Path.Combine(dir, "bench-history.jsonl"), line);
        }
        catch { /* ignore */ }
    }

    static readonly string[] TickLiftKeys =
    [
        "cpu_clock_mhz", "cpu_usage_pct", "cpu_temp_c", "cpu_power_w", "ppt_w",
        "board_power_w", "power_w", "hotspot_c", "gpu_temp_c", "clock_mhz", "voltage_mv",
        "throughput",
    ];

    static Dictionary<string, double?> ReadMetrics(JsonElement ev)
    {
        var d = new Dictionary<string, double?>();
        if (ev.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in m.EnumerateObject())
                d[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble() : null;
        }
        foreach (var name in TickLiftKeys)
        {
            if (d.ContainsKey(name)) continue;
            if (ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                d[name] = v.GetDouble();
        }
        return d;
    }

    static int? Int(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "zenloop.cmd")) ||
                Directory.Exists(Path.Combine(dir.FullName, "zenloop")))
                return dir.FullName;
        }
        var data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZenLoop");
        Directory.CreateDirectory(Path.Combine(data, "profiles"));
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        return data;
    }
}
