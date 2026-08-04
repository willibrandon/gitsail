using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Loads immutable unmerged-stage objects by validated object ID without placing paths in argv.
/// </summary>
internal sealed class ConflictStageContentService
{
    private const int SpoolMemoryThresholdBytes = 1024 * 1024;
    private const int MaximumObjectBytes = 1024 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;

    /// <summary>
    /// Initializes conflict-object loading over the sole typed child-process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal ConflictStageContentService(
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
    /// Loads every present base, ours, and theirs stage from Git's immutable object database.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="stages">The exact unmerged stage identities reported by Git status.</param>
    /// <param name="cancellationToken">Signals object-read cancellation.</param>
    /// <returns>The exact optional stage content in base, ours, and theirs order.</returns>
    internal async Task<ConflictStageContents> LoadAsync(
        CanonicalDirectory workingDirectory,
        ConflictStages stages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(stages);
        var baseTask = LoadStageAsync(workingDirectory, stages.Base, cancellationToken);
        var oursTask = LoadStageAsync(workingDirectory, stages.Ours, cancellationToken);
        var theirsTask = LoadStageAsync(workingDirectory, stages.Theirs, cancellationToken);
        await Task.WhenAll(baseTask, oursTask, theirsTask).ConfigureAwait(false);
        return new ConflictStageContents(
            await baseTask.ConfigureAwait(false),
            await oursTask.ConfigureAwait(false),
            await theirsTask.ConfigureAwait(false));
    }

    private async Task<ConflictStageContent?> LoadStageAsync(
        CanonicalDirectory workingDirectory,
        ConflictStage? stage,
        CancellationToken cancellationToken)
    {
        if (stage is null)
        {
            return null;
        }

        if (stage.Mode == GitFileMode.GitLink)
        {
            return new ConflictStageContent(stage, content: null);
        }

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("cat-file"),
                ProcessArgument.Literal("blob"),
                ProcessArgument.Literal(stage.ObjectId.ToString()),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.CreateSpooling(
                SpoolMemoryThresholdBytes,
                MaximumObjectBytes,
                MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        using var spool = result.StandardOutputSpool
            ?? throw new InvalidOperationException("Conflict-stage loading did not return its required byte spool.");
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git could not read a conflict-stage blob." : error);
        }

        if (spool.Length > int.MaxValue)
        {
            throw new InvalidDataException("A conflict-stage blob exceeds the supported in-memory length.");
        }

        var content = await spool.ReadSliceAsync(
            offset: 0,
            checked((int)spool.Length),
            cancellationToken).ConfigureAwait(false);
        return new ConflictStageContent(stage, content);
    }
}
