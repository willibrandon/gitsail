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
    /// Gets the current or most recent repository activity description.
    /// </summary>
    internal string Activity { get; }

    /// <summary>
    /// Gets whether one repository operation is currently active.
    /// </summary>
    internal bool IsBusy { get; }

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
    /// Unstages checked index paths or the focused fallback path.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    internal Task UnstageAsync(CancellationToken cancellationToken);
}
