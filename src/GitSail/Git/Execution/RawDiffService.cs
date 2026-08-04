using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures applicable repository patches as bounded exact bytes without path argv filtering.
/// </summary>
internal sealed class RawDiffService
{
    private const int SpoolMemoryThresholdBytes = 1024 * 1024;
    private const int MaximumPatchBytes = 1024 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;

    /// <summary>
    /// Initializes raw patch capture over the sole typed child-process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal RawDiffService(
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
    /// Captures a complete worktree or index patch and builds its exact file index.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="target">The repository side to compare.</param>
    /// <param name="generation">The generation assigned to the captured patch.</param>
    /// <param name="cancellationToken">Signals capture cancellation.</param>
    /// <returns>An owned raw diff document that the caller must dispose.</returns>
    internal async Task<RawDiffDocument> CaptureAsync(
        CanonicalDirectory workingDirectory,
        RawDiffTarget target,
        OperationGeneration generation,
        CancellationToken cancellationToken)
        => await CaptureAsync(
            workingDirectory,
            target,
            generation,
            contextLines: 3,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Captures a complete patch with an explicit nonnegative unified-context line count.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="target">The repository side to compare.</param>
    /// <param name="generation">The generation assigned to the captured patch.</param>
    /// <param name="contextLines">The explicit number of unchanged lines around each change.</param>
    /// <param name="cancellationToken">Signals capture cancellation.</param>
    /// <returns>An owned raw diff document that the caller must dispose.</returns>
    internal async Task<RawDiffDocument> CaptureAsync(
        CanonicalDirectory workingDirectory,
        RawDiffTarget target,
        OperationGeneration generation,
        int contextLines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        if (target is not RawDiffTarget.WorkTree and not RawDiffTarget.Index)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        if (contextLines is < 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextLines),
                "Diff context must be between 0 and 100000 lines.");
        }

        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("-c"),
            ProcessArgument.Literal("core.quotePath=true"),
            ProcessArgument.Literal("diff"),
            ProcessArgument.Literal("--patch"),
            ProcessArgument.Literal("--raw"),
            ProcessArgument.Literal("-z"),
            ProcessArgument.Literal("--no-color"),
            ProcessArgument.Literal("--no-ext-diff"),
            ProcessArgument.Literal("--no-textconv"),
            ProcessArgument.Literal($"--unified={contextLines}"),
            ProcessArgument.Literal("--full-index"),
            ProcessArgument.Literal("--binary"),
            ProcessArgument.Literal("--find-renames=50%"),
            ProcessArgument.Literal("--src-prefix=a/"),
            ProcessArgument.Literal("--dst-prefix=b/"),
            ProcessArgument.Literal("--no-relative"),
        };
        if (target == RawDiffTarget.Index)
        {
            arguments.Add(ProcessArgument.Literal("--cached"));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. arguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.CreateSpooling(
                SpoolMemoryThresholdBytes,
                MaximumPatchBytes,
                MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        var spool = result.StandardOutputSpool
            ?? throw new InvalidOperationException("Raw diff capture did not return its required byte spool.");
        if (result.ExitCode != 0)
        {
            spool.Dispose();
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git diff failed." : error);
        }

        try
        {
            var index = RawDiffParser.Parse(spool, generation);
            return new RawDiffDocument(spool, index);
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }
}
