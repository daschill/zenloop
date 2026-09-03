namespace ZenLoop.Core;

public readonly record struct PerCoreOutcome(int Core, bool Ok, string Reason);

/// <summary>
/// Guided Curve Optimizer reporting. CPU work is helper stress only — no BIOS/SMU/PBO writes.
/// </summary>
public static class PerCoreGuide
{
    public static IReadOnlyList<string> HelperArgs(int seconds, string mode, int core)
        => ["stress-cpu", "--seconds", seconds.ToString(), "--mode", mode, "--core", core.ToString()];

    public static IReadOnlyList<int> FailedCores(IEnumerable<PerCoreOutcome> results)
        => results.Where(r => !r.Ok).Select(r => r.Core).ToList();

    public static string FormatSummary(IReadOnlyList<PerCoreOutcome> results)
    {
        var failed = FailedCores(results);
        if (failed.Count == 0)
            return $"All {results.Count} logical cores passed.";
        return $"Failed cores: {string.Join(", ", failed)} — raise those CO offsets toward 0 in BIOS.";
    }
}
