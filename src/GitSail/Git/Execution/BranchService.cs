using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures exact branch/worktree state and performs revalidated Git-owned branch transactions.
/// </summary>
internal sealed class BranchService
{
    private const int MaximumBranchOutputBytes = 128 * 1024 * 1024;
    private const int MaximumWorktreeOutputBytes = 16 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private const int MaximumStableCaptureAttempts = 3;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RepositoryPreconditionService _preconditionService;

    /// <summary>
    /// Initializes branch capture and mutation over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    internal BranchService(
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
    }

    /// <summary>
    /// Captures one stable exact branch and linked-worktree catalog.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="cancellationToken">Signals catalog capture cancellation.</param>
    /// <returns>The stable catalog and its exact repository precondition.</returns>
    internal async Task<BranchCatalog> CaptureAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        for (var attempt = 0; attempt < MaximumStableCaptureAttempts; attempt++)
        {
            var before = await _preconditionService.CaptureOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var firstBranches = await ReadBranchesAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var firstWorktrees = await ReadWorktreesAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var secondBranches = await ReadBranchesAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var secondWorktrees = await ReadWorktreesAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var after = await _preconditionService.CaptureOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            if (before.Matches(after) &&
                firstBranches.Span.SequenceEqual(secondBranches.Span) &&
                firstWorktrees.Span.SequenceEqual(secondWorktrees.Span))
            {
                var worktrees = BranchCatalogParser.ParseWorktrees(secondWorktrees.Span);
                var branches = BranchCatalogParser.ParseBranches(secondBranches.Span, worktrees);
                return new BranchCatalog(after, branches, worktrees);
            }
        }

        throw new RepositoryPreconditionException(
            "Branches or linked worktrees continued changing while GitSail prepared the branch view; retry the refresh.");
    }

    /// <summary>
    /// Validates and normalizes one user-entered local branch name through Git.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="candidate">The user-entered branch name.</param>
    /// <param name="cancellationToken">Signals validation cancellation.</param>
    /// <returns>The exact normalized short and full local ref names.</returns>
    internal Task<ValidatedBranchName> ValidateLocalNameAsync(
        CanonicalDirectory workingDirectory,
        string candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return ValidateLocalNameAsync(
            workingDirectory,
            RefName.FromBytes(s_strictUtf8.GetBytes(candidate)),
            cancellationToken);
    }

    /// <summary>
    /// Validates and normalizes one exact local branch name proposal through Git.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="candidate">The exact proposed short branch name.</param>
    /// <param name="cancellationToken">Signals validation cancellation.</param>
    /// <returns>The exact normalized short and full local ref names.</returns>
    internal async Task<ValidatedBranchName> ValidateLocalNameAsync(
        CanonicalDirectory workingDirectory,
        RefName candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(candidate);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("check-ref-format"),
                ProcessArgument.Literal("--branch"),
                ProcessArgument.Native(candidate),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git rejected the local branch name.");
        }

        var normalizedBytes = TrimSingleLine(result.StandardOutput.Span);
        if (normalizedBytes.IsEmpty || normalizedBytes.Contains((byte)0))
        {
            throw new InvalidDataException("Git returned an invalid normalized local branch name.");
        }

        var shortName = RefName.FromBytes(normalizedBytes);
        return new ValidatedBranchName(shortName, CreateLocalFullName(shortName));
    }

    /// <summary>
    /// Preserves the complete tail after a remote name when proposing a local branch name.
    /// </summary>
    /// <param name="remoteBranch">The exact nonsymbolic remote-tracking source branch.</param>
    /// <returns>The exact unvalidated local name proposal.</returns>
    internal static RefName GetLocalNameProposal(BranchInfo remoteBranch)
    {
        ArgumentNullException.ThrowIfNull(remoteBranch);
        if (remoteBranch.Kind != BranchKind.RemoteTracking || remoteBranch.SymbolicTarget is not null)
        {
            throw new ArgumentException(
                "A local name can be proposed only from a nonsymbolic remote-tracking branch.",
                nameof(remoteBranch));
        }

        var bytes = remoteBranch.ShortName.GetBytes();
        var separator = bytes.IndexOf((byte)'/');
        if (separator < 1 || separator == bytes.Length - 1)
        {
            throw new InvalidDataException("The remote-tracking branch has no complete branch tail.");
        }

        return RefName.FromBytes(bytes[(separator + 1)..]);
    }

    /// <summary>
    /// Switches to an exact local branch after revalidating ref, worktree, HEAD, and index identities.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact branch catalog displayed to the user.</param>
    /// <param name="branch">The exact displayed local branch selection.</param>
    /// <param name="cancellationToken">Signals checkout cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal async Task<GitOperationResult> SwitchAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        BranchInfo branch,
        CancellationToken cancellationToken)
    {
        RequireLocalBranch(branch);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Checkout,
            cancellationToken).ConfigureAwait(false);
        var liveBranch = await RevalidateBranchAsync(
            workingDirectory,
            expectedCatalog,
            branch,
            cancellationToken).ConfigureAwait(false);
        if (liveBranch.IsCurrent)
        {
            return EmptyResult();
        }

        RejectOccupiedBranch(liveBranch, "switch to");
        return await RunCheckoutAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("switch"),
                ProcessArgument.Literal("--no-guess"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(liveBranch.ShortName),
            ],
            "Git could not switch to the selected branch.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates and switches to a local branch from an exact captured source branch.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact branch catalog displayed to the user.</param>
    /// <param name="name">The Git-validated destination local branch name.</param>
    /// <param name="startingPoint">The exact displayed source branch.</param>
    /// <param name="trackStartingPoint">Whether to configure the remote source as the direct upstream.</param>
    /// <param name="cancellationToken">Signals create-and-checkout cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal async Task<GitOperationResult> CreateAndSwitchAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        ValidatedBranchName name,
        BranchInfo startingPoint,
        bool trackStartingPoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(startingPoint);
        if (startingPoint.SymbolicTarget is not null)
        {
            throw new InvalidOperationException("A symbolic remote HEAD cannot be used as a branch source.");
        }

        if (trackStartingPoint && startingPoint.Kind != BranchKind.RemoteTracking)
        {
            throw new InvalidOperationException("Direct tracking requires a remote-tracking source branch.");
        }

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Checkout,
            cancellationToken).ConfigureAwait(false);
        var liveCatalog = await RevalidateCatalogAsync(
            workingDirectory,
            expectedCatalog,
            cancellationToken).ConfigureAwait(false);
        var liveSource = RequireMatchingBranch(liveCatalog, startingPoint);
        if (liveSource.SymbolicTarget is not null)
        {
            throw new RepositoryPreconditionException(
                "The selected branch source became symbolic; refresh before creating a branch.");
        }

        if (liveCatalog.Find(name.FullName) is not null)
        {
            throw new RepositoryPreconditionException(
                "The destination local branch now exists; refresh before choosing another name.");
        }

        return await RunCheckoutAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("switch"),
                ProcessArgument.Literal(trackStartingPoint ? "--track=direct" : "--no-track"),
                ProcessArgument.Literal("--create"),
                ProcessArgument.Native(name.ShortName),
                ProcessArgument.Native(liveSource.FullName),
            ],
            "Git could not create and switch to the local branch.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches HEAD at an exact captured branch target after revalidating repository identity.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact branch catalog displayed to the user.</param>
    /// <param name="startingPoint">The exact displayed source branch.</param>
    /// <param name="cancellationToken">Signals detached checkout cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal async Task<GitOperationResult> DetachAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        BranchInfo startingPoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startingPoint);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Checkout,
            cancellationToken).ConfigureAwait(false);
        var liveBranch = await RevalidateBranchAsync(
            workingDirectory,
            expectedCatalog,
            startingPoint,
            cancellationToken).ConfigureAwait(false);
        return await RunCheckoutAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("switch"),
                ProcessArgument.Literal("--detach"),
                ProcessArgument.Literal(liveBranch.TargetObjectId.ToString()),
            ],
            "Git could not detach HEAD at the selected commit.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames an exact local branch after validating destination absence and linked-worktree occupancy.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact branch catalog displayed to the user.</param>
    /// <param name="branch">The exact displayed local branch selection.</param>
    /// <param name="newName">The Git-validated destination local branch name.</param>
    /// <param name="cancellationToken">Signals rename cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal async Task<GitOperationResult> RenameAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        BranchInfo branch,
        ValidatedBranchName newName,
        CancellationToken cancellationToken)
    {
        RequireLocalBranch(branch);
        ArgumentNullException.ThrowIfNull(newName);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Checkout,
            cancellationToken).ConfigureAwait(false);
        var liveCatalog = await RevalidateCatalogAsync(
            workingDirectory,
            expectedCatalog,
            cancellationToken).ConfigureAwait(false);
        var liveBranch = RequireMatchingBranch(liveCatalog, branch);
        if (liveCatalog.Find(newName.FullName) is not null)
        {
            throw new RepositoryPreconditionException(
                "The destination local branch now exists; refresh before choosing another name.");
        }

        if (!liveBranch.IsCurrent)
        {
            RejectOccupiedBranch(liveBranch, "rename");
        }

        return await RunCheckoutAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("branch"),
                ProcessArgument.Literal("--move"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(liveBranch.ShortName),
                ProcessArgument.Native(newName.ShortName),
            ],
            "Git could not rename the selected branch.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes an exact unoccupied local branch using Git's selected mergedness policy.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact branch catalog displayed to the user.</param>
    /// <param name="branch">The exact displayed local branch selection.</param>
    /// <param name="mode">The safe or explicitly confirmed force policy.</param>
    /// <param name="cancellationToken">Signals deletion cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal async Task<GitOperationResult> DeleteAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        BranchInfo branch,
        BranchDeleteMode mode,
        CancellationToken cancellationToken)
    {
        RequireLocalBranch(branch);
        if (mode is not BranchDeleteMode.Safe and not BranchDeleteMode.Force)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Checkout,
            cancellationToken).ConfigureAwait(false);
        var liveBranch = await RevalidateBranchAsync(
            workingDirectory,
            expectedCatalog,
            branch,
            cancellationToken).ConfigureAwait(false);
        RejectOccupiedBranch(liveBranch, "delete");
        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>(mode == BranchDeleteMode.Safe ? 4 : 5);
        arguments.Add(ProcessArgument.Literal("branch"));
        arguments.Add(ProcessArgument.Literal("--delete"));
        if (mode == BranchDeleteMode.Force)
        {
            arguments.Add(ProcessArgument.Literal("--force"));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(liveBranch.ShortName));
        return await RunCheckoutAsync(
            workingDirectory,
            arguments.MoveToImmutable(),
            "Git could not delete the selected branch.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets the exact current branch to a selected commit using the confirmed reset mode.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact branch catalog displayed to the user.</param>
    /// <param name="currentBranch">The exact displayed current local branch.</param>
    /// <param name="targetObjectId">The exact commit resolved from the user's typed revision.</param>
    /// <param name="mode">The confirmed soft, mixed, or hard reset behavior.</param>
    /// <param name="cancellationToken">Signals reset cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal async Task<GitOperationResult> ResetCurrentAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        BranchInfo currentBranch,
        ObjectId targetObjectId,
        BranchResetMode mode,
        CancellationToken cancellationToken)
    {
        RequireLocalBranch(currentBranch);
        ArgumentNullException.ThrowIfNull(targetObjectId);
        if (!currentBranch.IsCurrent)
        {
            throw new InvalidOperationException("Only the current local branch can be reset through this transaction.");
        }

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Checkout,
            cancellationToken).ConfigureAwait(false);
        var liveBranch = await RevalidateBranchAsync(
            workingDirectory,
            expectedCatalog,
            currentBranch,
            cancellationToken).ConfigureAwait(false);
        if (!liveBranch.IsCurrent)
        {
            throw new RepositoryPreconditionException(
                "The current branch changed after the reset was prepared; refresh before resetting.");
        }

        var modeArgument = mode switch
        {
            BranchResetMode.Soft => "--soft",
            BranchResetMode.Mixed => "--mixed",
            BranchResetMode.Hard => "--hard",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return await RunCheckoutAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("reset"),
                ProcessArgument.Literal(modeArgument),
                ProcessArgument.Literal(targetObjectId.ToString()),
            ],
            "Git could not reset the current branch.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<BranchInfo> RevalidateBranchAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        BranchInfo expectedBranch,
        CancellationToken cancellationToken)
    {
        var liveCatalog = await RevalidateCatalogAsync(
            workingDirectory,
            expectedCatalog,
            cancellationToken).ConfigureAwait(false);
        return RequireMatchingBranch(liveCatalog, expectedBranch);
    }

    private async Task<BranchCatalog> RevalidateCatalogAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        var liveCatalog = await CaptureAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!expectedCatalog.Precondition.Matches(liveCatalog.Precondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD, its branch attachment, or the index changed after the branch view was prepared; refresh before continuing.");
        }

        return liveCatalog;
    }

    private async Task<ReadOnlyMemory<byte>> ReadBranchesAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("for-each-ref"),
                ProcessArgument.Literal("--format=%(refname)%00%(objectname)%00%(upstream)%00%(upstream:track)%00%(HEAD)%00%(symref)%00"),
                ProcessArgument.Literal("refs/heads/"),
                ProcessArgument.Literal("refs/remotes/"),
            ],
            MaximumBranchOutputBytes,
            "Git could not enumerate branches.",
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private async Task<ReadOnlyMemory<byte>> ReadWorktreesAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("worktree"),
                ProcessArgument.Literal("list"),
                ProcessArgument.Literal("--porcelain"),
                ProcessArgument.Literal("-z"),
            ],
            MaximumWorktreeOutputBytes,
            "Git could not enumerate linked worktrees.",
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private async Task<ProcessResult> RunReadAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        int outputLimit,
        string fallbackError,
        CancellationToken cancellationToken)
    {
        var invocationArguments = ImmutableArray.CreateBuilder<ProcessArgument>(arguments.Length + 1);
        invocationArguments.Add(ProcessArgument.Literal("--no-pager"));
        invocationArguments.AddRange(arguments);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            invocationArguments.MoveToImmutable(),
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(outputLimit, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, fallbackError);
        }

        return result;
    }

    private async Task<GitOperationResult> RunCheckoutAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        string fallbackError,
        CancellationToken cancellationToken)
    {
        var invocationArguments = ImmutableArray.CreateBuilder<ProcessArgument>(arguments.Length + 1);
        invocationArguments.Add(ProcessArgument.Literal("--no-pager"));
        invocationArguments.AddRange(arguments);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            invocationArguments.MoveToImmutable(),
            workingDirectory,
            _environmentFactory.CreateCheckoutEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(4 * 1024 * 1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, fallbackError);
        }

        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private static BranchInfo RequireMatchingBranch(BranchCatalog catalog, BranchInfo expectedBranch)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(expectedBranch);
        var liveBranch = catalog.Find(expectedBranch.FullName);
        if (liveBranch is null || !expectedBranch.Matches(liveBranch))
        {
            throw new RepositoryPreconditionException(
                "The selected branch, its target, tracking state, or worktree occupancy changed; refresh before continuing.");
        }

        return liveBranch;
    }

    private static void RequireLocalBranch(BranchInfo branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        if (branch.Kind != BranchKind.Local)
        {
            throw new ArgumentException("The operation requires a local branch.", nameof(branch));
        }
    }

    private static void RejectOccupiedBranch(BranchInfo branch, string operation)
    {
        if (!branch.OccupiedWorktrees.IsEmpty)
        {
            var paths = string.Join(", ", branch.OccupiedWorktrees.Select(static path => path.DisplayText));
            throw new InvalidOperationException(
                $"Cannot {operation} branch '{branch.ShortName.DisplayText}' because it is checked out at: {paths}");
        }
    }

    private static RefName CreateLocalFullName(RefName shortName)
    {
        ReadOnlySpan<byte> prefix = "refs/heads/"u8;
        var bytes = new byte[prefix.Length + shortName.GetBytes().Length];
        prefix.CopyTo(bytes);
        shortName.GetBytes().CopyTo(bytes.AsSpan(prefix.Length));
        return RefName.FromBytes(bytes);
    }

    private static ReadOnlySpan<byte> TrimSingleLine(ReadOnlySpan<byte> value)
    {
        if (!value.IsEmpty && value[^1] == (byte)'\n')
        {
            value = value[..^1];
            if (!value.IsEmpty && value[^1] == (byte)'\r')
            {
                value = value[..^1];
            }
        }

        if (value.Contains((byte)'\n') || value.Contains((byte)'\r'))
        {
            throw new InvalidDataException("Git returned more than one normalized branch-name line.");
        }

        return value;
    }

    private static GitOperationResult EmptyResult()
        => new(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static GitCommandException CreateCommandException(ProcessResult result, string fallback)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallback : error);
    }
}
