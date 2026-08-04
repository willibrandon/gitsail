using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Runs one preconditioned Git porcelain commit over an atomic recoverable draft.
/// </summary>
internal sealed class CommitService
{
    private const int MaximumCommitMessageBytes = 16 * 1024 * 1024;
    private const int MaximumCommitOutputBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RepositoryStatePathService _statePathService;
    private readonly RepositoryPreconditionService _preconditionService;
    private readonly PublishedAmendService _publishedAmendService;
    private readonly DetachedHeadWarningService _detachedHeadWarningService;

    /// <summary>
    /// Initializes commit execution over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    /// <param name="statePathService">The allowlisted repository-state path resolver.</param>
    internal CommitService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator,
        RepositoryStatePathService statePathService)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(statePathService);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _statePathService = statePathService;
        _preconditionService = new RepositoryPreconditionService(
            installation,
            runner,
            environmentFactory);
        _publishedAmendService = new PublishedAmendService(
            installation,
            runner,
            environmentFactory);
        _detachedHeadWarningService = new DetachedHeadWarningService(
            installation,
            runner,
            environmentFactory);
    }

    /// <summary>
    /// Commits the current index after verifying the prepared HEAD object, attachment, and index.
    /// </summary>
    /// <param name="snapshot">The repository snapshot against which the user prepared the commit.</param>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="request">The controlled message and commit options.</param>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>The verified created commit identity and bounded Git output.</returns>
    internal async Task<CommitTransactionResult> CommitAsync(
        RepositoryStatusSnapshot snapshot,
        CanonicalDirectory workingDirectory,
        CommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(request);
        var messageBytes = EncodeMessage(request.Message);

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Commit,
            cancellationToken).ConfigureAwait(false);
        var expectedPrecondition = snapshot.Precondition
            ?? throw new InvalidDataException("The prepared status generation has no repository precondition.");
        var currentPrecondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!Equals(currentPrecondition.HeadObjectId, expectedPrecondition.HeadObjectId))
        {
            throw new RepositoryPreconditionException(
                "HEAD changed after the commit was prepared; refresh and review the new tip before committing.");
        }

        if (!Equals(currentPrecondition.HeadName, expectedPrecondition.HeadName))
        {
            throw new RepositoryPreconditionException(
                "HEAD attachment changed after the commit was prepared; refresh and review the target branch before committing.");
        }

        if (!currentPrecondition.IndexFingerprint.Span.SequenceEqual(expectedPrecondition.IndexFingerprint.Span))
        {
            throw new RepositoryPreconditionException(
                "The index changed after the commit was prepared; refresh and review the staged content before committing.");
        }

        var currentHead = currentPrecondition.HeadObjectId;
        var detachedWarning = await _detachedHeadWarningService.FindAsync(
            workingDirectory,
            currentPrecondition,
            cancellationToken).ConfigureAwait(false);
        if (detachedWarning is not null &&
            !detachedWarning.Matches(request.ConfirmedDetachedHeadWarning))
        {
            throw new DetachedHeadConfirmationException(detachedWarning);
        }

        if (request.Amend)
        {
            var warning = await _publishedAmendService.FindAsync(
                workingDirectory,
                currentHead,
                cancellationToken).ConfigureAwait(false);
            if (warning is not null && !warning.Matches(request.ConfirmedPublishedAmendWarning))
            {
                throw new PublishedAmendConfirmationException(warning);
            }
        }

        await ValidateCommitterIdentityAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var draftPath = await _statePathService.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.EditMessage,
            cancellationToken).ConfigureAwait(false);
        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            draftPath,
            messageBytes,
            cancellationToken).ConfigureAwait(false);

        await ValidatePreparedPreconditionAsync(
            expectedPrecondition,
            workingDirectory,
            cancellationToken).ConfigureAwait(false);

        var result = await RunCommitAsync(
            workingDirectory,
            draftPath,
            request,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git commit failed; the draft was preserved." : error);
        }

        var newHead = await ResolveHeadAsync(workingDirectory, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidDataException("Git reported commit success without a resulting HEAD commit.");
        string? cleanupWarning = null;
        try
        {
            _ = await RepositoryStateFileSystem.DeleteIfExistsAsync(
                draftPath,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupWarning = $"The commit succeeded, but its recoverable draft could not be removed: {exception.Message}";
        }

        return new CommitTransactionResult(
            currentHead,
            newHead,
            result.StandardOutput,
            result.StandardError,
            cleanupWarning);
    }

    private async Task ValidatePreparedPreconditionAsync(
        RepositoryPrecondition expectedPrecondition,
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var currentPrecondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!expectedPrecondition.Matches(currentPrecondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD or the index changed while the commit was being prepared; refresh and review before committing.");
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
            OutputPolicy.Create(64 * 1024, 64 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git could not resolve the committer identity." : error);
        }
    }

    private async Task<ProcessResult> RunCommitAsync(
        CanonicalDirectory workingDirectory,
        GitPath draftPath,
        CommitRequest request,
        CancellationToken cancellationToken)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("commit"),
            ProcessArgument.Literal($"--file={GetManagedArgumentPath(draftPath)}"),
            ProcessArgument.Literal($"--cleanup={GetCleanupArgument(request.CleanupMode)}"),
            ProcessArgument.Literal("--no-status"),
        };
        if (request.Amend)
        {
            arguments.Add(ProcessArgument.Literal("--amend"));
        }

        if (request.Signoff)
        {
            arguments.Add(ProcessArgument.Literal("--signoff"));
        }

        if (request.Author is not null)
        {
            arguments.Add(ProcessArgument.Literal($"--author={request.Author}"));
        }

        if (request.SkipHooks)
        {
            arguments.Add(ProcessArgument.Literal("--no-verify"));
        }

        if (request.SignCommit)
        {
            arguments.Add(ProcessArgument.Literal(request.SigningKey is null
                ? "--gpg-sign"
                : $"--gpg-sign={request.SigningKey}"));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. arguments],
            workingDirectory,
            _environmentFactory.CreateCommitEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumCommitOutputBytes, MaximumCommitOutputBytes));
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ObjectId?> ResolveHeadAsync(
        CanonicalDirectory workingDirectory,
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
                ProcessArgument.Literal("HEAD^{commit}"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(4096, 64 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (result.StandardError.IsEmpty)
            {
                return null;
            }

            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(result.ExitCode, error);
        }

        var output = TrimLineEnding(result.StandardOutput.Span);
        if (!ObjectId.TryParseHex(output, out var objectId))
        {
            throw new InvalidDataException("Git returned an invalid HEAD object identifier.");
        }

        return objectId;
    }

    private static byte[] EncodeMessage(string message)
    {
        if (message.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A commit message cannot contain NUL.", nameof(message));
        }

        var bytes = s_strictUtf8.GetBytes(message);
        if (bytes.Length > MaximumCommitMessageBytes)
        {
            throw new ArgumentException(
                $"The commit message exceeds {MaximumCommitMessageBytes} UTF-8 bytes.",
                nameof(message));
        }

        return bytes;
    }

    private static string GetManagedArgumentPath(GitPath path)
        => path.Kind == NativePathKind.WindowsUtf16
            ? path.GetWindowsPath()
            : s_strictUtf8.GetString(path.GetUnixBytes());

    private static string GetCleanupArgument(CommitCleanupMode mode)
        => mode switch
        {
            CommitCleanupMode.Default => "default",
            CommitCleanupMode.Strip => "strip",
            CommitCleanupMode.Whitespace => "whitespace",
            CommitCleanupMode.Verbatim => "verbatim",
            CommitCleanupMode.Scissors => "scissors",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static ReadOnlySpan<byte> TrimLineEnding(ReadOnlySpan<byte> output)
    {
        if (!output.IsEmpty && output[^1] == (byte)'\n')
        {
            output = output[..^1];
            if (OperatingSystem.IsWindows() && !output.IsEmpty && output[^1] == (byte)'\r')
            {
                output = output[..^1];
            }
        }

        return output;
    }
}
