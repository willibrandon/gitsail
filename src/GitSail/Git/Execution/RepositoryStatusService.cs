using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Produces generation-stamped repository status snapshots through Git porcelain version 2.
/// </summary>
internal sealed class RepositoryStatusService
{
    private const int MaximumStableScanAttempts = 3;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly PorcelainV2StatusParser _parser;
    private readonly RepositoryPreconditionService _preconditionService;

    /// <summary>
    /// Initializes repository status scanning over explicit Git execution and parsing services.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="parser">The bounded porcelain version 2 parser.</param>
    internal RepositoryStatusService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        PorcelainV2StatusParser parser)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(parser);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _parser = parser;
        _preconditionService = new RepositoryPreconditionService(
            installation,
            runner,
            environmentFactory);
    }

    /// <summary>
    /// Scans all tracked and untracked paths in one repository operation generation.
    /// </summary>
    /// <param name="repository">The previously discovered repository locations.</param>
    /// <param name="workingDirectory">The canonical directory from which the repository was opened.</param>
    /// <param name="generation">The generation assigned to this scan.</param>
    /// <param name="cancellationToken">Signals scan cancellation.</param>
    /// <returns>The immutable complete status snapshot.</returns>
    internal async Task<RepositoryStatusSnapshot> ScanAsync(
        RepositoryLocation repository,
        CanonicalDirectory workingDirectory,
        OperationGeneration generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        for (var attempt = 0; attempt < MaximumStableScanAttempts; attempt++)
        {
            var before = await _preconditionService.CaptureOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var result = await RunStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            var after = await _preconditionService.CaptureOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            if (before.Matches(after))
            {
                var snapshot = _parser.Parse(result.StandardOutput.Span, repository, generation);
                if (!Equals(snapshot.HeadObjectId, after.HeadObjectId))
                {
                    throw new InvalidDataException(
                        "Git status and the captured repository precondition reported different HEAD objects.");
                }

                return snapshot with
                {
                    Precondition = after,
                };
            }
        }

        throw new RepositoryPreconditionException(
            "HEAD or the index continued changing while GitSail captured status; retry the refresh.");
    }

    private async Task<ProcessResult> RunStatusAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("status"),
                ProcessArgument.Literal("--porcelain=v2"),
                ProcessArgument.Literal("-z"),
                ProcessArgument.Literal("--branch"),
                ProcessArgument.Literal("--untracked-files=all"),
                ProcessArgument.Literal("--renames"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(64 * 1024 * 1024, 1024 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git status failed." : error);
        }

        return result;
    }
}
