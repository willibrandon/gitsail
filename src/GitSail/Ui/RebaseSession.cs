using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Coordinates interactive-rebase planning, exact state capture, and attached Git requests.
/// </summary>
internal sealed class RebaseSession : IDisposable
{
    private readonly CanonicalDirectory _workingDirectory;
    private readonly InteractiveRebaseService _service;
    private readonly RepositoryMutationCoordinator _coordinator;

    private RebaseSession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        InteractiveRebaseService service,
        RepositoryMutationCoordinator coordinator,
        RebaseOptions options)
    {
        _workingDirectory = workingDirectory;
        Repository = repository;
        Installation = installation;
        _service = service;
        _coordinator = coordinator;
        Upstream = new TextBoxState(options.Upstream ?? string.Empty);
        Onto = new TextBoxState(options.Onto ?? string.Empty);
        Activity = "Inspecting rebase state...";
    }

    /// <summary>
    /// Notifies the view that controlled rebase state has changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the discovered repository displayed by the rebase workflow.
    /// </summary>
    internal RepositoryLocation Repository { get; }

    /// <summary>
    /// Gets the resolved Git installation used by this rebase workflow.
    /// </summary>
    internal GitInstallation Installation { get; }

    /// <summary>
    /// Gets the canonical repository directory used by the conflict-resolution workspace.
    /// </summary>
    internal CanonicalDirectory WorkingDirectory => _workingDirectory;

    /// <summary>
    /// Gets the editable upstream revision input.
    /// </summary>
    internal TextBoxState Upstream { get; }

    /// <summary>
    /// Gets the editable optional new-base revision input.
    /// </summary>
    internal TextBoxState Onto { get; }

    /// <summary>
    /// Gets the exact prepared plan when no rebase is active.
    /// </summary>
    internal RebasePlan? Plan { get; private set; }

    /// <summary>
    /// Gets the exact Git-owned state when a rebase is active.
    /// </summary>
    internal RebaseState? State { get; private set; }

    /// <summary>
    /// Gets the latest user-visible rebase activity.
    /// </summary>
    internal string Activity { get; private set; }

    /// <summary>
    /// Gets whether structured rebase state is currently being captured.
    /// </summary>
    internal bool IsBusy { get; private set; }

    /// <summary>
    /// Gets whether the latest plan capture or attached Git action failed.
    /// </summary>
    internal bool HasFailure { get; private set; }

    /// <summary>
    /// Gets the action to run after the current terminal application has restored the terminal.
    /// </summary>
    internal RebaseRequestedAction? RequestedAction { get; private set; }

    /// <summary>
    /// Opens a repository and creates its interactive-rebase workflow.
    /// </summary>
    /// <param name="launchDirectory">The canonical directory supplied by the user.</param>
    /// <param name="options">The typed rebase command operands.</param>
    /// <param name="processEnvironment">The classified startup environment.</param>
    /// <param name="cancellationToken">Signals repository discovery cancellation.</param>
    /// <returns>The ready rebase session before its first state capture.</returns>
    internal static async Task<RebaseSession> OpenAsync(
        CanonicalDirectory launchDirectory,
        RebaseOptions options,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processEnvironment);
        var runner = new ChildProcessRunner();
        var environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
        var installation = await new GitVersionService(
                new ExecutableResolver(processEnvironment),
                runner)
            .GetAsync(launchDirectory, cancellationToken)
            .ConfigureAwait(false);
        var repository = await new RepositoryDiscoveryService(
                installation,
                runner,
                environmentFactory)
            .DiscoverAsync(launchDirectory, cancellationToken)
            .ConfigureAwait(false);
        var workingDirectory = CanonicalDirectory.Create(repository.WorkTree ?? repository.GitDirectory);
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current GitSail executable path is unavailable.");
        var sequenceEditorCommand = SequenceEditorCommandBuilder.Build(
            processPath,
            Environment.GetCommandLineArgs());
        var coordinator = new RepositoryMutationCoordinator();
        return new RebaseSession(
            workingDirectory,
            repository,
            installation,
            new InteractiveRebaseService(
                installation,
                runner,
                new TerminalChildProcessRunner(),
                environmentFactory,
                coordinator,
                sequenceEditorCommand,
                TimeProvider.System),
            coordinator,
            options);
    }

    /// <summary>
    /// Captures active rebase state or prepares an exact plan from the current revision inputs.
    /// </summary>
    /// <param name="cancellationToken">Signals state and plan capture cancellation.</param>
    /// <returns>A task that completes after the view reflects current repository state.</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Plan = null;
        Activity = "Inspecting rebase state...";
        NotifyChanged();
        try
        {
            State = await _service.CaptureStateAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            if (State is not null)
            {
                HasFailure = false;
                Activity = State.CurrentCommit is null
                    ? "Interactive rebase stopped and ready for recovery"
                    : $"Interactive rebase stopped at {State.CurrentCommit.ToString()[..12]}";
                return;
            }

            Activity = "Preparing exact rebase plan...";
            NotifyChanged();
            Plan = await _service.PrepareAsync(
                _workingDirectory,
                new RebaseOptions(
                    NormalizeRevision(Upstream.Text),
                    NormalizeRevision(Onto.Text)),
                cancellationToken).ConfigureAwait(false);
            HasFailure = false;
            Activity = $"Prepared {Plan.CommitCount} {(Plan.CommitCount == 1 ? "commit" : "commits")} for review";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Plan = null;
            HasFailure = true;
            Activity = $"Cannot prepare rebase: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Records a confirmed start request for execution after terminal restoration.
    /// </summary>
    internal void RequestStart()
    {
        if (Plan is null || State is not null || IsBusy)
        {
            return;
        }

        RequestedAction = RebaseRequestedAction.Start;
    }

    /// <summary>
    /// Records one recovery request for execution after terminal restoration.
    /// </summary>
    /// <param name="action">The requested continue, skip, edit, or abort action.</param>
    internal void RequestControl(RebaseRequestedAction action)
    {
        if (State is null || IsBusy || action == RebaseRequestedAction.Start)
        {
            return;
        }

        if (action == RebaseRequestedAction.EditTodo && !State.CanEditTodo)
        {
            Activity = "Git does not expose an editable todo for this rebase state.";
            NotifyChanged();
            return;
        }

        RequestedAction = action;
    }

    /// <summary>
    /// Clears the workspace request after the shell has restored the rebase view.
    /// </summary>
    internal void ClearRequestedAction()
        => RequestedAction = null;

    /// <summary>
    /// Runs the recorded Git action while no terminal application owns the terminal.
    /// </summary>
    /// <param name="cancellationToken">Signals attached child interruption.</param>
    /// <returns><see langword="true"/> when the requested rebase or abort completed.</returns>
    internal async Task<bool> RunRequestedActionAsync(CancellationToken cancellationToken)
    {
        var action = RequestedAction;
        RequestedAction = null;
        if (action is null)
        {
            return false;
        }

        try
        {
            RebaseResult result;
            if (action == RebaseRequestedAction.Start)
            {
                var plan = Plan
                    ?? throw new RepositoryPreconditionException("The confirmed rebase plan is no longer available.");
                result = await _service.StartAsync(
                    _workingDirectory,
                    plan,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var state = State
                    ?? throw new RepositoryPreconditionException("The displayed rebase state is no longer available.");
                result = await _service.ControlAsync(
                    _workingDirectory,
                    state,
                    MapControl(action.Value),
                    cancellationToken).ConfigureAwait(false);
            }

            State = result.State;
            Plan = null;
            HasFailure = false;
            if (result.Outcome == RebaseOutcome.Completed)
            {
                Activity = action == RebaseRequestedAction.Abort
                    ? "Rebase aborted and original repository state restored"
                    : "Interactive rebase completed";
                return true;
            }

            Activity = result.State?.CurrentCommit is { } current
                ? $"Rebase stopped at {current.ToString()[..12]}; resolve the issue or choose a recovery action"
                : "Rebase stopped; resolve the issue or choose a recovery action";
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            HasFailure = true;
            Activity = $"Rebase action failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            State = await TryCaptureStateAsync(cancellationToken).ConfigureAwait(false);
            if (State is null)
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Releases the repository mutation coordinator owned by this session.
    /// </summary>
    public void Dispose()
        => _coordinator.Dispose();

    private async Task<RebaseState?> TryCaptureStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _service.CaptureStateAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return null;
        }
    }

    private static RebaseControl MapControl(RebaseRequestedAction action)
        => action switch
        {
            RebaseRequestedAction.Continue => RebaseControl.Continue,
            RebaseRequestedAction.Skip => RebaseControl.Skip,
            RebaseRequestedAction.EditTodo => RebaseControl.EditTodo,
            RebaseRequestedAction.Abort => RebaseControl.Abort,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private static string? NormalizeRevision(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsExpectedFailure(Exception exception)
        => exception is ArgumentException or
            ExecutableResolutionException or
            GitCommandException or
            InvalidDataException or
            IOException or
            InvalidOperationException or
            RepositoryPreconditionException or
            UnauthorizedAccessException;

    private void NotifyChanged()
        => Changed?.Invoke();
}
