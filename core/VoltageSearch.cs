namespace ZenLoop.Core;

/// <summary>
/// GPU voltage-cap search: binary probe of a monotonic stable/unstable oracle,
/// plus the daily-driver margin applied after the first (highest) failing undervolt.
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
    {
        var asc = LinearCandidates(stockMv, minMv, stepMv).OrderBy(v => v).ToList();
        var evaluated = new List<int>();
        int lo = 0, hi = asc.Count - 1;
        int? minPass = null;
        int? maxFail = null;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            int v = asc[mid];
            evaluated.Add(v);
            if (isStable(v))
            {
                minPass = v;
                hi = mid - 1;
            }
            else
            {
                maxFail = maxFail is int mf ? Math.Max(mf, v) : v;
                lo = mid + 1;
            }
        }

        int daily = minPass ?? Math.Clamp(stockMv, legalMin, legalMax);
        if (maxFail is int fail)
            daily = Math.Max(daily, DailyVoltageAfterFail(fail, marginMv, legalMin, legalMax));
        daily = Math.Clamp(daily, legalMin, legalMax);

        return new VoltageSearchResult(minPass, maxFail, daily, evaluated);
    }
}

public sealed record VoltageSearchResult(
    int? MinStableMv,
    int? FailVoltageMv,
    int DailyMv,
    IReadOnlyList<int> Evaluated);
