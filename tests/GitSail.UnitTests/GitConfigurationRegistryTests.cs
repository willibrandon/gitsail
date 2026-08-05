using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies completeness and internal consistency of the typed configuration registry.
/// </summary>
[TestClass]
public sealed class GitConfigurationRegistryTests
{
    /// <summary>
    /// Verifies every registered pattern is unique, described, and has a valid declared default.
    /// </summary>
    [TestMethod]
    public void Definitions_WithDeclaredMetadata_AreUniqueAndValid()
    {
        var definitions = GitConfigurationRegistry.Definitions;

        Assert.IsGreaterThanOrEqualTo(85, definitions.Length);
        Assert.HasCount(
            definitions.Length,
            definitions.Select(static definition => definition.KeyPattern).Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var definition in definitions)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(definition.Description), definition.KeyPattern);
            if (definition.DefaultValue is not null)
            {
                Assert.IsTrue(
                    GitConfigurationValueValidator.TryParseText(
                        definition,
                        definition.DefaultValue,
                        out _,
                        out var error),
                    $"{definition.KeyPattern}: {error}");
            }
        }
    }

    /// <summary>
    /// Verifies fixed, dynamic, security-sensitive, and terminal-inapplicable keys resolve correctly.
    /// </summary>
    [TestMethod]
    public void Find_WithRequiredKeyFamilies_ReturnsMostSpecificDefinitions()
    {
        Assert.AreEqual(GitConfigurationValueKind.Boolean, GitConfigurationRegistry.Find("gui.trustmtime")!.ValueKind);
        Assert.AreEqual(GitConfigurationValueKind.String, GitConfigurationRegistry.Find("remote.origin.url")!.ValueKind);
        Assert.AreEqual(GitConfigurationValueKind.NativePath, GitConfigurationRegistry.Find("gui.recentrepo")!.ValueKind);
        Assert.AreEqual(GitConfigurationValueKind.Boolean, GitConfigurationRegistry.Find("mergetool.vscode.trustexitcode")!.ValueKind);
        Assert.AreEqual(GitConfigurationValueKind.String, GitConfigurationRegistry.Find("guitool.team/review.cmd")!.ValueKind);
        Assert.AreEqual(GitConfigurationValueKind.ChordList, GitConfigurationRegistry.Find("gitsail.keymap.repository.refresh")!.ValueKind);
        Assert.AreEqual(GitConfigurationValueKind.Capability, GitConfigurationRegistry.Find("gitsail.trustedRepository.abc123")!.ValueKind);
        Assert.IsFalse(GitConfigurationRegistry.Find("gui.geometry")!.IsTerminalApplicable);
        Assert.IsNull(GitConfigurationRegistry.Find("gittui.theme"));
        Assert.IsNull(GitConfigurationRegistry.Find("guitui.theme"));
    }

    /// <summary>
    /// Verifies every configuration key named by the design resolves through the locked registry contract.
    /// </summary>
    [TestMethod]
    public void Find_WithEveryRequiredDesignKey_ReturnsDefinition()
    {
        string[] exactKeys =
        [
            "user.name",
            "user.email",
            "user.signingkey",
            "commit.gpgsign",
            "commit.template",
            "commit.cleanup",
            "core.commentChar",
            "core.commentString",
            "i18n.commitEncoding",
            "gui.trustmtime",
            "gui.textconv",
            "gui.diffcontext",
            "gui.diffopts",
            "gui.displayuntracked",
            "gui.stageuntracked",
            "gui.maxfilesdisplayed",
            "gui.tabsize",
            "diff.renames",
            "diff.renameLimit",
            "branch.autoSetupMerge",
            "gui.matchtrackingbranch",
            "merge.summary",
            "merge.verbosity",
            "merge.diffstat",
            "merge.tool",
            "rerere.enabled",
            "push.default",
            "push.autoSetupRemote",
            "gui.pruneDuringFetch",
            "gui.recentrepo",
            "gui.maxrecentrepo",
            "gui.encoding",
            "gui.commitmsgwidth",
            "gui.newbranchtemplate",
            "gui.warndetachedcommit",
            "gui.spellingdictionary",
            "gui.search.case",
            "gui.search.regexp",
            "gui.gcwarning",
            "gui.autoexplore",
            "gui.fastcopyblame",
            "gui.copyblamethreshold",
            "gui.blamehistoryctx",
            "gui.usettk",
            "gui.fontui",
            "gui.fontdiff",
            "gui.geometry",
            "gui.wmstate",
            "gitsail.theme",
            "gitsail.colorDepth",
            "gitsail.unicode",
            "gitsail.ambiguousWidth",
            "gitsail.layout",
            "gitsail.restorePinnedMenus",
            "gitsail.showPushAction",
            "gitsail.autoRescan",
            "gitsail.wrapCommitMessage",
            "gitsail.clipboard",
            "gitsail.renameThreshold",
            "gitsail.safeForcePolicy",
            "gitsail.logLevel",
        ];
        foreach (var key in exactKeys)
        {
            Assert.IsNotNull(GitConfigurationRegistry.Find(key), key);
        }

        string[] dynamicKeys =
        [
            "color.diff.new",
            "remote.origin.url",
            "remote.origin.fetch",
            "mergetool.vscode.cmd",
            "guitool.review.cmd",
            "gitsail.keymap.Repository.Refresh",
            "gitsail.trustedRepository.0123456789abcdef",
        ];
        foreach (var key in dynamicKeys)
        {
            Assert.IsNotNull(GitConfigurationRegistry.Find(key), key);
        }
    }

    /// <summary>
    /// Verifies every shell-capable configuration family named by the security design is classified.
    /// </summary>
    [TestMethod]
    public void Find_WithExecutableConfiguration_ClassifiesBrokerInput()
    {
        string[] keys =
        [
            "core.hooksPath",
            "diff.external",
            "diff.markdown.textconv",
            "filter.lfs.process",
            "guitool.review.cmd",
            "merge.tool",
            "mergetool.vscode.cmd",
            "core.editor",
            "sequence.editor",
            "browser.firefox.cmd",
            "credential.helper",
            "core.sshCommand",
            "remote.origin.uploadpack",
            "gpg.program",
        ];
        foreach (var key in keys)
        {
            var definition = GitConfigurationRegistry.Find(key);
            Assert.IsNotNull(definition, key);
            Assert.AreNotEqual(GitConfigurationExecutionKind.None, definition.ExecutionKind, key);
        }
    }
}
