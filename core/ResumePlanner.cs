namespace ZenLoop.Core;

/// <summary>
/// Auto-tune resume: the persisted last-completed step is skipped, and the next planned step runs first.
/// Progress is invalidated when stock GPU fingerprint no longer matches (e.g. after a factory reset).
/// </summary>
public static class ResumePlanner
{
    public static IReadOnlyList<string> RemainingSteps(
        IReadOnlyList<string> planned,
        string? lastCompletedStep)
    {
        if (planned.Count == 0)
            return planned;
        if (string.IsNullOrEmpty(lastCompletedStep))
            return planned;
        int i = IndexOf(planned, lastCompletedStep);
        if (i < 0)
            return planned;
        return planned.Skip(i + 1).ToList();
    }

    /// <summary>
    /// Like <see cref="RemainingSteps"/>, but restarts the full plan when stock clocks/voltage
    /// no longer match what was recorded when progress was saved.
    /// </summary>
    public static IReadOnlyList<string> RemainingStepsIfStockMatches(
        IReadOnlyList<string> planned,
        string? lastCompletedStep,
        int stockVolt,
        int stockClock,
        int stockVram,
        int savedStockVolt,
        int savedStockClock,
        int savedStockVram)
    {
        if (!StockFingerprintMatches(stockVolt, stockClock, stockVram, savedStockVolt, savedStockClock, savedStockVram))
            return planned;
        return RemainingSteps(planned, lastCompletedStep);
    }

    public static bool StockFingerprintMatches(
        int stockVolt,
        int stockClock,
        int stockVram,
        int savedStockVolt,
        int savedStockClock,
        int savedStockVram)
        => stockVolt == savedStockVolt
           && stockClock == savedStockClock
           && stockVram == savedStockVram;

    public static bool ShouldSkip(string stepName, string? lastCompletedStep, IReadOnlyList<string> planned)
    {
        var remaining = RemainingSteps(planned, lastCompletedStep);
        return remaining.Count == 0 || !string.Equals(remaining[0], stepName, StringComparison.Ordinal);
    }

    static int IndexOf(IReadOnlyList<string> items, string name)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i], name, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
