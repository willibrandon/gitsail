using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Stages and unstages exact selected paths through NUL-delimited Git stdin protocols.
/// </summary>
internal sealed class IndexMutationService
{
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;

    /// <summary>
    /// Initializes index mutation over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    internal IndexMutationService(
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
    }

    /// <summary>
    /// Stages the exact selected paths, including additions, changes, and deletions.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="paths">The nonempty exact path selection.</param>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>The successful operation output and warnings.</returns>
    internal Task<GitOperationResult> StageAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyCollection<GitPath> paths,
        CancellationToken cancellationToken)
        => RunMutationAsync(
            workingDirectory,
            paths,
            ["add", "--pathspec-from-file=-", "--pathspec-file-nul"],
            cancellationToken);

    /// <summary>
    /// Stages every worktree change, including untracked paths and deletions.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>The successful operation output and warnings.</returns>
    internal Task<GitOperationResult> StageAllAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
        => RunMutationAsync(
            workingDirectory,
            ["add", "--all"],
            StandardInputSource.Empty(),
            cancellationToken);

    /// <summary>
    /// Unstages exact selected paths to HEAD or removes them from an unborn index.
    /// </summary>
    /// <param name="snapshot">The current precondition snapshot.</param>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="paths">The nonempty exact path selection.</param>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>The successful operation output and warnings.</returns>
    internal Task<GitOperationResult> UnstageAsync(
        RepositoryStatusSnapshot snapshot,
        CanonicalDirectory workingDirectory,
        IReadOnlyCollection<GitPath> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return RunMutationAsync(
            workingDirectory,
            paths,
            snapshot.HeadObjectId is null
                ? ["rm", "--cached", "-r", "--quiet", "--ignore-unmatch", "--pathspec-from-file=-", "--pathspec-file-nul"]
                : ["reset", "--quiet", "--pathspec-from-file=-", "--pathspec-file-nul", "HEAD"],
            cancellationToken);
    }

    /// <summary>
    /// Unstages every index entry to HEAD or clears an unborn index.
    /// </summary>
    /// <param name="snapshot">The current precondition snapshot.</param>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>The successful operation output and warnings.</returns>
    internal Task<GitOperationResult> UnstageAllAsync(
        RepositoryStatusSnapshot snapshot,
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return RunMutationAsync(
            workingDirectory,
            snapshot.HeadObjectId is null
                ? ["rm", "--cached", "-r", "--quiet", "--ignore-unmatch", "--", "."]
                : ["reset", "--quiet", "HEAD", "--", "."],
            StandardInputSource.Empty(),
            cancellationToken);
    }

    private async Task<GitOperationResult> RunMutationAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyCollection<GitPath> paths,
        string[] commandArguments,
        CancellationToken cancellationToken)
    {
        var pathspecInput = PathspecInputBuilder.Build(paths);
        return await RunMutationAsync(
            workingDirectory,
            commandArguments,
            StandardInputSource.FromBytes(pathspecInput),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitOperationResult> RunMutationAsync(
        CanonicalDirectory workingDirectory,
        string[] commandArguments,
        StandardInputSource standardInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(commandArguments);
        ArgumentNullException.ThrowIfNull(standardInput);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.UpdateIndex,
            cancellationToken).ConfigureAwait(false);
        var arguments = new List<ProcessArgument>(commandArguments.Length + 2)
        {
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
        };
        arguments.AddRange(commandArguments.Select(ProcessArgument.Literal));
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. arguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            standardInput,
            OutputPolicy.Create(1024 * 1024, 4 * 1024 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git index mutation failed." : error);
        }

        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

}
