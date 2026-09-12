using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZenLoop.Core;

/// <summary>
/// Optional non-DRM update check: compare local SemVer to a remote version JSON manifest.
/// </summary>
public static class UpdateChecker
{
    /// <summary>
    /// Default GitHub Releases asset. Publish <c>version.json</c> with each release
    /// (see <c>docs/version.example.json</c>). Alternate: raw path on a docs branch.
    /// </summary>
    public const string DefaultManifestUrl =
        "https://github.com/daschill/zenloop/releases/latest/download/version.json";

    /// <summary>Documented raw-JSON fallback when Releases assets are not used.</summary>
    public const string DocumentedRawManifestUrl =
        "https://raw.githubusercontent.com/daschill/zenloop/main/docs/version.json";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly Regex SemVerCore = new(
        @"^\s*v?(?<maj>\d+)(?:\.(?<min>\d+))?(?:\.(?<pat>\d+))?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed record Manifest(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("url")] string? Url = null,
        [property: JsonPropertyName("notes")] string? Notes = null,
        [property: JsonPropertyName("min_os")] string? MinOs = null);

    public sealed record Result(
        bool UpdateAvailable,
        string LocalVersion,
        string? RemoteVersion,
        string? DownloadUrl,
        string? Notes,
        string Message);

    /// <summary>Parse a SemVer-ish string into a comparable Version (major.minor.patch).</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = SemVerCore.Match(text);
        if (!m.Success) return false;
        var maj = int.Parse(m.Groups["maj"].Value);
        var min = m.Groups["min"].Success ? int.Parse(m.Groups["min"].Value) : 0;
        var pat = m.Groups["pat"].Success ? int.Parse(m.Groups["pat"].Value) : 0;
        version = new Version(maj, min, pat);
        return true;
    }

    /// <summary>Normalize to major.minor.patch for display.</summary>
    public static string NormalizeVersion(string? text)
        => TryParseVersion(text, out var v) ? $"{v.Major}.{v.Minor}.{v.Build}" : (text ?? "0.0.0").Trim();

    /// <returns>&gt;0 if a newer than b; 0 equal; &lt;0 if a older.</returns>
    public static int CompareVersions(string? a, string? b)
    {
        if (!TryParseVersion(a, out var va)) va = new Version(0, 0, 0);
        if (!TryParseVersion(b, out var vb)) vb = new Version(0, 0, 0);
        return va.CompareTo(vb);
    }

    public static bool IsNewer(string? remote, string? local)
        => CompareVersions(remote, local) > 0;

    public static Manifest? ParseManifest(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(json, JsonOpts);
            if (m is null || string.IsNullOrWhiteSpace(m.Version)) return null;
            if (!TryParseVersion(m.Version, out _)) return null;
            return m with
            {
                Version = NormalizeVersion(m.Version),
                Url = string.IsNullOrWhiteSpace(m.Url) ? null : m.Url.Trim(),
                Notes = string.IsNullOrWhiteSpace(m.Notes) ? null : m.Notes.Trim(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Result Evaluate(string localVersion, Manifest remote)
    {
        var local = NormalizeVersion(localVersion);
        var remoteVer = NormalizeVersion(remote.Version);
        var newer = IsNewer(remoteVer, local);
        if (newer)
        {
            var urlBit = string.IsNullOrWhiteSpace(remote.Url) ? "" : $" Download: {remote.Url}";
            return new Result(
                true, local, remoteVer, remote.Url, remote.Notes,
                $"Update available: {remoteVer} (you have {local}).{urlBit}");
        }
        return new Result(
            false, local, remoteVer, remote.Url, remote.Notes,
            $"Up to date ({local}).");
    }

    public static Result EvaluateJson(string localVersion, string json)
    {
        var manifest = ParseManifest(json);
        if (manifest is null)
            return new Result(false, NormalizeVersion(localVersion), null, null, null,
                "Update check failed: invalid version manifest JSON.");
        return Evaluate(localVersion, manifest);
    }

    /// <summary>Fetch manifest JSON and compare. Failures are soft (never throw for network).</summary>
    public static async Task<Result> CheckAsync(
        string localVersion,
        string? manifestUrl = null,
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl.Trim();
        var ownClient = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", $"ZenLoop/{NormalizeVersion(localVersion)}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new Result(false, NormalizeVersion(localVersion), null, null, null,
                    $"Update check skipped: HTTP {(int)resp.StatusCode} from manifest URL.");
            }
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return EvaluateJson(localVersion, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, NormalizeVersion(localVersion), null, null, null,
                "Update check skipped: " + ex.Message);
        }
        finally
        {
            if (ownClient) client.Dispose();
        }
    }
}
