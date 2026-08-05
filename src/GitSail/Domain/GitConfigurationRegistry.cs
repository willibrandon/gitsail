using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Exposes the complete typed configuration registry used by GitSail services and options.
/// </summary>
internal static class GitConfigurationRegistry
{
    private const GitConfigurationScopeMask AllScopes = GitConfigurationScopeMask.UserWritable;
    private const GitConfigurationScopeMask RepositoryScopes =
        GitConfigurationScopeMask.Local | GitConfigurationScopeMask.Worktree;
    private static readonly ImmutableArray<GitConfigurationDefinition> s_definitions =
    [
        String("user.name", null, AllScopes, "Commit author name"),
        String("user.email", null, AllScopes, "Commit author email"),
        String("user.signingkey", null, AllScopes, "Commit signing key"),
        Boolean("commit.gpgsign", "false", AllScopes, "Sign commits by default"),
        NativePath("commit.template", null, AllScopes, "Commit message template"),
        Enumeration("commit.cleanup", null, AllScopes,
            "Commit message cleanup mode", "strip", "whitespace", "verbatim", "scissors", "default"),
        String("core.commentchar", "#", AllScopes, "Commit comment character"),
        String("core.commentstring", null, AllScopes, "Commit comment string"),
        NativePath("core.hookspath", null, AllScopes, "Configured hooks directory",
            executionKind: GitConfigurationExecutionKind.Hooks),
        String("core.editor", null, AllScopes, "Commit editor command",
            executionKind: GitConfigurationExecutionKind.Editor),
        String("sequence.editor", null, AllScopes, "Interactive rebase sequence editor command",
            executionKind: GitConfigurationExecutionKind.Editor),
        String("core.askpass", null, AllScopes, "Credential prompt helper command",
            executionKind: GitConfigurationExecutionKind.CredentialHelper),
        String("core.sshcommand", null, AllScopes, "SSH transport command",
            executionKind: GitConfigurationExecutionKind.Ssh),
        String("i18n.commitencoding", "UTF-8", AllScopes, "Commit object encoding"),
        String("gpg.program", null, AllScopes, "OpenPGP signing program",
            executionKind: GitConfigurationExecutionKind.Signing),
        Enumeration("gpg.format", "openpgp", AllScopes,
            "Commit signing format", "openpgp", "x509", "ssh"),
        String("gpg.openpgp.program", null, AllScopes, "OpenPGP signing program override",
            executionKind: GitConfigurationExecutionKind.Signing),
        String("gpg.x509.program", null, AllScopes, "X.509 signing program",
            executionKind: GitConfigurationExecutionKind.Signing),
        String("gpg.ssh.program", null, AllScopes, "SSH signing program",
            executionKind: GitConfigurationExecutionKind.Signing),

        Boolean("gui.trustmtime", "false", AllScopes, "Trust file modification timestamps"),
        Boolean("gui.textconv", "true", AllScopes, "Use approved textconv drivers"),
        Integer("gui.diffcontext", "5", AllScopes, 1, 99, "Diff context lines"),
        Definition("gui.diffopts", GitConfigurationValueKind.DiffOptions, string.Empty, AllScopes,
            description: "Additional allowlisted diff options"),
        Boolean("gui.displayuntracked", "true", AllScopes, "Show untracked files"),
        Enumeration("gui.stageuntracked", "ask", AllScopes,
            "Untracked-file staging policy", "yes", "no", "ask"),
        Integer("gui.maxfilesdisplayed", "5000", AllScopes, 1, int.MaxValue,
            "Maximum displayed changed files"),
        Integer("gui.tabsize", "8", AllScopes, 1, 99, "Diff tab width"),
        Enumeration("diff.renames", "true", AllScopes,
            "Rename detection mode", "true", "false", "copies"),
        Integer("diff.renamelimit", "1000", AllScopes, 0, int.MaxValue,
            "Rename detection candidate limit"),
        String("diff.external", null, AllScopes, "External diff command",
            executionKind: GitConfigurationExecutionKind.Diff),

        Enumeration("branch.autosetupmerge", "true", AllScopes,
            "New-branch tracking policy", "true", "false", "always", "inherit", "simple"),
        Boolean("gui.matchtrackingbranch", "false", AllScopes,
            "Match new branch names to tracking branches"),
        Boolean("merge.summary", "false", AllScopes, "Show merge summary"),
        Integer("merge.verbosity", "2", AllScopes, 0, 5, "Merge output verbosity"),
        Boolean("merge.diffstat", "true", AllScopes, "Show merge diffstat"),
        String("merge.tool", null, AllScopes, "Preferred merge tool",
            executionKind: GitConfigurationExecutionKind.Tool),
        Boolean("mergetool.keepbackup", "true", AllScopes, "Keep mergetool backup files"),
        Boolean("mergetool.keeptemporaries", "false", AllScopes, "Keep mergetool temporary files"),
        Boolean("mergetool.prompt", "true", AllScopes, "Prompt before each mergetool invocation"),
        Boolean("mergetool.writetotemp", "false", AllScopes, "Write mergetool inputs to temporary files"),
        Boolean("rerere.enabled", "false", AllScopes, "Reuse recorded conflict resolutions"),

        Enumeration("push.default", "simple", AllScopes,
            "Default push ref selection", "nothing", "current", "upstream", "simple", "matching"),
        Boolean("push.autosetupremote", "false", AllScopes, "Set upstream on default push"),
        Boolean("gui.pruneduringfetch", "false", AllScopes, "Prune during fetch"),
        String("remote.pushdefault", null, AllScopes, "Default push remote"),
        String("web.browser", null, AllScopes, "Preferred browser tool",
            executionKind: GitConfigurationExecutionKind.Browser),
        String("credential.helper", null, AllScopes, "Credential helper command",
            allowsMultipleValues: true,
            executionKind: GitConfigurationExecutionKind.CredentialHelper),
        Boolean("credential.usehttppath", "false", AllScopes,
            "Include HTTP paths in credential contexts"),

        NativePath("gui.recentrepo", null, GitConfigurationScopeMask.Global,
            "Recent repository path", allowsMultipleValues: true),
        Integer("gui.maxrecentrepo", "10", AllScopes, 0, 100,
            "Maximum recent repository count"),
        String("gui.encoding", "system", AllScopes, "Default file-content encoding"),
        Integer("gui.commitmsgwidth", "75", AllScopes, 0, 9999,
            "Commit message visual width"),
        String("gui.newbranchtemplate", string.Empty, AllScopes, "New branch name template"),
        Boolean("gui.warndetachedcommit", "true", AllScopes,
            "Warn before committing on detached HEAD"),
        String("gui.spellingdictionary", string.Empty, AllScopes, "Commit spelling dictionary"),
        Enumeration("gui.search.case", "yes", AllScopes,
            "Search case policy", "yes", "no", "smart"),
        Boolean("gui.search.regexp", "false", AllScopes, "Use regular expressions for search"),
        Boolean("gui.gcwarning", "true", AllScopes, "Warn when object cleanup is recommended"),
        Boolean("gui.autoexplore", "false", AllScopes, "Open the tree browser after repository selection"),
        Boolean("gui.fastcopyblame", "false", AllScopes, "Limit copy blame to changed files"),
        Integer("gui.copyblamethreshold", "40", AllScopes, 20, 200,
            "Minimum copy-blame character count"),
        Integer("gui.blamehistoryctx", "7", AllScopes, 0, 300,
            "Blame history context radius in days"),

        Definition("gui.usettk", GitConfigurationValueKind.Boolean, "true",
            GitConfigurationScopeMask.None, isTerminalApplicable: false,
            description: "Desktop widget toolkit selection"),
        Definition("gui.fontui", GitConfigurationValueKind.String, null,
            GitConfigurationScopeMask.None, isTerminalApplicable: false,
            description: "Desktop interface font"),
        Definition("gui.fontdiff", GitConfigurationValueKind.String, null,
            GitConfigurationScopeMask.None, isTerminalApplicable: false,
            description: "Desktop diff font"),
        Definition("gui.geometry", GitConfigurationValueKind.String, null,
            GitConfigurationScopeMask.None, isTerminalApplicable: false,
            description: "Desktop window geometry"),
        Definition("gui.wmstate", GitConfigurationValueKind.String, null,
            GitConfigurationScopeMask.None, isTerminalApplicable: false,
            description: "Desktop window-manager state"),

        Enumeration("gitsail.theme", "auto", AllScopes,
            "Terminal theme", "auto", "light", "dark", "high-contrast", "color-blind"),
        Enumeration("gitsail.colordepth", "auto", AllScopes,
            "Terminal color-depth override", "auto", "none", "16", "256", "truecolor"),
        Enumeration("gitsail.unicode", "auto", AllScopes,
            "Unicode rendering policy", "auto", "unicode", "ascii"),
        Integer("gitsail.ambiguouswidth", null, AllScopes, 1, 2,
            "Ambiguous Unicode cell width"),
        Definition("gitsail.layout", GitConfigurationValueKind.Layout, null, AllScopes,
            description: "Versioned pane and splitter layout"),
        Boolean("gitsail.restorepinnedmenus", "true", AllScopes, "Restore pinned menu windows"),
        Boolean("gitsail.showpushaction", "false", AllScopes, "Show a persistent push action"),
        Boolean("gitsail.autorescan", "true", AllScopes, "Watch and validate repository changes"),
        Boolean("gitsail.wrapcommitmessage", "false", AllScopes,
            "Visually wrap the commit message editor"),
        Enumeration("gitsail.clipboard", "auto", AllScopes,
            "Terminal clipboard policy", "off", "auto", "osc52", "helper"),
        Integer("gitsail.renamethreshold", "50", AllScopes, 0, 100,
            "Rename detection similarity percentage"),
        Enumeration("gitsail.safeforcepolicy", "explicit-lease", AllScopes,
            "Force-push safety policy", "never", "explicit-lease"),
        Enumeration("gitsail.loglevel", "information", AllScopes,
            "Structured log verbosity", "trace", "debug", "information", "warning", "error", "critical", "none"),

        Definition("color.diff.*", GitConfigurationValueKind.Color, null, AllScopes,
            description: "Diff color and attribute expression"),
        String("remote.*.url", null, RepositoryScopes, "Remote fetch URL",
            allowsMultipleValues: true,
            executionKind: GitConfigurationExecutionKind.Remote,
            mayContainSecret: true),
        String("remote.*.pushurl", null, RepositoryScopes, "Remote push URL",
            allowsMultipleValues: true,
            executionKind: GitConfigurationExecutionKind.Remote,
            mayContainSecret: true),
        String("remote.*.fetch", null, RepositoryScopes, "Remote fetch refspec", allowsMultipleValues: true),
        String("remote.*.push", null, RepositoryScopes, "Remote push refspec", allowsMultipleValues: true),
        Boolean("remote.*.prune", null, RepositoryScopes, "Remote fetch pruning override"),
        Boolean("remote.*.prunetags", null, RepositoryScopes, "Remote tag pruning override"),
        Boolean("remote.*.mirror", "false", RepositoryScopes, "Remote mirror mode"),
        Enumeration("remote.*.tagopt", null, RepositoryScopes,
            "Remote tag-fetch override", "--tags", "--no-tags"),
        String("remote.*.proxy", null, RepositoryScopes, "Remote proxy command",
            executionKind: GitConfigurationExecutionKind.Remote,
            mayContainSecret: true),
        String("remote.*.uploadpack", null, RepositoryScopes, "Remote upload-pack command",
            executionKind: GitConfigurationExecutionKind.Remote),
        String("remote.*.receivepack", null, RepositoryScopes, "Remote receive-pack command",
            executionKind: GitConfigurationExecutionKind.Remote),
        String("diff.*.command", null, AllScopes, "External diff driver command",
            executionKind: GitConfigurationExecutionKind.Diff),
        String("diff.*.textconv", null, AllScopes, "Text-conversion driver command",
            executionKind: GitConfigurationExecutionKind.Diff),
        Boolean("diff.*.cachetextconv", "false", AllScopes, "Cache text-conversion output"),
        Boolean("diff.*.binary", null, AllScopes, "Treat a diff driver as binary"),
        String("filter.*.clean", null, AllScopes, "Content clean-filter command",
            executionKind: GitConfigurationExecutionKind.Filter),
        String("filter.*.smudge", null, AllScopes, "Content smudge-filter command",
            executionKind: GitConfigurationExecutionKind.Filter),
        String("filter.*.process", null, AllScopes, "Long-running content-filter command",
            executionKind: GitConfigurationExecutionKind.Filter),
        Boolean("filter.*.required", "false", AllScopes, "Require successful content filtering"),
        NativePath("mergetool.*.path", null, AllScopes, "Merge-tool executable path",
            executionKind: GitConfigurationExecutionKind.Tool),
        String("mergetool.*.cmd", null, AllScopes, "Merge-tool shell command",
            executionKind: GitConfigurationExecutionKind.Tool),
        Boolean("mergetool.*.trustexitcode", "false", AllScopes,
            "Trust merge-tool exit status"),
        String("guitool.*.cmd", null, AllScopes, "Configured Git GUI tool command",
            executionKind: GitConfigurationExecutionKind.Tool),
        String("guitool.*.title", null, AllScopes, "Configured tool title"),
        String("guitool.*.prompt", null, AllScopes, "Configured tool confirmation prompt"),
        Boolean("guitool.*.noconsole", "false", AllScopes, "Hide configured tool output"),
        Boolean("guitool.*.needsfile", "false", AllScopes, "Require one focused file"),
        Boolean("guitool.*.confirm", "false", AllScopes, "Confirm before tool execution"),
        String("guitool.*.argprompt", null, AllScopes, "Configured tool argument prompt"),
        String("guitool.*.revprompt", null, AllScopes, "Configured tool revision prompt"),
        Boolean("guitool.*.revunmerged", "false", AllScopes,
            "Restrict the tool revision prompt to unmerged revisions"),
        Boolean("guitool.*.norescan", "false", AllScopes, "Skip rescan after tool completion"),
        String("browser.*.cmd", null, AllScopes, "Browser shell command",
            executionKind: GitConfigurationExecutionKind.Browser),
        NativePath("browser.*.path", null, AllScopes, "Browser executable path",
            executionKind: GitConfigurationExecutionKind.Browser),
        Definition("gitsail.keymap.*", GitConfigurationValueKind.ChordList, null,
            GitConfigurationScopeMask.Global, description: "Action key-chord remapping"),
        Definition("gitsail.trustedrepository.*", GitConfigurationValueKind.Capability, null,
            GitConfigurationScopeMask.Global, description: "Repository executable capability grants"),
    ];

