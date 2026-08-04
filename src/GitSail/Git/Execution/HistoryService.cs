using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Globalization;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures structured commit history and exact immutable commit patches through Git.
/// </summary>
internal sealed class HistoryService
{
    private const int MaximumCatalogBytes = 512 * 1024 * 1024;
    private const int MaximumPatchBytes = 1024 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private const string HistoryFormat = "%H%x00%P%x00%an%x00%ae%x00%aI%x00%D%x00%G?%x00%s%x00%b%x00";
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly HistoryLogParser _parser;

    /// <summary>
    /// Initializes structured history capture over the typed child-process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal HistoryService(
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
        _parser = new HistoryLogParser();
    }

    /// <summary>
    /// Captures one bounded structured history request without parsing human-readable Git output.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="query">The typed revision, path, and count request.</param>
    /// <param name="cancellationToken">Signals history capture cancellation.</param>
    /// <returns>The ordered structured commit catalog.</returns>
    internal async Task<HistoryCatalog> CaptureAsync(
        CanonicalDirectory workingDirectory,
        HistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(query);
        if (query.MaximumCommitCount is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "History count must be between 1 and 1,000,000 commits.");
        }

        if (query.RevisionRange is null &&
            !await HasHeadCommitAsync(workingDirectory, cancellationToken).ConfigureAwait(false))
        {
            return new HistoryCatalog([]);
        }

        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("log"),
            ProcessArgument.Literal("--no-color"),
            ProcessArgument.Literal("--date-order"),
            ProcessArgument.Literal("--decorate=full"),
            ProcessArgument.Literal("--encoding=UTF-8"),
            ProcessArgument.Literal($"--max-count={query.MaximumCommitCount.ToString(CultureInfo.InvariantCulture)}"),
            ProcessArgument.Literal($"--format={HistoryFormat}"),
            ProcessArgument.Literal("-z"),
        };
        if (query.RevisionRange is not null)
        {
            arguments.Add(ProcessArgument.Literal("--end-of-options"));
            arguments.Add(ProcessArgument.Literal(query.RevisionRange.Value));
        }

        if (!query.Pathspecs.IsEmpty)
        {
            arguments.Add(ProcessArgument.Literal("--"));
            arguments.AddRange(query.Pathspecs.Select(ProcessArgument.Native));
        }

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. arguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumCatalogBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not load commit history.");
        }

        return new HistoryCatalog(_parser.Parse(result.StandardOutput.Span));
    }

    /// <summary>
    /// Captures the exact immutable details and patch for one selected commit object.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="commit">The exact selected commit object identifier.</param>
    /// <param name="cancellationToken">Signals patch capture cancellation.</param>
    /// <returns>The bounded raw commit presentation bytes.</returns>
    internal async Task<ReadOnlyMemory<byte>> ShowAsync(
        CanonicalDirectory workingDirectory,
        ObjectId commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(commit);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("show"),
                ProcessArgument.Literal("--no-color"),
                ProcessArgument.Literal("--no-ext-diff"),
                ProcessArgument.Literal("--no-textconv"),
                ProcessArgument.Literal("--encoding=UTF-8"),
                ProcessArgument.Literal("--decorate=full"),
                ProcessArgument.Literal("--date=iso-strict"),
                ProcessArgument.Literal("--format=fuller"),
                ProcessArgument.Literal("--stat"),
                ProcessArgument.Literal("--patch"),
                ProcessArgument.Literal("--binary"),
                ProcessArgument.Literal("--full-index"),
                ProcessArgument.Literal("--end-of-options"),
                ProcessArgument.Native(commit),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumPatchBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not show the selected commit.");
        }

        return result.StandardOutput;
    }

    private static GitCommandException CreateCommandException(ProcessResult result, string fallback)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(result.ExitCode, string.IsNullOrEmpty(error) ? fallback : error);
    }

    private async Task<bool> HasHeadCommitAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("rev-parse"),
                ProcessArgument.Literal("--verify"),
                ProcessArgument.Literal("--quiet"),
                ProcessArgument.Literal("--end-of-options"),
                ProcessArgument.Literal("HEAD^{commit}"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(4096, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }
}
