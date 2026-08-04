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
    private readonly RepositoryMutationCoordinator _coordinator;

    /// <summary>
    /// Initializes index mutation over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    internal IndexMutationService(
        GitInstallation installation,
        IChildProcessRunner runner,
        RepositoryMutationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(coordinator);
        _installation = installation;
        _runner = runner;
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

    private async Task<GitOperationResult> RunMutationAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyCollection<GitPath> paths,
        string[] commandArguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var pathspecInput = PathspecInputBuilder.Build(paths);
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
            CreateMutationEnvironment(),
            StandardInputSource.FromBytes(pathspecInput),
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

    private static ChildEnvironment CreateMutationEnvironment()
        => ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
            new KeyValuePair<string, string>("GIT_PAGER", "cat"),
        ]);
}
