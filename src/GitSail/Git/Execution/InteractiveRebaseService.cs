using GitSail.CommandLine;
using GitSail.Domain;
using System.Globalization;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Plans, starts, and controls interactive rebase while Git owns all sequencer state.
/// </summary>
internal sealed class InteractiveRebaseService
{
    private const int MaximumCountBytes = 64 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly ITerminalChildProcessRunner _terminalRunner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RepositoryPreconditionService _preconditionService;
    private readonly RepositoryWorktreeFingerprintService _worktreeFingerprintService;
    private readonly RepositoryStatePathService _statePathService;
    private readonly RevisionResolver _revisionResolver;
    private readonly string _sequenceEditorCommand;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes interactive rebase over typed captured and terminal-attached process boundaries.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The bounded child-process runner used for structured reads.</param>
    /// <param name="terminalRunner">The attached runner used only while the parent TUI is stopped.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    /// <param name="sequenceEditorCommand">The safely quoted current-executable helper command.</param>
    /// <param name="timeProvider">The clock used for one-time helper request expiry.</param>
    internal InteractiveRebaseService(
        GitInstallation installation,
        IChildProcessRunner runner,
        ITerminalChildProcessRunner terminalRunner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator,
        string sequenceEditorCommand,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(terminalRunner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceEditorCommand);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _installation = installation;
        _runner = runner;
        _terminalRunner = terminalRunner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _sequenceEditorCommand = sequenceEditorCommand;
        _timeProvider = timeProvider;
        _preconditionService = new RepositoryPreconditionService(
            installation,
            runner,
            environmentFactory);
        _worktreeFingerprintService = new RepositoryWorktreeFingerprintService(
            installation,
            runner,
            environmentFactory);
        _statePathService = new RepositoryStatePathService(
            installation,
            runner,
            environmentFactory);
        _revisionResolver = new RevisionResolver(installation, runner, environmentFactory);
    }

    /// <summary>
    /// Resolves an exact interactive-rebase plan and captures every confirmed repository byte.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="options">The typed command-line revision operands.</param>
    /// <param name="cancellationToken">Signals plan preparation cancellation.</param>
    /// <returns>The exact immutable rebase plan shown for confirmation.</returns>
    internal async Task<RebasePlan> PrepareAsync(
        CanonicalDirectory workingDirectory,
        RebaseOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(options);
        if (await CaptureStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(
                "Continue, skip, edit, or abort the current rebase before starting another one.");
        }

        await ValidateCommitterIdentityAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var upstream = await _revisionResolver.ResolveCommitAsync(
            workingDirectory,
            Revision.Create(options.Upstream ?? "@{upstream}"),
            cancellationToken).ConfigureAwait(false);
        var onto = options.Onto is null
            ? upstream
            : await _revisionResolver.ResolveCommitAsync(
                workingDirectory,
                Revision.Create(options.Onto),
                cancellationToken).ConfigureAwait(false);
        var before = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var head = before.HeadObjectId
            ?? throw new InvalidOperationException("Interactive rebase requires an existing HEAD commit.");
        var worktree = await _worktreeFingerprintService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var after = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!before.Matches(after))
        {
            throw new RepositoryPreconditionException(
                "HEAD or the index changed while the rebase plan was prepared; refresh and try again.");
        }

        var commitCount = await CountCommitsAsync(
            workingDirectory,
            upstream.CommitObjectId,
            head,
            cancellationToken).ConfigureAwait(false);
        if (commitCount == 0)
        {
            throw new InvalidOperationException("There are no commits after the selected upstream to rebase.");
        }

