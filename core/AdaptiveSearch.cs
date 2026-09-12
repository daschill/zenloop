namespace ZenLoop.Core;

/// <summary>
/// Adaptive step sizes, margins, and candidate narrowing from prior results and fault kinds.
/// </summary>
public sealed record AdaptiveSearchHints(
    int VoltageStepMv,
    int VoltageMarginMv,
    int ClockStepMhz,
    int CoStep,
    int CoMaxMagnitude,
    double PlateauEpsilonPct,
    int PlateauWindow,
    bool PreferConservativeClock,
    int ConfirmBackoffSteps);

public static class AdaptiveSearch
{
    public static AdaptiveSearchHints Defaults(int clockStepMhz = 25, int voltageStepMv = VoltageSearch.DefaultStepMv)
        => new(
            VoltageStepMv: Math.Max(1, voltageStepMv),
            VoltageMarginMv: VoltageSearch.DefaultMarginMv,
            ClockStepMhz: Math.Max(1, clockStepMhz),
            CoStep: CurveOptimizerSearch.DefaultStep,
            CoMaxMagnitude: CurveOptimizerSearch.DefaultMaxMagnitude,
            PlateauEpsilonPct: 1.5,
            PlateauWindow: 3,
            PreferConservativeClock: false,
            ConfirmBackoffSteps: 1);

    /// <summary>Tighten or loosen search based on the last observed fault.</summary>
    public static AdaptiveSearchHints FromFault(FaultKind kind, AdaptiveSearchHints? baseline = null)
    {
        var b = baseline ?? Defaults();
        return kind switch
        {
            FaultKind.Tdr => b with
            {
                VoltageStepMv = Math.Max(b.VoltageStepMv, 20),
                VoltageMarginMv = Math.Max(b.VoltageMarginMv, 25),
                ClockStepMhz = Math.Max(b.ClockStepMhz, 50),
                PreferConservativeClock = true,
                ConfirmBackoffSteps = 2,
                PlateauEpsilonPct = 2.0,
            },
            FaultKind.Whea => b with
            {
                VoltageMarginMv = Math.Max(b.VoltageMarginMv, 30),
                CoStep = Math.Max(b.CoStep, 5),
                CoMaxMagnitude = Math.Min(b.CoMaxMagnitude, 30),
                PreferConservativeClock = true,
                ConfirmBackoffSteps = 2,
            },
            FaultKind.Thermal => b with
            {
                ClockStepMhz = Math.Max(b.ClockStepMhz, 50),
                PreferConservativeClock = true,
                PlateauEpsilonPct = 1.0,
                PlateauWindow = 2,
                CoMaxMagnitude = Math.Min(b.CoMaxMagnitude, 35),
            },
            FaultKind.Instability => b with
            {
                VoltageStepMv = Math.Max(5, Math.Min(b.VoltageStepMv, 10)),
                ClockStepMhz = Math.Max(10, Math.Min(b.ClockStepMhz, 25)),
                CoStep = Math.Max(1, Math.Min(b.CoStep, 5)),
                ConfirmBackoffSteps = 1,
            },
            _ => b,
        };
    }

    /// <summary>
    /// Near a known fail cliff, shrink the voltage step so binary/linear probes land closer
    /// without adding a full linear sweep from stock.
    /// </summary>
    public static int NextVoltageStep(int baseStepMv, int? lastPassMv, int? lastFailMv)
    {
        if (baseStepMv < 1) baseStepMv = VoltageSearch.DefaultStepMv;
        if (lastPassMv is int pass && lastFailMv is int fail)
        {
            int gap = Math.Abs(pass - fail);
            if (gap > 0 && gap < baseStepMv * 2)
                return Math.Max(5, gap / 2);
        }
        return baseStepMv;
    }

    /// <summary>Coarse clock step far from the cliff; finer when the last climb just failed.</summary>
    public static int NextClockStep(int baseStepMhz, FaultKind lastFail, bool nearCliff)
    {
        if (baseStepMhz < 1) baseStepMhz = 25;
        if (lastFail == FaultKind.Thermal || lastFail == FaultKind.Tdr)
            return Math.Max(baseStepMhz, baseStepMhz * 2);
        if (nearCliff)
            return Math.Max(10, baseStepMhz / 2);
        return baseStepMhz;
    }

    /// <summary>
    /// When this machine already found a daily voltage, start probes near that band
    /// (stock → priorDaily−margin → priorFail) instead of the full legal range.
    /// </summary>
    public static List<int> VoltageCandidatesFromMemory(
        int stockMv,
        int minMv,
        int stepMv,
        int? priorDailyMv,
        int? priorFailMv)
    {
        if (stepMv < 1) stepMv = VoltageSearch.DefaultStepMv;
        if (minMv > stockMv) (minMv, stockMv) = (stockMv, minMv);

        if (priorDailyMv is not int daily)
            return VoltageSearch.LinearCandidates(stockMv, minMv, stepMv);

        daily = Math.Clamp(daily, minMv, stockMv);
        int floor = minMv;
        if (priorFailMv is int fail)
            floor = Math.Max(minMv, Math.Min(fail, daily) - stepMv * 2);

        // Always include stock, then descend from a band around prior daily.
        var set = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        set.Add(stockMv);
        for (int v = Math.Min(stockMv, daily + stepMv * 2); v >= floor; v -= stepMv)
            set.Add(Math.Clamp(v, minMv, stockMv));
        set.Add(daily);
        if (priorFailMv is int f)
            set.Add(Math.Clamp(f, minMv, stockMv));
        return set.ToList();
    }

    /// <summary>Seed Curve Optimizer binary search around previously stable magnitudes.</summary>
    public static (int LoMag, int HiMag) CoSearchWindow(int? priorMagnitude, int step, int maxMagnitude)
    {
        if (step < 1) step = CurveOptimizerSearch.DefaultStep;
        if (maxMagnitude < 0) maxMagnitude = 0;
        if (priorMagnitude is not int prior || prior <= 0)
            return (0, maxMagnitude);

        prior = Math.Clamp(prior, 0, maxMagnitude);
        int lo = Math.Max(0, prior - step * 2);
        int hi = Math.Min(maxMagnitude, prior + step);
        if (hi < lo) (lo, hi) = (hi, lo);
        return (lo, hi);
    }
}
