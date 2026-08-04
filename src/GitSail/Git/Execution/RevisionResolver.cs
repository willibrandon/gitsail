using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Resolves typed revision candidates to commits without allowing option interpretation.
/// </summary>
internal sealed class RevisionResolver
{
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;

    /// <summary>
    /// Initializes revision resolution over explicit Git execution services.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal RevisionResolver(
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
    /// Validates and peels one candidate to an exact commit object identifier.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="revision">The untrusted typed revision candidate.</param>
    /// <param name="cancellationToken">Signals resolution cancellation.</param>
    /// <returns>The candidate bound to its exact commit object identifier.</returns>
    internal async Task<ResolvedRevision> ResolveCommitAsync(
        CanonicalDirectory workingDirectory,
        Revision revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(revision);

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("rev-parse"),
                ProcessArgument.Literal("--verify"),
                ProcessArgument.Literal("--end-of-options"),
                ProcessArgument.Literal(revision.Value + "^{commit}"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(4096, 64 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git could not resolve the revision to a commit." : error);
        }

        var output = result.StandardOutput.Span;
        if (!output.IsEmpty && output[^1] == (byte)'\n')
        {
            output = output[..^1];
            if (OperatingSystem.IsWindows() && !output.IsEmpty && output[^1] == (byte)'\r')
            {
                output = output[..^1];
            }
        }

        if (!ObjectId.TryParseHex(output, out var objectId))
        {
            throw new InvalidDataException("Git returned an invalid resolved object identifier.");
        }

        return new ResolvedRevision(revision, objectId!);
    }
}
