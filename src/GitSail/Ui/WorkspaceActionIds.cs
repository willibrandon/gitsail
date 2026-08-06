using System.Collections.Immutable;
using Hex1b.Input;

namespace GitSail.Ui;

/// <summary>
/// Defines the stable identities shared by workspace bindings, menus, and commands.
/// </summary>
internal static class WorkspaceActionIds
{
    /// <summary>
    /// Identifies staging the focused or selected worktree paths.
    /// </summary>
    internal static readonly ActionId Stage = new("index.stage");

    /// <summary>
    /// Identifies staging every eligible worktree path.
    /// </summary>
    internal static readonly ActionId StageAll = new("index.stage-all");

    /// <summary>
    /// Identifies unstaging the focused or selected index paths.
    /// </summary>
    internal static readonly ActionId Unstage = new("index.unstage");

    /// <summary>
    /// Identifies unstaging every eligible index path.
    /// </summary>
    internal static readonly ActionId UnstageAll = new("index.unstage-all");

    /// <summary>
    /// Identifies decreasing the displayed diff context.
    /// </summary>
    internal static readonly ActionId LessContext = new("diff.less-context");

    /// <summary>
    /// Identifies increasing the displayed diff context.
    /// </summary>
    internal static readonly ActionId MoreContext = new("diff.more-context");

    /// <summary>
    /// Identifies refreshing the current repository workspace.
    /// </summary>
    internal static readonly ActionId Refresh = new("repository.refresh");

    /// <summary>
    /// Identifies opening context-sensitive help.
    /// </summary>
    internal static readonly ActionId Help = new("help.context");

    /// <summary>
    /// Identifies cycling between the workspace regions.
    /// </summary>
    internal static readonly ActionId CyclePanes = new("view.cycle-panes");

    /// <summary>
    /// Identifies focusing the changed-path filter.
    /// </summary>
    internal static readonly ActionId FindChangedPath = new("view.changed-path-filter");

    /// <summary>
    /// Identifies focusing text search in the active diff.
    /// </summary>
    internal static readonly ActionId FindDiffText = new("view.diff-text-search");

    /// <summary>
    /// Identifies selecting the next diff text match.
    /// </summary>
    internal static readonly ActionId NextDiffMatch = new("view.next-match");

    /// <summary>
    /// Identifies selecting the previous diff text match.
    /// </summary>
    internal static readonly ActionId PreviousDiffMatch = new("view.previous-match");

    /// <summary>
    /// Identifies opening the searchable command palette.
    /// </summary>
    internal static readonly ActionId CommandPalette = new("application.command-palette");

    /// <summary>
    /// Identifies opening the complete application menu.
    /// </summary>
    internal static readonly ActionId ApplicationMenu = new("application.menu");

    /// <summary>
    /// Identifies opening branches and linked worktrees.
    /// </summary>
    internal static readonly ActionId Branches = new("view.branches");

    /// <summary>
    /// Identifies opening stashes and exact patches.
    /// </summary>
    internal static readonly ActionId Stashes = new("view.stashes");

    /// <summary>
    /// Identifies preparing an untracked path for partial staging.
    /// </summary>
    internal static readonly ActionId PrepareUntracked = new("diff.prepare-untracked");

    /// <summary>
    /// Identifies reviewing and confirming an exact revert scope.
    /// </summary>
    internal static readonly ActionId Revert = new("diff.revert");

    /// <summary>
    /// Identifies undoing the most recent eligible revert.
    /// </summary>
    internal static readonly ActionId UndoRevert = new("diff.undo-revert");

    /// <summary>
    /// Identifies staging the hunk under the diff cursor.
    /// </summary>
    internal static readonly ActionId StageHunk = new("diff.stage-hunk");

    /// <summary>
    /// Identifies unstaging the hunk under the diff cursor.
    /// </summary>
    internal static readonly ActionId UnstageHunk = new("diff.unstage-hunk");

    /// <summary>
    /// Identifies staging or unstaging the selected diff lines.
    /// </summary>
    internal static readonly ActionId SelectedLines = new("diff.selected-lines");

    /// <summary>
    /// Identifies focusing the next diff hunk.
    /// </summary>
    internal static readonly ActionId NextHunk = new("diff.next-hunk");

    /// <summary>
    /// Identifies focusing the previous diff hunk.
    /// </summary>
    internal static readonly ActionId PreviousHunk = new("diff.previous-hunk");

    /// <summary>
    /// Identifies choosing our side of the focused conflict.
    /// </summary>
    internal static readonly ActionId UseOurs = new("merge.use-ours");

    /// <summary>
    /// Identifies choosing their side of the focused conflict.
    /// </summary>
    internal static readonly ActionId UseTheirs = new("merge.use-theirs");

    /// <summary>
    /// Identifies choosing the base side of the focused conflict.
    /// </summary>
    internal static readonly ActionId UseBase = new("merge.use-base");

    /// <summary>
    /// Identifies choosing both sides of the focused conflict.
    /// </summary>
    internal static readonly ActionId UseBoth = new("merge.use-both");

    /// <summary>
    /// Identifies focusing the next unresolved conflict.
    /// </summary>
    internal static readonly ActionId NextConflict = new("merge.next-conflict");

    /// <summary>
    /// Identifies toggling the conflict result executable mode.
    /// </summary>
    internal static readonly ActionId ToggleConflictMode = new("merge.toggle-mode");

    /// <summary>
    /// Identifies staging a complete conflict resolution.
    /// </summary>
    internal static readonly ActionId StageConflictResult = new("merge.stage-result");

    /// <summary>
    /// Identifies running the primary commit or completion action.
    /// </summary>
    internal static readonly ActionId Primary = new("commit.primary");

    /// <summary>
    /// Identifies closing the active workspace window.
    /// </summary>
    internal static readonly ActionId CloseWindow = new("application.close-window");

    /// <summary>
    /// Identifies quitting the current GitSail session.
    /// </summary>
    internal static readonly ActionId Quit = new("application.quit");

    private static readonly ImmutableArray<ActionId> s_all =
    [
        Stage,
        StageAll,
        Unstage,
        UnstageAll,
        LessContext,
        MoreContext,
        Refresh,
        Help,
        CyclePanes,
        FindChangedPath,
        FindDiffText,
        NextDiffMatch,
        PreviousDiffMatch,
        CommandPalette,
        ApplicationMenu,
        Branches,
        Stashes,
        PrepareUntracked,
        Revert,
        UndoRevert,
        StageHunk,
        UnstageHunk,
        SelectedLines,
        NextHunk,
        PreviousHunk,
        UseOurs,
        UseTheirs,
        UseBase,
        UseBoth,
        NextConflict,
        ToggleConflictMode,
        StageConflictResult,
        Primary,
        CloseWindow,
        Quit,
    ];

    /// <summary>
    /// Gets every stable workspace action identity for generated validation and discovery.
    /// </summary>
    internal static ImmutableArray<ActionId> All => s_all;

    /// <summary>
    /// Determines whether an identity belongs to GitSail's configurable workspace actions.
    /// </summary>
    /// <param name="actionId">The identity to inspect.</param>
    /// <returns><see langword="true"/> when the action is part of the registered workspace keymap.</returns>
    internal static bool IsKnown(ActionId actionId)
        => s_all.Contains(actionId);
}