        return new RebasePlan(
            head,
            upstream.CommitObjectId,
            onto.CommitObjectId,
            commitCount,
            after,
            worktree);
    }

    /// <summary>
    /// Starts one confirmed interactive rebase after exact repository revalidation.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="plan">The exact plan previously displayed to the user.</param>
    /// <param name="cancellationToken">Signals rebase cancellation and child interruption.</param>
    /// <returns>The completed or recoverably stopped rebase result.</returns>
    internal async Task<RebaseResult> StartAsync(
        CanonicalDirectory workingDirectory,
        RebasePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Rebase,
            cancellationToken).ConfigureAwait(false);
        if (await CaptureStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new RepositoryPreconditionException(
                "A rebase started after this confirmation was prepared; refresh before continuing.");
        }

        await RevalidatePlanAsync(workingDirectory, plan, cancellationToken).ConfigureAwait(false);
        await ValidateCommitterIdentityAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var todoPath = await _statePathService.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.RebaseTodo,
            cancellationToken).ConfigureAwait(false);
        var request = await RebaseSequenceEditorRequest.CreateAsync(
            todoPath,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var environment = _environmentFactory.CreateCommitEnvironment()
                .SetValue("GIT_SEQUENCE_EDITOR", _sequenceEditorCommand)
                .SetValue(RebaseSequenceEditorRequest.RequestPathVariable, request.FilePathText)
                .SetValue(RebaseSequenceEditorRequest.RequestSecretVariable, request.Secret);
            var exitCode = await RunAttachedAsync(
                workingDirectory,
                [
                    ProcessArgument.Literal("rebase"),
                    ProcessArgument.Literal("--interactive"),
                    ProcessArgument.Literal($"--onto={plan.Onto}"),
                    ProcessArgument.Literal(plan.Upstream.ToString()),
                ],
                environment,
                cancellationToken).ConfigureAwait(false);
            return await ClassifyAsync(
                workingDirectory,
                exitCode,
                "Git could not complete the interactive rebase.",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RebaseSequenceEditorRequest.DeleteIfExistsAsync(
                request,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Captures current Git-owned interactive or stopped rebase state.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="cancellationToken">Signals state capture cancellation.</param>
    /// <returns>The current rebase state, or <see langword="null"/> when no rebase is active.</returns>
    internal async Task<RebaseState?> CaptureStateAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var markerPath = await _statePathService.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.RebaseInteractiveMarker,
            cancellationToken).ConfigureAwait(false);
        var marker = await RepositoryStateFileSystem.ReadIfExistsAsync(
            markerPath,
            maximumBytes: 64,
            cancellationToken).ConfigureAwait(false);
        var applyMarkerPath = await _statePathService.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.RebaseApplyMarker,
            cancellationToken).ConfigureAwait(false);
        var applyMarker = await RepositoryStateFileSystem.ReadIfExistsAsync(
            applyMarkerPath,
            maximumBytes: 64,
            cancellationToken).ConfigureAwait(false);
        var currentCommit = await TryResolvePseudoRefAsync(
            workingDirectory,
            "REBASE_HEAD^{commit}",
            cancellationToken).ConfigureAwait(false);
        if (marker is null && applyMarker is null)
        {
            return null;
        }

        var todoPath = await _statePathService.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.RebaseTodo,
            cancellationToken).ConfigureAwait(false);
        var todo = await RepositoryStateFileSystem.ReadIfExistsAsync(
            todoPath,
            maximumBytes: 16 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        return new RebaseState(currentCommit, CanEditTodo: marker is not null && todo is not null);
    }

    /// <summary>
    /// Runs Continue, Skip, Edit Todo, or Abort against an exact displayed rebase state.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedState">The exact state displayed before the action.</param>
    /// <param name="control">The requested Git-owned recovery action.</param>
    /// <param name="cancellationToken">Signals child cancellation and interruption.</param>
    /// <returns>The completed or still-stopped rebase result.</returns>
    internal async Task<RebaseResult> ControlAsync(
        CanonicalDirectory workingDirectory,
        RebaseState expectedState,
        RebaseControl control,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedState);
        if (control == RebaseControl.EditTodo && !expectedState.CanEditTodo)
        {
            throw new InvalidOperationException("Git does not expose an editable todo for this rebase state.");
        }

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Rebase,
            cancellationToken).ConfigureAwait(false);
        var liveState = await CaptureStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!Equals(liveState, expectedState))
        {
            throw new RepositoryPreconditionException(
                "The rebase changed after it was displayed; refresh before continuing.");
        }

        if (control == RebaseControl.Continue)
        {
            await ValidateCommitterIdentityAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        }

        RebaseSequenceEditorRequestHandle? request = null;
        var environment = _environmentFactory.CreateCommitEnvironment();
        if (control == RebaseControl.EditTodo)
        {
            var todoPath = await _statePathService.ResolveAsync(
                workingDirectory,
                RepositoryStateFile.RebaseTodo,
                cancellationToken).ConfigureAwait(false);
            request = await RebaseSequenceEditorRequest.CreateAsync(
                todoPath,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
            environment = environment
                .SetValue("GIT_SEQUENCE_EDITOR", _sequenceEditorCommand)
                .SetValue(RebaseSequenceEditorRequest.RequestPathVariable, request.FilePathText)
                .SetValue(RebaseSequenceEditorRequest.RequestSecretVariable, request.Secret);
        }

        try
        {
            var exitCode = await RunAttachedAsync(
                workingDirectory,
                [
                    ProcessArgument.Literal("rebase"),
                    ProcessArgument.Literal(GetControlArgument(control)),
                ],
                environment,
                cancellationToken).ConfigureAwait(false);
            var result = await ClassifyAsync(
                workingDirectory,
                exitCode,
                $"Git could not {GetControlDescription(control)} the rebase.",
                cancellationToken).ConfigureAwait(false);
            if (control == RebaseControl.Abort && result.Outcome != RebaseOutcome.Completed)
            {
                throw new InvalidDataException("Git retained rebase state after reporting a successful abort.");
            }

            return result;
        }
        finally
        {
            if (request is not null)
            {
                await RebaseSequenceEditorRequest.DeleteIfExistsAsync(
                    request,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task RevalidatePlanAsync(
        CanonicalDirectory workingDirectory,
        RebasePlan plan,
        CancellationToken cancellationToken)
    {
        var precondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!plan.Precondition.Matches(precondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD, its branch attachment, or the index changed after the rebase was confirmed.");
        }

        var worktree = await _worktreeFingerprintService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!plan.WorktreeFingerprint.Matches(worktree))
        {
            throw new RepositoryPreconditionException(
                "The worktree changed after the rebase was confirmed; review the current files and retry.");
        }

        var finalPrecondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!plan.Precondition.Matches(finalPrecondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD or the index changed while the rebase was rechecked; refresh and retry.");
        }

        var commitCount = await CountCommitsAsync(
            workingDirectory,
            plan.Upstream,
            plan.Head,
            cancellationToken).ConfigureAwait(false);
        if (commitCount != plan.CommitCount)
        {
            throw new RepositoryPreconditionException(
                "The commits selected for rebase changed after confirmation; refresh and retry.");
        }
    }

    private async Task<int> CountCommitsAsync(
        CanonicalDirectory workingDirectory,
        ObjectId upstream,
        ObjectId head,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("rev-list"),
                ProcessArgument.Literal("--count"),
                ProcessArgument.Literal($"{upstream}..{head}"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumCountBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not count the commits selected for rebase.");
        }

        var text = Encoding.ASCII.GetString(result.StandardOutput.Span).Trim();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0)
        {
            throw new InvalidDataException("Git returned an invalid interactive-rebase commit count.");
        }

        return count;
    }

    private async Task ValidateCommitterIdentityAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("var"),
                ProcessArgument.Literal("GIT_COMMITTER_IDENT"),
            ],
            workingDirectory,
            _environmentFactory.CreateCommitEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumCountBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(
                result,
                "Git could not resolve the committer identity required by interactive rebase.");
        }
    }

    private async Task<ObjectId?> TryResolvePseudoRefAsync(
        CanonicalDirectory workingDirectory,
        string revision,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("rev-parse"),
                ProcessArgument.Literal("--verify"),
                ProcessArgument.Literal("--quiet"),
                ProcessArgument.Literal("--end-of-options"),
                ProcessArgument.Literal(revision),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(4096, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && result.StandardError.IsEmpty)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not inspect current rebase state.");
        }

        var output = result.StandardOutput.Span;
        while (!output.IsEmpty && output[^1] is (byte)'\r' or (byte)'\n')
        {
            output = output[..^1];
        }
        if (!ObjectId.TryParseHex(output, out var objectId) || objectId is null)
        {
            throw new InvalidDataException("Git returned an invalid rebase object identifier.");
        }

        return objectId;
    }

    private async Task<int> RunAttachedAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyList<ProcessArgument> arguments,
        ChildEnvironment environment,
        CancellationToken cancellationToken)
    {
        var completeArguments = new List<ProcessArgument>(arguments.Count + 1)
        {
            ProcessArgument.Literal("--no-pager"),
        };
        completeArguments.AddRange(arguments);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. completeArguments],
            workingDirectory,
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024, 1024));
        return await _terminalRunner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RebaseResult> ClassifyAsync(
        CanonicalDirectory workingDirectory,
        int exitCode,
        string fallbackError,
        CancellationToken cancellationToken)
    {
        var state = await CaptureStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (exitCode == 0 && state is null)
        {
            return new RebaseResult(RebaseOutcome.Completed, State: null);
        }

        if (state is not null)
        {
            return new RebaseResult(RebaseOutcome.Stopped, state);
        }

        throw new GitCommandException(exitCode, fallbackError);
    }

    private static GitCommandException CreateCommandException(ProcessResult result, string fallback)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallback : error);
    }

    private static string GetControlArgument(RebaseControl control)
        => control switch
        {
            RebaseControl.Continue => "--continue",
            RebaseControl.Skip => "--skip",
            RebaseControl.EditTodo => "--edit-todo",
            RebaseControl.Abort => "--abort",
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };

    private static string GetControlDescription(RebaseControl control)
        => control switch
        {
            RebaseControl.Continue => "continue",
            RebaseControl.Skip => "skip the current commit in",
            RebaseControl.EditTodo => "edit the remaining todo for",
            RebaseControl.Abort => "abort",
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };
}
