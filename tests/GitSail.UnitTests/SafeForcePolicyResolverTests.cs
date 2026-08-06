using GitSail.Domain;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies force-push configuration resolves to a fail-closed typed policy.
/// </summary>
[TestClass]
public sealed class SafeForcePolicyResolverTests
{
    /// <summary>
    /// Verifies the registered absent-value default permits the exact confirmation flow.
    /// </summary>
    [TestMethod]
    public void Resolve_WithAbsentValue_ReturnsExplicitLeasePolicy()
    {
        var policy = SafeForcePolicyResolver.Resolve(new GitConfigurationSnapshot([]));

        Assert.AreEqual(SafeForcePolicy.ExplicitLease, policy);
    }

    /// <summary>
    /// Verifies mixed-case Git spelling still selects the configured never policy.
    /// </summary>
    [TestMethod]
    public void Resolve_WithNeverValue_ReturnsNeverPolicy()
    {
        var configuration = new GitConfigurationSnapshot(
        [
            Entry("gitsail.safeForcePolicy", "never"),
        ]);

        var policy = SafeForcePolicyResolver.Resolve(configuration);

        Assert.AreEqual(SafeForcePolicy.Never, policy);
    }

    /// <summary>
    /// Verifies an invalid explicit policy cannot accidentally permit a forced update.
    /// </summary>
    [TestMethod]
    public void Resolve_WithInvalidValue_ReturnsNeverPolicy()
    {
        var configuration = new GitConfigurationSnapshot(
        [
            Entry("gitsail.safeforcepolicy", "force-everything"),
        ]);

        var policy = SafeForcePolicyResolver.Resolve(configuration);

        Assert.AreEqual(SafeForcePolicy.Never, policy);
    }

    private static GitConfigurationEntry Entry(string key, string value)
        => new(
            GitConfigurationScope.Local,
            GitConfigurationOrigin.FromBytes("file:local"u8),
            GitConfigurationKey.FromBytes(Encoding.UTF8.GetBytes(key)),
            GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(value)));
}
