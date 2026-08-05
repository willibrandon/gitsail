using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Collections.Immutable;
using System.Globalization;
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
        => await ScanAsync(
            repository,
            workingDirectory,
            generation,
            [],
            GitDiffRuntimeConfiguration.Default,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Scans status through Git while restricting the request to exact native pathspecs.
    /// </summary>
    /// <param name="repository">The previously discovered repository locations.</param>
    /// <param name="workingDirectory">The canonical directory from which the repository was opened.</param>
    /// <param name="generation">The generation assigned to this scan.</param>
    /// <param name="pathspecs">The exact optional native pathspecs sent to Git.</param>
    /// <param name="cancellationToken">Signals scan cancellation.</param>
    /// <returns>The immutable path-restricted status snapshot.</returns>
    internal async Task<RepositoryStatusSnapshot> ScanAsync(
        RepositoryLocation repository,
        CanonicalDirectory workingDirectory,
        OperationGeneration generation,
        ImmutableArray<GitPath> pathspecs,
        CancellationToken cancellationToken)
        => await ScanAsync(
            repository,
            workingDirectory,
            generation,
            pathspecs,
            GitDiffRuntimeConfiguration.Default,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Scans status with explicit validated rename, copy, threshold, and exhaustive-search configuration.
    /// </summary>
    /// <param name="repository">The previously discovered repository locations.</param>
    /// <param name="workingDirectory">The canonical directory from which the repository was opened.</param>
    /// <param name="generation">The generation assigned to this scan.</param>
    /// <param name="pathspecs">The exact optional native pathspecs sent to Git.</param>
    /// <param name="configuration">The validated effective diff configuration.</param>
    /// <param name="cancellationToken">Signals scan cancellation.</param>
    /// <returns>The immutable path-restricted status snapshot.</returns>
    internal async Task<RepositoryStatusSnapshot> ScanAsync(
        RepositoryLocation repository,
        CanonicalDirectory workingDirectory,
        OperationGeneration generation,
        ImmutableArray<GitPath> pathspecs,
        GitDiffRuntimeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(configuration);

        for (var attempt = 0; attempt < MaximumStableScanAttempts; attempt++)
        {
            var before = await _preconditionService.CaptureOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var result = await RunStatusAsync(
                workingDirectory,
                pathspecs.IsDefault ? [] : pathspecs,
                configuration,
                cancellationToken).ConfigureAwait(false);
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

                if (!after.MatchesStatusHeadName(snapshot.HeadName))
                {
                    throw new InvalidDataException(
                        "Git status and the captured repository precondition reported different HEAD attachments.");
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
        ImmutableArray<GitPath> pathspecs,
        GitDiffRuntimeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        List<ProcessArgument> arguments =
        [
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("-c"),
            ProcessArgument.Literal($"status.renameLimit={configuration.RenameLimit.ToString(CultureInfo.InvariantCulture)}"),
            ProcessArgument.Literal("-c"),
            ProcessArgument.Literal($"status.renames={GetRenameConfiguration(configuration.RenameDetection)}"),
            ProcessArgument.Literal("status"),
            ProcessArgument.Literal("--porcelain=v2"),
            ProcessArgument.Literal("-z"),
            ProcessArgument.Literal("--branch"),
            ProcessArgument.Literal("--untracked-files=all"),
            configuration.RenameDetection == GitRenameDetectionMode.Disabled
                ? ProcessArgument.Literal("--no-renames")
                : ProcessArgument.Literal(
                    $"--find-renames={configuration.RenameThreshold.ToString(CultureInfo.InvariantCulture)}%"),
            ProcessArgument.Literal("--"),
            .. pathspecs.Select(ProcessArgument.Native),
        ];
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. arguments],
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

    private static string GetRenameConfiguration(GitRenameDetectionMode mode)
        => mode switch
        {
            GitRenameDetectionMode.Disabled => "false",
            GitRenameDetectionMode.Renames => "true",
            GitRenameDetectionMode.Copies => "copies",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
}
