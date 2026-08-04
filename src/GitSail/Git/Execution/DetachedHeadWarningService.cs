using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Resolves Git's detached-commit warning policy and describes the exact detached HEAD.
/// </summary>
internal sealed class DetachedHeadWarningService
{
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;

    /// <summary>
    /// Initializes detached-warning inspection over the sole child-process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal DetachedHeadWarningService(
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
    /// Finds the enabled warning for an exact detached HEAD or reports that none applies.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="precondition">The exact live HEAD object and symbolic attachment state.</param>
    /// <param name="cancellationToken">Signals configuration inspection cancellation.</param>
    /// <returns>The exact warning when detached warnings are enabled; otherwise <see langword="null"/>.</returns>
    internal async Task<DetachedHeadWarning?> FindAsync(
        CanonicalDirectory workingDirectory,
        RepositoryPrecondition precondition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(precondition);
        if (precondition.HeadObjectId is null || precondition.HeadName is not null)
        {
            return null;
        }

        return await IsWarningEnabledAsync(workingDirectory, cancellationToken).ConfigureAwait(false)
            ? new DetachedHeadWarning(precondition.HeadObjectId)
            : null;
    }

    private async Task<bool> IsWarningEnabledAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--null"),
                ProcessArgument.Literal("--type=bool"),
                ProcessArgument.Literal("--get"),
                ProcessArgument.Literal("gui.warndetachedcommit"),
            ],
            workingDirectory,
            _environmentFactory.CreateConfigurationReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024, 64 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && result.StandardOutput.IsEmpty)
        {
            return true;
        }

        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error)
                    ? "Git could not resolve gui.warndetachedcommit."
                    : error);
        }

        var output = result.StandardOutput.Span;
        if (output.SequenceEqual("true\0"u8))
        {
            return true;
        }

        if (output.SequenceEqual("false\0"u8))
        {
            return false;
        }

        throw new InvalidDataException(
            "Git returned an invalid canonical gui.warndetachedcommit value.");
    }
}
