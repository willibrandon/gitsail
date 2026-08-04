using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures exact in-progress merge state and delegates a confirmed abort to Git porcelain.
/// </summary>
internal sealed class MergeAbortService
{
    private const int MaximumMergeHeadBytes = 64 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RepositoryPreconditionService _preconditionService;
    private readonly RawDiffService _rawDiffService;
    private readonly GitPath _mergeHeadPath;

    /// <summary>
    /// Initializes merge-abort inspection and execution over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    /// <param name="mergeHeadPath">The verified repository-state path returned by Git for MERGE_HEAD.</param>
    internal MergeAbortService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator,
        GitPath mergeHeadPath)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(mergeHeadPath);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _preconditionService = new RepositoryPreconditionService(
            installation,
            runner,
            environmentFactory);
        _rawDiffService = new RawDiffService(installation, runner, environmentFactory);
        _mergeHeadPath = mergeHeadPath;
    }

    /// <summary>
    /// Captures a stable exact merge-abort warning for the displayed repository generation.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedPrecondition">The exact HEAD and index state captured by the displayed status.</param>
    /// <param name="cancellationToken">Signals merge-state inspection cancellation.</param>
    /// <returns>The exact active merge warning, or <see langword="null"/> when no merge is active.</returns>
    internal async Task<MergeAbortWarning?> FindWarningAsync(
        CanonicalDirectory workingDirectory,
        RepositoryPrecondition expectedPrecondition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedPrecondition);
        var firstState = await ReadMergeStateAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (firstState is null)
        {
            var repeatedAbsence = await ReadMergeStateAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            if (repeatedAbsence is null)
            {
                return null;
            }

            throw new RepositoryPreconditionException(
                "A merge started while GitSail captured repository state; refresh and retry.");
        }

        await ValidatePreconditionAsync(
            workingDirectory,
            expectedPrecondition,
            cancellationToken).ConfigureAwait(false);
        var firstWorkTreeFingerprint = await CaptureWorkTreeFingerprintAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var secondState = await ReadMergeStateAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!MergeStatesMatch(firstState, secondState))
        {
            throw new RepositoryPreconditionException(
                "The in-progress merge changed while GitSail captured it; refresh and retry.");
        }

        await ValidatePreconditionAsync(
            workingDirectory,
            expectedPrecondition,
            cancellationToken).ConfigureAwait(false);
        var finalWorkTreeFingerprint = await CaptureWorkTreeFingerprintAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var finalState = await ReadMergeStateAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!MergeStatesMatch(secondState, finalState) ||
            !firstWorkTreeFingerprint.AsSpan().SequenceEqual(finalWorkTreeFingerprint))
        {
            throw new RepositoryPreconditionException(
                "The in-progress merge changed while GitSail captured it; refresh and retry.");
        }

        var capturedState = finalState!.Value;
        return new MergeAbortWarning(
            expectedPrecondition,
            capturedState.MergeHeads,
            finalWorkTreeFingerprint,
            capturedState.MergeAutostash);
    }

    /// <summary>
    /// Aborts only the exact merge state that the user inspected and confirmed.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="confirmedWarning">The exact merge warning displayed by the confirmation dialog.</param>
    /// <param name="cancellationToken">Signals abort cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal async Task<GitOperationResult> AbortAsync(
        CanonicalDirectory workingDirectory,
        MergeAbortWarning confirmedWarning,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(confirmedWarning);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Abort,
            cancellationToken).ConfigureAwait(false);
        var liveWarning = await FindWarningAsync(
            workingDirectory,
            confirmedWarning.Precondition,
            cancellationToken).ConfigureAwait(false);
        if (liveWarning is null)
        {
            throw new RepositoryPreconditionException(
                "The merge ended after the abort confirmation was prepared; refresh before continuing.");
        }

        if (!confirmedWarning.Matches(liveWarning))
        {
            throw new RepositoryPreconditionException(
                "The in-progress merge changed after the abort confirmation was prepared; refresh and confirm the current merge.");
        }

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("merge"),
                ProcessArgument.Literal("--abort"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git could not abort the merge." : error);
        }

        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private async Task ValidatePreconditionAsync(
        CanonicalDirectory workingDirectory,
        RepositoryPrecondition expectedPrecondition,
        CancellationToken cancellationToken)
    {
        var livePrecondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!expectedPrecondition.Matches(livePrecondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD, its branch attachment, or the index changed after the merge view was prepared; refresh before aborting.");
        }
    }

    private async Task<(ImmutableArray<ObjectId> MergeHeads, ObjectId? MergeAutostash)?> ReadMergeStateAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var mergeHeadContent = await RepositoryStateFileSystem.ReadIfExistsAsync(
            _mergeHeadPath,
            MaximumMergeHeadBytes,
            cancellationToken).ConfigureAwait(false);
        if (mergeHeadContent is null)
        {
            return null;
        }

        var mergeAutostash = await ReadMergeAutostashAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        return (ParseMergeHeads(mergeHeadContent), mergeAutostash);
    }

    private async Task<byte[]> CaptureWorkTreeFingerprintAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        using var diff = await _rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(0),
            contextLines: 0,
            cancellationToken).ConfigureAwait(false);
        return await diff.ComputeSha256Async(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ObjectId?> ReadMergeAutostashAsync(
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
                ProcessArgument.Literal("MERGE_AUTOSTASH"),
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
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error)
                    ? "Git could not resolve MERGE_AUTOSTASH."
                    : error);
        }

        return ParseSingleObjectId(result.StandardOutput.Span, "MERGE_AUTOSTASH");
    }

    private static ImmutableArray<ObjectId> ParseMergeHeads(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            throw new InvalidDataException("Git's MERGE_HEAD state file is empty.");
        }

        var builder = ImmutableArray.CreateBuilder<ObjectId>();
        var remaining = content;
        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOf((byte)'\n');
            var line = separator < 0 ? remaining : remaining[..separator];
            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (!ObjectId.TryParseHex(line, out var objectId) || objectId is null)
            {
                throw new InvalidDataException("Git's MERGE_HEAD state file contains an invalid object identifier.");
            }

            builder.Add(objectId);
            if (separator < 0)
            {
                break;
            }

            remaining = remaining[(separator + 1)..];
        }

        return builder.ToImmutable();
    }

    private static ObjectId ParseSingleObjectId(ReadOnlySpan<byte> content, string stateName)
    {
        if (!content.IsEmpty && content[^1] == (byte)'\n')
        {
            content = content[..^1];
            if (!content.IsEmpty && content[^1] == (byte)'\r')
            {
                content = content[..^1];
            }
        }

        if (!ObjectId.TryParseHex(content, out var objectId) || objectId is null)
        {
            throw new InvalidDataException($"Git's {stateName} state file contains an invalid object identifier.");
        }

        return objectId;
    }

    private static bool MergeStatesMatch(
        (ImmutableArray<ObjectId> MergeHeads, ObjectId? MergeAutostash)? first,
        (ImmutableArray<ObjectId> MergeHeads, ObjectId? MergeAutostash)? second)
        => first is null
            ? second is null
            : second is not null &&
                first.Value.MergeHeads.AsSpan().SequenceEqual(second.Value.MergeHeads.AsSpan()) &&
                Equals(first.Value.MergeAutostash, second.Value.MergeAutostash);
}
