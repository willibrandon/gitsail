using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using Hex1b.Documents;

namespace GitSail.Ui;

/// <summary>
/// Coordinates asynchronous status reads and serialized index mutations for one open repository.
/// </summary>
internal sealed class RepositoryWorkspaceSession : IRepositoryWorkspaceSession, IDisposable
{
    private const int MaximumPresentedPatchBytes = 4 * 1024 * 1024;
    private readonly CanonicalDirectory _workingDirectory;
    private readonly RepositoryLocation _repository;
    private readonly RepositoryStatusService _statusService;
    private readonly IndexMutationService _indexMutationService;
    private readonly RawDiffService _rawDiffService;
    private readonly RepositoryMutationCoordinator _mutationCoordinator;
    private RawDiffDocument? _workTreeDiff;
    private RawDiffDocument? _indexDiff;
    private RawDiffFile? _focusedPatchFile;
    private RawDiffTarget? _focusedPatchTarget;
    private OperationGeneration _focusedPatchGeneration;
    private OperationGeneration _generation;
    private int _operationInProgress;

    private RepositoryWorkspaceSession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        RepositoryStatusService statusService,
        IndexMutationService indexMutationService,
        RawDiffService rawDiffService,
        RepositoryMutationCoordinator mutationCoordinator,
        RepositoryStatusSnapshot snapshot)
    {
        _workingDirectory = workingDirectory;
        _repository = repository;
        _statusService = statusService;
        _indexMutationService = indexMutationService;
        _rawDiffService = rawDiffService;
        _mutationCoordinator = mutationCoordinator;
        _generation = snapshot.Generation;
        Installation = installation;
        State = new StatusWorkspaceState(snapshot);
        Diff = new DiffViewState();
        Activity = "Ready";
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
    /// Opens a non-bare repository and captures its first complete status generation.
    /// </summary>
    /// <param name="workingDirectory">The canonical directory supplied by the user.</param>
    /// <param name="cancellationToken">Signals startup cancellation.</param>
    /// <returns>The discovered repository, Git installation, and a workspace unless the repository is bare.</returns>
    internal static async Task<(
        RepositoryWorkspaceSession? Session,
        RepositoryLocation Repository,
        GitInstallation Installation)> OpenAsync(
        CanonicalDirectory workingDirectory,
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
        var rawDiffService = new RawDiffService(installation, runner, environmentFactory);
        var session = new RepositoryWorkspaceSession(
            repositoryWorkingDirectory,
            repository,
            installation,
            statusService,
            indexMutationService,
            rawDiffService,
            mutationCoordinator,
            snapshot);
        await session.CaptureDiffsAsync(generation, cancellationToken).ConfigureAwait(false);
        await session.LoadActiveDiffAsync(cancellationToken).ConfigureAwait(false);
        return (session, repository, installation);
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

    /// <inheritdoc />
    public void Dispose()
    {
        _workTreeDiff?.Dispose();
        _indexDiff?.Dispose();
        _mutationCoordinator.Dispose();
    }

    private async Task RunAsync(
        string pendingActivity,
        string successActivity,
        Func<CancellationToken, Task<GitOperationResult>>? mutation,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            Activity = "Another repository operation is already running";
            NotifyChanged();
            return;
        }

        Activity = pendingActivity;
        NotifyChanged();
        try
        {
            if (mutation is not null)
            {
                await mutation(cancellationToken).ConfigureAwait(false);
            }

            await ScanAsync(cancellationToken).ConfigureAwait(false);
            Activity = successActivity;
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
            cancellationToken);
        var indexTask = _rawDiffService.CaptureAsync(
            _workingDirectory,
            RawDiffTarget.Index,
            generation,
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
            ClearFocusedPatch();
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
                token => _indexMutationService.UnstagePatchAsync(
                    _workingDirectory,
                    selectedPatch,
                    token),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunAsync(
            "Staging hunk...",
            "Hunk staged",
            token => _indexMutationService.StagePatchAsync(
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

    private static bool IsExpectedFailure(Exception exception)
        => exception is GitCommandException or InvalidDataException or IOException or UnauthorizedAccessException;

    private static string FormatPathCount(int count)
        => count == 1 ? "1 path" : $"{count} paths";
}
