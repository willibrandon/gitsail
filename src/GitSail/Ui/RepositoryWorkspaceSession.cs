using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using Hex1b.Documents;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Coordinates asynchronous status reads and serialized index mutations for one open repository.
/// </summary>
internal sealed class RepositoryWorkspaceSession : IRepositoryWorkspaceSession, IAsyncDisposable
{
    private const int MaximumPresentedPatchBytes = 4 * 1024 * 1024;
    private const int MaximumCommitDraftBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly CanonicalDirectory _workingDirectory;
    private readonly RepositoryLocation _repository;
    private readonly RepositoryStatusService _statusService;
    private readonly IndexMutationService _indexMutationService;
    private readonly RepositoryPatchService _patchService;
    private readonly CommitService _commitService;
    private readonly CommitDraftStore _commitDraftStore;
    private readonly RawDiffService _rawDiffService;
    private readonly RepositoryMutationCoordinator _mutationCoordinator;
    private RawDiffDocument? _workTreeDiff;
    private RawDiffDocument? _indexDiff;
    private RawDiffFile? _focusedPatchFile;
    private RawDiffTarget? _focusedPatchTarget;
    private OperationGeneration _focusedPatchGeneration;
    private OperationGeneration _generation;
    private int _diffContextLines = 3;
    private int _operationInProgress;
    private string? _completionActivityOverride;
    private RevertUndoState? _revertUndoState;

