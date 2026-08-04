using GitSail.Git.Execution;

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
    /// Gets whether the current repository state can start a commit transaction.
    /// </summary>
    internal bool CanCommit { get; }

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
    /// Commits the current index through the Git-owned porcelain transaction.
    /// </summary>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    internal Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Commits through Git after a separate confirmation requested bypass of its bypassable hooks.
    /// </summary>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    internal Task CommitWithoutHooksAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Completes citool without creating a commit after validating the index has no unmerged entries.
    /// </summary>
    /// <param name="cancellationToken">Signals completion cancellation.</param>
    /// <returns>A completed task after validation and state publication.</returns>
    internal Task CompleteWithoutCommitAsync(CancellationToken cancellationToken);
}
