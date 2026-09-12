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
    {
        var sb = new StringBuilder();
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
        {
            sb.AppendLine(delta.Summary);
            sb.AppendLine(delta.Report());
        }
        else
            sb.AppendLine("Bench compare: incomplete (need baseline + current).");

        return sb.ToString().TrimEnd();
    }
}
