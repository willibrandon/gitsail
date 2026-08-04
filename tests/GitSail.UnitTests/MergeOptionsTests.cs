using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies merge options reject contradictory or strategy-incompatible combinations.
/// </summary>
[TestClass]
public sealed class MergeOptionsTests
{
    /// <summary>
    /// Verifies squash and stop-before-commit cannot redundantly describe one transaction.
    /// </summary>
    [TestMethod]
    public void Constructor_WithSquashAndStopBeforeCommit_ThrowsArgumentException()
    {
        _ = Assert.ThrowsExactly<ArgumentException>(() => new MergeOptions(
            MergeFastForwardMode.Default,
            MergeStrategy.Default,
            MergeConflictPreference.Default,
            squash: true,
            stopBeforeCommit: true,
            GitOptionOverride.Configured,
            GitOptionOverride.Configured,
            GitOptionOverride.Configured));
    }

    /// <summary>
    /// Verifies an ort-only conflict preference cannot leak into another strategy.
    /// </summary>
    [TestMethod]
    public void Constructor_WithResolveAndTheirsPreference_ThrowsArgumentException()
    {
        _ = Assert.ThrowsExactly<ArgumentException>(() => new MergeOptions(
            MergeFastForwardMode.Default,
            MergeStrategy.Resolve,
            MergeConflictPreference.Theirs,
            squash: false,
            stopBeforeCommit: false,
            GitOptionOverride.Configured,
            GitOptionOverride.Configured,
            GitOptionOverride.Configured));
    }

    /// <summary>
    /// Verifies the default option set honors Git configuration without semantic overrides.
    /// </summary>
    [TestMethod]
    public void CreateDefault_ReturnsConfigurationHonoringOptions()
    {
        var options = MergeOptions.CreateDefault();

        Assert.AreEqual(MergeFastForwardMode.Default, options.FastForwardMode);
        Assert.AreEqual(MergeStrategy.Default, options.Strategy);
        Assert.AreEqual(MergeConflictPreference.Default, options.ConflictPreference);
        Assert.IsFalse(options.Squash);
        Assert.IsFalse(options.StopBeforeCommit);
        Assert.AreEqual(GitOptionOverride.Configured, options.AutoStash);
        Assert.AreEqual(GitOptionOverride.Configured, options.RerereAutoUpdate);
        Assert.AreEqual(GitOptionOverride.Configured, options.VerifySignatures);
    }
}
