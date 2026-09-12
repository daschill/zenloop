namespace ZenLoop.Core;

public readonly record struct FaultEvent(string? Provider, int Id, string? Message);

/// <summary>
/// Classifies Windows Event Log records as WHEA / display-timeout faults for a failed tune step.
/// </summary>
public static class FaultClassifier
{
    public static bool IsFault(FaultEvent e)
    {
        var provider = e.Provider ?? "";
        var message = e.Message ?? "";

        if (provider.Contains("WHEA", StringComparison.OrdinalIgnoreCase))
            return true;

        // Display driver stopped responding (TDR) — require Display family or TDR wording.
        if (e.Id == 4101)
        {
            if (provider.Equals("Display", StringComparison.OrdinalIgnoreCase))
                return true;
            if (LooksLikeDisplayTimeout(message))
                return true;
        }

        if (provider.Equals("Display", StringComparison.OrdinalIgnoreCase) && e.Id is 1001)
            return true;

        if (LooksLikeDisplayTimeout(message))
            return true;

        if (message.Contains("WHEA", StringComparison.OrdinalIgnoreCase)
            && (e.Id is 18 or 19 or 47 || message.Contains("corrected hardware", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    public static string Describe(FaultEvent e)
        => $"{e.Provider} id={e.Id}: {(e.Message ?? "").Trim()}";

    static bool LooksLikeDisplayTimeout(string message)
        => message.Contains("Display driver", StringComparison.OrdinalIgnoreCase)
           && (message.Contains("stopped responding", StringComparison.OrdinalIgnoreCase)
               || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || message.Contains("recovered", StringComparison.OrdinalIgnoreCase)
               || message.Contains("TDR", StringComparison.OrdinalIgnoreCase));
}
