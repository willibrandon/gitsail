using GitSail.Domain;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures stable tracked content and porcelain-reported path occupancy through Git.
/// </summary>
internal sealed class RepositoryWorktreeFingerprintService
{
    private const int MaximumStableCaptureAttempts = 3;
    private const int MaximumStatusBytes = 512 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RawDiffService _rawDiffService;

    /// <summary>
    /// Initializes action-relevant worktree fingerprint capture over the sole process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal RepositoryWorktreeFingerprintService(
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
        _rawDiffService = new RawDiffService(installation, runner, environmentFactory);
    }

    /// <summary>
    /// Captures one stable action-relevant worktree fingerprint or rejects continuing changes.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="cancellationToken">Signals fingerprint capture cancellation.</param>
    /// <returns>The stable SHA-256 identity used to guard stash apply and pop.</returns>
    internal async Task<RepositoryWorktreeFingerprint> CaptureAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        for (var attempt = 0; attempt < MaximumStableCaptureAttempts; attempt++)
        {
            var first = await CaptureOnceAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            var second = await CaptureOnceAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            if (first is not null && second is not null && first.Matches(second))
            {
                return second;
            }
        }

        throw new RepositoryPreconditionException(
            "Tracked content or reported worktree path occupancy continued changing while GitSail captured the worktree; retry the refresh.");
    }

    private async Task<RepositoryWorktreeFingerprint?> CaptureOnceAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var statusBefore = await ReadStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        using var worktreeDiff = await _rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(0),
            contextLines: 0,
            cancellationToken).ConfigureAwait(false);
        var trackedDigest = await worktreeDiff.ComputeSha256Async(cancellationToken).ConfigureAwait(false);
        var statusAfter = await ReadStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!statusBefore.Span.SequenceEqual(statusAfter.Span))
        {
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSegment(hash, "status"u8, statusAfter.Span);
        AppendSegment(hash, "tracked"u8, trackedDigest);
        return new RepositoryWorktreeFingerprint(hash.GetHashAndReset());
    }

    private async Task<ReadOnlyMemory<byte>> ReadStatusAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("status"),
                ProcessArgument.Literal("--porcelain=v2"),
                ProcessArgument.Literal("-z"),
                ProcessArgument.Literal("--untracked-files=all"),
                ProcessArgument.Literal("--ignored=matching"),
                ProcessArgument.Literal("--no-renames"),
            ],
            MaximumStatusBytes,
            "Git could not capture complete worktree status.",
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private async Task<ProcessResult> RunReadAsync(
        CanonicalDirectory workingDirectory,
        List<ProcessArgument> arguments,
        int maximumOutputBytes,
        string fallbackError,
        CancellationToken cancellationToken)
    {
        var completeArguments = new List<ProcessArgument>(arguments.Count + 2)
        {
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
        };
        completeArguments.AddRange(arguments);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. completeArguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(maximumOutputBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? fallbackError : error);
        }

        return result;
    }

    private static void AppendSegment(
        IncrementalHash hash,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> bytes)
    {
        AppendLength(hash, label.Length);
        hash.AppendData(label);
        AppendLength(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendLength(IncrementalHash hash, int length)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        hash.AppendData(bytes);
    }
}
