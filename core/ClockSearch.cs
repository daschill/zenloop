namespace ZenLoop.Core;

/// <summary>
/// GPU core-clock search candidates by Optimize goal.
/// </summary>
public static class ClockSearch
{
    /// <summary>
    /// Balanced: mild OC above stock. Performance: climb to max. Efficiency: stock and lower clocks.
    /// </summary>
    public static IReadOnlyList<int> Candidates(
        string goal,
        int stockMhz,
        int lastGoodMhz,
        int clockLo,
        int clockHi,
        int stepMhz)
    {
        if (stepMhz < 1) stepMhz = 25;
        goal = (goal ?? "balanced").Trim().ToLowerInvariant();

        if (goal is "efficiency" or "eff")
        {
            // Stock → lower. Caller keeps the last passing clock (prefers undervolt + lower MHz).
            return new[]
                {
                    stockMhz,
                    Math.Max(clockLo, stockMhz - 100),
                    Math.Max(clockLo, stockMhz - 200),
                }
                .Distinct()
                .OrderByDescending(c => c)
                .ToList();
        }

        int max = clockHi;
        if (goal is "balanced" or "balance")
        {
            // Mild OC: about +150 MHz or six steps, whichever is larger, still capped by hardware max.
            int mildCap = stockMhz + Math.Max(stepMhz * 6, 150);
            max = Math.Min(clockHi, mildCap);
        }

        var list = new List<int>();
        for (int c = lastGoodMhz + stepMhz; c <= max; c += stepMhz)
            list.Add(c);
        return list;
    }

    public static string DescribeGoal(string goal) => (goal ?? "balanced").Trim().ToLowerInvariant() switch
    {
        "performance" or "perf" => "Performance: undervolt, then push core/VRAM clocks toward the limit.",
        "efficiency" or "eff" => "Efficiency: undervolt, then try stock and lower core clocks for more work/W.",
        _ => "Balanced: undervolt, then a mild core overclock (not max).",
    };
}
