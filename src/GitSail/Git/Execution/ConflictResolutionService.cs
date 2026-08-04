using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Stages one exact conflict result through rollback-capable index and filtered-worktree operations.
/// </summary>
internal sealed class ConflictResolutionService
{
    private const int MaximumFilteredResultBytes = 1024 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RepositoryStatusService _statusService;

    /// <summary>
    /// Initializes exact conflict-result staging over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    internal ConflictResolutionService(
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
        _statusService = new RepositoryStatusService(
            installation,
            runner,
            environmentFactory,
            new PorcelainV2StatusParser());
    }

    /// <summary>
    /// Verifies live stages, stages the resolved blob, and atomically installs Git-filtered worktree bytes.
    /// </summary>
    /// <param name="repository">The canonical repository locations and object format.</param>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedEntry">The exact unmerged path and stages shown to the user.</param>
    /// <param name="resultMode">The selected regular or executable result mode.</param>
    /// <param name="resolvedContent">The exact marker-free clean blob content.</param>
    /// <param name="cancellationToken">Signals cancellation with index rollback before completion.</param>
    /// <returns>The successful Git operation output and aggregate warnings.</returns>
    internal async Task<GitOperationResult> ResolveAsync(
        RepositoryLocation repository,
        CanonicalDirectory workingDirectory,
        RepositoryStatusEntry expectedEntry,
        GitFileMode resultMode,
        ReadOnlyMemory<byte> resolvedContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedEntry);
        if (expectedEntry.Kind != RepositoryStatusEntryKind.Unmerged || expectedEntry.ConflictStages is null)
        {
            throw new ArgumentException("Conflict resolution requires an exact unmerged status entry.", nameof(expectedEntry));
        }

        if (resultMode is not (GitFileMode.RegularFile or GitFileMode.ExecutableFile))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultMode),
                "Built-in content resolution supports regular file modes only.");
        }

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.UpdateIndex,
            cancellationToken).ConfigureAwait(false);
        await ValidateLiveStagesAsync(
            repository,
            workingDirectory,
            expectedEntry,
            cancellationToken).ConfigureAwait(false);
        var hashResult = await HashBlobAsync(
            workingDirectory,
            resolvedContent,
            cancellationToken).ConfigureAwait(false);
        var resolvedIndexInput = ConflictIndexInfoBuilder.BuildResolved(
            expectedEntry.Path,
            resultMode,
            hashResult.ObjectId);
        var rollbackInput = ConflictIndexInfoBuilder.BuildUnmerged(
            expectedEntry.Path,
            expectedEntry.ConflictStages,
            repository.ObjectFormat);
        var indexReplaced = false;
        GitPath? temporaryPath = null;
        try
        {
            var updateResult = await UpdateIndexAsync(
                workingDirectory,
                resolvedIndexInput,
                cancellationToken).ConfigureAwait(false);
            indexReplaced = true;
            var checkoutResult = await CheckoutTemporaryAsync(
                repository,
                workingDirectory,
                expectedEntry.Path,
                cancellationToken).ConfigureAwait(false);
            temporaryPath = checkoutResult.TemporaryPath;
            var filteredContent = await RepositoryStateFileSystem.ReadIfExistsAsync(
                temporaryPath,
                MaximumFilteredResultBytes,
                cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Git's filtered conflict-result file disappeared before installation.");
            _ = await RepositoryStateFileSystem.DeleteIfExistsAsync(
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            temporaryPath = null;
            var destination = RepositoryWorkTreePathService.Resolve(repository, expectedEntry.Path);
            await RepositoryStateFileSystem.WriteWorkTreeFileAtomicallyAsync(
                destination,
                filteredContent,
                resultMode,
                cancellationToken).ConfigureAwait(false);
            indexReplaced = false;
            return new GitOperationResult(
                updateResult.StandardOutput,
                CombineWarnings(hashResult.StandardError, updateResult.StandardError, checkoutResult.StandardError));
        }
        catch (Exception originalException)
        {
            if (indexReplaced)
            {
                try
                {
                    _ = await UpdateIndexAsync(
                        workingDirectory,
                        rollbackInput,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    throw new RepositoryPreconditionException(
                        $"Conflict resolution failed ({originalException.Message}) and the original unmerged index stages could not be restored ({rollbackException.Message}).");
                }
            }

            throw;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    _ = await RepositoryStateFileSystem.DeleteIfExistsAsync(
                        temporaryPath,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private async Task ValidateLiveStagesAsync(
        RepositoryLocation repository,
        CanonicalDirectory workingDirectory,
        RepositoryStatusEntry expectedEntry,
        CancellationToken cancellationToken)
    {
        var snapshot = await _statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(1),
            cancellationToken).ConfigureAwait(false);
        var liveEntry = snapshot.Entries.FirstOrDefault(
            entry => entry.Path.Equals(expectedEntry.Path));
        if (liveEntry?.Kind != RepositoryStatusEntryKind.Unmerged ||
            liveEntry.ConflictStages != expectedEntry.ConflictStages)
        {
            throw new RepositoryPreconditionException(
                "The conflict stages changed after the resolution view was prepared; refresh before staging a result.");
        }
    }

    private async Task<(ObjectId ObjectId, ReadOnlyMemory<byte> StandardError)> HashBlobAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("hash-object"),
                ProcessArgument.Literal("-w"),
                ProcessArgument.Literal("--stdin"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            StandardInputSource.FromBytes(content.Span),
            OutputPolicy.Create(1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not write the resolved conflict blob.");
        }

        var output = TrimLineEnding(result.StandardOutput.Span);
        if (!ObjectId.TryParseHex(output, out var objectId) || objectId is null)
        {
            throw new InvalidDataException("Git returned an invalid resolved conflict blob identifier.");
        }

        return (objectId, result.StandardError);
    }

    private async Task<GitOperationResult> UpdateIndexAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> indexInfo,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--literal-pathspecs"),
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("update-index"),
                ProcessArgument.Literal("-z"),
                ProcessArgument.Literal("--index-info"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            StandardInputSource.FromBytes(indexInfo.Span),
            OutputPolicy.Create(1024 * 1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not update the resolved conflict index entry.");
        }

        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private async Task<(GitPath TemporaryPath, ReadOnlyMemory<byte> StandardError)> CheckoutTemporaryAsync(
        RepositoryLocation repository,
        CanonicalDirectory workingDirectory,
        GitPath path,
        CancellationToken cancellationToken)
    {
        var pathInput = PathspecInputBuilder.Build([path]);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--literal-pathspecs"),
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("checkout-index"),
                ProcessArgument.Literal("--force"),
                ProcessArgument.Literal("--temp"),
                ProcessArgument.Literal("-z"),
                ProcessArgument.Literal("--stdin"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            StandardInputSource.FromBytes(pathInput),
            OutputPolicy.Create(1024 * 1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not materialize the filtered conflict result.");
        }

        var temporaryName = ParseTemporaryName(result.StandardOutput.Span, pathInput);
        var relativeTemporaryPath = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(temporaryName)
            : GitPath.FromUnixBytes(Encoding.ASCII.GetBytes(temporaryName));
        return (
            RepositoryWorkTreePathService.Resolve(repository, relativeTemporaryPath),
            result.StandardError);
    }

    private static string ParseTemporaryName(
        ReadOnlySpan<byte> output,
        ReadOnlySpan<byte> pathInput)
    {
        if (output.IsEmpty || output[^1] != 0 || output[..^1].Contains((byte)0))
        {
            throw new InvalidDataException("Git returned an invalid checkout-index temporary record.");
        }

        var record = output[..^1];
        var separator = record.IndexOf((byte)'\t');
        if (separator <= 0 ||
            !record[(separator + 1)..].SequenceEqual(pathInput[..^1]))
        {
            throw new InvalidDataException("Git returned a mismatched checkout-index temporary path.");
        }

        var nameBytes = record[..separator];
        foreach (var value in nameBytes)
        {
            if (value is not (>= (byte)'a' and <= (byte)'z') and
                not (>= (byte)'A' and <= (byte)'Z') and
                not (>= (byte)'0' and <= (byte)'9') and
                not (byte)'.' and
                not (byte)'_' and
                not (byte)'-')
            {
                throw new InvalidDataException("Git returned an unsafe checkout-index temporary name.");
            }
        }

        return Encoding.ASCII.GetString(nameBytes);
    }

    private static ReadOnlyMemory<byte> CombineWarnings(params ReadOnlyMemory<byte>[] warnings)
    {
        var nonempty = warnings.Where(static warning => !warning.IsEmpty).ToArray();
        if (nonempty.Length == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var length = nonempty.Sum(static warning => warning.Length + 1) - 1;
        var result = new byte[length];
        var offset = 0;
        for (var index = 0; index < nonempty.Length; index++)
        {
            nonempty[index].Span.CopyTo(result.AsSpan(offset));
            offset += nonempty[index].Length;
            if (index < nonempty.Length - 1)
            {
                result[offset++] = (byte)'\n';
            }
        }

        return result;
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
