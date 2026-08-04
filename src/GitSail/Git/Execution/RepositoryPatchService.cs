using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Validates and applies exact generated patches to the index or worktree in an explicit direction.
/// </summary>
internal sealed class RepositoryPatchService
{
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RepositoryPreconditionService _preconditionService;

    /// <summary>
    /// Initializes exact patch application over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    internal RepositoryPatchService(
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
        _preconditionService = new RepositoryPreconditionService(installation, runner, environmentFactory);
    }

    /// <summary>
    /// Validates and stages one exact patch through Git's forward cached apply transaction.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="patch">The nonempty exact patch bytes.</param>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>The successful apply output and warnings.</returns>
    internal Task<GitOperationResult> StageAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> patch,
        CancellationToken cancellationToken)
        => ApplyAsync(workingDirectory, patch, cached: true, reverse: false, cancellationToken);

    /// <summary>
    /// Validates and unstages one exact patch through Git's reverse cached apply transaction.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="patch">The nonempty exact patch bytes.</param>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>The successful reverse-apply output and warnings.</returns>
    internal Task<GitOperationResult> UnstageAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> patch,
        CancellationToken cancellationToken)
        => ApplyAsync(workingDirectory, patch, cached: true, reverse: true, cancellationToken);

    /// <summary>
    /// Validates and reverts one exact patch through Git's reverse worktree apply transaction.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="patch">The nonempty exact patch bytes.</param>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>The successful reverse-apply output and its live repository precondition.</returns>
    internal async Task<RevertOperationResult> RevertAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> patch,
        CancellationToken cancellationToken)
    {
        ValidatePatchArguments(workingDirectory, patch);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.ApplyPatch,
            cancellationToken).ConfigureAwait(false);
        var precondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var result = await ApplyUnderLeaseAsync(
            workingDirectory,
            patch,
            cached: false,
            reverse: true,
            cancellationToken).ConfigureAwait(false);
        return new RevertOperationResult(result, precondition);
    }

    /// <summary>
    /// Validates and reapplies one exact reverted patch to the worktree for one-level undo.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="patch">The nonempty exact patch bytes retained from the revert.</param>
    /// <param name="expectedPrecondition">The live HEAD and index identity captured before the revert.</param>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>The successful forward-apply output and warnings.</returns>
    internal async Task<GitOperationResult> UndoRevertAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> patch,
        RepositoryPrecondition expectedPrecondition,
        CancellationToken cancellationToken)
    {
        ValidatePatchArguments(workingDirectory, patch);
        ArgumentNullException.ThrowIfNull(expectedPrecondition);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.ApplyPatch,
            cancellationToken).ConfigureAwait(false);
        var livePrecondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!expectedPrecondition.Matches(livePrecondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD or the staged index changed after the revert; refresh and review before restoring discarded worktree content.");
        }

        return await ApplyUnderLeaseAsync(
            workingDirectory,
            patch,
            cached: false,
            reverse: false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether live HEAD, index, and worktree content still permit one exact revert undo.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="patch">The nonempty exact patch bytes retained from the revert.</param>
    /// <param name="expectedPrecondition">The live HEAD and index identity captured before the revert.</param>
    /// <param name="cancellationToken">Signals eligibility-check cancellation.</param>
    /// <returns><see langword="true"/> only when every undo precondition still matches.</returns>
    internal async Task<bool> IsUndoRevertEligibleAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> patch,
        RepositoryPrecondition expectedPrecondition,
        CancellationToken cancellationToken)
    {
        ValidatePatchArguments(workingDirectory, patch);
        ArgumentNullException.ThrowIfNull(expectedPrecondition);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.ApplyPatch,
            cancellationToken).ConfigureAwait(false);
        var livePrecondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!expectedPrecondition.Matches(livePrecondition))
        {
            return false;
        }

        try
        {
            _ = await RunApplyAsync(
                workingDirectory,
                patch,
                cached: false,
                reverse: false,
                checkOnly: true,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (GitCommandException)
        {
            return false;
        }
    }

    private async Task<GitOperationResult> ApplyAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> patch,
        bool cached,
        bool reverse,
        CancellationToken cancellationToken)
    {
        ValidatePatchArguments(workingDirectory, patch);

        await using var lease = await _coordinator.AcquireAsync(
            cached ? RepositoryMutationPurpose.UpdateIndex : RepositoryMutationPurpose.ApplyPatch,
            cancellationToken).ConfigureAwait(false);
        return await ApplyUnderLeaseAsync(
            workingDirectory,
            patch,
            cached,
            reverse,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitOperationResult> ApplyUnderLeaseAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> patch,
        bool cached,
        bool reverse,
        CancellationToken cancellationToken)
    {
        _ = await RunApplyAsync(
            workingDirectory,
            patch,
            cached,
            reverse,
            checkOnly: true,
            cancellationToken).ConfigureAwait(false);
        return await RunApplyAsync(
            workingDirectory,
            patch,
            cached,
            reverse,
            checkOnly: false,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePatchArguments(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> patch)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        if (patch.IsEmpty)
        {
            throw new ArgumentException("A patch mutation requires nonempty exact bytes.", nameof(patch));
        }
    }

    private async Task<GitOperationResult> RunApplyAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> patch,
        bool cached,
        bool reverse,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("apply"),
            ProcessArgument.Literal("--whitespace=nowarn"),
        };
        if (cached)
        {
            arguments.Add(ProcessArgument.Literal("--cached"));
        }

        if (reverse)
        {
            arguments.Add(ProcessArgument.Literal("--reverse"));
        }

        if (checkOnly)
        {
            arguments.Add(ProcessArgument.Literal("--check"));
        }

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. arguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            StandardInputSource.FromBytes(patch.Span),
            OutputPolicy.Create(1024 * 1024, 4 * 1024 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error)
                    ? checkOnly ? "Git rejected the patch preflight." : "Git patch mutation failed."
                    : error);
        }

        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }
}
