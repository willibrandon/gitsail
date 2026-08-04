using GitSail.Domain;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures stable live HEAD and staged-index identities exclusively through read-only Git commands.
/// </summary>
internal sealed class RepositoryPreconditionService
{
    private const int MaximumCaptureAttempts = 3;
    private const int SpoolMemoryThresholdBytes = 1024 * 1024;
    private const int MaximumIndexBytes = 1024 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;

    /// <summary>
    /// Initializes live repository precondition capture over the sole child-process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal RepositoryPreconditionService(
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
    /// Captures one stable live repository precondition or rejects a concurrently changing HEAD.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="cancellationToken">Signals read cancellation.</param>
    /// <returns>The stable HEAD identity and exact staged-index fingerprint.</returns>
    internal async Task<RepositoryPrecondition> CaptureAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        for (var attempt = 0; attempt < MaximumCaptureAttempts; attempt++)
        {
            var headBefore = await CaptureHeadAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            var indexFingerprint = await CaptureIndexFingerprintAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var headAfter = await CaptureHeadAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            if (Equals(headBefore, headAfter))
            {
                return new RepositoryPrecondition(headAfter, indexFingerprint);
            }
        }

        throw new RepositoryPreconditionException(
            "HEAD continued changing while GitSail captured mutation preconditions; refresh and retry.");
    }

    /// <summary>
    /// Captures one HEAD and index pair for a caller that independently brackets and validates repository stability.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="cancellationToken">Signals read cancellation.</param>
    /// <returns>The sequentially observed HEAD identity and exact staged-index fingerprint.</returns>
    internal async Task<RepositoryPrecondition> CaptureOnceAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var head = await CaptureHeadAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var indexFingerprint = await CaptureIndexFingerprintAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        return new RepositoryPrecondition(head, indexFingerprint);
    }

    private async Task<ObjectId?> CaptureHeadAsync(
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
                ProcessArgument.Literal("HEAD"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && result.StandardOutput.IsEmpty)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not resolve the live HEAD precondition.");
        }

        var bytes = TrimLineEnding(result.StandardOutput.Span);
        return ObjectId.TryParseHex(bytes, out var objectId)
            ? objectId
            : throw new InvalidDataException("Git returned an invalid live HEAD object identifier.");
    }

    private async Task<byte[]> CaptureIndexFingerprintAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--literal-pathspecs"),
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("ls-files"),
                ProcessArgument.Literal("--stage"),
                ProcessArgument.Literal("--full-name"),
                ProcessArgument.Literal("-z"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.CreateSpooling(
                SpoolMemoryThresholdBytes,
                MaximumIndexBytes,
                MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        using var spool = result.StandardOutputSpool
            ?? throw new InvalidOperationException("Index precondition capture did not return its required byte spool.");
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not capture the live index precondition.");
        }

        await using var stream = spool.OpenRead();
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static ReadOnlySpan<byte> TrimLineEnding(ReadOnlySpan<byte> value)
    {
        if (!value.IsEmpty && value[^1] == (byte)'\n')
        {
            value = value[..^1];
            if (!value.IsEmpty && value[^1] == (byte)'\r')
            {
                value = value[..^1];
            }
        }

        return value;
    }

    private static GitCommandException CreateCommandException(ProcessResult result, string fallback)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallback : error);
    }
}
