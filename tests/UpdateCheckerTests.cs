using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.2.0", "1.2.0", 0)]
    [InlineData("v1.2.0", "1.2.0", 0)]
    [InlineData("1.2.1", "1.2.0", 1)]
    [InlineData("1.1.9", "1.2.0", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1", "1.0.0", 0)]
    public void CompareVersions_orders_semver_core(string a, string b, int expectedSign)
    {
        var cmp = UpdateChecker.CompareVersions(a, b);
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    [Fact]
    public void TryParseVersion_rejects_garbage()
    {
        Assert.False(UpdateChecker.TryParseVersion("", out _));
        Assert.False(UpdateChecker.TryParseVersion("not-a-version", out _));
        Assert.True(UpdateChecker.TryParseVersion("1.2.3-beta", out var v));
        Assert.Equal(new Version(1, 2, 3), v);
    }

    [Fact]
    public void ParseManifest_reads_version_url_notes()
    {
        var m = UpdateChecker.ParseManifest("""
            {
              "version": "v1.3.0",
              "url": "https://example.com/dl",
              "notes": "fixes"
            }
            """);
        Assert.NotNull(m);
        Assert.Equal("1.3.0", m!.Version);
        Assert.Equal("https://example.com/dl", m.Url);
        Assert.Equal("fixes", m.Notes);
    }

    [Fact]
    public void ParseManifest_rejects_missing_or_invalid_version()
    {
        Assert.Null(UpdateChecker.ParseManifest("{}"));
        Assert.Null(UpdateChecker.ParseManifest("""{"version":""}"""));
        Assert.Null(UpdateChecker.ParseManifest("""{"version":"abc"}"""));
        Assert.Null(UpdateChecker.ParseManifest("not json"));
    }

    [Fact]
    public void Evaluate_detects_update_available()
    {
        var remote = new UpdateChecker.Manifest("1.2.0", "https://example.com/r", "n");
        var result = UpdateChecker.Evaluate("1.1.0", remote);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("1.2.0", result.RemoteVersion);
        Assert.Contains("Update available", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://example.com/r", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_same_or_older_remote_is_up_to_date()
    {
        var same = UpdateChecker.Evaluate("1.2.0", new UpdateChecker.Manifest("1.2.0"));
        Assert.False(same.UpdateAvailable);
        Assert.Contains("Up to date", same.Message, StringComparison.OrdinalIgnoreCase);

        var older = UpdateChecker.EvaluateJson("1.2.0", """{"version":"1.1.0"}""");
        Assert.False(older.UpdateAvailable);
    }

    [Fact]
    public void EvaluateJson_soft_fails_on_bad_payload()
    {
        var bad = UpdateChecker.EvaluateJson("1.0.0", "{nope");
        Assert.False(bad.UpdateAvailable);
        Assert.Contains("invalid", bad.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_and_raw_manifest_urls_are_documented_constants()
    {
        Assert.Contains("releases/latest/download/version.json", UpdateChecker.DefaultManifestUrl);
        Assert.Contains("raw.githubusercontent.com", UpdateChecker.DocumentedRawManifestUrl);
        Assert.Contains("version.json", UpdateChecker.DocumentedRawManifestUrl);
    }

    [Fact]
    public void AppSettings_round_trips_update_manifest_url()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zenloop-upd-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "app-settings.json");
        try
        {
            var s = new AppSettings { UpdateManifestUrl = UpdateChecker.DocumentedRawManifestUrl };
            s.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal(UpdateChecker.DocumentedRawManifestUrl, loaded.UpdateManifestUrl);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
