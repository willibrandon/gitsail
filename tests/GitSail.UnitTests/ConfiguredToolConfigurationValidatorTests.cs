using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies complete configured-tool draft validation before Git configuration writes.
/// </summary>
[TestClass]
public sealed class ConfiguredToolConfigurationValidatorTests
{
    /// <summary>
    /// Verifies a bounded exact tool name and command produce valid concrete keys.
    /// </summary>
    [TestMethod]
    public void TryValidate_WithCompleteDraft_AcceptsEveryField()
    {
        var configuration = Create("team/review", "printf review");

        var valid = ConfiguredToolConfigurationValidator.TryValidate(configuration, out var error);

        Assert.IsTrue(valid, error);
        Assert.IsNull(error);
    }

    /// <summary>
    /// Verifies empty commands, control-bearing names, and invalid Unicode are rejected.
    /// </summary>
    /// <param name="name">The proposed configured-tool name.</param>
    /// <param name="command">The proposed opaque command.</param>
    [TestMethod]
    [DataRow("review", "")]
    [DataRow("bad\nname", "printf review")]
    public void TryValidate_WithInvalidDraft_RejectsBeforeWriting(string name, string command)
    {
        var valid = ConfiguredToolConfigurationValidator.TryValidate(
            Create(name, command),
            out var error);

        Assert.IsFalse(valid);
        Assert.IsNotNull(error);
    }

    /// <summary>
    /// Verifies unpaired UTF-16 surrogates cannot enter command configuration.
    /// </summary>
    [TestMethod]
    public void TryValidate_WithInvalidUnicode_RejectsBeforeWriting()
    {
        var valid = ConfiguredToolConfigurationValidator.TryValidate(
            Create("review", new string((char)0xd800, 1)),
            out var error);

        Assert.IsFalse(valid);
        Assert.IsNotNull(error);
    }

    private static ConfiguredToolConfiguration Create(string name, string command)
        => new(
            name,
            command,
            "Review changes",
            "Run review?",
            "Arguments",
            "Revision",
            NoConsole: false,
            NeedsFile: true,
            Confirm: true,
            RevisionUnmerged: false,
            NoRescan: false);
}
