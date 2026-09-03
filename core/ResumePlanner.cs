namespace ZenLoop.Core;

/// <summary>
/// Auto-tune resume: the persisted last-completed step is skipped, and the next planned step runs first.
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

    public static bool ShouldSkip(string stepName, string? lastCompletedStep, IReadOnlyList<string> planned)
    {
        var remaining = RemainingSteps(planned, lastCompletedStep);
        return remaining.Count == 0 || remaining[0] != stepName;
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
