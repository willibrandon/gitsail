using GitSail.Domain;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies effective configured-tool discovery, precedence, flags, and validation.
/// </summary>
[TestClass]
public sealed class ConfiguredToolCatalogTests
{
    /// <summary>
    /// Verifies effective command precedence and companion settings produce one ordered tool.
    /// </summary>
    [TestMethod]
    public void Create_WithConfiguredTool_UsesEffectiveCommandSourceAndOptions()
    {
        var configuration = new GitConfigurationSnapshot(
        [
            Entry(GitConfigurationScope.Global, "guitool.team/review.cmd", "global command"),
            Entry(GitConfigurationScope.Local, "guitool.team/review.cmd", "local command"),
            Entry(GitConfigurationScope.Local, "guitool.team/review.title", "Review changes"),
            Entry(GitConfigurationScope.Local, "guitool.team/review.prompt", "Run review?"),
            Entry(GitConfigurationScope.Local, "guitool.team/review.needsfile", "true"),
            Entry(GitConfigurationScope.Local, "guitool.team/review.noconsole", "yes"),
            Entry(GitConfigurationScope.Local, "guitool.team/review.norescan", "on"),
        ]);

        var catalog = ConfiguredToolCatalog.Create(configuration);

        Assert.HasCount(1, catalog.Tools);
        var tool = catalog.Tools[0];
        Assert.AreEqual("team/review", tool.Name);
        Assert.AreEqual("Review changes", tool.Title);
        Assert.AreEqual("local command", tool.Command);
        Assert.AreEqual(GitConfigurationScope.Local, tool.SourceScope);
        Assert.AreEqual("Run review?", tool.Prompt);
        Assert.IsTrue(tool.NeedsFile);
        Assert.IsTrue(tool.NoConsole);
        Assert.IsTrue(tool.NoRescan);
        Assert.IsTrue(tool.IsAvailable);
        Assert.IsNull(catalog.Warning);
    }

    /// <summary>
    /// Verifies an empty effective command remains visible with an actionable unavailable reason.
    /// </summary>
    [TestMethod]
    public void Create_WithEmptyCommand_RetainsUnavailableTool()
    {
        var configuration = new GitConfigurationSnapshot(
        [
            Entry(GitConfigurationScope.Local, "guitool.review.cmd", string.Empty),
        ]);

        var catalog = ConfiguredToolCatalog.Create(configuration);

        Assert.HasCount(1, catalog.Tools);
        Assert.IsFalse(catalog.Tools[0].IsAvailable);
        StringAssert.Contains(catalog.Tools[0].UnavailableReason, "empty", StringComparison.OrdinalIgnoreCase);
    }

    private static GitConfigurationEntry Entry(
        GitConfigurationScope scope,
        string key,
        string value)
        => new(
            scope,
            GitConfigurationOrigin.FromBytes("file:test-config"u8),
            GitConfigurationKey.FromBytes(Encoding.UTF8.GetBytes(key)),
            GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(value)));
}
