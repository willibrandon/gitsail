using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Selects and loads the highest-precedence recoverable, Git-created, or amend commit message.
/// </summary>
internal sealed class CommitMessageInitializationService
{
    private const int MaximumCommitMessageBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;

    /// <summary>
    /// Initializes commit-message selection over bounded direct state reads and structured Git output.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal CommitMessageInitializationService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
    }

    /// <summary>
    /// Loads recovery first, then matching merge or squash state, then the exact commit selected for amend.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="recoveryPaths">The ordered exact GitSail recovery-state paths.</param>
    /// <param name="mergeMessagePath">The Git-resolved merge-message path.</param>
    /// <param name="squashMessagePath">The Git-resolved squash-message path.</param>
    /// <param name="hasMergeHead">Whether Git's current worktree state has a pending merge parent.</param>
    /// <param name="amendHead">The exact commit selected for amend, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Signals message loading cancellation.</param>
    /// <returns>The selected UTF-8 editor message and its precedence source.</returns>
    internal async Task<CommitMessageInitialization> LoadAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyList<GitPath> recoveryPaths,
        GitPath mergeMessagePath,
        GitPath squashMessagePath,
        bool hasMergeHead,
        ObjectId? amendHead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(recoveryPaths);
        ArgumentNullException.ThrowIfNull(mergeMessagePath);
        ArgumentNullException.ThrowIfNull(squashMessagePath);
        foreach (var path in recoveryPaths)
        {
            ArgumentNullException.ThrowIfNull(path);
            var recovered = await ReadStateMessageAsync(path, cancellationToken).ConfigureAwait(false);
            if (recovered is not null)
            {
                return new CommitMessageInitialization(
                    recovered,
                    CommitMessageInitializationKind.Recovery);
            }
        }

        if (hasMergeHead)
        {
            var mergeMessage = await ReadStateMessageAsync(
                mergeMessagePath,
                cancellationToken).ConfigureAwait(false);
            if (mergeMessage is not null)
            {
                return new CommitMessageInitialization(
                    mergeMessage,
                    CommitMessageInitializationKind.Merge);
            }
        }

        var squashMessage = await ReadStateMessageAsync(
            squashMessagePath,
            cancellationToken).ConfigureAwait(false);
        if (squashMessage is not null)
        {
            return new CommitMessageInitialization(
                squashMessage,
                CommitMessageInitializationKind.Squash);
        }

        if (amendHead is null)
        {
            return new CommitMessageInitialization(
                string.Empty,
                CommitMessageInitializationKind.Empty);
        }

        return new CommitMessageInitialization(
            await LoadAmendMessageAsync(
                    workingDirectory,
                    amendHead,
                    cancellationToken)
                .ConfigureAwait(false),
            CommitMessageInitializationKind.Amend);
    }

    private static async Task<string?> ReadStateMessageAsync(
        GitPath path,
        CancellationToken cancellationToken)
    {
        var bytes = await RepositoryStateFileSystem.ReadIfExistsAsync(
            path,
            MaximumCommitMessageBytes,
            cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Decode(bytes);
    }

    private async Task<string> LoadAmendMessageAsync(
        CanonicalDirectory workingDirectory,
        ObjectId commit,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("log"),
                ProcessArgument.Literal("--max-count=1"),
                ProcessArgument.Literal("--encoding=UTF-8"),
                ProcessArgument.Literal("--format=format:%B"),
                ProcessArgument.Literal("--end-of-options"),
                ProcessArgument.Literal(commit.ToString()),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumCommitMessageBytes, 1024 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git could not load the amend commit message." : error);
        }

        return Decode(result.StandardOutput.Span);
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var message = s_strictUtf8.GetString(bytes);
            if (message.Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidDataException("A commit message contains NUL.");
            }

            return message;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("A commit message is not valid UTF-8.", exception);
        }
    }
}
