using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Resolves only design-allowlisted direct repository files through Git path semantics.
/// </summary>
internal sealed class RepositoryStatePathService
{
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;

    /// <summary>
    /// Initializes allowlisted repository-state path resolution.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal RepositoryStatePathService(
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
    }

    /// <summary>
    /// Resolves one allowlisted state file to its canonical absolute native path.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="stateFile">The closed-set repository state file identifier.</param>
    /// <param name="cancellationToken">Signals path-resolution cancellation.</param>
    /// <returns>The exact absolute path emitted by Git.</returns>
    internal async Task<GitPath> ResolveAsync(
        CanonicalDirectory workingDirectory,
        RepositoryStateFile stateFile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var repositoryPath = GetRepositoryPath(stateFile);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("rev-parse"),
                ProcessArgument.Literal("--path-format=absolute"),
                ProcessArgument.Literal("--git-path"),
                ProcessArgument.Literal(repositoryPath),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 64 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error)
                    ? "Git could not resolve the repository state path."
                    : error);
        }

        var output = TrimLineEnding(result.StandardOutput.Span);
        if (output.IsEmpty)
        {
            throw new InvalidDataException("Git returned an empty repository state path.");
        }

        if (OperatingSystem.IsWindows())
        {
            var path = Encoding.UTF8.GetString(output);
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidDataException("Git returned a non-absolute repository state path.");
            }

            return GitPath.FromWindowsPath(path);
        }

        if (output[0] != (byte)'/')
        {
            throw new InvalidDataException("Git returned a non-absolute repository state path.");
        }

        return GitPath.FromUnixBytes(output);
    }

    private static string GetRepositoryPath(RepositoryStateFile stateFile)
        => stateFile switch
        {
            RepositoryStateFile.Message => "GITGUI_MSG",
            RepositoryStateFile.MessageBackup => "GITGUI_BCK",
            RepositoryStateFile.EditMessage => "GITGUI_EDITMSG",
            RepositoryStateFile.PrepareCommitMessage => "PREPARE_COMMIT_MSG",
            RepositoryStateFile.CommitEditMessage => "COMMIT_EDITMSG",
            RepositoryStateFile.MergeMessage => "MERGE_MSG",
            RepositoryStateFile.SquashMessage => "SQUASH_MSG",
            RepositoryStateFile.MergeHead => "MERGE_HEAD",
            RepositoryStateFile.IndexLock => "index.lock",
            _ => throw new ArgumentOutOfRangeException(nameof(stateFile)),
        };

    private static ReadOnlySpan<byte> TrimLineEnding(ReadOnlySpan<byte> output)
    {
        if (!output.IsEmpty && output[^1] == (byte)'\n')
        {
            output = output[..^1];
            if (OperatingSystem.IsWindows() && !output.IsEmpty && output[^1] == (byte)'\r')
            {
                output = output[..^1];
            }
        }

        return output;
    }
}
