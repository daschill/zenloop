namespace ZenLoop.Core;

/// <summary>
/// Early stop helpers: plateau detection and prune-on-declining-climb so OC searches
/// finish faster without walking every remaining candidate after the cliff is clear.
/// </summary>
public static class SearchConvergence
{
    /// <summary>
    /// True when the last <paramref name="window"/> scores improve by less than
    /// <paramref name="epsilonPct"/> percent relative to the first of that window.
    /// </summary>
    public static bool IsPlateau(IReadOnlyList<double> scores, int window = 3, double epsilonPct = 1.5)
    {
        if (window < 2) window = 2;
        if (scores.Count < window) return false;
        var slice = scores.Skip(scores.Count - window).ToList();
        if (slice.Any(s => double.IsNaN(s) || double.IsInfinity(s))) return false;
        double first = slice[0];
        if (Math.Abs(first) < 1e-12)
            return slice.All(s => Math.Abs(s - first) < 1e-9);
        double max = slice.Max();
        double min = slice.Min();
        double spanPct = (max - min) / Math.Abs(first) * 100.0;
        return spanPct < epsilonPct;
    }

    /// <summary>
    /// While climbing clocks, stop if score fell for <paramref name="declines"/> consecutive steps
    /// after at least one improvement (diminishing / negative returns).
    /// </summary>
    public static bool ShouldPruneClimb(IReadOnlyList<double> scores, int declines = 2)
    {
        if (declines < 1) declines = 1;
        if (scores.Count < declines + 1) return false;
        bool sawRise = false;
        for (int i = 1; i < scores.Count - declines; i++)
        {
            if (scores[i] > scores[i - 1] + 1e-9)
                sawRise = true;
        }
        if (!sawRise) return false;
        for (int i = scores.Count - declines; i < scores.Count; i++)
        {
            if (scores[i] >= scores[i - 1] - 1e-9)
                return false;
        }
        return true;
    }

    /// <summary>
    /// After a fail, decide whether remaining higher (OC) or lower (UV) candidates should be skipped.
    /// For OC climbs, anything above the fail is pruned; for UV descends, anything below.
    /// </summary>
    public static IReadOnlyList<int> PruneAfterFail(
        IReadOnlyList<int> remaining,
        int failedValue,
        bool climbing)
    {
        if (remaining.Count == 0) return remaining;
        return climbing
            ? remaining.Where(v => v < failedValue).ToList()
            : remaining.Where(v => v > failedValue).ToList();
    }
}
