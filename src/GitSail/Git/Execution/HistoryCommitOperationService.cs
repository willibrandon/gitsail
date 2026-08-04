using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Plans, runs, and resumes exact cherry-pick and commit-revert operations through Git.
/// </summary>
internal sealed class HistoryCommitOperationService
{
    private const int MaximumOperationOutputBytes = 64 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RepositoryPreconditionService _preconditionService;
    private readonly RepositoryWorktreeFingerprintService _worktreeFingerprintService;

    /// <summary>
    /// Initializes history commit operations over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    internal HistoryCommitOperationService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _preconditionService = new RepositoryPreconditionService(
            installation,
            runner,
            environmentFactory);
        _worktreeFingerprintService = new RepositoryWorktreeFingerprintService(
            installation,
            runner,
            environmentFactory);
    }

    /// <summary>
    /// Captures an exact confirmation plan for one selected commit and optional merge parent.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="commit">The exact structured commit selected from history.</param>
    /// <param name="operation">The requested cherry-pick or commit-revert operation.</param>
    /// <param name="mainlineParent">The one-based mainline parent selected for a merge commit.</param>
    /// <param name="cancellationToken">Signals plan preparation cancellation.</param>
    /// <returns>The immutable commit operation plan shown to the user.</returns>
    internal async Task<HistoryCommitOperationPlan> PrepareAsync(
        CanonicalDirectory workingDirectory,
        HistoryCommit commit,
        HistoryCommitOperation operation,
        int? mainlineParent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(commit);
        ValidateMainlineParent(commit, mainlineParent);
        if (await CaptureStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(
                "Finish or abort the current cherry-pick or commit revert before starting another one.");
        }

        await RequireCommitAsync(
            workingDirectory,
            commit.ObjectId,
            cancellationToken).ConfigureAwait(false);
        await ValidateCommitterIdentityAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var before = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (before.HeadObjectId is null)
        {
            throw new InvalidOperationException(
                "Cherry-pick and commit revert require an existing current commit.");
        }

        var worktreeFingerprint = await _worktreeFingerprintService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var after = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!before.Matches(after))
        {
            throw new RepositoryPreconditionException(
                "HEAD or the index changed while GitSail prepared the history action; retry it against the current repository.");
        }

        return new HistoryCommitOperationPlan(
            operation,
            commit.ObjectId,
            mainlineParent,
            after,
            worktreeFingerprint);
    }

    /// <summary>
    /// Executes one confirmed history commit operation after revalidating all displayed state.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="plan">The exact plan previously displayed for confirmation.</param>
    /// <param name="cancellationToken">Signals operation cancellation.</param>
    /// <returns>The completed or stopped Git operation result.</returns>
    internal async Task<HistoryCommitOperationResult> ExecuteAsync(
        CanonicalDirectory workingDirectory,
        HistoryCommitOperationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.ApplyCommit,
            cancellationToken).ConfigureAwait(false);
        if (await CaptureStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new RepositoryPreconditionException(
                "A cherry-pick or commit revert started after this confirmation was prepared; refresh before continuing.");
        }

        var precondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!plan.Precondition.Matches(precondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD, its branch attachment, or the index changed after this history action was prepared; retry it against the current repository.");
        }

        var worktreeFingerprint = await _worktreeFingerprintService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!plan.WorktreeFingerprint.Matches(worktreeFingerprint))
        {
            throw new RepositoryPreconditionException(
                "The worktree changed after this history action was prepared; retry it after reviewing the current files.");
        }

        var finalPrecondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!plan.Precondition.Matches(finalPrecondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD or the index changed while GitSail rechecked this history action; retry it against the current repository.");
        }

        await RequireCommitAsync(
            workingDirectory,
            plan.Commit,
            cancellationToken).ConfigureAwait(false);
        await ValidateCommitterIdentityAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(
            workingDirectory,
            BuildStartArguments(plan),
            cancellationToken).ConfigureAwait(false);
        var classified = await ClassifyResultAsync(
            workingDirectory,
            result,
            GetFailureMessage(plan.Operation),
            cancellationToken).ConfigureAwait(false);
        if (classified.State is not null &&
            (classified.State.Operation != plan.Operation ||
                !classified.State.Commit.Equals(plan.Commit)))
        {
            throw new InvalidDataException(
                "Git stopped on history operation state that does not match the confirmed commit.");
        }

        return classified;
    }

    /// <summary>
    /// Continues one still-current stopped cherry-pick or commit-revert operation.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedState">The exact stopped operation previously displayed.</param>
    /// <param name="cancellationToken">Signals continue cancellation.</param>
    /// <returns>The completed or still-stopped Git operation result.</returns>
    internal Task<HistoryCommitOperationResult> ContinueAsync(
        CanonicalDirectory workingDirectory,
        HistoryCommitOperationState expectedState,
        CancellationToken cancellationToken)
        => RunControlAsync(
            workingDirectory,
            expectedState,
            "--continue",
            allowStoppedResult: true,
            cancellationToken);

    /// <summary>
    /// Skips one still-current stopped cherry-pick or commit-revert operation.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedState">The exact stopped operation previously displayed.</param>
    /// <param name="cancellationToken">Signals skip cancellation.</param>
    /// <returns>The completed or next stopped Git operation result.</returns>
    internal Task<HistoryCommitOperationResult> SkipAsync(
        CanonicalDirectory workingDirectory,
        HistoryCommitOperationState expectedState,
        CancellationToken cancellationToken)
        => RunControlAsync(
            workingDirectory,
            expectedState,
            "--skip",
            allowStoppedResult: true,
            cancellationToken);

    /// <summary>
    /// Aborts one still-current stopped cherry-pick or commit-revert operation.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedState">The exact stopped operation previously displayed.</param>
    /// <param name="cancellationToken">Signals abort cancellation.</param>
    /// <returns>The completed abort result.</returns>
    internal Task<HistoryCommitOperationResult> AbortAsync(
        CanonicalDirectory workingDirectory,
        HistoryCommitOperationState expectedState,
        CancellationToken cancellationToken)
        => RunControlAsync(
            workingDirectory,
            expectedState,
            "--abort",
            allowStoppedResult: false,
            cancellationToken);

    /// <summary>
    /// Reads the exact cherry-pick or commit-revert state currently retained by Git.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="cancellationToken">Signals state capture cancellation.</param>
    /// <returns>The current operation state, or <see langword="null"/> when neither operation is active.</returns>
    internal async Task<HistoryCommitOperationState?> CaptureStateAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var cherryPick = await TryResolvePseudoRefAsync(
            workingDirectory,
            "CHERRY_PICK_HEAD^{commit}",
            cancellationToken).ConfigureAwait(false);
        var revert = await TryResolvePseudoRefAsync(
            workingDirectory,
            "REVERT_HEAD^{commit}",
            cancellationToken).ConfigureAwait(false);
        if (cherryPick is not null && revert is not null)
        {
            throw new InvalidDataException(
                "Git reported both cherry-pick and commit-revert state at the same time.");
        }

        return cherryPick is not null
            ? new HistoryCommitOperationState(HistoryCommitOperation.CherryPick, cherryPick)
            : revert is not null
                ? new HistoryCommitOperationState(HistoryCommitOperation.Revert, revert)
                : null;
    }

    private async Task<HistoryCommitOperationResult> RunControlAsync(
        CanonicalDirectory workingDirectory,
        HistoryCommitOperationState expectedState,
        string control,
        bool allowStoppedResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedState);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.ApplyCommit,
            cancellationToken).ConfigureAwait(false);
        var state = await CaptureStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!Equals(state, expectedState))
        {
            throw new RepositoryPreconditionException(
                "The stopped history operation changed after it was displayed; refresh before continuing.");
        }

        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal(GetCommand(expectedState.Operation)),
                ProcessArgument.Literal(control),
            ],
            cancellationToken).ConfigureAwait(false);
        if (!allowStoppedResult && result.ExitCode != 0)
        {
            throw CreateCommandException(
                result,
                $"Git could not {FormatControl(control)} the {GetDisplayName(expectedState.Operation)}.");
        }

        var classified = await ClassifyResultAsync(
            workingDirectory,
            result,
            $"Git could not {FormatControl(control)} the {GetDisplayName(expectedState.Operation)}.",
            cancellationToken).ConfigureAwait(false);
        if (!allowStoppedResult && classified.Outcome == HistoryCommitOperationOutcome.Stopped)
        {
            throw new InvalidDataException(
                $"Git retained {GetDisplayName(expectedState.Operation)} state after reporting a successful abort.");
        }

        return classified;
    }

    private async Task<HistoryCommitOperationResult> ClassifyResultAsync(
        CanonicalDirectory workingDirectory,
        ProcessResult result,
        string fallbackError,
        CancellationToken cancellationToken)
    {
        var state = await CaptureStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0 && state is null)
        {
            return new HistoryCommitOperationResult(
                HistoryCommitOperationOutcome.Completed,
                result.StandardOutput,
                result.StandardError,
                State: null);
        }

        if (state is not null)
        {
            return new HistoryCommitOperationResult(
                HistoryCommitOperationOutcome.Stopped,
                result.StandardOutput,
                result.StandardError,
                state);
        }

        throw CreateCommandException(result, fallbackError);
    }

    private async Task RequireCommitAsync(
        CanonicalDirectory workingDirectory,
        ObjectId commit,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("cat-file"),
                ProcessArgument.Literal("-e"),
                ProcessArgument.Literal($"{commit}^{{commit}}"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "The selected history commit is no longer available.");
        }
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
            OutputPolicy.Create(64 * 1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(
                result,
                "Git could not resolve the committer identity required by this history action.");
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
            throw CreateCommandException(result, "Git could not inspect the current history operation.");
        }

        var output = TrimLineEnding(result.StandardOutput.Span);
        if (!ObjectId.TryParseHex(output, out var objectId) || objectId is null)
        {
            throw new InvalidDataException("Git returned an invalid history operation object identifier.");
        }

        return objectId;
    }

    private async Task<ProcessResult> RunAsync(
        CanonicalDirectory workingDirectory,
        List<ProcessArgument> arguments,
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
            _environmentFactory.CreateCommitEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumOperationOutputBytes, MaximumErrorBytes));
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static List<ProcessArgument> BuildStartArguments(HistoryCommitOperationPlan plan)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal(GetCommand(plan.Operation)),
            ProcessArgument.Literal("--no-edit"),
        };
        if (plan.MainlineParent is { } mainlineParent)
        {
            arguments.Add(ProcessArgument.Literal($"--mainline={mainlineParent}"));
        }

        arguments.Add(ProcessArgument.Literal(plan.Commit.ToString()));
        return arguments;
    }

    private static void ValidateMainlineParent(HistoryCommit commit, int? mainlineParent)
    {
        if (commit.Parents.Length > 1)
        {
            if (mainlineParent is null ||
                mainlineParent < 1 ||
                mainlineParent > commit.Parents.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(mainlineParent),
                    $"A merge commit requires a mainline parent between 1 and {commit.Parents.Length}.");
            }

            return;
        }

        if (mainlineParent is not null)
        {
            throw new ArgumentException(
                "A non-merge commit does not accept a mainline parent.",
                nameof(mainlineParent));
        }
    }

    private static GitCommandException CreateCommandException(ProcessResult result, string fallback)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallback : error);
    }

    private static string GetCommand(HistoryCommitOperation operation)
        => operation switch
        {
            HistoryCommitOperation.CherryPick => "cherry-pick",
            HistoryCommitOperation.Revert => "revert",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static string GetDisplayName(HistoryCommitOperation operation)
        => operation switch
        {
            HistoryCommitOperation.CherryPick => "cherry-pick",
            HistoryCommitOperation.Revert => "commit revert",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static string GetFailureMessage(HistoryCommitOperation operation)
        => operation switch
        {
            HistoryCommitOperation.CherryPick => "Git could not cherry-pick the selected commit.",
            HistoryCommitOperation.Revert => "Git could not revert the selected commit.",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static string FormatControl(string control)
        => control switch
        {
            "--continue" => "continue",
            "--skip" => "skip",
            "--abort" => "abort",
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };

    private static ReadOnlySpan<byte> TrimLineEnding(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && bytes[^1] is (byte)'\n' or (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        return bytes;
    }
}
