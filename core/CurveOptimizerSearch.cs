namespace ZenLoop.Core;

/// <summary>
/// Per-core Curve Optimizer auto-search. Oracle: (coreIndex, signedOffset) => stable.
/// Negative offsets are undervolts (Ryzen Master). Uses binary magnitude search, then
/// optional all-core confirmation with per-core backoff.
/// </summary>
public static class CurveOptimizerSearch
{
    public const int DefaultStep = 5;
    public const int DefaultMaxMagnitude = 40;

    public static CpuPboProfile Search(
        int logicalCores,
        Func<int, int, bool> coreStableAtSignedOffset,
        int pptWatts,
        int tdcAmps,
        int edcAmps,
        int boostOverrideMhz,
        int scalar = 1,
        int step = DefaultStep,
        int maxMagnitude = DefaultMaxMagnitude)
    {
        if (logicalCores < 1) throw new ArgumentOutOfRangeException(nameof(logicalCores));
        if (step < 1) step = DefaultStep;

        return SearchAsync(
            logicalCores,
            (core, off) => Task.FromResult(coreStableAtSignedOffset(core, off)),
            pptWatts, tdcAmps, edcAmps, boostOverrideMhz, scalar, step, maxMagnitude)
            .GetAwaiter().GetResult();
    }

    public static async Task<CpuPboProfile> SearchAsync(
        int logicalCores,
        Func<int, int, Task<bool>> coreStableAtSignedOffset,
        int pptWatts,
        int tdcAmps,
        int edcAmps,
        int boostOverrideMhz,
        int scalar = 1,
        int step = DefaultStep,
        int maxMagnitude = DefaultMaxMagnitude)
    {
        var detailed = await SearchDetailedAsync(
            logicalCores, coreStableAtSignedOffset,
            pptWatts, tdcAmps, edcAmps, boostOverrideMhz, scalar, step, maxMagnitude)
            .ConfigureAwait(false);
        return detailed.Profile;
    }

    public static async Task<CurveOptimizerSearchResult> SearchDetailedAsync(
        int logicalCores,
        Func<int, int, Task<bool>> coreStableAtSignedOffset,
        int pptWatts,
        int tdcAmps,
        int edcAmps,
        int boostOverrideMhz,
        int scalar = 1,
        int step = DefaultStep,
        int maxMagnitude = DefaultMaxMagnitude)
    {
        if (logicalCores < 1) throw new ArgumentOutOfRangeException(nameof(logicalCores));
        if (step < 1) step = DefaultStep;
        if (maxMagnitude < 0) maxMagnitude = 0;

        var cores = new List<CurveOptimizerCore>(logicalCores);
        int probes = 0;
        for (int i = 0; i < logicalCores; i++)
        {
            int bestMag = 0;
            probes++;
            if (await coreStableAtSignedOffset(i, 0).ConfigureAwait(false))
            {
                int lo = 1;
                int hi = maxMagnitude / step;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    int mag = mid * step;
                    probes++;
                    if (await coreStableAtSignedOffset(i, -mag).ConfigureAwait(false))
                    {
                        bestMag = mag;
                        lo = mid + 1;
                    }
                    else
                        hi = mid - 1;
                }
            }
            cores.Add(new CurveOptimizerCore
            {
                Core = i,
                Sign = "Negative",
                Magnitude = bestMag,
            });
        }

        var profile = new CpuPboProfile
        {
            PboEnabled = true,
            PptWatts = pptWatts,
            TdcAmps = tdcAmps,
            EdcAmps = edcAmps,
            BoostOverrideMhz = boostOverrideMhz,
            Scalar = scalar,
            Cores = cores,
        };
        return new CurveOptimizerSearchResult(profile, probes);
    }

    /// <summary>
    /// After per-core search, stress the full CO vector. On fail, back off the most aggressive
    /// core by one step and retry until stable or all cores are at 0.
    /// </summary>
    public static async Task<CpuPboProfile> ConfirmAllCoresAsync(
        CpuPboProfile profile,
        Func<IReadOnlyList<int>, Task<bool>> allCoresStableAtOffsets,
        int step = DefaultStep,
        int maxRounds = 64)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(allCoresStableAtOffsets);
        if (step < 1) step = DefaultStep;
        if (maxRounds < 1) maxRounds = 1;

        var ordered = profile.Cores.OrderBy(c => c.Core).ToList();
        if (ordered.Count == 0)
            return profile;

        var offsets = ordered.Select(c => c.SignedOffset).ToList();
        for (int round = 0; round < maxRounds; round++)
        {
            if (await allCoresStableAtOffsets(offsets).ConfigureAwait(false))
                break;

            int worst = -1;
            int worstMag = 0;
            for (int i = 0; i < offsets.Count; i++)
            {
                int mag = Math.Max(0, -offsets[i]);
                if (mag > worstMag)
                {
                    worstMag = mag;
                    worst = i;
                }
            }
            if (worst < 0 || worstMag <= 0)
                break;

            int next = worstMag - step;
            offsets[worst] = next > 0 ? -next : 0;
        }

        var confirmed = profile.Clone();
        confirmed.Cores = offsets.Select((off, i) => CurveOptimizerCore.FromSigned(i, off)).ToList();
        return confirmed;
    }

    /// <summary>Upper bound on linear probes for <paramref name="logicalCores"/> (for tests).</summary>
    public static int MaxLinearProbes(int logicalCores, int maxMagnitude, int step)
    {
        if (step < 1) step = DefaultStep;
        int perCore = 1 + Math.Max(0, maxMagnitude / step); // offset 0 + each step
        return logicalCores * perCore;
    }
}

public sealed record CurveOptimizerSearchResult(CpuPboProfile Profile, int ProbeCount);
