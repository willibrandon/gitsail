using GitSail.Domain;
using GitSail.Git.Execution;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Supplies controlled repository state and asynchronous actions to the workspace view.
/// </summary>
internal interface IRepositoryWorkspaceSession
{
    /// <summary>
    /// Notifies the view that controlled workspace state has changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the resolved Git installation used by the open repository.
    /// </summary>
    internal GitInstallation Installation { get; }

    /// <summary>
    /// Gets controlled file-pane focus, selection, and status state.
    /// </summary>
    internal StatusWorkspaceState State { get; }

    /// <summary>
    /// Gets controlled searchable branch-window catalog, filter, and focus state.
    /// </summary>
    internal BranchWorkspaceState Branches { get; }

    /// <summary>
    /// Gets controlled searchable worktree-window catalog, filter, and focus state.
    /// </summary>
    internal WorktreeWorkspaceState Worktrees { get; }

    /// <summary>
    /// Gets controlled searchable remote-window catalog, filter, and focus state.
    /// </summary>
    internal RemoteWorkspaceState Remotes { get; }

    /// <summary>
    /// Gets controlled searchable stash-window catalog, preview, filter, and focus state.
    /// </summary>
    internal StashWorkspaceState Stashes { get; }

    /// <summary>
    /// Gets separate read-only standard-output and standard-error transport presentations.
    /// </summary>
    internal TransportOutputState TransportOutput { get; }

    /// <summary>
    /// Gets the serialized nonpersistent credential prompt state for transport operations.
    /// </summary>
    internal CredentialPromptCoordinator CredentialPrompts { get; }

    /// <summary>
    /// Gets the current read-only diff editor presentation for the focused path.
    /// </summary>
    internal DiffViewState Diff { get; }

    /// <summary>
    /// Gets the persistent writable commit-message editor state.
    /// </summary>
    internal CommitMessageState CommitMessage { get; }

    /// <summary>
    /// Gets the lifted options used to construct the next Git-owned commit transaction.
    /// </summary>
    internal CommitOptionsState CommitOptions { get; }

    /// <summary>
    /// Gets the current local remote-tracking warning for amending HEAD, when one applies.
    /// </summary>
    internal PublishedAmendWarning? PublishedAmendWarning { get; }

    /// <summary>
    /// Gets the exact detached HEAD warning required by the current Git configuration.
    /// </summary>
    internal DetachedHeadWarning? DetachedHeadWarning { get; }

    /// <summary>
    /// Gets the exact active merge state requiring confirmation before Git-owned abort.
    /// </summary>
    internal MergeAbortWarning? MergeAbortWarning { get; }

    /// <summary>
    /// Gets the canonical worktree requested for opening after this view closes.
    /// </summary>
    internal CanonicalDirectory? RequestedOpenDirectory { get; }

    /// <summary>
    /// Gets the current or most recent repository activity description.
    /// </summary>
    internal string Activity { get; }

    /// <summary>
    /// Gets whether one repository operation is currently active.
    /// </summary>
    internal bool IsBusy { get; }

    /// <summary>
    /// Gets whether the current worktree diff cursor identifies an exact applicable hunk.
    /// </summary>
    internal bool CanStageFocusedHunk { get; }

    /// <summary>
    /// Gets whether the current index diff cursor identifies an exact applicable hunk.
    /// </summary>
    internal bool CanUnstageFocusedHunk { get; }

    /// <summary>
    /// Gets whether the current worktree diff cursor set selects applicable changed lines.
    /// </summary>
    internal bool CanStageSelectedLines { get; }

    /// <summary>
    /// Gets whether the current index diff cursor set selects applicable changed lines.
    /// </summary>
    internal bool CanUnstageSelectedLines { get; }

    /// <summary>
    /// Gets whether the current worktree patch can be reverted as a complete file.
    /// </summary>
    internal bool CanRevertFocusedFile { get; }

    /// <summary>
    /// Gets whether the current worktree diff cursor identifies an exact revertible hunk.
    /// </summary>
    internal bool CanRevertFocusedHunk { get; }

    /// <summary>
    /// Gets whether the current worktree cursor set selects exact revertible changed lines.
    /// </summary>
    internal bool CanRevertSelectedLines { get; }

    /// <summary>
    /// Gets whether the most recent successful revert remains eligible for one-level undo.
    /// </summary>
    internal bool CanUndoRevert { get; }

