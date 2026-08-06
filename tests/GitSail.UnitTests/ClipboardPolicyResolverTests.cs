using GitSail.Domain;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies clipboard configuration resolves to a fail-closed typed policy.
/// </summary>
[TestClass]
public sealed class ClipboardPolicyResolverTests
{
    /// <summary>
    /// Verifies the registered absent-value default selects automatic helper fallback.
    /// </summary>
    [TestMethod]
    public void Resolve_WithAbsentValue_ReturnsAutoPolicy()
    {
        var policy = ClipboardPolicyResolver.Resolve(new GitConfigurationSnapshot([]));

        Assert.AreEqual(ClipboardPolicy.Auto, policy);
    }

    /// <summary>
    /// Verifies each registered value resolves without depending on Git key casing.
    /// </summary>
    /// <param name="value">The configured enumeration value.</param>
    /// <param name="expected">The expected typed policy.</param>
    [TestMethod]
    [DataRow("off", "Off")]
    [DataRow("auto", "Auto")]
    [DataRow("OSC52", "Osc52")]
    [DataRow("helper", "Helper")]
    public void Resolve_WithRegisteredValue_ReturnsSelectedPolicy(
        string value,
        string expected)
    {
        var configuration = new GitConfigurationSnapshot(
        [
            Entry("gitsail.clipboard", value),
        ]);

        var policy = ClipboardPolicyResolver.Resolve(configuration);

        Assert.AreEqual(Enum.Parse<ClipboardPolicy>(expected), policy);
    }

    /// <summary>
    /// Verifies an invalid explicit value cannot emit clipboard content.
    /// </summary>
    [TestMethod]
    public void Resolve_WithInvalidValue_ReturnsOffPolicy()
    {
        var configuration = new GitConfigurationSnapshot(
        [
            Entry("gitsail.clipboard", "guess"),
        ]);

        var policy = ClipboardPolicyResolver.Resolve(configuration);

        Assert.AreEqual(ClipboardPolicy.Off, policy);
    }

    private static GitConfigurationEntry Entry(string key, string value)
        => new(
            GitConfigurationScope.Local,
            GitConfigurationOrigin.FromBytes("file:local"u8),
            GitConfigurationKey.FromBytes(Encoding.UTF8.GetBytes(key)),
            GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(value)));
}
