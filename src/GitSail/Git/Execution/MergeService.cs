using GitSail.Domain;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Prepares exact merge confirmations and executes Git-owned merge transactions.
/// </summary>
internal sealed class MergeService
{
    private const int MaximumOperationOutputBytes = 128 * 1024 * 1024;
    private const int MaximumUnmergedOutputBytes = 512 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly BranchService _branchService;
    private readonly RepositoryWorktreeFingerprintService _worktreeFingerprintService;

    /// <summary>
    /// Initializes merge preparation and execution over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    internal MergeService(
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
        _branchService = new BranchService(installation, runner, environmentFactory, coordinator);
        _worktreeFingerprintService = new RepositoryWorktreeFingerprintService(
            installation,
            runner,
            environmentFactory);
    }

    /// <summary>
    /// Captures a stable exact merge source, worktree, and reachability preview.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact branch catalog displayed to the user.</param>
    /// <param name="source">The exact displayed branch selected for merging.</param>
    /// <param name="cancellationToken">Signals merge-plan capture cancellation.</param>
    /// <returns>The exact confirmation snapshot for later revalidation.</returns>
    internal async Task<MergePlan> PrepareAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        BranchInfo source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ValidateSource(source);
        var firstCatalog = await _branchService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var firstSource = RequireCurrentSource(expectedCatalog, firstCatalog, source);
        var headObjectId = firstCatalog.Precondition.HeadObjectId
            ?? throw new MergeOperationException("An unborn HEAD cannot start a merge transaction.");
        var fingerprint = await _worktreeFingerprintService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var secondCatalog = await _branchService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var secondSource = RequireCurrentSource(expectedCatalog, secondCatalog, source);
        if (!firstCatalog.Precondition.Matches(secondCatalog.Precondition) ||
            !firstSource.Matches(secondSource))
        {
            throw new RepositoryPreconditionException(
                "HEAD, the index, or the selected merge source changed while GitSail prepared the confirmation; refresh and retry.");
        }

        var counts = await ReadReachabilityCountsAsync(
            workingDirectory,
            headObjectId,
            secondSource.TargetObjectId,
            cancellationToken).ConfigureAwait(false);
        var relationship = counts.IncomingOnly == 0
            ? MergeRelationship.AlreadyIntegrated
            : counts.CurrentOnly == 0
                ? MergeRelationship.FastForward
                : MergeRelationship.Diverged;
        return new MergePlan(
            secondCatalog.Precondition,
            fingerprint,
            secondSource,
            relationship,
            counts.CurrentOnly,
            counts.IncomingOnly);
    }

    /// <summary>
    /// Executes one exact confirmed merge and classifies Git's resulting repository state.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="plan">The exact merge confirmation displayed to the user.</param>
    /// <param name="options">The validated typed merge options.</param>
    /// <param name="cancellationToken">Signals merge execution cancellation.</param>
    /// <returns>The classified merge transition and exact operation output.</returns>
    internal async Task<MergeExecutionResult> ExecuteAsync(
        CanonicalDirectory workingDirectory,
        MergePlan plan,
        MergeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Merge,
            cancellationToken).ConfigureAwait(false);
        var firstCatalog = await _branchService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        RequireMatchingPlanCatalog(plan, firstCatalog);
        var liveFingerprint = await _worktreeFingerprintService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var secondCatalog = await _branchService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        RequireMatchingPlanCatalog(plan, secondCatalog);
        if (!firstCatalog.Precondition.Matches(secondCatalog.Precondition) ||
            !plan.WorktreeFingerprint.Matches(liveFingerprint))
        {
            throw new RepositoryPreconditionException(
                "HEAD, the index, or the worktree changed after the merge confirmation was shown; refresh and retry.");
        }

        var invocation = new ProcessInvocation(
            _installation.Executable,
            BuildArguments(plan.Source.TargetObjectId, options),
            workingDirectory,
            _environmentFactory.CreateCheckoutEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumOperationOutputBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        var hasUnmergedEntries = await HasUnmergedEntriesAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var hasMergeHead = await HasMergeHeadAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var operation = new GitOperationResult(result.StandardOutput, result.StandardError);
        if (result.ExitCode != 0)
        {
            if (hasUnmergedEntries)
            {
                return new MergeExecutionResult(MergeOutcome.Conflicts, operation, hasMergeHead);
            }

            throw CreateCommandException(result, "Git could not merge the selected commit.");
        }

        if (hasUnmergedEntries)
        {
            throw new InvalidDataException("Git reported a successful merge while leaving unmerged index entries.");
        }

        if (options.Squash)
        {
            return new MergeExecutionResult(MergeOutcome.SquashPrepared, operation, hasMergeHead);
        }

        return new MergeExecutionResult(
            hasMergeHead ? MergeOutcome.StoppedBeforeCommit : MergeOutcome.Completed,
            operation,
            hasMergeHead);
    }

    private async Task<(int CurrentOnly, int IncomingOnly)> ReadReachabilityCountsAsync(
        CanonicalDirectory workingDirectory,
        ObjectId current,
        ObjectId incoming,
        CancellationToken cancellationToken)
    {
        var range = $"{current}...{incoming}";
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("rev-list"),
                ProcessArgument.Literal("--left-right"),
                ProcessArgument.Literal("--count"),
                ProcessArgument.Literal(range),
            ],
            1024,
            cancellationToken).ConfigureAwait(false);
        var bytes = TrimLineEnding(result.StandardOutput.Span);
        var separator = bytes.IndexOf((byte)'\t');
        if (separator <= 0 || separator == bytes.Length - 1 ||
            !int.TryParse(bytes[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var currentOnly) ||
            !int.TryParse(bytes[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var incomingOnly) ||
            currentOnly < 0 || incomingOnly < 0)
        {
            throw new InvalidDataException("Git returned invalid merge reachability counts.");
        }

        return (currentOnly, incomingOnly);
    }

    private async Task<bool> HasUnmergedEntriesAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("ls-files"),
                ProcessArgument.Literal("--unmerged"),
                ProcessArgument.Literal("-z"),
            ],
            MaximumUnmergedOutputBytes,
            cancellationToken).ConfigureAwait(false);
        return !result.StandardOutput.IsEmpty;
    }

    private async Task<bool> HasMergeHeadAsync(
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
                ProcessArgument.Literal("MERGE_HEAD"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is 0 or 1)
        {
            return result.ExitCode == 0;
        }

        throw CreateCommandException(result, "Git could not inspect pending merge state.");
    }

    private async Task<ProcessResult> RunReadAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyList<ProcessArgument> arguments,
        int maximumOutputBytes,
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
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(maximumOutputBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not inspect merge state.");
        }

        return result;
    }

    private static ImmutableArray<ProcessArgument> BuildArguments(
        ObjectId sourceObjectId,
        MergeOptions options)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("merge"),
            ProcessArgument.Literal("--progress"),
            ProcessArgument.Literal("--no-edit"),
        };
        AddFastForward(arguments, options.FastForwardMode);
        if (options.Squash)
        {
            arguments.Add(ProcessArgument.Literal("--squash"));
        }

        if (options.StopBeforeCommit)
        {
            arguments.Add(ProcessArgument.Literal("--no-commit"));
        }

        AddOverride(arguments, options.AutoStash, "--autostash", "--no-autostash");
        AddOverride(
            arguments,
            options.RerereAutoUpdate,
            "--rerere-autoupdate",
            "--no-rerere-autoupdate");
        AddOverride(
            arguments,
            options.VerifySignatures,
            "--verify-signatures",
            "--no-verify-signatures");
        if (options.Strategy != MergeStrategy.Default)
        {
            arguments.Add(ProcessArgument.Literal(options.Strategy switch
            {
                MergeStrategy.Ort => "--strategy=ort",
                MergeStrategy.Resolve => "--strategy=resolve",
                MergeStrategy.Ours => "--strategy=ours",
                MergeStrategy.Subtree => "--strategy=subtree",
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            }));
        }

        if (options.ConflictPreference != MergeConflictPreference.Default)
        {
            arguments.Add(ProcessArgument.Literal(options.ConflictPreference switch
            {
                MergeConflictPreference.Ours => "--strategy-option=ours",
                MergeConflictPreference.Theirs => "--strategy-option=theirs",
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            }));
        }

        arguments.Add(ProcessArgument.Literal(sourceObjectId.ToString()));
        return [.. arguments];
    }

    private static void AddFastForward(
        List<ProcessArgument> arguments,
        MergeFastForwardMode mode)
    {
        var value = mode switch
        {
            MergeFastForwardMode.Default => null,
            MergeFastForwardMode.FastForwardOnly => "--ff-only",
            MergeFastForwardMode.NoFastForward => "--no-ff",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        if (value is not null)
        {
            arguments.Add(ProcessArgument.Literal(value));
        }
    }

    private static void AddOverride(
        List<ProcessArgument> arguments,
        GitOptionOverride option,
        string enabledArgument,
        string disabledArgument)
    {
        var value = option switch
        {
            GitOptionOverride.Configured => null,
            GitOptionOverride.Enabled => enabledArgument,
            GitOptionOverride.Disabled => disabledArgument,
            _ => throw new ArgumentOutOfRangeException(nameof(option)),
        };
        if (value is not null)
        {
            arguments.Add(ProcessArgument.Literal(value));
        }
    }

    private static BranchInfo RequireCurrentSource(
        BranchCatalog expectedCatalog,
        BranchCatalog liveCatalog,
        BranchInfo expectedSource)
    {
        if (!expectedCatalog.Precondition.Matches(liveCatalog.Precondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD or the index changed after the branch view was prepared; refresh before merging.");
        }

        var expectedCatalogSource = expectedCatalog.Find(expectedSource.FullName);
        var liveSource = liveCatalog.Find(expectedSource.FullName);
        if (expectedCatalogSource is null ||
            liveSource is null ||
            !expectedCatalogSource.Matches(expectedSource) ||
            !liveSource.Matches(expectedSource))
        {
            throw new RepositoryPreconditionException(
                "The selected merge source changed after the branch view was prepared; refresh before merging.");
        }

        return liveSource;
    }

    private static void RequireMatchingPlanCatalog(MergePlan plan, BranchCatalog liveCatalog)
    {
        if (!plan.Precondition.Matches(liveCatalog.Precondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD or the index changed after the merge confirmation was shown; refresh and retry.");
        }

        var liveSource = liveCatalog.Find(plan.Source.FullName);
        if (liveSource is null || !liveSource.Matches(plan.Source))
        {
            throw new RepositoryPreconditionException(
                "The selected source branch moved after the merge confirmation was shown; refresh and retry.");
        }
    }

    private static void ValidateSource(BranchInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SymbolicTarget is not null)
        {
            throw new MergeOperationException("A symbolic remote HEAD cannot be merged directly.");
        }

        if (source.IsCurrent)
        {
            throw new MergeOperationException("The current branch cannot be merged into itself.");
        }
    }

    private static GitCommandException CreateCommandException(ProcessResult result, string fallbackError)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallbackError : error);
    }

    private static ReadOnlySpan<byte> TrimLineEnding(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && bytes[^1] is (byte)'\r' or (byte)'\n')
        {
            bytes = bytes[..^1];
        }

        return bytes;
    }
}