    /// <summary>
    /// Gets whether the focused untracked path can be prepared for exact hunk and line staging.
    /// </summary>
    internal bool CanPrepareUntrackedPatch { get; }

    /// <summary>
    /// Gets whether the current repository state can start a commit transaction.
    /// </summary>
    internal bool CanCommit { get; }

    /// <summary>
    /// Gets whether an exact in-progress merge is currently available to abort.
    /// </summary>
    internal bool CanAbortMerge { get; }

    /// <summary>
    /// Gets whether an unchanged configured commit template currently prevents commit.
    /// </summary>
    internal bool NeedsCommitTemplateEdit { get; }

    /// <summary>
    /// Gets whether the requested single-transaction workflow completed successfully.
    /// </summary>
    internal bool IsCitoolCompleted { get; }

    /// <summary>
    /// Gets whether no unresolved index entries prevent successful no-commit completion.
    /// </summary>
    internal bool CanCompleteWithoutCommit { get; }

    /// <summary>
    /// Gets the explicit unchanged-line count surrounding diff changes.
    /// </summary>
    internal int DiffContextLines { get; }

    /// <summary>
    /// Gets whether the diff pane currently owns an editable, generation-matched conflict result.
    /// </summary>
    internal bool IsConflictResolutionActive { get; }

    /// <summary>
    /// Gets whether the result-editor cursor is inside an unresolved conflict marker block.
    /// </summary>
    internal bool CanChooseFocusedConflictChunk { get; }

    /// <summary>
    /// Gets whether the marker-free conflict result can be staged through verified index rollback.
    /// </summary>
    internal bool CanStageConflictResolution { get; }

    /// <summary>
    /// Gets whether the active blob-backed conflict may toggle its staged executable bit.
    /// </summary>
    internal bool CanToggleConflictExecutable { get; }

    /// <summary>
    /// Gets whether the active conflict result will be staged as an executable regular file.
    /// </summary>
    internal bool ConflictResultIsExecutable { get; }

    /// <summary>
    /// Gets the number of original conflict chunks whose generated markers have been removed.
    /// </summary>
    internal int ResolvedConflictChunkCount { get; }

    /// <summary>
    /// Gets the number of original conflict chunks in the active editable merge result.
    /// </summary>
    internal int ConflictChunkCount { get; }

    /// <summary>
    /// Focuses one worktree row and loads its generation-matched raw patch presentation.
    /// </summary>
    /// <param name="index">The absolute worktree row index.</param>
    /// <param name="cancellationToken">Signals patch loading cancellation.</param>
    /// <returns>A task that completes after the read-only editor presentation is current.</returns>
    internal Task FocusUnstagedAsync(int index, CancellationToken cancellationToken);

