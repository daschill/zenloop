namespace ZenLoop.Core;

/// <summary>
/// Per-core Curve Optimizer auto-search. Oracle: (coreIndex, signedOffset) => stable.
/// Negative offsets are undervolts (Ryzen Master).
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
        if (logicalCores < 1) throw new ArgumentOutOfRangeException(nameof(logicalCores));
        if (step < 1) step = DefaultStep;

        var cores = new List<CurveOptimizerCore>(logicalCores);
        for (int i = 0; i < logicalCores; i++)
        {
            int bestMag = 0;
            if (await coreStableAtSignedOffset(i, 0).ConfigureAwait(false))
            {
                for (int mag = step; mag <= maxMagnitude; mag += step)
                {
                    if (!await coreStableAtSignedOffset(i, -mag).ConfigureAwait(false))
                        break;
                    bestMag = mag;
                }
            }
            cores.Add(new CurveOptimizerCore
            {
                Core = i,
                Sign = "Negative",
                Magnitude = bestMag,
            });
        }

        return new CpuPboProfile
        {
            PboEnabled = true,
            PptWatts = pptWatts,
            TdcAmps = tdcAmps,
            EdcAmps = edcAmps,
            BoostOverrideMhz = boostOverrideMhz,
            Scalar = scalar,
            Cores = cores,
        };
    }
}
