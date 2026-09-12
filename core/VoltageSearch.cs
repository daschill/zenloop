namespace ZenLoop.Core;

/// <summary>
/// GPU voltage-cap search: binary probe of a monotonic stable/unstable oracle,
/// with linear fallback when the oracle is noisy/non-monotonic, plus daily-driver
/// margin and a post-search confirmation walk.
/// </summary>
public static class VoltageSearch
{
    public const int DefaultMarginMv = 15;
    public const int DefaultStepMv = 15;

    public static int DailyVoltageAfterFail(int failVoltageMv, int marginMv, int legalMin, int legalMax)
    {
        if (marginMv <= 0)
            throw new ArgumentOutOfRangeException(nameof(marginMv), "Safety margin must be positive.");
        return Math.Clamp(failVoltageMv + marginMv, legalMin, legalMax);
    }

    /// <summary>Descending list stock, stock-step, … down to min (inclusive).</summary>
    public static List<int> LinearCandidates(int stockMv, int minMv, int stepMv)
    {
        if (stepMv < 1) stepMv = 1;
        if (minMv > stockMv) (minMv, stockMv) = (stockMv, minMv);
        var list = new List<int>();
        for (int v = stockMv; v >= minMv; v -= stepMv)
            list.Add(v);
        if (list.Count == 0 || list[^1] != minMv)
            list.Add(minMv);
        return list;
    }

    /// <summary>
    /// Count linear probes from stock downward until the oracle fails (inclusive of that fail).
    /// </summary>
    public static int CountLinearUntilFail(int stockMv, int minMv, int stepMv, Func<int, bool> isStable)
    {
        int n = 0;
        foreach (var v in LinearCandidates(stockMv, minMv, stepMv))
        {
            n++;
            if (!isStable(v))
                break;
        }
        return n;
    }

    public static VoltageSearchResult FindMinStable(
        int stockMv,
        int minMv,
        int stepMv,
        int legalMin,
        int legalMax,
        Func<int, bool> isStable,
        int marginMv = DefaultMarginMv)
        => FindMinStableAsync(
                stockMv, minMv, stepMv, legalMin, legalMax,
                v => Task.FromResult(isStable(v)),
                marginMv)
            .GetAwaiter().GetResult();

    public static async Task<VoltageSearchResult> FindMinStableAsync(
        int stockMv,
        int minMv,
        int stepMv,
        int legalMin,
        int legalMax,
        Func<int, Task<bool>> isStable,
        int marginMv = DefaultMarginMv)
    {
        if (stepMv < 1) stepMv = DefaultStepMv;
        var asc = LinearCandidates(stockMv, minMv, stepMv).OrderBy(v => v).ToList();
        var evaluated = new List<int>();
        var outcomes = new Dictionary<int, bool>();

        int lo = 0, hi = asc.Count - 1;
        int? minPass = null;
        int? maxFail = null;
        bool nonMonotonic = false;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            int v = asc[mid];
            evaluated.Add(v);
            bool stable = await isStable(v).ConfigureAwait(false);
            outcomes[v] = stable;
            if (IsNonMonotonic(outcomes))
            {
                nonMonotonic = true;
                break;
            }
            if (stable)
            {
                minPass = minPass is int mp ? Math.Min(mp, v) : v;
                hi = mid - 1;
            }
            else
            {
                maxFail = maxFail is int mf ? Math.Max(mf, v) : v;
                lo = mid + 1;
            }
        }

        if (nonMonotonic)
        {
            // Higher voltage should be more stable; fall back to linear from stock downward.
            return await LinearFindMinStableAsync(
                stockMv, minMv, stepMv, legalMin, legalMax, isStable, marginMv, evaluated)
                .ConfigureAwait(false);
        }

        // Post-check: if any undervolt passed, stock must also pass (monotonic UV).
        if (minPass is int passed && passed < stockMv)
        {
            if (!outcomes.ContainsKey(stockMv))
            {
                evaluated.Add(stockMv);
                bool stockOk = await isStable(stockMv).ConfigureAwait(false);
                outcomes[stockMv] = stockOk;
            }
            if (IsNonMonotonic(outcomes) || outcomes.TryGetValue(stockMv, out var ok) && !ok)
            {
                return await LinearFindMinStableAsync(
                    stockMv, minMv, stepMv, legalMin, legalMax, isStable, marginMv, evaluated)
                    .ConfigureAwait(false);
            }
        }

