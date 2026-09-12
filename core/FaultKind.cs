namespace ZenLoop.Core;

/// <summary>
/// Coarse fault taxonomy used to adapt step sizes, margins, and backoff.
/// </summary>
public enum FaultKind
{
    None = 0,
    /// <summary>CPU/memory corrected or uncorrected hardware error (WHEA).</summary>
    Whea,
    /// <summary>GPU display driver timeout / TDR.</summary>
    Tdr,
    /// <summary>Thermal trip during stress (GPU/CPU/VRAM caps).</summary>
    Thermal,
    /// <summary>Generic stress fail without a more specific classification.</summary>
    Instability,
}