    /// <summary>
    /// Gets every exact and dynamic registered configuration definition.
    /// </summary>
    internal static ImmutableArray<GitConfigurationDefinition> Definitions => s_definitions;

    /// <summary>
    /// Finds the exact or most-specific dynamic definition for one canonical key.
    /// </summary>
    /// <param name="key">The canonical Git configuration key.</param>
    /// <returns>The matching definition, or <see langword="null"/> for an unregistered key.</returns>
    internal static GitConfigurationDefinition? Find(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        foreach (var definition in s_definitions)
        {
            if (!definition.IsPattern && definition.Matches(key))
            {
                return definition;
            }
        }

        return s_definitions
            .Where(static definition => definition.IsPattern)
            .Where(definition => definition.Matches(key))
            .OrderByDescending(static definition => definition.KeyPattern.Length)
            .FirstOrDefault();
    }

    private static GitConfigurationDefinition Boolean(
        string key,
        string? defaultValue,
        GitConfigurationScopeMask scopes,
        string description)
        => Definition(key, GitConfigurationValueKind.Boolean, defaultValue, scopes, description: description);

    private static GitConfigurationDefinition Integer(
        string key,
        string? defaultValue,
        GitConfigurationScopeMask scopes,
        long minimum,
        long maximum,
        string description)
        => Definition(
            key,
            GitConfigurationValueKind.Integer,
            defaultValue,
            scopes,
            minimum: minimum,
            maximum: maximum,
            description: description);