    private RepositoryWorkspaceSession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        RepositoryStatusService statusService,
        IndexMutationService indexMutationService,
        RepositoryPatchService patchService,
        CommitService commitService,
        CommitDraftStore commitDraftStore,
        RawDiffService rawDiffService,
        RepositoryMutationCoordinator mutationCoordinator,
        RepositoryStatusSnapshot snapshot,
        string commitDraft,
        bool amend)
    {
        _workingDirectory = workingDirectory;
        _repository = repository;
        _statusService = statusService;
        _indexMutationService = indexMutationService;
        _patchService = patchService;
        _commitService = commitService;
        _commitDraftStore = commitDraftStore;
        _rawDiffService = rawDiffService;
        _mutationCoordinator = mutationCoordinator;
        _generation = snapshot.Generation;
        Installation = installation;
        State = new StatusWorkspaceState(snapshot);
        Diff = new DiffViewState();
        CommitMessage = new CommitMessageState(commitDraft);
        CommitOptions = new CommitOptionsState(amend);
        CommitMessage.Changed += HandleCommitMessageChanged;
        _commitDraftStore.PersistenceFailed += HandleCommitDraftPersistenceFailed;
        Activity = string.IsNullOrEmpty(commitDraft) ? "Ready" : "Recovered commit draft";
    }

    /// <summary>
    /// Notifies the shell that controlled workspace state has changed and should be rendered.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the resolved Git installation used for this repository session.
    /// </summary>
    public GitInstallation Installation { get; }

    /// <summary>
    /// Gets the controlled status-pane and selection state.
    /// </summary>
    public StatusWorkspaceState State { get; }

    /// <summary>
    /// Gets the current generation-matched read-only diff editor presentation.
    /// </summary>
    public DiffViewState Diff { get; }

    /// <summary>
    /// Gets the persistent writable commit-message editor state.
    /// </summary>
    public CommitMessageState CommitMessage { get; }

    /// <summary>
    /// Gets the lifted options used to construct the next commit transaction.
    /// </summary>
    public CommitOptionsState CommitOptions { get; }

    /// <summary>
    /// Gets a short, control-safe description of the current or most recent operation.
    /// </summary>
    public string Activity { get; private set; }

    /// <summary>
    /// Gets whether an asynchronous refresh or mutation is currently active.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _operationInProgress) != 0;

    /// <summary>
    /// Gets whether the current worktree diff cursor identifies an exact applicable hunk.
    /// </summary>
    public bool CanStageFocusedHunk => !IsBusy && GetFocusedHunk(RawDiffTarget.WorkTree) is not null;

    /// <summary>
    /// Gets whether the current index diff cursor identifies an exact applicable hunk.
    /// </summary>
    public bool CanUnstageFocusedHunk => !IsBusy && GetFocusedHunk(RawDiffTarget.Index) is not null;

    /// <summary>
    /// Gets whether the current worktree diff cursor set selects applicable changed lines.
    /// </summary>
    public bool CanStageSelectedLines => !IsBusy && HasSelectedChangedLines(RawDiffTarget.WorkTree);

    /// <summary>
    /// Gets whether the current index diff cursor set selects applicable changed lines.
    /// </summary>
    public bool CanUnstageSelectedLines => !IsBusy && HasSelectedChangedLines(RawDiffTarget.Index);

    /// <summary>
    /// Gets whether the current worktree patch can be reverted as a complete file.
    /// </summary>
    public bool CanRevertFocusedFile => !IsBusy && HasCurrentFocusedPatch(RawDiffTarget.WorkTree);

    /// <summary>
    /// Gets whether the current worktree diff cursor identifies an exact revertible hunk.
    /// </summary>
    public bool CanRevertFocusedHunk => CanStageFocusedHunk;

    /// <summary>
    /// Gets whether the current worktree cursor set selects exact revertible changed lines.
    /// </summary>
    public bool CanRevertSelectedLines => CanStageSelectedLines;

    /// <summary>
    /// Gets whether the most recent successful revert remains eligible for one-level undo.
    /// </summary>
    public bool CanUndoRevert => !IsBusy &&
        _revertUndoState is not null &&
        Equals(_revertUndoState.Precondition.HeadObjectId, State.Snapshot.HeadObjectId);

    /// <summary>
    /// Gets whether the focused untracked path can be prepared for exact hunk and line staging.
    /// </summary>
    public bool CanPrepareUntrackedPatch => !IsBusy &&
        State.ActivePane == StatusWorkspacePane.Unstaged &&
        State.FocusedItem?.Entry.Kind == RepositoryStatusEntryKind.Untracked;

    /// <summary>
    /// Gets whether staged changes or an existing commit are available for the selected transaction.
    /// </summary>
    public bool CanCommit => !IsBusy &&
        (State.StagedItems.Length > 0 ||
            (CommitOptions.Amend && State.Snapshot.HeadObjectId is not null));

    /// <summary>
    /// Gets whether the requested single-transaction workflow completed successfully.
    /// </summary>
    public bool IsCitoolCompleted { get; private set; }

    /// <summary>
    /// Gets whether the current index can complete no-commit citool successfully.
    /// </summary>
    public bool CanCompleteWithoutCommit => !IsBusy &&
        !State.Snapshot.Entries.Any(static entry => entry.Kind == RepositoryStatusEntryKind.Unmerged);

    /// <summary>
    /// Gets the explicit unchanged-line count surrounding each captured change.
    /// </summary>
    public int DiffContextLines => _diffContextLines;

    /// <summary>
    /// Opens a non-bare repository and captures its first complete status generation.
    /// </summary>
    /// <param name="workingDirectory">The canonical directory supplied by the user.</param>
    /// <param name="amend">Whether the first commit transaction begins in amend mode.</param>
    /// <param name="cancellationToken">Signals startup cancellation.</param>
    /// <returns>The discovered repository, Git installation, and a workspace unless the repository is bare.</returns>
    internal static async Task<(
        RepositoryWorkspaceSession? Session,
        RepositoryLocation Repository,
        GitInstallation Installation)> OpenAsync(
        CanonicalDirectory workingDirectory,
        bool amend,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var processEnvironment = new RuntimeProcessEnvironment();
        var resolver = new ExecutableResolver(processEnvironment);
        var runner = new ChildProcessRunner();
        var environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
        var installation = await new GitVersionService(resolver, runner)
            .GetAsync(workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        var repository = await new RepositoryDiscoveryService(installation, runner, environmentFactory)
            .DiscoverAsync(workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (repository.IsBare)
        {
            return (Session: null, repository, installation);
        }

        var repositoryWorkingDirectory = CanonicalDirectory.Create(repository.WorkTree!);
        var statusService = new RepositoryStatusService(
            installation,
            runner,
            environmentFactory,
            new PorcelainV2StatusParser());
        var generation = new OperationGeneration(1);
        var snapshot = await statusService
            .ScanAsync(repository, repositoryWorkingDirectory, generation, cancellationToken)
            .ConfigureAwait(false);
        var mutationCoordinator = new RepositoryMutationCoordinator();
        var indexMutationService = new IndexMutationService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator);
        var patchService = new RepositoryPatchService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator);
        var rawDiffService = new RawDiffService(installation, runner, environmentFactory);
        var statePathService = new RepositoryStatePathService(installation, runner, environmentFactory);
        var commitService = new CommitService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator,
            statePathService);
        var editMessagePath = await statePathService.ResolveAsync(
            repositoryWorkingDirectory,
            RepositoryStateFile.EditMessage,
            cancellationToken).ConfigureAwait(false);
        var messagePath = await statePathService.ResolveAsync(
            repositoryWorkingDirectory,
            RepositoryStateFile.Message,
            cancellationToken).ConfigureAwait(false);
        var backupPath = await statePathService.ResolveAsync(
            repositoryWorkingDirectory,
            RepositoryStateFile.MessageBackup,
            cancellationToken).ConfigureAwait(false);
        var commitDraft = await LoadCommitDraftAsync(
            editMessagePath,
            messagePath,
            backupPath,
            cancellationToken).ConfigureAwait(false);
        var commitDraftStore = new CommitDraftStore(
            messagePath,
            backupPath,
            commitDraft,
            TimeSpan.FromMilliseconds(500));
        var session = new RepositoryWorkspaceSession(
            repositoryWorkingDirectory,
            repository,
            installation,
            statusService,
            indexMutationService,
            patchService,
            commitService,
            commitDraftStore,
            rawDiffService,
            mutationCoordinator,
            snapshot,
            commitDraft,
            amend);
        try
        {
            await session.CaptureDiffsAsync(generation, cancellationToken).ConfigureAwait(false);
            await session.LoadActiveDiffAsync(cancellationToken).ConfigureAwait(false);
            return (session, repository, installation);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Focuses one worktree row and loads its exact raw patch into the read-only editor presentation.
    /// </summary>
    /// <param name="index">The absolute worktree row index.</param>
    /// <param name="cancellationToken">Signals patch loading cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    public async Task FocusUnstagedAsync(int index, CancellationToken cancellationToken)
    {
        State.FocusUnstaged(index);
        await LoadFocusedDiffAsync(RawDiffTarget.WorkTree, cancellationToken).ConfigureAwait(false);
        NotifyChanged();
    }

    /// <summary>
    /// Focuses one index row and loads its exact raw patch into the read-only editor presentation.
    /// </summary>
    /// <param name="index">The absolute index row index.</param>
    /// <param name="cancellationToken">Signals patch loading cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    public async Task FocusStagedAsync(int index, CancellationToken cancellationToken)
    {
        State.FocusStaged(index);
        await LoadFocusedDiffAsync(RawDiffTarget.Index, cancellationToken).ConfigureAwait(false);
        NotifyChanged();
    }

    /// <summary>
    /// Stages the complete exact hunk under the read-only diff editor cursor.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task StageFocusedHunkAsync(CancellationToken cancellationToken)
        => RunFocusedHunkMutationAsync(RawDiffTarget.WorkTree, cancellationToken);

    /// <summary>
    /// Unstages the complete exact hunk under the read-only diff editor cursor.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task UnstageFocusedHunkAsync(CancellationToken cancellationToken)
        => RunFocusedHunkMutationAsync(RawDiffTarget.Index, cancellationToken);

    /// <summary>
    /// Stages every exact changed line selected by the worktree diff editor cursor set.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task StageSelectedLinesAsync(CancellationToken cancellationToken)
        => RunSelectedLineMutationAsync(RawDiffTarget.WorkTree, cancellationToken);

    /// <summary>
    /// Unstages every exact changed line selected by the index diff editor cursor set.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task UnstageSelectedLinesAsync(CancellationToken cancellationToken)
        => RunSelectedLineMutationAsync(RawDiffTarget.Index, cancellationToken);

    /// <summary>
    /// Reverts the complete focused worktree file after destructive confirmation by the view.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public async Task RevertFocusedFileAsync(CancellationToken cancellationToken)
    {
        var file = _focusedPatchFile;
        var document = _workTreeDiff;
        if (!CanRevertFocusedFile || file is null || document is null)
        {
            await ReportNoSelectionAsync("No unstaged file available to revert").ConfigureAwait(false);
            return;
        }

        var patch = await document.ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        await RunRevertPatchAsync("file", patch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts the focused worktree hunk after destructive confirmation by the view.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public async Task RevertFocusedHunkAsync(CancellationToken cancellationToken)
    {
        var hunk = GetFocusedHunk(RawDiffTarget.WorkTree);
        var file = _focusedPatchFile;
        var document = _workTreeDiff;
        if (hunk is null || file is null || document is null)
        {
            await ReportNoSelectionAsync("No unstaged hunk under the diff cursor").ConfigureAwait(false);
            return;
        }

        var patch = await document.ReadHunkPatchAsync(file, hunk, cancellationToken).ConfigureAwait(false);
        await RunRevertPatchAsync("hunk", patch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts selected worktree changed lines after destructive confirmation by the view.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public async Task RevertSelectedLinesAsync(CancellationToken cancellationToken)
    {
        var selectedLineNumbers = GetSelectedChangedLineNumbers(RawDiffTarget.WorkTree);
        var file = _focusedPatchFile;
        var document = _workTreeDiff;
        if (selectedLineNumbers.Count == 0 || file is null || document is null)
        {
            await ReportNoSelectionAsync("No unstaged changed lines selected").ConfigureAwait(false);
            return;
        }

        var patch = await document.ReadSelectedLinesPatchAsync(
            file,
            selectedLineNumbers,
            RawPatchSelectionSide.PreserveNewSide,
            cancellationToken).ConfigureAwait(false);
        await RunRevertPatchAsync("selected lines", patch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reapplies the most recent exact reverted patch while its HEAD and content preconditions match.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after undo and reconciliation.</returns>
    public Task UndoRevertAsync(CancellationToken cancellationToken)
    {
        var undoState = _revertUndoState;
        if (!CanUndoRevert || undoState is null)
        {
            return ReportNoSelectionAsync("No revert is available to undo");
        }

        return RunAsync(
            "Undoing the most recent revert...",
            "Revert undone",
            async token =>
            {
                if (!Equals(undoState.Precondition.HeadObjectId, State.Snapshot.HeadObjectId))
                {
                    throw new RepositoryPreconditionException(
                        "HEAD changed after the revert; refresh and review before restoring discarded worktree content.");
                }

                var result = await _patchService.UndoRevertAsync(
                    _workingDirectory,
                    undoState.Patch,
                    undoState.Precondition,
                    token).ConfigureAwait(false);
                _revertUndoState = null;
                return result;
            },
            cancellationToken,
            preserveRevertUndo: true);
    }

    /// <summary>
    /// Records intent-to-add for the focused untracked path and loads its exact unstaged patch.
    /// </summary>
    /// <param name="cancellationToken">Signals index mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task PrepareFocusedUntrackedPatchAsync(CancellationToken cancellationToken)
    {
        var item = State.FocusedItem;
        return !CanPrepareUntrackedPatch || item is null
            ? ReportNoSelectionAsync("No focused untracked path is available to prepare")
            : RunAsync(
                "Preparing untracked patch...",
                "Untracked patch ready for hunk and line staging",
                token => _indexMutationService.PrepareIntentToAddAsync(
                    _workingDirectory,
                    [item.Path],
                    token),
                cancellationToken);
    }

    /// <summary>
    /// Moves the read-only diff cursor to the next exact hunk header.
    /// </summary>
    /// <returns>A completed task after cursor movement and view invalidation.</returns>
    public Task FocusNextHunkAsync()
        => MoveFocusedHunkAsync(moveForward: true);

    /// <summary>
    /// Moves the read-only diff cursor to the preceding or containing exact hunk header.
    /// </summary>
    /// <returns>A completed task after cursor movement and view invalidation.</returns>
    public Task FocusPreviousHunkAsync()
        => MoveFocusedHunkAsync(moveForward: false);

    /// <summary>
    /// Decreases diff context by one line without mutating repository content.
    /// </summary>
    /// <param name="cancellationToken">Signals patch recapture cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    public Task DecreaseDiffContextAsync(CancellationToken cancellationToken)
        => ChangeDiffContextAsync(-1, cancellationToken);

    /// <summary>
    /// Increases diff context by one line without mutating repository content.
    /// </summary>
    /// <param name="cancellationToken">Signals patch recapture cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    public Task IncreaseDiffContextAsync(CancellationToken cancellationToken)
        => ChangeDiffContextAsync(1, cancellationToken);

    /// <summary>
    /// Refreshes status without changing the repository or discarding controlled selection.
    /// </summary>
    /// <param name="cancellationToken">Signals refresh cancellation.</param>
    /// <returns>A task that completes after the workspace is current.</returns>
    public Task RefreshAsync(CancellationToken cancellationToken)
        => RunAsync("Refreshing status...", "Status refreshed", mutation: null, cancellationToken);

    /// <summary>
    /// Stages checked worktree paths, or the focused path when no rows are checked.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task StageAsync(CancellationToken cancellationToken)
    {
        var paths = State.GetPathsToStage();
        return paths.Count == 0
            ? ReportNoSelectionAsync("Nothing to stage")
            : RunAsync(
                $"Staging {FormatPathCount(paths.Count)}...",
                $"Staged {FormatPathCount(paths.Count)}",
                token => _indexMutationService.StageAsync(_workingDirectory, paths, token),
                cancellationToken);
    }

    /// <summary>
    /// Stages every worktree change regardless of presentation filtering or selection.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task StageAllAsync(CancellationToken cancellationToken)
        => State.UnstagedItems.Length == 0
            ? ReportNoSelectionAsync("Nothing to stage")
            : RunAsync(
                "Staging all changes...",
                "Staged all changes",
                token => _indexMutationService.StageAllAsync(_workingDirectory, token),
                cancellationToken);

    /// <summary>
    /// Unstages checked index paths, or the focused path when no rows are checked.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task UnstageAsync(CancellationToken cancellationToken)
    {
        var paths = State.GetPathsToUnstage();
        var snapshot = State.Snapshot;
        return paths.Count == 0
            ? ReportNoSelectionAsync("Nothing to unstage")
            : RunAsync(
                $"Unstaging {FormatPathCount(paths.Count)}...",
                $"Unstaged {FormatPathCount(paths.Count)}",
                token => _indexMutationService.UnstageAsync(snapshot, _workingDirectory, paths, token),
                cancellationToken);
    }

    /// <summary>
    /// Unstages every index entry to HEAD or clears the complete unborn index.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task UnstageAllAsync(CancellationToken cancellationToken)
    {
        var snapshot = State.Snapshot;
        return State.StagedItems.Length == 0
            ? ReportNoSelectionAsync("Nothing to unstage")
            : RunAsync(
                "Unstaging all changes...",
                "Unstaged all changes",
                token => _indexMutationService.UnstageAllAsync(snapshot, _workingDirectory, token),
                cancellationToken);
    }

    /// <summary>
    /// Commits the current index through Git and retains the editor draft on failure.
    /// </summary>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    public Task CommitAsync(CancellationToken cancellationToken)
        => CommitAsync(skipHooks: false, cancellationToken);

    /// <summary>
    /// Commits through Git after a separate confirmation requested bypass of its bypassable hooks.
    /// </summary>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    public Task CommitWithoutHooksAsync(CancellationToken cancellationToken)
        => CommitAsync(skipHooks: true, cancellationToken);

    private Task CommitAsync(bool skipHooks, CancellationToken cancellationToken)
        => !CanCommit
            ? ReportNoSelectionAsync("No commit transaction is available")
            : RunAsync(
                skipHooks ? "Committing without bypassable hooks..." : "Committing transaction...",
                "Commit completed",
                token => RunCommitAsync(skipHooks, token),
                cancellationToken);

    /// <summary>
    /// Completes no-commit citool only when the current index contains no unresolved entries.
    /// </summary>
    /// <param name="cancellationToken">Signals completion cancellation.</param>
    /// <returns>A completed task after validation and state publication.</returns>
    public Task CompleteWithoutCommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanCompleteWithoutCommit)
        {
            return ReportNoSelectionAsync(
                IsBusy
                    ? "Another repository operation is already running"
                    : "Resolve and stage every unmerged path before finishing");
        }

        IsCitoolCompleted = true;
        return ReportNoSelectionAsync("Index preparation completed");
    }

    /// <summary>
    /// Flushes the latest recoverable draft and releases repository-session resources.
    /// </summary>
    /// <returns>A value task that completes after pending recovery state is durable.</returns>
    public async ValueTask DisposeAsync()
    {
        CommitMessage.Changed -= HandleCommitMessageChanged;
        _commitDraftStore.PersistenceFailed -= HandleCommitDraftPersistenceFailed;
        try
        {
            await _commitDraftStore.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _workTreeDiff?.Dispose();
            _indexDiff?.Dispose();
            _mutationCoordinator.Dispose();
        }
    }

    private async Task RunAsync(
        string pendingActivity,
        string successActivity,
        Func<CancellationToken, Task<GitOperationResult>>? mutation,
        CancellationToken cancellationToken,
        Action? beforeScan = null,
        bool preserveRevertUndo = false)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            Activity = "Another repository operation is already running";
            NotifyChanged();
            return;
        }

        Activity = pendingActivity;
        _completionActivityOverride = null;
        NotifyChanged();
        try
        {
            if (mutation is not null)
            {
                await mutation(cancellationToken).ConfigureAwait(false);
                if (!preserveRevertUndo)
                {
                    _revertUndoState = null;
                }
            }

            beforeScan?.Invoke();
            await ScanAsync(cancellationToken).ConfigureAwait(false);
            Activity = _completionActivityOverride ?? successActivity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            if (mutation is not null)
            {
                await TryReconcileAfterFailureAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _completionActivityOverride = null;
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        _generation = _generation.Next();
        var snapshot = await _statusService
            .ScanAsync(_repository, _workingDirectory, _generation, cancellationToken)
            .ConfigureAwait(false);
        await CaptureDiffsAsync(_generation, cancellationToken).ConfigureAwait(false);
        State.ApplySnapshot(snapshot);
        await LoadActiveDiffAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureDiffsAsync(
        OperationGeneration generation,
        CancellationToken cancellationToken)
    {
        var workTreeTask = _rawDiffService.CaptureAsync(
            _workingDirectory,
            RawDiffTarget.WorkTree,
            generation,
            _diffContextLines,
            cancellationToken);
        var indexTask = _rawDiffService.CaptureAsync(
            _workingDirectory,
            RawDiffTarget.Index,
            generation,
            _diffContextLines,
            cancellationToken);
        try
        {
            await Task.WhenAll(workTreeTask, indexTask).ConfigureAwait(false);
        }
        catch
        {
            if (workTreeTask.IsCompletedSuccessfully)
            {
                workTreeTask.Result.Dispose();
            }

            if (indexTask.IsCompletedSuccessfully)
            {
                indexTask.Result.Dispose();
            }

            throw;
        }

        var previousWorkTree = _workTreeDiff;
        var previousIndex = _indexDiff;
        _workTreeDiff = workTreeTask.Result;
        _indexDiff = indexTask.Result;
        previousWorkTree?.Dispose();
        previousIndex?.Dispose();
    }

    private Task LoadActiveDiffAsync(CancellationToken cancellationToken)
        => LoadFocusedDiffAsync(
            State.ActivePane == StatusWorkspacePane.Staged
                ? RawDiffTarget.Index
                : RawDiffTarget.WorkTree,
            cancellationToken);

    private async Task LoadFocusedDiffAsync(
        RawDiffTarget target,
        CancellationToken cancellationToken)
    {
        var item = State.FocusedItem;
        var document = target == RawDiffTarget.Index ? _indexDiff : _workTreeDiff;
        var generation = State.Snapshot.Generation;
        var side = target == RawDiffTarget.Index ? "Staged" : "Unstaged";
        if (item is null)
        {
            ClearFocusedPatch();
            Diff.SetContent("Diff", "Select a changed path to inspect its patch.", generation);
            return;
        }

        var title = $"{side}: {item.Path.DisplayText}";
        if (document is null || document.Index.Generation != generation)
        {
            ClearFocusedPatch();
            Diff.SetContent(title, "Patch data is not current; refresh the repository.", generation);
            return;
        }

        var file = document.Index.Find(item.Path);
        if (file is null)
        {
            ClearFocusedPatch();
            var message = item.Entry.Kind == RepositoryStatusEntryKind.Untracked
                ? "This untracked path has no Git patch until it is staged or added with intent-to-add."
                : "Git emitted no patch content for this status entry.";
            Diff.SetContent(title, message, generation);
            return;
        }

        if (file.IsBinary)
        {
            _focusedPatchFile = file;
            _focusedPatchTarget = target;
            _focusedPatchGeneration = generation;
            Diff.SetContent(
                title,
                $"Binary patch data retained ({file.Length} exact bytes). Text presentation is disabled.",
                generation);
            return;
        }

        var bytes = await document.ReadFilePrefixAsync(
            file,
            MaximumPresentedPatchBytes,
            cancellationToken).ConfigureAwait(false);
        var text = RawPatchPresentationDecoder.Decode(bytes, file.Length > bytes.Length);
        _focusedPatchFile = file;
        _focusedPatchTarget = target;
        _focusedPatchGeneration = generation;

        Diff.SetContent(title, text, generation);
    }

    private async Task RunFocusedHunkMutationAsync(
        RawDiffTarget target,
        CancellationToken cancellationToken)
    {
        var hunk = GetFocusedHunk(target);
        var file = _focusedPatchFile;
        var document = target == RawDiffTarget.Index ? _indexDiff : _workTreeDiff;
        if (hunk is null || file is null || document is null || document.Index.Generation != _focusedPatchGeneration)
        {
            await ReportNoSelectionAsync(target == RawDiffTarget.Index
                ? "No staged hunk under the diff cursor"
                : "No unstaged hunk under the diff cursor").ConfigureAwait(false);
            return;
        }

        var selectedPatch = await document.ReadHunkPatchAsync(
            file,
            hunk,
            cancellationToken).ConfigureAwait(false);
        if (target == RawDiffTarget.Index)
        {
            await RunAsync(
                "Unstaging hunk...",
                "Hunk unstaged",
                token => _patchService.UnstageAsync(
                    _workingDirectory,
                    selectedPatch,
                    token),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunAsync(
            "Staging hunk...",
            "Hunk staged",
            token => _patchService.StageAsync(
                _workingDirectory,
                selectedPatch,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private RawPatchHunk? GetFocusedHunk(RawDiffTarget target)
    {
        if (_focusedPatchTarget != target ||
            _focusedPatchGeneration != State.Snapshot.Generation ||
            _focusedPatchFile is null)
        {
            return null;
        }

        var position = Diff.Editor.Document.OffsetToPosition(Diff.Editor.Cursor.Position);
        return _focusedPatchFile.PatchIndex.FindHunkAtLine(position.Line);
    }

    private async Task RunSelectedLineMutationAsync(
        RawDiffTarget target,
        CancellationToken cancellationToken)
    {
        var selectedLineNumbers = GetSelectedChangedLineNumbers(target);
        var file = _focusedPatchFile;
        var document = target == RawDiffTarget.Index ? _indexDiff : _workTreeDiff;
        if (selectedLineNumbers.Count == 0 || file is null || document is null ||
            document.Index.Generation != _focusedPatchGeneration)
        {
            await ReportNoSelectionAsync(target == RawDiffTarget.Index
                ? "No staged changed lines selected"
                : "No unstaged changed lines selected").ConfigureAwait(false);
            return;
        }

        var selectedPatch = await document.ReadSelectedLinesPatchAsync(
            file,
            selectedLineNumbers,
            target == RawDiffTarget.Index
                ? RawPatchSelectionSide.PreserveNewSide
                : RawPatchSelectionSide.PreserveOldSide,
            cancellationToken).ConfigureAwait(false);
        if (target == RawDiffTarget.Index)
        {
            await RunAsync(
                "Unstaging selected lines...",
                "Selected lines unstaged",
                token => _patchService.UnstageAsync(
                    _workingDirectory,
                    selectedPatch,
                    token),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunAsync(
            "Staging selected lines...",
            "Selected lines staged",
            token => _patchService.StageAsync(
                _workingDirectory,
                selectedPatch,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private Task RunRevertPatchAsync(
        string scope,
        byte[] patch,
        CancellationToken cancellationToken)
        => RunAsync(
            $"Reverting {scope}...",
            $"Reverted {scope}; undo available",
            async token =>
            {
                var result = await _patchService.RevertAsync(
                    _workingDirectory,
                    patch,
                    token).ConfigureAwait(false);
                _revertUndoState = new RevertUndoState(patch, result.Precondition);
                return result.Operation;
            },
            cancellationToken,
            preserveRevertUndo: true);

    private bool HasSelectedChangedLines(RawDiffTarget target)
        => GetSelectedChangedLineNumbers(target).Count > 0;

    private bool HasCurrentFocusedPatch(RawDiffTarget target)
    {
        var document = target == RawDiffTarget.Index ? _indexDiff : _workTreeDiff;
        return _focusedPatchTarget == target &&
            _focusedPatchGeneration == State.Snapshot.Generation &&
            _focusedPatchFile is not null &&
            document is not null &&
            document.Index.Generation == _focusedPatchGeneration;
    }

    private HashSet<int> GetSelectedChangedLineNumbers(RawDiffTarget target)
    {
        var selectedLineNumbers = new HashSet<int>();
        if (_focusedPatchTarget != target ||
            _focusedPatchGeneration != State.Snapshot.Generation ||
            _focusedPatchFile is null)
        {
            return selectedLineNumbers;
        }

        return DiffLineSelectionMapper.GetChangedLineNumbers(
            Diff.Editor,
            _focusedPatchFile.PatchIndex);
    }

    private Task MoveFocusedHunkAsync(bool moveForward)
    {
        if (_focusedPatchFile is null ||
            _focusedPatchTarget is null ||
            _focusedPatchGeneration != State.Snapshot.Generation)
        {
            return ReportNoSelectionAsync("No current diff hunk to navigate");
        }

        var editor = Diff.Editor;
        var currentLine = editor.Document.OffsetToPosition(editor.Cursor.Position).Line;
        var hunk = moveForward
            ? _focusedPatchFile.PatchIndex.FindNextHunk(currentLine)
            : _focusedPatchFile.PatchIndex.FindPreviousHunk(currentLine);
        if (hunk is null)
        {
            return ReportNoSelectionAsync(moveForward ? "No later hunk" : "No earlier hunk");
        }

        editor.Cursor.Position = editor.Document.PositionToOffset(
            new DocumentPosition(hunk.StartLineNumber, 1));
        editor.Cursor.ClearSelection();
        Activity = moveForward ? "Focused next hunk" : "Focused previous hunk";
        NotifyChanged();
        return Task.CompletedTask;
    }

    private Task ChangeDiffContextAsync(int delta, CancellationToken cancellationToken)
    {
        var next = Math.Clamp(_diffContextLines + delta, 0, 100_000);
        if (next == _diffContextLines)
        {
            return ReportNoSelectionAsync($"Diff context remains {_diffContextLines}");
        }

        return RunAsync(
            $"Changing diff context to {next}...",
            $"Diff context: {next}",
            mutation: null,
            cancellationToken,
            beforeScan: () => _diffContextLines = next);
    }

    private async Task<GitOperationResult> RunCommitAsync(
        bool skipHooks,
        CancellationToken cancellationToken)
    {
        var commitMessage = CommitMessage.Message;
        var editorVersion = CommitMessage.Version;
        var draftVersion = _commitDraftStore.Version;
        var result = await _commitService.CommitAsync(
            State.Snapshot,
            _workingDirectory,
            CommitOptions.CreateRequest(commitMessage, skipHooks),
            cancellationToken).ConfigureAwait(false);
        var retainedNewerDraft = false;
        string? recoveryWarning = null;
        try
        {
            if (CommitMessage.Version == editorVersion &&
                await _commitDraftStore.TryDiscardAsync(
                    draftVersion,
                    CancellationToken.None).ConfigureAwait(false))
            {
                CommitMessage.Clear();
            }
            else
            {
                retainedNewerDraft = true;
                await _commitDraftStore.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            recoveryWarning = $"The commit succeeded, but draft recovery cleanup failed: {exception.Message}";
            if (CommitMessage.Version == editorVersion)
            {
                CommitMessage.Clear();
            }
        }

        var shortObjectId = result.NewHead.ToString()[..12];
        var completionParts = new List<string> { $"Committed {shortObjectId}" };
        if (skipHooks)
        {
            completionParts.Add("hook bypass explicitly requested");
        }

        if (retainedNewerDraft)
        {
            completionParts.Add("retained newer draft");
        }

        if (result.DraftCleanupWarning is not null)
        {
            completionParts.Add(TerminalTextSanitizer.Sanitize(result.DraftCleanupWarning));
        }

        if (recoveryWarning is not null)
        {
            completionParts.Add(TerminalTextSanitizer.Sanitize(recoveryWarning));
        }

        _completionActivityOverride = string.Join("; ", completionParts);
        IsCitoolCompleted = true;
        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private static async Task<string> LoadCommitDraftAsync(
        GitPath editMessagePath,
        GitPath messagePath,
        GitPath backupPath,
        CancellationToken cancellationToken)
    {
        foreach (var path in new[] { editMessagePath, messagePath, backupPath })
        {
            var bytes = await RepositoryStateFileSystem.ReadIfExistsAsync(
                path,
                MaximumCommitDraftBytes,
                cancellationToken).ConfigureAwait(false);
            if (bytes is not null)
            {
                try
                {
                    var message = s_strictUtf8.GetString(bytes);
                    if (message.Contains('\0', StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("A recoverable commit-message draft contains NUL.");
                    }

                    return message;
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException(
                        "A recoverable commit-message draft is not valid UTF-8.",
                        exception);
                }
            }
        }

        return string.Empty;
    }

    private void ClearFocusedPatch()
    {
        _focusedPatchFile = null;
        _focusedPatchTarget = null;
        _focusedPatchGeneration = default;
    }

    private async Task TryReconcileAfterFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ScanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed and status could not refresh: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
    }

    private Task ReportNoSelectionAsync(string activity)
    {
        Activity = activity;
        NotifyChanged();
        return Task.CompletedTask;
    }

    private void NotifyChanged()
        => Changed?.Invoke();

    private void HandleCommitMessageChanged()
        => _commitDraftStore.ScheduleSave(CommitMessage.Message);

    private void HandleCommitDraftPersistenceFailed(Exception exception)
    {
        Activity = $"Draft autosave failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        NotifyChanged();
    }

    private static bool IsExpectedFailure(Exception exception)
        => exception is GitCommandException or RepositoryPreconditionException or
            InvalidDataException or IOException or UnauthorizedAccessException;

    private static string FormatPathCount(int count)
        => count == 1 ? "1 path" : $"{count} paths";
}
