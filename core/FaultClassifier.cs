namespace ZenLoop.Core;

public readonly record struct FaultEvent(string? Provider, int Id, string? Message);

/// <summary>
/// Classifies Windows Event Log records and stress fail reasons as WHEA / TDR / thermal / instability.
/// </summary>
public static class FaultClassifier
{
    public static bool IsFault(FaultEvent e) => Classify(e) != FaultKind.None;

    public static FaultKind Classify(FaultEvent e)
    {
        var provider = e.Provider ?? "";
        var message = e.Message ?? "";

        if (provider.Contains("WHEA", StringComparison.OrdinalIgnoreCase)
            || (message.Contains("WHEA", StringComparison.OrdinalIgnoreCase)
                && (e.Id is 18 or 19 or 47 || message.Contains("corrected hardware", StringComparison.OrdinalIgnoreCase))))
            return FaultKind.Whea;

        if (e.Id == 4101)
        {
            if (provider.Equals("Display", StringComparison.OrdinalIgnoreCase))
                return FaultKind.Tdr;
            if (LooksLikeDisplayTimeout(message))
                return FaultKind.Tdr;
        }

        if (provider.Equals("Display", StringComparison.OrdinalIgnoreCase) && e.Id is 1001)
            return FaultKind.Tdr;

        if (LooksLikeDisplayTimeout(message))
            return FaultKind.Tdr;

        return FaultKind.None;
    }

    /// <summary>Map Autotune / Safety trip reason strings into a fault kind.</summary>
    public static FaultKind ClassifyReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FaultKind.None;
        if (reason.Contains("WHEA", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("corrected hardware", StringComparison.OrdinalIgnoreCase))
            return FaultKind.Whea;
        if (LooksLikeDisplayTimeout(reason)
            || reason.Contains("TDR", StringComparison.OrdinalIgnoreCase)
            || (reason.Contains("Display", StringComparison.OrdinalIgnoreCase)
                && reason.Contains("id=4101", StringComparison.OrdinalIgnoreCase)))
            return FaultKind.Tdr;
        if (reason.Contains("temp", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("hotspot", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("thermal", StringComparison.OrdinalIgnoreCase))
            return FaultKind.Thermal;
        if (string.Equals(reason, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "cancelled", StringComparison.OrdinalIgnoreCase))
            return FaultKind.None;
        return FaultKind.Instability;
    }

    /// <summary>Most severe among a set (WHEA &gt; TDR &gt; Thermal &gt; Instability &gt; None).</summary>
    public static FaultKind MostSevere(IEnumerable<FaultKind> kinds)
    {
        FaultKind worst = FaultKind.None;
        foreach (var k in kinds)
        {
            if (Severity(k) > Severity(worst))
                worst = k;
        }
        return worst;
    }

    public static FaultKind ParseKind(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return FaultKind.None;
        return Enum.TryParse<FaultKind>(name.Trim(), ignoreCase: true, out var k) ? k : FaultKind.None;
    }

    public static string Describe(FaultEvent e)
        => $"{e.Provider} id={e.Id}: {(e.Message ?? "").Trim()}";

    static int Severity(FaultKind k) => k switch
    {
        FaultKind.Whea => 4,
        FaultKind.Tdr => 3,
        FaultKind.Thermal => 2,
        FaultKind.Instability => 1,
        _ => 0,
    };

    static bool LooksLikeDisplayTimeout(string message)
        => message.Contains("Display driver", StringComparison.OrdinalIgnoreCase)
           && (message.Contains("stopped responding", StringComparison.OrdinalIgnoreCase)
               || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || message.Contains("recovered", StringComparison.OrdinalIgnoreCase)
               || message.Contains("TDR", StringComparison.OrdinalIgnoreCase));
}
