namespace ZenLoop.Core;

/// <summary>Why a per-app hot-apply tick did or did not apply a pack.</summary>
public enum AppProfileApplyDecision
{
    /// <summary>Caller should import/apply the matched pack now.</summary>
    Apply,

    /// <summary>Store matching is off.</summary>
    SkipStoreDisabled,

    /// <summary>User has not enabled automatic apply (default).</summary>
    SkipAutoApplyOff,

    /// <summary>No foreground process matched an enabled rule.</summary>
    SkipNoMatch,

    /// <summary>Optimize / exclusive tune is in progress — do not fight it.</summary>
    SkipBusy,

    /// <summary>Same pack already applied for this focus session.</summary>
    SkipAlreadyApplied,

    /// <summary>Foreground just changed; waiting for debounce window.</summary>
    SkipDebounce,

    /// <summary>Matched rule but pack file is missing or unreadable path.</summary>
    SkipMissingPack,
}

/// <summary>
/// Pure gating for Adrenalin-style per-app hot-apply.
/// Process watching and ADLX/SMU apply stay in the app layer.
/// </summary>
public static class AppProfileHotApply
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Decide whether to apply for this poll tick.
    /// <paramref name="matched"/> is the rule for the current foreground identity (or null).
    /// <paramref name="focusChangedUtc"/> is when the foreground match identity last changed.
    /// </summary>
    public static AppProfileApplyDecision Decide(
        AppProfileStore store,
        bool autoApplyEnabled,
        bool optimizeBusy,
        AppProfileRule? matched,
        string? lastAppliedPackPath,
        DateTime utcNow,
        DateTime? focusChangedUtc,
        TimeSpan? debounce = null,
        Func<string, bool>? packExists = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Enabled)
            return AppProfileApplyDecision.SkipStoreDisabled;
        if (!autoApplyEnabled)
            return AppProfileApplyDecision.SkipAutoApplyOff;
        if (optimizeBusy)
            return AppProfileApplyDecision.SkipBusy;
        if (matched is null)
            return AppProfileApplyDecision.SkipNoMatch;

        var exists = packExists ?? File.Exists;
        if (string.IsNullOrWhiteSpace(matched.PackPath) || !exists(matched.PackPath))
            return AppProfileApplyDecision.SkipMissingPack;

        var window = debounce ?? DefaultDebounce;
        if (focusChangedUtc is DateTime changed && utcNow - changed < window)
            return AppProfileApplyDecision.SkipDebounce;

        if (!string.IsNullOrEmpty(lastAppliedPackPath)
            && string.Equals(lastAppliedPackPath, matched.PackPath, StringComparison.OrdinalIgnoreCase))
            return AppProfileApplyDecision.SkipAlreadyApplied;

        return AppProfileApplyDecision.Apply;
    }

    public static string Describe(AppProfileApplyDecision d) => d switch
    {
        AppProfileApplyDecision.Apply => "apply",
        AppProfileApplyDecision.SkipStoreDisabled => "store disabled",
        AppProfileApplyDecision.SkipAutoApplyOff => "auto-apply off",
        AppProfileApplyDecision.SkipNoMatch => "no match",
        AppProfileApplyDecision.SkipBusy => "optimize busy",
        AppProfileApplyDecision.SkipAlreadyApplied => "already applied",
        AppProfileApplyDecision.SkipDebounce => "debounce",
        AppProfileApplyDecision.SkipMissingPack => "missing pack",
        _ => d.ToString(),
    };
}

/// <summary>
/// Stateful helper: tracks focus identity + last applied pack across poll ticks.
/// Thread-affine; call from UI timer or a single watcher thread.
/// </summary>
public sealed class AppProfileHotApplySession
{
    string? _focusKey;
    DateTime? _focusChangedUtc;
    string? _lastAppliedPackPath;

    public string? FocusKey => _focusKey;
    public string? LastAppliedPackPath => _lastAppliedPackPath;
    public DateTime? FocusChangedUtc => _focusChangedUtc;

    /// <summary>
    /// Update focus tracking from the current foreground identity, then evaluate apply.
    /// Returns the decision and the matched rule (when any).
    /// </summary>
    public (AppProfileApplyDecision Decision, AppProfileRule? Matched) Tick(
        AppProfileStore store,
        bool autoApplyEnabled,
        bool optimizeBusy,
        string? foregroundProcessNameOrPath,
        DateTime utcNow,
        TimeSpan? debounce = null,
        Func<string, bool>? packExists = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var matched = AppProfileMatcher.MatchForeground(store, foregroundProcessNameOrPath);
        var key = matched is null
            ? NormalizeFocusKey(foregroundProcessNameOrPath)
            : matched.Id + "|" + matched.PackPath;

        if (!string.Equals(key, _focusKey, StringComparison.OrdinalIgnoreCase))
        {
            _focusKey = key;
            _focusChangedUtc = utcNow;
            // Leaving a matched app clears "already applied" so returning re-applies after debounce.
            if (matched is null
                || !string.Equals(_lastAppliedPackPath, matched.PackPath, StringComparison.OrdinalIgnoreCase))
            {
                // Keep lastApplied until a different pack is applied; SkipAlreadyApplied
                // only fires when the same pack is still focused after debounce.
            }
        }

        var decision = AppProfileHotApply.Decide(
            store, autoApplyEnabled, optimizeBusy, matched,
            _lastAppliedPackPath, utcNow, _focusChangedUtc, debounce, packExists);
        return (decision, matched);
    }

    public void MarkApplied(string packPath)
    {
        _lastAppliedPackPath = packPath;
    }

    public void ClearApplied()
    {
        _lastAppliedPackPath = null;
    }

    static string NormalizeFocusKey(string? identity)
        => string.IsNullOrWhiteSpace(identity) ? "" : identity.Trim().ToLowerInvariant();
}
