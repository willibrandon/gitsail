using GitSail.Domain;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies transport presentation preserves lines while redacting secrets and terminal controls.
/// </summary>
[TestClass]
public sealed class TransportTextFormatterTests
{
    /// <summary>
    /// Verifies configured credential URLs, carriage-return progress, and escape bytes are rendered safely.
    /// </summary>
    [TestMethod]
    public void Format_WithRemoteProgressAndControls_RedactsAndPreservesLines()
    {
        var name = RemoteName.FromBytes("origin"u8);
        var url = RemoteUrl.FromText("https://person:password@example.invalid/repository?token=secret");
        var catalog = new RemoteCatalog(
        [
            new RemoteInfo(name, [url], [url]),
        ]);

        var formatted = TransportTextFormatter.Format(
            "Fetching https://person:password@example.invalid/repository?token=secret\r50%\r100%\n\u001b[31m"u8,
            catalog);

        Assert.AreEqual(
            "Fetching https://example.invalid/repository?<redacted>\n50%\n100%\n<U+001B>[31m",
            formatted);
    }

    /// <summary>
    /// Verifies an effective URL produced by Git rewriting is redacted even when absent from the catalog.
    /// </summary>
    [TestMethod]
    public void Format_WithEffectiveCredentialUrl_RedactsAdditionalUrl()
    {
        var name = RemoteName.FromBytes("origin"u8);
        var configuredUrl = RemoteUrl.FromText("alias:");
        var effectiveUrl = RemoteUrl.FromText(
            "https://person:password@example.invalid/repository?token=secret");
        var catalog = new RemoteCatalog(
        [
            new RemoteInfo(name, [configuredUrl], [configuredUrl]),
        ]);

        var formatted = TransportTextFormatter.Format(
            "Pushed to https://person:password@example.invalid/repository?token=secret\n"u8,
            catalog,
            [effectiveUrl]);

        Assert.AreEqual("Pushed to https://example.invalid/repository?<redacted>\n", formatted);
    }
}
