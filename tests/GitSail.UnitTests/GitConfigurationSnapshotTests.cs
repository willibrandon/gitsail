using GitSail.Domain;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies typed configuration resolution without collapsing scope, origin, emptiness, or invalid values.
/// </summary>
[TestClass]
public sealed class GitConfigurationSnapshotTests
{
    private static readonly string[] s_expectedDiffOptions =
    [
        "-U2",
        "--ignore-all-space",
        "--histogram",
        "--stat",
    ];

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

    /// <summary>
    /// Verifies Git's case-insensitive key semantics apply to single and multivalue lookup.
    /// </summary>
    [TestMethod]
    public void Resolve_WithMixedCaseKeySpelling_MatchesCanonicalRegistryKey()
    {
        var snapshot = new GitConfigurationSnapshot(
        [
            Entry(GitConfigurationScope.Local, "gitsail.restorePinnedMenus", "false", "file:local"),
            Entry(GitConfigurationScope.Global, "GUI.RecentRepo", "/first", "file:global"),
        ]);

        var restore = snapshot.Resolve("gitsail.restorepinnedmenus", GitConfigurationScope.Local);
        var recent = snapshot.GetExplicitValues("gui.recentrepo", GitConfigurationScope.Global);

        Assert.AreEqual(GitConfigurationResolutionState.Explicit, restore.State);
        Assert.IsFalse(restore.EffectiveParsedValue!.BooleanValue);
        Assert.HasCount(1, recent);
        Assert.AreEqual("/first", Encoding.UTF8.GetString(recent[0].Value.GetBytes()));
    }

    /// <summary>
    /// Verifies effective registered diff values become one immutable runtime configuration.
    /// </summary>
    [TestMethod]
    public void Resolve_WithDiffRuntimeValues_PreservesTypedBehavior()
    {
        var snapshot = new GitConfigurationSnapshot(
        [
            Entry(GitConfigurationScope.Local, "gui.diffcontext", "12", "file:local"),
            Entry(
                GitConfigurationScope.Local,
                "gui.diffopts",
                "-U2 --ignore-all-space --histogram --stat",
                "file:local"),
            Entry(GitConfigurationScope.Local, "diff.renames", "copies", "file:local"),
            Entry(GitConfigurationScope.Local, "diff.renamelimit", "321", "file:local"),
            Entry(GitConfigurationScope.Local, "gitsail.renamethreshold", "73", "file:local"),
            Entry(GitConfigurationScope.Local, "gui.tabsize", "6", "file:local"),
        ]);

        var configuration = GitDiffRuntimeConfiguration.Resolve(snapshot);
        var changedContext = configuration.WithContextLines(4);

        Assert.AreEqual(12, configuration.ContextLines);
        CollectionAssert.AreEqual(
            s_expectedDiffOptions,
            configuration.AdditionalOptions);
        Assert.AreEqual(GitRenameDetectionMode.Copies, configuration.RenameDetection);
        Assert.AreEqual(321, configuration.RenameLimit);
        Assert.AreEqual(73, configuration.RenameThreshold);
        Assert.AreEqual(6, configuration.TabSize);
        Assert.AreEqual(4, changedContext.ContextLines);
        Assert.AreEqual(configuration.AdditionalOptions, changedContext.AdditionalOptions);
        Assert.AreEqual(configuration.RenameDetection, changedContext.RenameDetection);
    }

    /// <summary>
    /// Verifies absent and invalid effective diff values use safe registered runtime defaults.
    /// </summary>
    [TestMethod]
    public void Resolve_WithAbsentOrInvalidDiffValues_UsesSafeDefaults()
    {
        var absent = GitDiffRuntimeConfiguration.Resolve(new GitConfigurationSnapshot([]));
        var invalid = GitDiffRuntimeConfiguration.Resolve(new GitConfigurationSnapshot(
        [
            Entry(GitConfigurationScope.Local, "gui.diffcontext", "invalid", "file:local"),
            Entry(GitConfigurationScope.Local, "gui.diffopts", "--ext-diff", "file:local"),
            Entry(GitConfigurationScope.Local, "diff.renames", "invalid", "file:local"),
            Entry(GitConfigurationScope.Local, "diff.renamelimit", "-1", "file:local"),
            Entry(GitConfigurationScope.Local, "gitsail.renamethreshold", "101", "file:local"),
            Entry(GitConfigurationScope.Local, "gui.tabsize", "0", "file:local"),
        ]));

        Assert.AreEqual(GitDiffRuntimeConfiguration.Default, absent);
        Assert.AreEqual(GitDiffRuntimeConfiguration.Default, invalid);
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