    private static GitConfigurationDefinition NativePath(
        string key,
        string? defaultValue,
        GitConfigurationScopeMask scopes,
        string description,
        bool allowsMultipleValues = false,
        GitConfigurationExecutionKind executionKind = GitConfigurationExecutionKind.None)
        => Definition(
            key,
            GitConfigurationValueKind.NativePath,
            defaultValue,
            scopes,
            allowsMultipleValues: allowsMultipleValues,
            executionKind: executionKind,
            description: description);

    private static GitConfigurationDefinition Enumeration(
        string key,
        string? defaultValue,
        GitConfigurationScopeMask scopes,
        string description,
        params string[] allowedValues)
        => Definition(
            key,
            GitConfigurationValueKind.Enumeration,
            defaultValue,
            scopes,
            allowedValues: [.. allowedValues],
            description: description);

    private static GitConfigurationDefinition String(
        string key,
        string? defaultValue,
        GitConfigurationScopeMask scopes,
        string description,
        bool allowsMultipleValues = false,
        GitConfigurationExecutionKind executionKind = GitConfigurationExecutionKind.None,
        bool mayContainSecret = false)
        => Definition(
            key,
            GitConfigurationValueKind.String,
            defaultValue,
            scopes,
            allowsMultipleValues: allowsMultipleValues,
            executionKind: executionKind,
            mayContainSecret: mayContainSecret,
            description: description);

    private static GitConfigurationDefinition Definition(
        string key,
        GitConfigurationValueKind kind,
        string? defaultValue,
        GitConfigurationScopeMask scopes,
        ImmutableArray<string> allowedValues = default,
        long? minimum = null,
        long? maximum = null,
        bool allowsMultipleValues = false,
        bool isTerminalApplicable = true,
        GitConfigurationExecutionKind executionKind = GitConfigurationExecutionKind.None,
        bool mayContainSecret = false,
        string description = "")
        => new(
            key,
            kind,
            defaultValue,
            scopes,
            allowedValues.IsDefault ? [] : allowedValues,
            minimum,
            maximum,
            allowsMultipleValues,
            isTerminalApplicable,
            executionKind,
            mayContainSecret,
            description);
}
