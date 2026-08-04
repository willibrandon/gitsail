using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;

namespace GitSail.Ui;

/// <summary>
/// Coordinates asynchronous status reads and serialized index mutations for one open repository.
/// </summary>
internal sealed class RepositoryWorkspaceSession : IRepositoryWorkspaceSession, IDisposable
{
    private readonly CanonicalDirectory _workingDirectory;
    private readonly RepositoryLocation _repository;
    private readonly RepositoryStatusService _statusService;
    private readonly IndexMutationService _indexMutationService;
    private readonly RepositoryMutationCoordinator _mutationCoordinator;
    private OperationGeneration _generation;
    private int _operationInProgress;

    private RepositoryWorkspaceSession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        RepositoryStatusService statusService,
        IndexMutationService indexMutationService,
        RepositoryMutationCoordinator mutationCoordinator,
        RepositoryStatusSnapshot snapshot)
    {
        _workingDirectory = workingDirectory;
        _repository = repository;
        _statusService = statusService;
        _indexMutationService = indexMutationService;
        _mutationCoordinator = mutationCoordinator;
        _generation = snapshot.Generation;
        Installation = installation;
        State = new StatusWorkspaceState(snapshot);
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
    /// Gets a short, control-safe description of the current or most recent operation.
    /// </summary>
    public string Activity { get; private set; }

    /// <summary>
    /// Gets whether an asynchronous refresh or mutation is currently active.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _operationInProgress) != 0;

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
        var session = new RepositoryWorkspaceSession(
            repositoryWorkingDirectory,
            repository,
            installation,
            statusService,
            indexMutationService,
            mutationCoordinator,
            snapshot);
        return (session, repository, installation);
    }

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

    /// <inheritdoc />
    public void Dispose()
        => _mutationCoordinator.Dispose();

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
        State.ApplySnapshot(snapshot);
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
