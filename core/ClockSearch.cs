namespace ZenLoop.Core;

/// <summary>
/// GPU core-clock search candidates by Optimize goal, with adaptive step / memory seeding.
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
        => Candidates(goal, stockMhz, lastGoodMhz, clockLo, clockHi, stepMhz, hints: null, priorGoodMhz: null);

    public static IReadOnlyList<int> Candidates(
        string goal,
        int stockMhz,
        int lastGoodMhz,
        int clockLo,
        int clockHi,
        int stepMhz,
        AdaptiveSearchHints? hints,
        int? priorGoodMhz)
    {
        if (stepMhz < 1) stepMhz = 25;
        if (hints is not null)
            stepMhz = Math.Max(1, hints.ClockStepMhz);
        goal = TuneScore.NormalizeGoal(goal);

        if (goal is "efficiency")
        {
            // Stock → lower. Caller keeps the best-scoring clock (prefers undervolt + lower MHz).
            var eff = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)))
            {
                stockMhz,
                Math.Max(clockLo, stockMhz - 100),
                Math.Max(clockLo, stockMhz - 200),
            };
            if (priorGoodMhz is int pg && pg >= clockLo && pg <= stockMhz)
                eff.Add(pg);
            // Finer mid points only when caller passed adaptive hints.
            if (hints is not null && stepMhz <= 25)
            {
                eff.Add(Math.Max(clockLo, stockMhz - 50));
                eff.Add(Math.Max(clockLo, stockMhz - 150));
            }
            return eff.ToList();
        }

        int max = clockHi;
        if (goal is "balanced")
        {
            // Mild OC: about +150 MHz or six steps, whichever is larger, still capped by hardware max.
            int mildCap = stockMhz + Math.Max(stepMhz * 6, 150);
            if (hints is { PreferConservativeClock: true })
                mildCap = stockMhz + Math.Max(stepMhz * 4, 100);
            max = Math.Min(clockHi, mildCap);
        }

        int start = lastGoodMhz;
        if (priorGoodMhz is int prior && prior > lastGoodMhz && prior <= max)
        {
            // Resume near what previously worked — still verify from lastGood upward in AdaptiveWalk.
            start = Math.Max(lastGoodMhz, prior - stepMhz);
        }

        var list = new List<int>();
        for (int c = start + stepMhz; c <= max; c += stepMhz)
            list.Add(c);
        return list;
    }

    /// <summary>
    /// Walk candidates for OC goals: stop on fail (with prune), plateau, or declining scores.
    /// Efficiency / scored goals keep the best TuneScore among passes.
    /// </summary>
    public static async Task<ClockSearchResult> WalkAsync(
        string goal,
        IReadOnlyList<int> candidates,
        Func<int, Task<(bool Ok, double Throughput, double? PowerW, double? TempC, string? FailReason)>> probe,
        AdaptiveSearchHints? hints = null,
        double referenceThroughput = 0)
    {
        hints ??= AdaptiveSearch.Defaults();
        goal = TuneScore.NormalizeGoal(goal);
        bool efficiency = goal is "efficiency";
        bool scorePick = efficiency || goal is "balanced";

        var evaluated = new List<int>();
        var scores = new List<double>();
        var passes = new List<TuneCandidate>();
        int? lastGood = null;
        FaultKind lastFault = FaultKind.None;
        double refThr = referenceThroughput;

        foreach (var c in candidates)
        {
            var (ok, thr, power, temp, reason) = await probe(c).ConfigureAwait(false);
            evaluated.Add(c);
            if (!ok)
            {
                lastFault = FaultClassifier.MostSevere(new[] { lastFault, FaultClassifier.ClassifyReason(reason) });
                // Efficiency probes sparse lower clocks — keep going. OC climbs prune the rest.
                if (!efficiency) break;
                continue;
            }

            lastGood = c;
            if (refThr <= 0 && thr > 0) refThr = thr;
            var cand = new TuneCandidate(c, thr, power, temp);
            passes.Add(cand);
            scores.Add(TuneScore.ScoreCandidate(goal, cand, refThr));

            if (!efficiency && SearchConvergence.IsPlateau(scores, hints.PlateauWindow, hints.PlateauEpsilonPct))
                break;
            if (!efficiency && SearchConvergence.ShouldPruneClimb(scores))
                break;
        }

        int? best = lastGood;
        if (scorePick && passes.Count > 0)
            best = TuneScore.PickBest(goal, passes, refThr) ?? lastGood;

        return new ClockSearchResult(best, lastGood, evaluated, lastFault, passes.Count);
    }

    public static string DescribeGoal(string goal) => TuneScore.NormalizeGoal(goal) switch
    {
        "performance" => "Performance: undervolt, then push core/VRAM clocks toward the limit.",
        "efficiency" => "Efficiency: undervolt, then try stock and lower core clocks for more work/W.",
        _ => "Balanced: undervolt, then a mild core overclock (not max).",
    };
}

public sealed record ClockSearchResult(
    int? BestMhz,
    int? LastStableMhz,
    IReadOnlyList<int> Evaluated,
    FaultKind LastFault,
    int PassCount);