    /// <summary>
    /// Focuses one index row and loads its generation-matched raw patch presentation.
    /// </summary>
    /// <param name="index">The absolute index row index.</param>
    /// <param name="cancellationToken">Signals patch loading cancellation.</param>
    /// <returns>A task that completes after the read-only editor presentation is current.</returns>
    internal Task FocusStagedAsync(int index, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the unresolved marker block under the result-editor cursor with one exact side choice.
    /// </summary>
    /// <param name="choice">The exact base, ours, theirs, or both content choice.</param>
    /// <returns>A completed task after editor replacement, next-conflict focus, and invalidation.</returns>
    internal Task ChooseFocusedConflictChunkAsync(ConflictResolutionChoice choice);

    /// <summary>
    /// Moves the editable result cursor to the next unresolved generated conflict marker block.
    /// </summary>
    /// <returns>A completed task after cursor movement and invalidation.</returns>
    internal Task FocusNextUnresolvedConflictAsync();

    /// <summary>
    /// Toggles the regular-file executable bit selected for the active conflict result.
    /// </summary>
    /// <returns>A completed task after result-mode mutation and invalidation.</returns>
    internal Task ToggleConflictExecutableAsync();

    /// <summary>
    /// Stages the marker-free editable conflict result after exact live-stage validation.
    /// </summary>
    /// <param name="cancellationToken">Signals conflict staging cancellation.</param>
    /// <returns>A task that completes after rollback-capable mutation and reconciliation.</returns>
    internal Task StageConflictResolutionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stages the complete raw hunk under the diff editor cursor after Git preflight validation.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task StageFocusedHunkAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Unstages the complete raw hunk under the diff editor cursor after Git preflight validation.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task UnstageFocusedHunkAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stages every exact changed line selected by the worktree diff editor cursor set.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task StageSelectedLinesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Unstages every exact changed line selected by the index diff editor cursor set.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task UnstageSelectedLinesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reverts the complete focused worktree file after the view obtains destructive confirmation.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task RevertFocusedFileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reverts the focused worktree hunk after the view obtains destructive confirmation.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task RevertFocusedHunkAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reverts selected worktree changed lines after the view obtains destructive confirmation.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task RevertSelectedLinesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reapplies the most recent exact reverted patch while its preconditions still match.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after undo and reconciliation.</returns>
    internal Task UndoRevertAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records intent-to-add for the focused untracked path and loads its exact unstaged patch.
    /// </summary>
    /// <param name="cancellationToken">Signals index mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task PrepareFocusedUntrackedPatchAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Moves the read-only diff cursor to the next exact hunk header.
    /// </summary>
    /// <returns>A completed task after cursor movement and view invalidation.</returns>
    internal Task FocusNextHunkAsync();

    /// <summary>
    /// Moves the read-only diff cursor to the preceding or containing exact hunk header.
    /// </summary>
    /// <returns>A completed task after cursor movement and view invalidation.</returns>
    internal Task FocusPreviousHunkAsync();

    /// <summary>
    /// Decreases diff context and publishes a newly captured repository generation.
    /// </summary>
    /// <param name="cancellationToken">Signals patch recapture cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    internal Task DecreaseDiffContextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Increases diff context and publishes a newly captured repository generation.
    /// </summary>
    /// <param name="cancellationToken">Signals patch recapture cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    internal Task IncreaseDiffContextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Refreshes the complete repository status snapshot.
    /// </summary>
    /// <param name="cancellationToken">Signals refresh cancellation.</param>
    /// <returns>A task that completes after reconciliation.</returns>
    internal Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads one stable exact branch and linked-worktree catalog for the branch window.
    /// </summary>
    /// <param name="cancellationToken">Signals catalog capture cancellation.</param>
    /// <returns>A task that completes after controlled branch state is current.</returns>
    internal Task LoadBranchesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads one stable exact linked-worktree catalog for the worktree window.
    /// </summary>
    /// <param name="cancellationToken">Signals catalog capture cancellation.</param>
    /// <returns>A task that completes after controlled worktree state is current.</returns>
    internal Task LoadWorktreesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Requests opening one exact existing worktree after the current view closes.
    /// </summary>
    /// <param name="worktree">The exact displayed worktree.</param>
    /// <returns>A completed task after path and catalog validation.</returns>
    internal Task OpenWorktreeAsync(WorktreeInfo worktree);

    /// <summary>
    /// Creates a linked worktree from one exact displayed branch or object.
    /// </summary>
    /// <param name="source">The exact displayed starting branch and object.</param>
    /// <param name="targetDirectory">The absolute or current-worktree-relative target directory.</param>
    /// <param name="mode">How the new worktree obtains its HEAD.</param>
    /// <param name="newBranchName">The user-entered branch name required by new-branch mode.</param>
    /// <param name="trackSource">Whether a new branch directly tracks its remote source.</param>
    /// <param name="lockAfterCreation">Whether Git atomically locks the new worktree.</param>
    /// <param name="lockReason">The optional literal lock reason.</param>
    /// <param name="openAfterCreation">Whether the new worktree opens after successful creation.</param>
    /// <param name="cancellationToken">Signals creation cancellation.</param>
    /// <returns>A task that completes after Git-owned creation and reconciliation.</returns>
    internal Task AddWorktreeAsync(
        BranchInfo source,
        string targetDirectory,
        WorktreeAddMode mode,
        string? newBranchName,
        bool trackSource,
        bool lockAfterCreation,
        string? lockReason,
        bool openAfterCreation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves one exact linked worktree to a new canonical target.
    /// </summary>
    /// <param name="worktree">The exact displayed linked worktree.</param>
    /// <param name="targetDirectory">The absolute or current-worktree-relative new location.</param>
    /// <param name="cancellationToken">Signals movement cancellation.</param>
    /// <returns>A task that completes after Git-owned movement and reconciliation.</returns>
    internal Task MoveWorktreeAsync(
        WorktreeInfo worktree,
        string targetDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Locks one exact linked worktree with an optional literal reason.
    /// </summary>
    /// <param name="worktree">The exact displayed linked worktree.</param>
    /// <param name="reason">The optional literal lock reason.</param>
    /// <param name="cancellationToken">Signals lock cancellation.</param>
    /// <returns>A task that completes after Git-owned locking and reconciliation.</returns>
    internal Task LockWorktreeAsync(
        WorktreeInfo worktree,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Unlocks one exact linked worktree after catalog revalidation.
    /// </summary>
    /// <param name="worktree">The exact displayed linked worktree.</param>
    /// <param name="cancellationToken">Signals unlock cancellation.</param>
    /// <returns>A task that completes after Git-owned unlocking and reconciliation.</returns>
    internal Task UnlockWorktreeAsync(
        WorktreeInfo worktree,
        CancellationToken cancellationToken);

    /// <summary>
    /// Captures exact status and submodule data for linked-worktree removal confirmation.
    /// </summary>
    /// <param name="worktree">The exact displayed linked worktree.</param>
    /// <param name="cancellationToken">Signals removal inspection cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    internal Task<WorktreeRemovalPlan?> PrepareWorktreeRemovalAsync(
        WorktreeInfo worktree,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes one exact linked worktree after the displayed plan is confirmed.
    /// </summary>
    /// <param name="plan">The exact reviewed worktree status and submodule plan.</param>
    /// <param name="force">Whether deletion of retained worktree content was explicitly confirmed.</param>
    /// <param name="cancellationToken">Signals removal cancellation.</param>
    /// <returns>A task that completes after Git-owned removal and reconciliation.</returns>
    internal Task RemoveWorktreeAsync(
        WorktreeRemovalPlan plan,
        bool force,
        CancellationToken cancellationToken);

    /// <summary>
    /// Captures Git's exact dry-run list of stale linked-worktree records.
    /// </summary>
    /// <param name="cancellationToken">Signals prune preview cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    internal Task<WorktreePrunePlan?> PrepareWorktreePruneAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Prunes only the stale worktree records in the confirmed unchanged dry-run output.
    /// </summary>
    /// <param name="plan">The exact dry-run output reviewed by the user.</param>
    /// <param name="cancellationToken">Signals prune cancellation.</param>
    /// <returns>A task that completes after Git-owned pruning and reconciliation.</returns>
    internal Task PruneWorktreesAsync(
        WorktreePrunePlan plan,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks Git to repair one existing worktree path selected by the user.
    /// </summary>
    /// <param name="path">The absolute or current-worktree-relative existing directory.</param>
    /// <param name="cancellationToken">Signals repair cancellation.</param>
    /// <returns>A task that completes after Git-owned repair and reconciliation.</returns>
    internal Task RepairWorktreeAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Switches to an exact local branch selected from the displayed catalog.
    /// </summary>
    /// <param name="branch">The exact displayed local branch.</param>
    /// <param name="cancellationToken">Signals checkout cancellation.</param>
    /// <returns>A task that completes after Git-owned checkout and reconciliation.</returns>
    internal Task SwitchBranchAsync(BranchInfo branch, CancellationToken cancellationToken);

    /// <summary>
    /// Creates and switches to a local branch from an exact displayed source branch.
    /// </summary>
    /// <param name="source">The exact displayed source branch.</param>
    /// <param name="name">The user-entered local branch name validated by Git.</param>
    /// <param name="trackSource">Whether a remote source becomes the explicit direct upstream.</param>
    /// <param name="cancellationToken">Signals creation and checkout cancellation.</param>
    /// <returns>A task that completes after Git-owned creation and reconciliation.</returns>
    internal Task CreateAndSwitchBranchAsync(
        BranchInfo source,
        string name,
        bool trackSource,
        CancellationToken cancellationToken);

    /// <summary>
    /// Detaches HEAD at the exact target of a displayed source branch.
    /// </summary>
    /// <param name="source">The exact displayed source branch.</param>
    /// <param name="cancellationToken">Signals detached checkout cancellation.</param>
    /// <returns>A task that completes after Git-owned checkout and reconciliation.</returns>
    internal Task DetachBranchAsync(BranchInfo source, CancellationToken cancellationToken);

    /// <summary>
    /// Renames an exact displayed local branch to a Git-validated user-entered name.
    /// </summary>
    /// <param name="branch">The exact displayed local branch.</param>
    /// <param name="newName">The user-entered destination local branch name.</param>
    /// <param name="cancellationToken">Signals rename cancellation.</param>
    /// <returns>A task that completes after Git-owned rename and reconciliation.</returns>
    internal Task RenameBranchAsync(
        BranchInfo branch,
        string newName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an exact displayed unoccupied local branch with the selected mergedness policy.
    /// </summary>
    /// <param name="branch">The exact displayed local branch.</param>
    /// <param name="mode">The safe or explicitly confirmed force policy.</param>
    /// <param name="cancellationToken">Signals deletion cancellation.</param>
    /// <returns>A task that completes after Git-owned deletion and reconciliation.</returns>
    internal Task DeleteBranchAsync(
        BranchInfo branch,
        BranchDeleteMode mode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a typed revision and resets the exact current branch with the confirmed mode.
    /// </summary>
    /// <param name="branch">The exact displayed current local branch.</param>
    /// <param name="revision">The untrusted user-entered revision expression.</param>
    /// <param name="mode">The confirmed soft, mixed, or hard reset mode.</param>
    /// <param name="cancellationToken">Signals resolution and reset cancellation.</param>
    /// <returns>A task that completes after Git-owned reset and reconciliation.</returns>
    internal Task ResetCurrentBranchAsync(
        BranchInfo branch,
        string revision,
        BranchResetMode mode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prepares an exact selected-branch merge confirmation without mutating repository state.
    /// </summary>
    /// <param name="source">The exact displayed source branch.</param>
    /// <param name="cancellationToken">Signals merge-plan capture cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    internal Task<MergePlan?> PrepareMergeAsync(
        BranchInfo source,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes one exact confirmed merge with validated typed options.
    /// </summary>
    /// <param name="plan">The exact merge confirmation displayed to the user.</param>
    /// <param name="options">The validated typed merge options.</param>
    /// <param name="cancellationToken">Signals merge execution cancellation.</param>
    /// <returns>A task that completes after Git-owned merge and reconciliation.</returns>
    internal Task MergeAsync(
        MergePlan plan,
        MergeOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads one stable exact configured remote catalog for the remote workspace.
    /// </summary>
    /// <param name="cancellationToken">Signals catalog capture cancellation.</param>
    /// <returns>A task that completes after controlled remote state is current.</returns>
    internal Task LoadRemotesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Focuses one visible exact remote row.
    /// </summary>
    /// <param name="index">The absolute filtered remote row index.</param>
    /// <returns>A completed task after controlled focus publication.</returns>
    internal Task FocusRemoteAsync(int index);

    /// <summary>
    /// Adds one Git-validated remote name and exact user-entered URL.
    /// </summary>
    /// <param name="name">The user-entered remote name.</param>
    /// <param name="url">The user-entered remote URL.</param>
    /// <param name="cancellationToken">Signals remote-add cancellation.</param>
    /// <returns>A task that completes after Git-owned addition and reconciliation.</returns>
    internal Task AddRemoteAsync(string name, string url, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one exact displayed remote after cancel-first user confirmation.
    /// </summary>
    /// <param name="remote">The exact displayed remote to remove.</param>
    /// <param name="cancellationToken">Signals remote-removal cancellation.</param>
    /// <returns>A task that completes after Git-owned removal and reconciliation.</returns>
    internal Task RemoveRemoteAsync(RemoteInfo remote, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches one exact displayed remote with validated typed options.
    /// </summary>
    /// <param name="remote">The exact displayed remote to fetch.</param>
    /// <param name="options">The validated typed fetch options.</param>
    /// <param name="cancellationToken">Signals fetch cancellation.</param>
    /// <returns>A task that completes after Git-owned transport and reconciliation.</returns>
    internal Task FetchRemoteAsync(
        RemoteInfo remote,
        FetchOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches every exact displayed configured remote with validated typed options.
    /// </summary>
    /// <param name="options">The validated typed fetch options.</param>
    /// <param name="cancellationToken">Signals fetch-all cancellation.</param>
    /// <returns>A task that completes after Git-owned transport and reconciliation.</returns>
    internal Task FetchAllRemotesAsync(FetchOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Prepares exact Git dry-run output for one selected remote prune confirmation.
    /// </summary>
    /// <param name="remote">The exact displayed remote to preview.</param>
    /// <param name="cancellationToken">Signals prune-preview cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    internal Task<RemotePrunePlan?> PreparePruneRemoteAsync(
        RemoteInfo remote,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prunes one exact confirmed remote against its displayed dry-run plan.
    /// </summary>
    /// <param name="plan">The exact prune confirmation displayed to the user.</param>
    /// <param name="cancellationToken">Signals prune cancellation.</param>
    /// <returns>A task that completes after Git-owned pruning and reconciliation.</returns>
    internal Task PruneRemoteAsync(RemotePrunePlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves one configured push URL into an exact local or SSH initialization plan.
    /// </summary>
    /// <param name="remote">The exact displayed configured remote.</param>
    /// <param name="configuredUrlIndex">The selected configured push-URL index.</param>
    /// <param name="cancellationToken">Signals initialization planning cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    internal Task<RemoteInitializationPlan?> PrepareRemoteInitializationAsync(
        RemoteInfo remote,
        int configuredUrlIndex,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates one exact confirmed local or SSH bare repository without changing the current repository.
    /// </summary>
    /// <param name="plan">The exact initialization plan displayed to the user.</param>
    /// <param name="cancellationToken">Signals initialization cancellation.</param>
    /// <returns>A task that completes after exact target creation and verification.</returns>
    internal Task InitializeRemoteAsync(
        RemoteInitializationPlan plan,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prepares one exact Git-resolved default push confirmation for the selected remote.
    /// </summary>
    /// <param name="remote">The exact displayed destination remote.</param>
    /// <param name="followTags">The configured or explicit reachable annotated-tag behavior.</param>
    /// <param name="cancellationToken">Signals push planning cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    internal Task<PushPlan?> PreparePushAsync(
        RemoteInfo remote,
        GitOptionOverride followTags,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes one exact confirmed push with validated typed safety and upstream choices.
    /// </summary>
    /// <param name="plan">The exact push plan displayed to the user.</param>
    /// <param name="options">The validated typed push options.</param>
    /// <param name="cancellationToken">Signals push cancellation.</param>
    /// <returns>A task that completes after Git-owned push and reconciliation.</returns>
    internal Task PushAsync(
        PushPlan plan,
        PushOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads one stable complete list of exact local tag refs for tag-push selection.
    /// </summary>
    /// <param name="cancellationToken">Signals local-tag loading cancellation.</param>
    /// <returns>Every exact local tag ref in bytewise order.</returns>
    internal Task<ImmutableArray<RefName>> LoadLocalTagsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads the stable union of exact branch refs advertised by a selected remote's push URLs.
    /// </summary>
    /// <param name="remote">The exact displayed destination remote.</param>
    /// <param name="cancellationToken">Signals remote-branch loading cancellation.</param>
    /// <returns>Every exact advertised remote branch ref in bytewise order.</returns>
    internal Task<ImmutableArray<RefName>> LoadRemoteBranchesAsync(
        RemoteInfo remote,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prepares one exact selected local tag update for the displayed remote.
    /// </summary>
    /// <param name="remote">The exact displayed destination remote.</param>
    /// <param name="tag">The exact fully qualified local tag ref.</param>
    /// <param name="cancellationToken">Signals tag-push planning cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    internal Task<PushPlan?> PrepareTagPushAsync(
        RemoteInfo remote,
        RefName tag,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prepares one exact selected advertised remote branch deletion.
    /// </summary>
    /// <param name="remote">The exact displayed destination remote.</param>
    /// <param name="branch">The exact fully qualified advertised branch ref.</param>
    /// <param name="cancellationToken">Signals deletion planning cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    internal Task<PushPlan?> PrepareRemoteBranchDeletionAsync(
        RemoteInfo remote,
        RefName branch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads one stable exact stash catalog and the focused entry's patch preview.
    /// </summary>
    /// <param name="cancellationToken">Signals catalog and preview capture cancellation.</param>
    /// <returns>A task that completes after controlled stash state is current.</returns>
    internal Task LoadStashesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies a filter and loads the newly focused exact stash patch when needed.
    /// </summary>
    /// <param name="filter">The latest user-entered incremental filter text.</param>
    /// <param name="cancellationToken">Signals patch preview capture cancellation.</param>
    /// <returns>A task that completes after filter, focus, and preview state are current.</returns>
    internal Task FilterStashesAsync(string filter, CancellationToken cancellationToken);

    /// <summary>
    /// Focuses one visible stash row and loads its exact patch preview.
    /// </summary>
    /// <param name="index">The absolute filtered stash row index.</param>
    /// <param name="cancellationToken">Signals patch preview capture cancellation.</param>
    /// <returns>A task that completes after focus and preview state are current.</returns>
    internal Task FocusStashAsync(int index, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a stash from the current displayed repository generation and typed options.
    /// </summary>
    /// <param name="options">The validated noninteractive stash-create options.</param>
    /// <param name="cancellationToken">Signals stash creation cancellation.</param>
    /// <returns>A task that completes after Git-owned creation and reconciliation.</returns>
    internal Task CreateStashAsync(StashCreateOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Applies one exact displayed stash without removing it from the reflog.
    /// </summary>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="restoreIndex">Whether Git should also restore its index state.</param>
    /// <param name="cancellationToken">Signals stash application cancellation.</param>
    /// <returns>A task that completes after Git-owned application and reconciliation.</returns>
    internal Task ApplyStashAsync(
        StashInfo stash,
        bool restoreIndex,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pops one exact displayed stash after a cancel-first user confirmation.
    /// </summary>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="restoreIndex">Whether Git should also restore its index state.</param>
    /// <param name="cancellationToken">Signals stash pop cancellation.</param>
    /// <returns>A task that completes after Git-owned pop and reconciliation.</returns>
    internal Task PopStashAsync(
        StashInfo stash,
        bool restoreIndex,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drops one exact displayed stash after a cancel-first user confirmation.
    /// </summary>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="cancellationToken">Signals stash deletion cancellation.</param>
    /// <returns>A task that completes after Git-owned deletion and reconciliation.</returns>
    internal Task DropStashAsync(StashInfo stash, CancellationToken cancellationToken);

    /// <summary>
    /// Stages checked worktree paths or the focused fallback path.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task StageAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stages every worktree change through one serialized repository mutation.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task StageAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Unstages checked index paths or the focused fallback path.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task UnstageAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Unstages every index entry through one serialized repository mutation.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task UnstageAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Toggles amend mode and refreshes its local remote-tracking publication warning when enabling it.
    /// </summary>
    /// <param name="cancellationToken">Signals amend-safety inspection cancellation.</param>
    /// <returns>A task that completes after the lifted option and warning are current.</returns>
    internal Task ToggleAmendAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Aborts the exact merge state displayed and confirmed by the view through Git porcelain.
    /// </summary>
    /// <param name="confirmedWarning">The exact merge warning displayed by the confirmation dialog.</param>
    /// <param name="cancellationToken">Signals abort cancellation.</param>
    /// <returns>A task that completes after Git-owned abort and repository reconciliation.</returns>
    internal Task AbortMergeAsync(
        MergeAbortWarning confirmedWarning,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits the current index through the Git-owned porcelain transaction.
    /// </summary>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    internal Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Commits after the view explicitly confirms every current detached or publication warning.
    /// </summary>
    /// <param name="confirmedPublishedAmendWarning">The exact publication warning displayed by the view.</param>
    /// <param name="confirmedDetachedHeadWarning">The exact detached HEAD warning displayed by the view.</param>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    internal Task CommitAfterWarningsAsync(
        PublishedAmendWarning? confirmedPublishedAmendWarning,
        DetachedHeadWarning? confirmedDetachedHeadWarning,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits through Git after a separate confirmation requested bypass of its bypassable hooks.
    /// </summary>
    /// <param name="confirmedPublishedAmendWarning">The exact publication warning displayed with the bypass warning.</param>
    /// <param name="confirmedDetachedHeadWarning">The exact detached HEAD warning displayed with the bypass warning.</param>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    internal Task CommitWithoutHooksAsync(
        PublishedAmendWarning? confirmedPublishedAmendWarning,
        DetachedHeadWarning? confirmedDetachedHeadWarning,
        CancellationToken cancellationToken);

    /// <summary>
    /// Completes citool without creating a commit after validating the index has no unmerged entries.
    /// </summary>
    /// <param name="cancellationToken">Signals completion cancellation.</param>
    /// <returns>A completed task after validation and state publication.</returns>
    internal Task CompleteWithoutCommitAsync(CancellationToken cancellationToken);
}
