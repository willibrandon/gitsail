using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Performs revalidated Git-owned linked-worktree creation and management.
/// </summary>
internal sealed class WorktreeService
{
    private const int MaximumOutputBytes = 64 * 1024 * 1024;
    private const int MaximumReasonCharacters = 16 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly BranchService _branchService;

    /// <summary>
    /// Initializes linked-worktree operations over shared Git and mutation boundaries.
    /// </summary>
    /// <param name="installation">The resolved compatible Git installation.</param>
    /// <param name="runner">The sole shell-free child-process boundary.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    /// <param name="branchService">The stable branch and worktree catalog service.</param>
    internal WorktreeService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator,
        BranchService branchService)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(branchService);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _branchService = branchService;
    }

    /// <summary>
    /// Creates one linked worktree from an exact displayed branch or object.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact branch and worktree catalog shown to the user.</param>
    /// <param name="request">The target, starting point, HEAD mode, tracking, and lock choices.</param>
    /// <param name="cancellationToken">Signals worktree creation cancellation.</param>
    /// <returns>The canonical new worktree and exact bounded Git output.</returns>
    internal async Task<WorktreeCreationResult> AddAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        WorktreeAddRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(request);
        ValidateReason(request.LockReason);
        var target = new RepositoryTargetPlanner(workingDirectory).Prepare(request.TargetDirectory);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Worktree,
            cancellationToken).ConfigureAwait(false);
        var liveCatalog = await RevalidateCatalogAsync(
            workingDirectory,
            expectedCatalog,
            cancellationToken).ConfigureAwait(false);
        var startingPoint = RequireMatchingBranch(liveCatalog, request.StartingPoint);
        EnsureTargetIsUnassigned(liveCatalog, target.TargetPath);
        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>();
        arguments.Add(ProcessArgument.Literal("--no-pager"));
        arguments.Add(ProcessArgument.Literal("worktree"));
        arguments.Add(ProcessArgument.Literal("add"));
        arguments.Add(ProcessArgument.Literal("--quiet"));
        arguments.Add(ProcessArgument.Literal("--checkout"));
        if (request.LockAfterCreation)
        {
            arguments.Add(ProcessArgument.Literal("--lock"));
            if (!string.IsNullOrEmpty(request.LockReason))
            {
                arguments.Add(ProcessArgument.Literal("--reason"));
                arguments.Add(ProcessArgument.Literal(request.LockReason));
            }
        }

        AddHeadModeArguments(arguments, liveCatalog, startingPoint, request);
        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(target.TargetPath));
        arguments.Add(request.Mode switch
        {
            WorktreeAddMode.ExistingBranch => ProcessArgument.Native(startingPoint.ShortName),
            WorktreeAddMode.NewBranch => ProcessArgument.Native(startingPoint.FullName),
            WorktreeAddMode.Detached => ProcessArgument.Literal(startingPoint.TargetObjectId.ToString()),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "Unknown worktree mode."),
        });
        var result = await RunAsync(
            workingDirectory,
            arguments.ToImmutable(),
            _environmentFactory.CreateCheckoutEnvironment(),
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, "Git could not create the linked worktree.");
        return new WorktreeCreationResult(
            CanonicalDirectory.Create(target.ManagedTargetPath),
            ToOperationResult(result));
    }

    /// <summary>
    /// Moves one exact linked worktree to a new canonical target without force.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact catalog shown to the user.</param>
    /// <param name="worktree">The exact linked worktree selected for movement.</param>
    /// <param name="targetDirectory">The absolute or current-worktree-relative new location.</param>
    /// <param name="cancellationToken">Signals worktree movement cancellation.</param>
    /// <returns>The canonical new location and exact bounded Git output.</returns>
    internal async Task<WorktreeCreationResult> MoveAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        WorktreeInfo worktree,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(worktree);
        var target = new RepositoryTargetPlanner(workingDirectory).Prepare(targetDirectory);
        if (target.ExistedBeforeOperation)
        {
            throw new WorktreeOperationException("Choose a new worktree destination that does not already exist.");
        }

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Worktree,
            cancellationToken).ConfigureAwait(false);
        var liveCatalog = await RevalidateCatalogAsync(
            workingDirectory,
            expectedCatalog,
            cancellationToken).ConfigureAwait(false);
        var liveWorktree = RequireMutableLinkedWorktree(liveCatalog, worktree);
        if (liveWorktree.IsLocked)
        {
            throw new WorktreeOperationException("Unlock the linked worktree before moving it.");
        }

        EnsureTargetIsUnassigned(liveCatalog, target.TargetPath);
        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("worktree"),
                ProcessArgument.Literal("move"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(liveWorktree.Path),
                ProcessArgument.Native(target.TargetPath),
            ],
            _environmentFactory.CreateCheckoutEnvironment(),
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, "Git could not move the linked worktree.");
        return new WorktreeCreationResult(
            CanonicalDirectory.Create(target.ManagedTargetPath),
            ToOperationResult(result));
    }

    /// <summary>
    /// Locks one exact linked worktree with an optional literal reason.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact catalog shown to the user.</param>
    /// <param name="worktree">The exact linked worktree selected for locking.</param>
    /// <param name="reason">The optional literal lock reason.</param>
    /// <param name="cancellationToken">Signals lock cancellation.</param>
    /// <returns>The exact bounded Git output.</returns>
    internal Task<GitOperationResult> LockAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        WorktreeInfo worktree,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        return ChangeLockAsync(
            workingDirectory,
            expectedCatalog,
            worktree,
            lockWorktree: true,
            reason,
            cancellationToken);
    }

    /// <summary>
    /// Unlocks one exact linked worktree after revalidating its identity.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact catalog shown to the user.</param>
    /// <param name="worktree">The exact linked worktree selected for unlocking.</param>
    /// <param name="cancellationToken">Signals unlock cancellation.</param>
    /// <returns>The exact bounded Git output.</returns>
    internal Task<GitOperationResult> UnlockAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        WorktreeInfo worktree,
        CancellationToken cancellationToken)
        => ChangeLockAsync(
            workingDirectory,
            expectedCatalog,
            worktree,
            lockWorktree: false,
            reason: null,
            cancellationToken);

    /// <summary>
    /// Captures exact status and submodule data for an explicit removal confirmation.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="expectedCatalog">The exact catalog shown to the user.</param>
    /// <param name="worktree">The exact linked worktree selected for removal.</param>
    /// <param name="cancellationToken">Signals removal inspection cancellation.</param>
    /// <returns>The exact plan that must be revalidated before removal.</returns>
    internal async Task<WorktreeRemovalPlan> PrepareRemovalAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        WorktreeInfo worktree,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(worktree);
        var liveCatalog = await RevalidateCatalogAsync(
            workingDirectory,
            expectedCatalog,
            cancellationToken).ConfigureAwait(false);
        var liveWorktree = RequireMutableLinkedWorktree(liveCatalog, worktree);
        if (liveWorktree.IsLocked)
        {
            throw new WorktreeOperationException("Unlock the linked worktree before removing it.");
        }

        var inspection = await CaptureRemovalInspectionAsync(
            liveWorktree,
            cancellationToken).ConfigureAwait(false);
        return new WorktreeRemovalPlan(
            liveCatalog,
            liveWorktree,
            inspection.Status,
            inspection.Submodules);
    }

    /// <summary>
    /// Removes one exact reviewed linked worktree through Git.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="plan">The exact status and submodule plan reviewed by the user.</param>
    /// <param name="force">Whether the user explicitly confirmed deletion of retained worktree content.</param>
    /// <param name="cancellationToken">Signals removal cancellation.</param>
    /// <returns>The exact bounded Git output.</returns>
    internal async Task<GitOperationResult> RemoveAsync(
        CanonicalDirectory workingDirectory,
        WorktreeRemovalPlan plan,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.RequiresForce && !force)
        {
            throw new WorktreeOperationException(
                "This linked worktree contains files or submodules; confirm force removal to delete them.");
        }

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Worktree,
            cancellationToken).ConfigureAwait(false);
        var liveCatalog = await RevalidateCatalogAsync(
            workingDirectory,
            plan.Catalog,
            cancellationToken).ConfigureAwait(false);
        var liveWorktree = RequireMutableLinkedWorktree(liveCatalog, plan.Worktree);
        if (liveWorktree.IsLocked)
        {
            throw new WorktreeOperationException("Unlock the linked worktree before removing it.");
        }

        var inspection = await CaptureRemovalInspectionAsync(
            liveWorktree,
            cancellationToken).ConfigureAwait(false);
        if (!inspection.Status.AsSpan().SequenceEqual(plan.Status.AsSpan()) ||
            !inspection.Submodules.AsSpan().SequenceEqual(plan.SubmoduleStatus.AsSpan()))
        {
            throw new RepositoryPreconditionException(
                "The linked worktree contents changed after confirmation; inspect it again before removal.");
        }

        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>();
        arguments.Add(ProcessArgument.Literal("--no-pager"));
        arguments.Add(ProcessArgument.Literal("worktree"));
        arguments.Add(ProcessArgument.Literal("remove"));
        if (force)
        {
            arguments.Add(ProcessArgument.Literal("--force"));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(liveWorktree.Path));
        var result = await RunAsync(
            workingDirectory,
            arguments.ToImmutable(),
            _environmentFactory.CreateCheckoutEnvironment(),
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, "Git could not remove the linked worktree.");
        return ToOperationResult(result);
    }

    /// <summary>
    /// Captures Git's exact dry-run list of stale linked-worktree records.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="cancellationToken">Signals prune preview cancellation.</param>
    /// <returns>The repository precondition and exact bounded dry-run output.</returns>
    internal async Task<WorktreePrunePlan> PreparePruneAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var catalog = await _branchService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var result = await RunPrunePreviewAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, "Git could not inspect stale linked-worktree records.");
        return new WorktreePrunePlan(
            catalog.Precondition,
            [.. result.StandardOutput.Span],
            [.. result.StandardError.Span]);
    }

    /// <summary>
    /// Prunes only the stale linked-worktree records in the reviewed dry-run output.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="plan">The exact dry-run output reviewed by the user.</param>
    /// <param name="cancellationToken">Signals prune cancellation.</param>
    /// <returns>The exact bounded Git output.</returns>
    internal async Task<GitOperationResult> PruneAsync(
        CanonicalDirectory workingDirectory,
        WorktreePrunePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Worktree,
            cancellationToken).ConfigureAwait(false);
        var catalog = await _branchService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!plan.Precondition.Matches(catalog.Precondition))
        {
            throw new RepositoryPreconditionException(
                "Repository state changed after the prune preview; inspect stale worktrees again.");
        }

        var preview = await RunPrunePreviewAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(preview, "Git could not revalidate stale linked-worktree records.");
        if (!preview.StandardOutput.Span.SequenceEqual(plan.StandardOutput.AsSpan()) ||
            !preview.StandardError.Span.SequenceEqual(plan.StandardError.AsSpan()))
        {
            throw new RepositoryPreconditionException(
                "The stale linked-worktree list changed after confirmation; inspect it again before pruning.");
        }

        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("worktree"),
                ProcessArgument.Literal("prune"),
                ProcessArgument.Literal("--verbose"),
            ],
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, "Git could not prune stale linked-worktree records.");
        return ToOperationResult(result);
    }

    /// <summary>
    /// Asks Git to repair one existing absolute or current-worktree-relative worktree path.
    /// </summary>
    /// <param name="workingDirectory">The canonical current worktree directory.</param>
    /// <param name="path">The existing worktree directory selected by the user.</param>
    /// <param name="cancellationToken">Signals repair cancellation.</param>
    /// <returns>The exact bounded Git output.</returns>
    internal async Task<GitOperationResult> RepairAsync(
        CanonicalDirectory workingDirectory,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var managedPath = Path.GetFullPath(path, GetManagedPath(workingDirectory));
        var directory = CanonicalDirectory.Create(managedPath);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Worktree,
            cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("worktree"),
                ProcessArgument.Literal("repair"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(CreateNativePath(directory)),
            ],
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, "Git could not repair the linked-worktree connection.");
        return ToOperationResult(result);
    }

    private async Task<GitOperationResult> ChangeLockAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        WorktreeInfo worktree,
        bool lockWorktree,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(worktree);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Worktree,
            cancellationToken).ConfigureAwait(false);
        var liveCatalog = await RevalidateCatalogAsync(
            workingDirectory,
            expectedCatalog,
            cancellationToken).ConfigureAwait(false);
        var liveWorktree = RequireMutableLinkedWorktree(liveCatalog, worktree);
        if (liveWorktree.IsLocked == lockWorktree)
        {
            throw new WorktreeOperationException(lockWorktree
                ? "The selected linked worktree is already locked."
                : "The selected linked worktree is already unlocked.");
        }

        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>();
        arguments.Add(ProcessArgument.Literal("--no-pager"));
        arguments.Add(ProcessArgument.Literal("worktree"));
        arguments.Add(ProcessArgument.Literal(lockWorktree ? "lock" : "unlock"));
        if (lockWorktree && !string.IsNullOrEmpty(reason))
        {
            arguments.Add(ProcessArgument.Literal("--reason"));
            arguments.Add(ProcessArgument.Literal(reason));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(liveWorktree.Path));
        var result = await RunAsync(
            workingDirectory,
            arguments.ToImmutable(),
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(result, lockWorktree
            ? "Git could not lock the linked worktree."
            : "Git could not unlock the linked worktree.");
        return ToOperationResult(result);
    }

    private async Task<(ImmutableArray<byte> Status, ImmutableArray<byte> Submodules)>
        CaptureRemovalInspectionAsync(
            WorktreeInfo worktree,
            CancellationToken cancellationToken)
    {
        CanonicalDirectory directory;
        try
        {
            directory = CanonicalDirectory.Create(worktree.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WorktreeOperationException(
                $"The selected worktree directory is unavailable; use prune for a missing worktree. {exception.Message}");
        }

        var statusTask = RunAsync(
            directory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("status"),
                ProcessArgument.Literal("--porcelain=v2"),
                ProcessArgument.Literal("-z"),
                ProcessArgument.Literal("--untracked-files=all"),
                ProcessArgument.Literal("--ignored=matching"),
                ProcessArgument.Literal("--ignore-submodules=none"),
            ],
            _environmentFactory.CreateRepositoryReadEnvironment(),
            cancellationToken);
        var submoduleTask = RunAsync(
            directory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("submodule"),
                ProcessArgument.Literal("status"),
                ProcessArgument.Literal("--recursive"),
            ],
            _environmentFactory.CreateRepositoryReadEnvironment(),
            cancellationToken);
        await Task.WhenAll(statusTask, submoduleTask).ConfigureAwait(false);
        ThrowIfFailed(statusTask.Result, "Git could not inspect linked-worktree status.");
        ThrowIfFailed(submoduleTask.Result, "Git could not inspect linked-worktree submodules.");
        return ([.. statusTask.Result.StandardOutput.Span], [.. submoduleTask.Result.StandardOutput.Span]);
    }

    private Task<ProcessResult> RunPrunePreviewAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
        => RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("worktree"),
                ProcessArgument.Literal("prune"),
                ProcessArgument.Literal("--dry-run"),
                ProcessArgument.Literal("--verbose"),
            ],
            _environmentFactory.CreateRepositoryReadEnvironment(),
            cancellationToken);

    private async Task<BranchCatalog> RevalidateCatalogAsync(
        CanonicalDirectory workingDirectory,
        BranchCatalog expectedCatalog,
        CancellationToken cancellationToken)
    {
        var liveCatalog = await _branchService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!expectedCatalog.Precondition.Matches(liveCatalog.Precondition) ||
            expectedCatalog.Branches.Length != liveCatalog.Branches.Length ||
            expectedCatalog.Worktrees.Length != liveCatalog.Worktrees.Length ||
            !expectedCatalog.Branches.Zip(liveCatalog.Branches).All(
                static pair => pair.First.Matches(pair.Second)) ||
            !expectedCatalog.Worktrees.Zip(liveCatalog.Worktrees).All(
                static pair => pair.First.Matches(pair.Second)))
        {
            throw new RepositoryPreconditionException(
                "Branches or linked worktrees changed after display; refresh before continuing.");
        }

        return liveCatalog;
    }

    private async Task<ProcessResult> RunAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        ChildEnvironment environment,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            arguments,
            workingDirectory,
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumOutputBytes, MaximumOutputBytes));
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static void AddHeadModeArguments(
        ImmutableArray<ProcessArgument>.Builder arguments,
        BranchCatalog catalog,
        BranchInfo startingPoint,
        WorktreeAddRequest request)
    {
        switch (request.Mode)
        {
            case WorktreeAddMode.ExistingBranch:
                if (startingPoint.Kind != BranchKind.Local || !startingPoint.OccupiedWorktrees.IsEmpty)
                {
                    throw new WorktreeOperationException(
                        "An existing branch worktree requires an unoccupied local branch.");
                }

                if (request.NewBranchName is not null || request.TrackStartingPoint)
                {
                    throw new WorktreeOperationException(
                        "Existing-branch mode cannot create or track another branch.");
                }

                break;
            case WorktreeAddMode.NewBranch:
                if (request.NewBranchName is null)
                {
                    throw new WorktreeOperationException("Enter and validate a new local branch name.");
                }

                if (catalog.Find(request.NewBranchName.FullName) is not null)
                {
                    throw new WorktreeOperationException("The requested new local branch already exists.");
                }

                if (request.TrackStartingPoint && startingPoint.Kind != BranchKind.RemoteTracking)
                {
                    throw new WorktreeOperationException(
                        "Direct tracking requires a remote-tracking starting point.");
                }

                arguments.Add(ProcessArgument.Literal(
                    request.TrackStartingPoint ? "--track" : "--no-track"));
                arguments.Add(ProcessArgument.Literal("-b"));
                arguments.Add(ProcessArgument.Native(request.NewBranchName.ShortName));
                break;
            case WorktreeAddMode.Detached:
                if (request.NewBranchName is not null || request.TrackStartingPoint)
                {
                    throw new WorktreeOperationException(
                        "Detached mode cannot create or track a branch.");
                }

                arguments.Add(ProcessArgument.Literal("--detach"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "Unknown worktree mode.");
        }
    }

    private static BranchInfo RequireMatchingBranch(BranchCatalog catalog, BranchInfo expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var live = catalog.Find(expected.FullName);
        if (live is null || !expected.Matches(live) || live.SymbolicTarget is not null)
        {
            throw new RepositoryPreconditionException(
                "The selected branch changed after display; refresh before creating a worktree.");
        }

        return live;
    }

    private static WorktreeInfo RequireMutableLinkedWorktree(
        BranchCatalog catalog,
        WorktreeInfo expected)
    {
        if (catalog.Worktrees.IsEmpty || catalog.Worktrees[0].Path.Equals(expected.Path))
        {
            throw new WorktreeOperationException("The main worktree cannot be moved, locked, or removed.");
        }

        var live = catalog.Worktrees.FirstOrDefault(item => item.Path.Equals(expected.Path));
        if (live is null || !expected.Matches(live))
        {
            throw new RepositoryPreconditionException(
                "The selected linked worktree changed after display; refresh before continuing.");
        }

        return live;
    }

    private static void EnsureTargetIsUnassigned(BranchCatalog catalog, GitPath target)
    {
        if (catalog.Worktrees.Any(worktree => worktree.Path.Equals(target)))
        {
            throw new WorktreeOperationException("The target is already assigned to a worktree.");
        }
    }

    private static void ValidateReason(string? reason)
    {
        if (reason is null)
        {
            return;
        }

        if (reason.Contains('\0', StringComparison.Ordinal))
        {
            throw new WorktreeOperationException("A worktree lock reason cannot contain NUL.");
        }

        if (reason.Length > MaximumReasonCharacters)
        {
            throw new WorktreeOperationException(
                $"A worktree lock reason cannot exceed {MaximumReasonCharacters} characters.");
        }
    }

    private static void ThrowIfFailed(ProcessResult result, string operation)
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

        throw new WorktreeOperationException(
            error.Length == 0 ? $"{operation} Exit code: {result.ExitCode}." : $"{operation} {error}");
    }

    private static GitOperationResult ToOperationResult(ProcessResult result)
        => new(result.StandardOutput, result.StandardError);

    private static string GetManagedPath(CanonicalDirectory directory)
        => directory.Kind == NativePathKind.WindowsUtf16
            ? directory.GetWindowsPath()
            : s_strictUtf8.GetString(directory.GetUnixBytes());

    private static GitPath CreateNativePath(CanonicalDirectory directory)
        => directory.Kind == NativePathKind.WindowsUtf16
            ? GitPath.FromWindowsPath(directory.GetWindowsPath())
            : GitPath.FromUnixBytes(directory.GetUnixBytes());
}