        int daily = minPass ?? Math.Clamp(stockMv, legalMin, legalMax);
        if (maxFail is int fail)
            daily = Math.Max(daily, DailyVoltageAfterFail(fail, marginMv, legalMin, legalMax));
        daily = Math.Clamp(daily, legalMin, legalMax);

        return new VoltageSearchResult(minPass, maxFail, daily, evaluated, UsedLinearFallback: false);
    }

    /// <summary>
    /// Stress-validate the daily voltage; if it fails, walk up by step toward stock until stable.
    /// </summary>
    public static async Task<int> ConfirmDailyAsync(
        int dailyMv,
        int stockMv,
        int stepMv,
        int legalMin,
        int legalMax,
        Func<int, Task<bool>> isStable)
    {
        if (stepMv < 1) stepMv = DefaultStepMv;
        dailyMv = Math.Clamp(dailyMv, legalMin, legalMax);
        stockMv = Math.Clamp(stockMv, legalMin, legalMax);
        for (int v = dailyMv; v <= stockMv; v += stepMv)
        {
            v = Math.Min(v, stockMv);
            if (await isStable(v).ConfigureAwait(false))
                return Math.Clamp(v, legalMin, legalMax);
            if (v >= stockMv) break;
        }
        return Math.Clamp(stockMv, legalMin, legalMax);
    }

    public static int ConfirmDaily(
        int dailyMv,
        int stockMv,
        int stepMv,
        int legalMin,
        int legalMax,
        Func<int, bool> isStable)
        => ConfirmDailyAsync(dailyMv, stockMv, stepMv, legalMin, legalMax, v => Task.FromResult(isStable(v)))
            .GetAwaiter().GetResult();

    static async Task<VoltageSearchResult> LinearFindMinStableAsync(
        int stockMv,
        int minMv,
        int stepMv,
        int legalMin,
        int legalMax,
        Func<int, Task<bool>> isStable,
        int marginMv,
        List<int> priorEvaluated)
    {
        var evaluated = new List<int>(priorEvaluated);
        int? minPass = null;
        int? maxFail = null;
        foreach (var v in LinearCandidates(stockMv, minMv, stepMv))
        {
            if (!evaluated.Contains(v))
                evaluated.Add(v);
            bool stable = await isStable(v).ConfigureAwait(false);
            if (stable)
                minPass = v; // descending: last pass is the lowest so far
            else
            {
                maxFail = v;
                break;
            }
        }

        // Linear descending: minPass is the last successful (lowest) voltage.
        int daily = minPass ?? Math.Clamp(stockMv, legalMin, legalMax);
        if (maxFail is int fail)
            daily = Math.Max(daily, DailyVoltageAfterFail(fail, marginMv, legalMin, legalMax));
        daily = Math.Clamp(daily, legalMin, legalMax);
        return new VoltageSearchResult(minPass, maxFail, daily, evaluated, UsedLinearFallback: true);
    }

    /// <summary>Widen daily margin after hard faults; keep default otherwise.</summary>
    public static int MarginForFault(FaultKind kind, int baseMarginMv = DefaultMarginMv)
    {
        if (baseMarginMv <= 0) baseMarginMv = DefaultMarginMv;
        return kind switch
        {
            FaultKind.Tdr or FaultKind.Whea => Math.Max(baseMarginMv, 25),
            FaultKind.Thermal => Math.Max(baseMarginMv, 20),
            _ => baseMarginMv,
        };
    }

    /// <summary>
    /// Narrow the search floor using machine memory so binary search converges faster
    /// without going below a previously observed fail cliff (minus a small cushion).
    /// </summary>
    public static int SearchFloorFromMemory(int legalMin, int stepMv, int? priorDailyMv, int? priorFailMv)
    {
        if (stepMv < 1) stepMv = DefaultStepMv;
        if (priorFailMv is int fail)
            return Math.Max(legalMin, fail - stepMv);
        if (priorDailyMv is int daily)
            return Math.Max(legalMin, daily - stepMv * 4);
        return legalMin;
    }

        /// <summary>Fail at a higher mV than a known pass violates undervolt monotonicity.</summary>
    static bool IsNonMonotonic(Dictionary<int, bool> outcomes)
    {
        foreach (var (vPass, ok) in outcomes)
        {
            if (!ok) continue;
            foreach (var (vFail, failOk) in outcomes)
            {
                if (!failOk && vFail > vPass)
                    return true;
            }
        }
        return false;
    }
}

public sealed record VoltageSearchResult(
    int? MinStableMv,
    int? FailVoltageMv,
    int DailyMv,
    IReadOnlyList<int> Evaluated,
    bool UsedLinearFallback = false);
