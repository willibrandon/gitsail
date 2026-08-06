using GitSail.Domain;
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures object statistics and runs bounded Git-owned repository care operations.
/// </summary>
internal sealed class RepositoryMaintenanceService
{
    private const int MaximumStatisticsBytes = 1024 * 1024;
    private const int MaximumOperationOutputBytes = 64 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly CredentialPromptBroker _credentialPromptBroker;

    /// <summary>
    /// Initializes repository care over explicit Git, process, environment, mutation, and prompt boundaries.
    /// </summary>
    /// <param name="installation">The resolved compatible Git installation.</param>
    /// <param name="runner">The sole shell-free child-process boundary.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    /// <param name="credentialPromptBroker">The authenticated prompt broker used by configured maintenance tasks.</param>
    internal RepositoryMaintenanceService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator,
        CredentialPromptBroker credentialPromptBroker)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(credentialPromptBroker);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _credentialPromptBroker = credentialPromptBroker;
    }

    /// <summary>
    /// Captures Git's complete verbose object count without exposing alternate database paths.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository worktree directory.</param>
    /// <param name="cancellationToken">Signals statistics cancellation.</param>
    /// <returns>The parsed nonnegative object and storage counts.</returns>
    internal async Task<RepositoryStatistics> CaptureStatisticsAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("count-objects"),
                ProcessArgument.Literal("--verbose"),
            ],
            _environmentFactory.CreateRepositoryReadEnvironment(),
            OutputPolicy.Create(MaximumStatisticsBytes, MaximumStatisticsBytes),
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, "Git could not inspect repository object storage.");
        return ParseStatistics(result.StandardOutput.Span);
    }

    /// <summary>
    /// Runs the repository's configured foreground maintenance tasks through Git.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository worktree directory.</param>
    /// <param name="cancellationToken">Signals maintenance cancellation.</param>
    /// <returns>The exact bounded Git output.</returns>
    internal async Task<GitOperationResult> RunConfiguredMaintenanceAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Maintenance,
            cancellationToken).ConfigureAwait(false);
        await using var credentialOperation = _credentialPromptBroker.StartOperation(
            "Repository maintenance",
            cancellationToken);
        var environment = credentialOperation.ConfigureEnvironment(
            _environmentFactory.CreateTransportEnvironment());
        return await RunSuccessfulOperationAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("maintenance"),
                ProcessArgument.Literal("run"),
            ],
            environment,
            "Git could not complete configured repository maintenance.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one foreground full garbage collection through Git without detaching.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository worktree directory.</param>
    /// <param name="cancellationToken">Signals garbage-collection cancellation.</param>
    /// <returns>The exact bounded Git output.</returns>
    internal async Task<GitOperationResult> RunGarbageCollectionAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Maintenance,
            cancellationToken).ConfigureAwait(false);
        return await RunSuccessfulOperationAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("gc"),
            ],
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            "Git could not complete repository garbage collection.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies complete object and reference integrity through Git without writing lost-found files.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository worktree directory.</param>
    /// <param name="cancellationToken">Signals verification cancellation.</param>
    /// <returns>The exact bounded Git output, including ordinary dangling-object reports.</returns>
    internal async Task<GitOperationResult> VerifyAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Maintenance,
            cancellationToken).ConfigureAwait(false);
        return await RunSuccessfulOperationAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("fsck"),
                ProcessArgument.Literal("--full"),
                ProcessArgument.Literal("--no-progress"),
            ],
            _environmentFactory.CreateRepositoryReadEnvironment(),
            "Git found a repository integrity problem.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses bounded <c>git count-objects --verbose</c> bytes without decoding alternate paths.
    /// </summary>
    /// <param name="bytes">The complete bounded command output.</param>
    /// <returns>The validated statistics snapshot.</returns>
    internal static RepositoryStatistics ParseStatistics(ReadOnlySpan<byte> bytes)
    {
        long? looseObjectCount = null;
        long? looseObjectSizeKiB = null;
        long? packedObjectCount = null;
        long? packCount = null;
        long? packSizeKiB = null;
        long? prunePackableObjectCount = null;
        long? garbageFileCount = null;
        long? garbageSizeKiB = null;
        var alternateCount = 0;
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            var line = newline < 0 ? bytes : bytes[..newline];
            bytes = newline < 0 ? [] : bytes[(newline + 1)..];
            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.IsEmpty)
            {
                continue;
            }

            var separator = line.IndexOf((byte)':');
            if (separator <= 0)
            {
                throw new InvalidDataException("Git returned a malformed object-statistics record.");
            }

            var key = line[..separator];
            var value = TrimAsciiSpace(line[(separator + 1)..]);
            if (key.SequenceEqual("alternate"u8))
            {
                alternateCount = checked(alternateCount + 1);
                continue;
            }

            if (key.SequenceEqual("count"u8))
            {
                SetOnce(ref looseObjectCount, ParseNonnegativeInteger(value));
            }
            else if (key.SequenceEqual("size"u8))
            {
                SetOnce(ref looseObjectSizeKiB, ParseNonnegativeInteger(value));
            }
            else if (key.SequenceEqual("in-pack"u8))
            {
                SetOnce(ref packedObjectCount, ParseNonnegativeInteger(value));
            }
            else if (key.SequenceEqual("packs"u8))
            {
                SetOnce(ref packCount, ParseNonnegativeInteger(value));
            }
            else if (key.SequenceEqual("size-pack"u8))
            {
                SetOnce(ref packSizeKiB, ParseNonnegativeInteger(value));
            }
            else if (key.SequenceEqual("prune-packable"u8))
            {
                SetOnce(ref prunePackableObjectCount, ParseNonnegativeInteger(value));
            }
            else if (key.SequenceEqual("garbage"u8))
            {
                SetOnce(ref garbageFileCount, ParseNonnegativeInteger(value));
            }
            else if (key.SequenceEqual("size-garbage"u8))
            {
                SetOnce(ref garbageSizeKiB, ParseNonnegativeInteger(value));
            }
        }

        return new RepositoryStatistics(
            Require(looseObjectCount, "count"),
            Require(looseObjectSizeKiB, "size"),
            Require(packedObjectCount, "in-pack"),
            Require(packCount, "packs"),
            Require(packSizeKiB, "size-pack"),
            Require(prunePackableObjectCount, "prune-packable"),
            Require(garbageFileCount, "garbage"),
            Require(garbageSizeKiB, "size-garbage"),
            alternateCount);
    }

    private async Task<GitOperationResult> RunSuccessfulOperationAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        ChildEnvironment environment,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            workingDirectory,
            arguments,
            environment,
            OutputPolicy.Create(MaximumOperationOutputBytes, MaximumOperationOutputBytes),
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, failureMessage);
        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private async Task<ProcessResult> RunAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        ChildEnvironment environment,
        OutputPolicy outputPolicy,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            arguments,
            workingDirectory,
            environment,
            StandardInputSource.Empty(),
            outputPolicy);
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfFailed(ProcessResult result, string fallbackMessage)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        if (error.Length == 0)
        {
            error = Encoding.UTF8.GetString(result.StandardOutput.Span).Trim();
        }

        throw new RepositoryMaintenanceException(
            result.ExitCode,
            error.Length == 0 ? fallbackMessage : error,
            result.StandardOutput,
            result.StandardError);
    }

    private static ReadOnlySpan<byte> TrimAsciiSpace(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && value[0] is (byte)' ' or (byte)'\t')
        {
            value = value[1..];
        }

        while (!value.IsEmpty && value[^1] is (byte)' ' or (byte)'\t')
        {
            value = value[..^1];
        }

        return value;
    }

    private static long ParseNonnegativeInteger(ReadOnlySpan<byte> value)
    {
        if (!Utf8Parser.TryParse(value, out long parsed, out var consumed) ||
            consumed != value.Length ||
            parsed < 0)
        {
            throw new InvalidDataException("Git returned an invalid object-statistics count.");
        }

        return parsed;
    }

    private static void SetOnce(ref long? target, long value)
    {
        if (target is not null)
        {
            throw new InvalidDataException("Git returned a duplicate object-statistics field.");
        }

        target = value;
    }

    private static long Require(long? value, string key)
        => value ?? throw new InvalidDataException(
            $"Git object statistics omitted the required '{key}' field.");
}
