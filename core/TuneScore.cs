namespace ZenLoop.Core;

/// <summary>Observed metrics for one tune candidate (clock / voltage / CO vector).</summary>
public readonly record struct TuneCandidate(
    int Key,
    double Throughput,
    double? PowerW,
    double? TempC);

/// <summary>
/// Multi-objective scoring aligned to Optimize goals: performance, balanced, efficiency.
/// Higher is better. Missing power/temp telemetry degrades gracefully to throughput-only.
/// </summary>
public static class TuneScore
{
    public static double Score(
        string goal,
        double throughput,
        double? powerW,
        double? tempC,
        double referenceThroughput = 0)
    {
        if (throughput <= 0) return double.NegativeInfinity;
        goal = NormalizeGoal(goal);

        double speed = referenceThroughput > 0
            ? throughput / referenceThroughput
            : throughput;

        double? eff = null;
        if (powerW is double w && w > 0)
            eff = throughput / w;

        double? cool = null;
        if (tempC is double t && t > 0)
            // Soft preference for cooler: 1.0 at 60 C, ~0.7 at 90 C.
            cool = Math.Clamp(1.2 - (t / 200.0), 0.4, 1.2);

        return goal switch
        {
            "performance" or "perf" => ScorePerformance(speed, cool, eff),
            "efficiency" or "eff" => ScoreEfficiency(speed, eff, cool),
            _ => ScoreBalanced(speed, cool, eff),
        };
    }

    public static double ScoreCandidate(string goal, TuneCandidate c, double referenceThroughput = 0)
        => Score(goal, c.Throughput, c.PowerW, c.TempC, referenceThroughput);

    /// <summary>Pick the best key among scored candidates; returns null if none are finite.</summary>
    public static int? PickBest(string goal, IReadOnlyList<TuneCandidate> candidates, double referenceThroughput = 0)
    {
        if (candidates.Count == 0) return null;
        int? bestKey = null;
        double best = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            double s = ScoreCandidate(goal, c, referenceThroughput);
            if (s > best)
            {
                best = s;
                bestKey = c.Key;
            }
        }
        return best is double.NegativeInfinity ? null : bestKey;
    }

    public static string NormalizeGoal(string? goal)
    {
        goal = (goal ?? "balanced").Trim().ToLowerInvariant();
        return goal switch
        {
            "perf" => "performance",
            "eff" => "efficiency",
            "balance" => "balanced",
            _ => goal,
        };
    }

    static double ScorePerformance(double speed, double? cool, double? eff)
    {
        // Dominated by speed; mild soft penalties only.
        double s = speed * 100.0;
        if (cool is double c) s *= 0.85 + 0.15 * c;
        if (eff is double e) s += Math.Log(1 + e) * 0.5;
        return s;
    }

    static double ScoreBalanced(double speed, double? cool, double? eff)
    {
        double s = speed * 60.0;
        if (cool is double c) s += c * 25.0;
        else s += 15.0;
        if (eff is double e) s += Math.Min(40.0, e * 8.0);
        else s += speed * 10.0;
        return s;
    }

    static double ScoreEfficiency(double speed, double? eff, double? cool)
    {
        if (eff is double e)
        {
            double s = e * 50.0 + speed * 15.0;
            if (cool is double c) s *= 0.9 + 0.1 * c;
            return s;
        }
        // No power telemetry: prefer modest speed (not max OC).
        double fallback = speed * 40.0;
        if (cool is double c2) fallback *= 0.85 + 0.15 * c2;
        return fallback;
    }
}
