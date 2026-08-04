using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Loads explicit Git configuration values with their scope and origin provenance.
/// </summary>
internal sealed class GitConfigurationService
{
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly GitConfigurationParser _parser;

    /// <summary>
    /// Initializes configuration loading over explicit Git execution and parsing services.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="parser">The bounded configuration response parser.</param>
    internal GitConfigurationService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        GitConfigurationParser parser)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(parser);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _parser = parser;
    }

    /// <summary>
    /// Loads every visible configuration entry without collapsing precedence or duplicate values.
    /// </summary>
    /// <param name="workingDirectory">The canonical directory whose repository configuration is visible.</param>
    /// <param name="cancellationToken">Signals configuration loading cancellation.</param>
    /// <returns>The ordered explicit configuration entries reported by Git.</returns>
    internal async Task<ImmutableArray<GitConfigurationEntry>> LoadAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--null"),
                ProcessArgument.Literal("--list"),
                ProcessArgument.Literal("--show-origin"),
                ProcessArgument.Literal("--show-scope"),
            ],
            workingDirectory,
            _environmentFactory.CreateConfigurationReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(64 * 1024 * 1024, 1024 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git configuration loading failed." : error);
        }

        return _parser.Parse(result.StandardOutput.Span);
    }
}
