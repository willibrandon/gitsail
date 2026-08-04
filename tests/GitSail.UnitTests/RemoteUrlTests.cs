using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies exact remote URL retention and credential-safe display formatting.
/// </summary>
[TestClass]
public sealed class RemoteUrlTests
{
    /// <summary>
    /// Verifies URL user information and query values never enter the display representation.
    /// </summary>
    [TestMethod]
    public void RedactedDisplayText_WithCredentialsAndQuery_RemovesSecrets()
    {
        var url = RemoteUrl.FromText(
            "https://person:password@example.invalid/team/repository?token=secret#fragment");

        Assert.AreEqual(
            "https://example.invalid/team/repository?<redacted>#fragment",
            url.RedactedDisplayText);
        Assert.IsFalse(url.RedactedDisplayText.Contains("person", StringComparison.Ordinal));
        Assert.IsFalse(url.RedactedDisplayText.Contains("password", StringComparison.Ordinal));
        Assert.IsFalse(url.RedactedDisplayText.Contains("secret", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies scp-style user information is removed while the exact host and path remain visible.
    /// </summary>
    [TestMethod]
    public void RedactedDisplayText_WithScpStyleUrl_RemovesUserInformation()
    {
        var url = RemoteUrl.FromText("developer@example.invalid:team/repository.git");

        Assert.AreEqual(
            "example.invalid:team/repository.git",
            url.RedactedDisplayText);
    }

    /// <summary>
    /// Verifies exact diagnostic URL occurrences are replaced before exception construction.
    /// </summary>
    [TestMethod]
    public void RedactFrom_WithCredentialUrl_RemovesEverySecretOccurrence()
    {
        const string rawUrl = "https://person:password@example.invalid/repository?token=secret";
        var url = RemoteUrl.FromText(rawUrl);

        var redacted = url.RedactFrom($"first {rawUrl} second {rawUrl}");

        Assert.AreEqual(
            "first https://example.invalid/repository?<redacted> second https://example.invalid/repository?<redacted>",
            redacted);
        Assert.IsFalse(redacted.Contains("password", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("secret", StringComparison.Ordinal));
    }
}
