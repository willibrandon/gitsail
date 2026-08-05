using GitSail.Domain;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies typed configuration resolution without collapsing scope, origin, emptiness, or invalid values.
/// </summary>
[TestClass]
public sealed class GitConfigurationSnapshotTests
{
    /// <summary>
    /// Verifies selected-scope state and effective command overrides remain independently visible.
    /// </summary>
    [TestMethod]
    public void Resolve_WithInvalidLocalAndValidCommand_PreservesBothStates()
    {
        var snapshot = new GitConfigurationSnapshot(
        [
            Entry(GitConfigurationScope.Global, "gui.trustmtime", "true", "file:global"),
            Entry(GitConfigurationScope.Local, "gui.trustmtime", "sometimes", "file:local"),
            Entry(GitConfigurationScope.Command, "gui.trustmtime", "false", "command line:"),
        ]);

        var local = snapshot.Resolve("gui.trustmtime", GitConfigurationScope.Local);
        var worktree = snapshot.Resolve("gui.trustmtime", GitConfigurationScope.Worktree);

        Assert.AreEqual(GitConfigurationResolutionState.ExplicitInvalid, local.State);
        StringAssert.Contains(local.ExplicitValidationError, "Git boolean");
        Assert.IsFalse(local.EffectiveParsedValue!.BooleanValue);
        Assert.AreEqual(GitConfigurationScope.Command, local.EffectiveEntry!.Scope);
        Assert.AreEqual(GitConfigurationResolutionState.Inherited, worktree.State);
        Assert.IsFalse(worktree.EffectiveParsedValue!.BooleanValue);
    }

    /// <summary>
    /// Verifies absent defaults and explicit empty values are distinct states.
    /// </summary>
    [TestMethod]
    public void Resolve_WithDefaultAndExplicitEmpty_DistinguishesAbsenceAndEmpty()
    {
        var absent = new GitConfigurationSnapshot([])
            .Resolve("gitsail.theme", GitConfigurationScope.Global);
        var empty = new GitConfigurationSnapshot(
        [
            Entry(GitConfigurationScope.Global, "gui.newbranchtemplate", string.Empty, "file:global"),
        ]).Resolve("gui.newbranchtemplate", GitConfigurationScope.Global);

        Assert.AreEqual(GitConfigurationResolutionState.Absent, absent.State);
        Assert.AreEqual("auto", absent.EffectiveParsedValue!.Text);
        Assert.AreEqual(GitConfigurationResolutionState.ExplicitEmpty, empty.State);
        Assert.AreEqual(string.Empty, empty.ExplicitParsedValue!.Text);
    }

    /// <summary>
    /// Verifies multivalue entries retain exact order and selected scope.
    /// </summary>
    [TestMethod]
    public void GetExplicitValues_WithRecentRepositories_RetainsAllValues()
    {
        var snapshot = new GitConfigurationSnapshot(
        [
            Entry(GitConfigurationScope.Global, "gui.recentrepo", "/first", "file:global"),
            Entry(GitConfigurationScope.Global, "gui.recentrepo", "/second", "file:global"),
            Entry(GitConfigurationScope.Local, "gui.recentrepo", "/local", "file:local"),
        ]);

        var values = snapshot.GetExplicitValues("gui.recentrepo", GitConfigurationScope.Global);

        Assert.HasCount(2, values);
        Assert.AreEqual("/first", Encoding.UTF8.GetString(values[0].Value.GetBytes()));
        Assert.AreEqual("/second", Encoding.UTF8.GetString(values[1].Value.GetBytes()));
    }

    private static GitConfigurationEntry Entry(
        GitConfigurationScope scope,
        string key,
        string value,
        string origin)
        => new(
            scope,
            GitConfigurationOrigin.FromBytes(Encoding.UTF8.GetBytes(origin)),
            GitConfigurationKey.FromBytes(Encoding.UTF8.GetBytes(key)),
            GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(value)));
}
