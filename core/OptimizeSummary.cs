using System.Text;

namespace ZenLoop.Core;

/// <summary>
/// User-facing Optimize result text (progress checklist + post-tune summary).
/// </summary>
public static class OptimizeSummary
{
    public static string PhaseChecklist(IReadOnlyList<string> planned, string? lastCompletedStep, string? currentStep)
    {
        var remaining = new HashSet<string>(
            ResumePlanner.RemainingSteps(planned, lastCompletedStep),
            StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var step in planned)
        {
            bool isCurrent = string.Equals(step, currentStep, StringComparison.Ordinal);
            bool isDone = !isCurrent && !remaining.Contains(step);
            char mark = isCurrent ? '…' : isDone ? '✓' : '·';
            sb.Append(mark).Append(' ').Append(step).Append("  ");
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatPostTune(
        BenchDelta? delta,
        bool gpuOk,
        string? gpuReason,
        int? dailyMv,
        int? clockMhz,
        int? vramMhz,
        CpuPboProfile? cpu)
        => FormatEndSummary(
            passed: gpuOk && delta is not null,
            delta, gpuOk, gpuReason, dailyMv, clockMhz, vramMhz, cpu);

    /// <summary>
    /// Clear PASS/FAIL banner with score/temp/power deltas and optional abort reason.
    /// </summary>
    public static string FormatEndSummary(
        bool passed,
        BenchDelta? delta,
        bool gpuOk,
        string? gpuReason,
        int? dailyMv,
        int? clockMhz,
        int? vramMhz,
        CpuPboProfile? cpu,
        string? abortReason = null,
        double? baselineScore = null,
        double? tunedScore = null,
        bool resumed = false)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(abortReason))
            sb.AppendLine($"=== Optimize ABORT ===");
        else
            sb.AppendLine(passed ? "=== Optimize PASS ===" : "=== Optimize FAIL ===");

        if (!string.IsNullOrWhiteSpace(abortReason))
            sb.AppendLine("Abort reason: " + abortReason.Trim());

        if (resumed)
            sb.AppendLine("Resumed from checkpoint.");

        if (baselineScore is double b && tunedScore is double t)
        {
            var pct = b > 1e-9 ? (t - b) / b * 100.0 : 0;
            sb.AppendLine($"Score: {b:0.#} → {t:0.#} ({pct:+0.0;-0.0;0}%)");
        }
        else if (delta is not null)
            sb.AppendLine($"Speed: {delta.SpeedPct:+0.0;-0.0;0}% (geomean CPU/GPU/RAM)");

        if (delta is not null)
        {
            var bits = new List<string>();
            if (delta.HasTemp)
                bits.Add($"temp {delta.TempDeltaC:+0.0;-0.0;0}°C");
            if (delta.HasPower)
            {
                bits.Add($"power {delta.PowerDeltaW:+0.0;-0.0;0} W");
                bits.Add($"efficiency {delta.EfficiencyPct:+0.0;-0.0;0}%");
            }
            if (bits.Count > 0)
                sb.AppendLine("Deltas: " + string.Join("  ·  ", bits));
            sb.AppendLine(delta.Summary);
        }
        else if (string.IsNullOrWhiteSpace(abortReason))
            sb.AppendLine("Bench compare: incomplete (need baseline + current).");

        sb.AppendLine(gpuOk ? "GPU tune: PASS" : $"GPU tune: FAIL ({gpuReason ?? "unknown"})");
        if (dailyMv is int v)
        {
            sb.Append($"  Daily {v} mV");
            if (clockMhz is int c) sb.Append($" @ {c} MHz");
            if (vramMhz is int m) sb.Append($", VRAM {m}");
            sb.AppendLine();
        }
        if (cpu is { Cores.Count: > 0 })
        {
            int avg = (int)Math.Round(cpu.Cores.Average(c => c.Magnitude));
            int min = cpu.Cores.Min(c => c.Magnitude);
            int max = cpu.Cores.Max(c => c.Magnitude);
            sb.AppendLine($"CPU Curve Optimizer: −{min}…−{max} (avg −{avg}) across {cpu.Cores.Count} cores");
        }
        else
            sb.AppendLine("CPU Curve Optimizer: not applied");

        if (delta is not null)
            sb.AppendLine(delta.Report());

        return sb.ToString().TrimEnd();
    }

    public static string FormatAbort(string reason)
        => FormatEndSummary(
            passed: false, delta: null, gpuOk: false, gpuReason: null,
            dailyMv: null, clockMhz: null, vramMhz: null, cpu: null,
            abortReason: reason);
}
