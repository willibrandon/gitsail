using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies fetch options retain only defined pruning and tag-policy values.
/// </summary>
[TestClass]
public sealed class FetchOptionsTests
{
    /// <summary>
    /// Verifies default fetch options preserve all effective Git configuration.
    /// </summary>
    [TestMethod]
    public void CreateDefault_ReturnsConfigurationHonoringOptions()
    {
        var options = FetchOptions.CreateDefault();

        Assert.AreEqual(GitOptionOverride.Configured, options.Prune);
        Assert.AreEqual(FetchTagMode.Configured, options.Tags);
    }

    /// <summary>
    /// Verifies undefined enum values cannot cross the typed fetch boundary.
    /// </summary>
    [TestMethod]
    public void Constructor_WithUndefinedValues_ThrowsArgumentOutOfRangeException()
    {
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FetchOptions(
            (GitOptionOverride)99,
            FetchTagMode.Configured));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FetchOptions(
            GitOptionOverride.Configured,
            (FetchTagMode)99));
    }
}
