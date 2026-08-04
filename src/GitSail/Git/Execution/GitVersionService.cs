using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Resolves Git and obtains its version through the typed process boundary.
/// </summary>
internal sealed class GitVersionService
{
    private readonly ExecutableResolver _resolver;
    private readonly IChildProcessRunner _runner;

    /// <summary>
    /// Initializes Git version discovery over explicit execution services.
    /// </summary>
    /// <param name="resolver">The trusted executable resolver.</param>
    /// <param name="runner">The sole child-process runner.</param>
    internal GitVersionService(ExecutableResolver resolver, IChildProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(runner);
        _resolver = resolver;
        _runner = runner;
    }

    /// <summary>
    /// Resolves Git and parses the exact output from <c>git --version</c>.
    /// </summary>
    /// <param name="workingDirectory">The canonical working directory for the read-only probe.</param>
    /// <param name="cancellationToken">Signals probe cancellation.</param>
    /// <returns>The resolved Git installation.</returns>
    internal async Task<GitInstallation> GetAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var executable = _resolver.Resolve(ProgramKind.Git);
        var environment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
            new KeyValuePair<string, string>("GIT_PAGER", "cat"),
            new KeyValuePair<string, string>("GIT_OPTIONAL_LOCKS", "0"),
        ]);
        var invocation = new ProcessInvocation(
            executable,
            [ProcessArgument.Literal("--version")],
            workingDirectory,
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(4096, 4096));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git version discovery failed." : error);
        }

        if (!GitVersion.TryParse(result.StandardOutput.Span, out var version))
        {
            throw new InvalidDataException("Git returned an unrecognized version response.");
        }

        return new GitInstallation(executable, version);
    }
}
