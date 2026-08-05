using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;

namespace GitSail.Ui;

/// <summary>
/// Coordinates structured history capture, filtering, focus, and exact commit previews.
/// </summary>
internal sealed class HistorySession : IDisposable
{
    private static readonly TimeSpan PreviewSelectionSettleDelay = TimeSpan.FromMilliseconds(75);
    private readonly CanonicalDirectory _workingDirectory;
    private readonly HistoryService _service;
    private readonly HistoryCommitOperationService _operationService;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly HistoryQuery _query;
    private readonly Lock _previewGate = new();
    private CancellationTokenSource? _previewCancellation;
    private int _previewRequest;

    private HistorySession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        HistoryService service,
        HistoryCommitOperationService operationService,
        RepositoryMutationCoordinator coordinator,
        HistoryQuery query)
    {
        _workingDirectory = workingDirectory;
        Repository = repository;
        Installation = installation;
        _service = service;
        _operationService = operationService;
        _coordinator = coordinator;
        _query = query;
        State = new HistoryWorkspaceState();
        Activity = "Ready to load commit history";
    }

    /// <summary>
    /// Notifies the view that controlled history state has changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the discovered repository displayed by the history workflow.
    /// </summary>
    internal RepositoryLocation Repository { get; }

    /// <summary>
    /// Gets the resolved Git installation used by this history workflow.
    /// </summary>
    internal GitInstallation Installation { get; }

    /// <summary>
    /// Gets the controlled structured history and preview state.
    /// </summary>
    internal HistoryWorkspaceState State { get; }

    /// <summary>
    /// Gets the current or most recent history activity description.
    /// </summary>
    internal string Activity { get; private set; }

    /// <summary>
    /// Gets whether a history capture or preview operation is active.
    /// </summary>
    internal bool IsBusy { get; private set; }

    /// <summary>
    /// Gets whether the most recent structured history capture failed.
    /// </summary>
    internal bool HasLoadFailure { get; private set; }

    /// <summary>
    /// Gets the exact cherry-pick or commit-revert state currently retained by Git.
    /// </summary>
    internal HistoryCommitOperationState? PendingOperation { get; private set; }

    /// <summary>
    /// Opens a repository and creates its structured history workflow.
    /// </summary>
    /// <param name="launchDirectory">The canonical directory supplied by the user.</param>
    /// <param name="options">The typed history command operands.</param>
    /// <param name="processEnvironment">The classified startup environment.</param>
    /// <param name="cancellationToken">Signals repository discovery cancellation.</param>
    /// <returns>The ready history session before its first bounded capture.</returns>
    internal static async Task<HistorySession> OpenAsync(
        CanonicalDirectory launchDirectory,
        HistoryOptions options,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processEnvironment);
        var resolver = new ExecutableResolver(processEnvironment);
        var runner = new ChildProcessRunner();
        var environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
        var installation = await new GitVersionService(resolver, runner)
            .GetAsync(launchDirectory, cancellationToken)
            .ConfigureAwait(false);
        var repository = await new RepositoryDiscoveryService(installation, runner, environmentFactory)
            .DiscoverAsync(launchDirectory, cancellationToken)
            .ConfigureAwait(false);
        var workingDirectory = CanonicalDirectory.Create(repository.WorkTree ?? repository.GitDirectory);
        var pathspecs = await CommandPathspecResolver.ResolveAsync(
            options.Pathspecs,
            options.NativePathspecs,
            options.PathspecFile,
            options.PathspecFileNul,
            cancellationToken).ConfigureAwait(false);

        var query = new HistoryQuery(
            options.RevisionRange is null ? null : Revision.Create(options.RevisionRange),
            pathspecs,
            2_000);
        var coordinator = new RepositoryMutationCoordinator();
        return new HistorySession(
            workingDirectory,
            repository,
            installation,
            new HistoryService(installation, runner, environmentFactory),
            new HistoryCommitOperationService(
                installation,
                runner,
                environmentFactory,
                coordinator),
            coordinator,
            query);
    }

    /// <summary>
    /// Reloads the bounded structured history and the focused exact commit preview.
    /// </summary>
    /// <param name="cancellationToken">Signals history capture cancellation.</param>
    /// <returns>A task that completes after controlled history state is current.</returns>
    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        CancelQueuedPreview();
        IsBusy = true;
        State.Clear();
        Activity = "Loading structured commit history...";
        NotifyChanged();
        try
        {
            var catalog = await _service.CaptureAsync(
                _workingDirectory,
                _query,
                cancellationToken).ConfigureAwait(false);
            State.ApplyCatalog(catalog);
            HasLoadFailure = false;
            await CaptureFocusedPreviewAsync(cancellationToken).ConfigureAwait(false);
            PendingOperation = await _operationService.CaptureStateAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            Activity = PendingOperation is null
                ? catalog.Commits.IsEmpty
                    ? "No commits match this history request"
                    : $"Loaded {catalog.Commits.Length} {(catalog.Commits.Length == 1 ? "commit" : "commits")}"
                : GetStoppedActivity(PendingOperation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            HasLoadFailure = true;
            State.SetPreviewMessage(TerminalTextSanitizer.Sanitize(exception.Message));
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Applies an incremental history filter and updates the focused exact preview.
    /// </summary>
    /// <param name="filter">The latest user-entered filter text.</param>
    /// <param name="cancellationToken">Signals preview capture cancellation.</param>
    /// <returns>A completed task after the filtered rows update and the final preview is queued.</returns>
    internal Task FilterAsync(string filter, CancellationToken cancellationToken)
    {
        State.SetFilter(filter);
        NotifyChanged();
        QueueFocusedPreview(cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Focuses one visible history row and loads its exact immutable commit preview.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    /// <param name="cancellationToken">Signals preview capture cancellation.</param>
    /// <returns>A completed task after focus updates and the final preview is queued.</returns>
    internal Task FocusAsync(int index, CancellationToken cancellationToken)
    {
        State.Focus(index);
        NotifyChanged();
        QueueFocusedPreview(cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves commit focus by one bounded relative offset.
    /// </summary>
    /// <param name="offset">The signed visible-row offset.</param>
    /// <param name="cancellationToken">Signals preview capture cancellation.</param>
    /// <returns>A task that completes after focus and preview state are current.</returns>
    internal Task MoveFocusAsync(int offset, CancellationToken cancellationToken)
    {
        if (State.VisibleItems.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var currentIndex = State.FocusedIndex;
        var index = Math.Clamp(currentIndex + offset, 0, State.VisibleItems.Length - 1);
        if (index == currentIndex)
        {
            return Task.CompletedTask;
        }

        return FocusAsync(index, cancellationToken);
    }

    /// <summary>
    /// Prepares one exact selected history commit operation for a confirmation dialog.
    /// </summary>
    /// <param name="operation">The requested cherry-pick or commit-revert operation.</param>
    /// <param name="mainlineParent">The one-based mainline parent for a selected merge commit.</param>
    /// <param name="cancellationToken">Signals plan preparation cancellation.</param>
    /// <returns>The exact confirmation plan, or <see langword="null"/> when preparation fails.</returns>
    internal async Task<HistoryCommitOperationPlan?> PrepareOperationAsync(
        HistoryCommitOperation operation,
        int? mainlineParent,
        CancellationToken cancellationToken)
    {
        var commit = State.FocusedItem?.Commit;
        if (commit is null || IsBusy)
        {
            return null;
        }

        IsBusy = true;
        Activity = $"Preparing {GetOperationName(operation)} confirmation...";
        NotifyChanged();
        try
        {
            var plan = await _operationService.PrepareAsync(
                _workingDirectory,
                commit,
                operation,
                mainlineParent,
                cancellationToken).ConfigureAwait(false);
            Activity = $"Ready to confirm {GetOperationName(operation)}";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return null;
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Executes one exact history operation after the user confirms its prepared plan.
    /// </summary>
    /// <param name="plan">The exact displayed operation plan.</param>
    /// <param name="cancellationToken">Signals operation cancellation.</param>
    /// <returns>A task that completes after history and retained operation state are current.</returns>
    internal Task ExecuteOperationAsync(
        HistoryCommitOperationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return RunOperationAsync(
            () => _operationService.ExecuteAsync(_workingDirectory, plan, cancellationToken),
            $"Running {GetOperationName(plan.Operation)}...",
            $"{GetOperationPastTense(plan.Operation)} {plan.Commit.ToString()[..12]}",
            cancellationToken);
    }

    /// <summary>
    /// Continues the exact stopped history operation currently displayed by the session.
    /// </summary>
    /// <param name="cancellationToken">Signals continue cancellation.</param>
    /// <returns>A task that completes after Git either completes or stops again.</returns>
    internal Task ContinueOperationAsync(CancellationToken cancellationToken)
    {
        var state = PendingOperation;
        return state is null
            ? Task.CompletedTask
            : RunOperationAsync(
                () => _operationService.ContinueAsync(_workingDirectory, state, cancellationToken),
                $"Continuing {GetOperationName(state.Operation)}...",
                $"Continued {GetOperationName(state.Operation)}",
                cancellationToken);
    }

    /// <summary>
    /// Skips the exact stopped history operation currently displayed by the session.
    /// </summary>
    /// <param name="cancellationToken">Signals skip cancellation.</param>
    /// <returns>A task that completes after Git either completes or stops on another commit.</returns>
    internal Task SkipOperationAsync(CancellationToken cancellationToken)
    {
        var state = PendingOperation;
        return state is null
            ? Task.CompletedTask
            : RunOperationAsync(
                () => _operationService.SkipAsync(_workingDirectory, state, cancellationToken),
                $"Skipping {GetOperationName(state.Operation)}...",
                $"Skipped {GetOperationName(state.Operation)}",
                cancellationToken);
    }

    /// <summary>
    /// Aborts the exact stopped history operation currently displayed by the session.
    /// </summary>
    /// <param name="cancellationToken">Signals abort cancellation.</param>
    /// <returns>A task that completes after Git restores the pre-operation repository state.</returns>
    internal Task AbortOperationAsync(CancellationToken cancellationToken)
    {
        var state = PendingOperation;
        return state is null
            ? Task.CompletedTask
            : RunOperationAsync(
                () => _operationService.AbortAsync(_workingDirectory, state, cancellationToken),
                $"Aborting {GetOperationName(state.Operation)}...",
                $"Aborted {GetOperationName(state.Operation)}",
                cancellationToken);
    }

    /// <summary>
    /// Releases the repository mutation coordinator owned by this history session.
    /// </summary>
    public void Dispose()
    {
        CancelQueuedPreview();
        _coordinator.Dispose();
    }

    private async Task RunOperationAsync(
        Func<Task<HistoryCommitOperationResult>> operation,
        string runningActivity,
        string completedActivity,
        CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Activity = runningActivity;
        NotifyChanged();
        var reload = false;
        try
        {
            var result = await operation().ConfigureAwait(false);
            PendingOperation = result.State;
            if (result.Outcome == HistoryCommitOperationOutcome.Stopped && result.State is not null)
            {
                Activity = GetStoppedActivity(result.State);
            }
            else
            {
                Activity = completedActivity;
                reload = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            PendingOperation = await TryCaptureOperationStateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }

        if (reload)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!HasLoadFailure)
            {
                Activity = completedActivity;
                NotifyChanged();
            }
        }
    }

    private async Task<HistoryCommitOperationState?> TryCaptureOperationStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _operationService.CaptureStateAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return PendingOperation;
        }
    }

    private void QueueFocusedPreview(CancellationToken cancellationToken)
    {
        var request = Interlocked.Increment(ref _previewRequest);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_previewGate)
        {
            _previewCancellation?.Cancel();
            _previewCancellation = cancellation;
            _ = RunQueuedPreviewAsync(request, cancellation);
        }
    }

    private async Task RunQueuedPreviewAsync(
        int request,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(PreviewSelectionSettleDelay, cancellation.Token).ConfigureAwait(false);
            await CaptureFocusedPreviewAsync(cancellation.Token, request).ConfigureAwait(false);
            if (request == Volatile.Read(ref _previewRequest))
            {
                NotifyChanged();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_previewGate)
            {
                if (ReferenceEquals(_previewCancellation, cancellation))
                {
                    _previewCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void CancelQueuedPreview()
    {
        Interlocked.Increment(ref _previewRequest);
        lock (_previewGate)
        {
            _previewCancellation?.Cancel();
            _previewCancellation = null;
        }
    }

    private Task CaptureFocusedPreviewAsync(CancellationToken cancellationToken)
        => CaptureFocusedPreviewAsync(cancellationToken, Interlocked.Increment(ref _previewRequest));

    private async Task CaptureFocusedPreviewAsync(
        CancellationToken cancellationToken,
        int request)
    {
        var commit = State.FocusedItem?.Commit;
        if (commit is null)
        {
            State.SetPreviewMessage(
                State.Catalog?.Commits.IsEmpty == true
                    ? "No commits match this history request."
                    : "No commit matches the current filter.");
            return;
        }

        try
        {
            var bytes = await _service.ShowAsync(
                _workingDirectory,
                commit.ObjectId,
                cancellationToken).ConfigureAwait(false);
            if (request == Volatile.Read(ref _previewRequest) &&
                State.FocusedItem?.Commit.ObjectId.Equals(commit.ObjectId) == true)
            {
                State.SetPreview(commit, RawPatchPresentationDecoder.Decode(bytes.Span, isTruncated: false));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (request == Volatile.Read(ref _previewRequest))
            {
                State.SetPreviewMessage(TerminalTextSanitizer.Sanitize(exception.Message));
            }
        }
    }

    private static bool IsExpectedFailure(Exception exception)
        => exception is ArgumentException or
            ExecutableResolutionException or
            GitCommandException or
            InvalidOperationException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException;

    private static string GetStoppedActivity(HistoryCommitOperationState state)
        => $"{GetOperationName(state.Operation)} stopped at {state.Commit.ToString()[..12]}; resolve files, then Continue, Skip, or Abort";

    private static string GetOperationName(HistoryCommitOperation operation)
        => operation switch
        {
            HistoryCommitOperation.CherryPick => "cherry-pick",
            HistoryCommitOperation.Revert => "commit revert",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static string GetOperationPastTense(HistoryCommitOperation operation)
        => operation switch
        {
            HistoryCommitOperation.CherryPick => "Cherry-picked",
            HistoryCommitOperation.Revert => "Reverted",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private void NotifyChanged()
        => Changed?.Invoke();
}
