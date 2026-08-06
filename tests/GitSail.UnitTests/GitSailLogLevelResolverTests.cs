using GitSail.Domain;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies structured trace verbosity resolves from typed Git configuration.
/// </summary>
[TestClass]
public sealed class GitSailLogLevelResolverTests
{
    /// <summary>
    /// Verifies the registered absent-value default selects information severity.
    /// </summary>
    [TestMethod]
    public void Resolve_WithAbsentValue_ReturnsInformation()
    {
        var level = GitSailLogLevelResolver.Resolve(new GitConfigurationSnapshot([]));

        Assert.AreEqual(GitSailLogLevel.Information, level);
    }

    /// <summary>
    /// Verifies every registered value resolves without depending on Git key casing.
    /// </summary>
    /// <param name="value">The configured enumeration value.</param>
    /// <param name="expected">The expected typed level name.</param>
    [TestMethod]
    [DataRow("trace", "Trace")]
    [DataRow("debug", "Debug")]
    [DataRow("information", "Information")]
    [DataRow("warning", "Warning")]
    [DataRow("error", "Error")]
    [DataRow("critical", "Critical")]
    [DataRow("none", "None")]
    public void Resolve_WithRegisteredValue_ReturnsSelectedLevel(
        string value,
        string expected)
    {
        var configuration = new GitConfigurationSnapshot(
        [
            Entry("gitsail.logLevel", value),
        ]);

        var level = GitSailLogLevelResolver.Resolve(configuration);

        Assert.AreEqual(Enum.Parse<GitSailLogLevel>(expected), level);
    }

    /// <summary>
    /// Verifies invalid verbosity disables subsequent trace output instead of increasing disclosure.
    /// </summary>
    [TestMethod]
    public void Resolve_WithInvalidValue_ReturnsNone()
    {
        var configuration = new GitConfigurationSnapshot(
        [
            Entry("gitsail.loglevel", "everything"),
        ]);

        var level = GitSailLogLevelResolver.Resolve(configuration);

        Assert.AreEqual(GitSailLogLevel.None, level);
    }

    private static GitConfigurationEntry Entry(string key, string value)
        => new(
            GitConfigurationScope.Local,
            GitConfigurationOrigin.FromBytes("file:local"u8),
            GitConfigurationKey.FromBytes(Encoding.UTF8.GetBytes(key)),
            GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(value)));
}
