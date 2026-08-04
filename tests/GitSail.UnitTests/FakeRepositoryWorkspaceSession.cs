using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Supplies deterministic controlled repository state to headless workspace view tests.
/// </summary>
internal sealed class FakeRepositoryWorkspaceSession : IRepositoryWorkspaceSession
{
    /// <summary>
    /// Initializes a fake workspace session with the supplied status entries.
    /// </summary>
    /// <param name="entries">The status entries exposed by the fake repository.</param>
    internal FakeRepositoryWorkspaceSession(params RepositoryStatusEntry[] entries)
    {
        GitVersion.TryParse("git version 2.50.0"u8, out var version);
        Installation = new GitInstallation(
            new ResolvedExecutable(
                ProgramKind.Git,
                OperatingSystem.IsWindows() ? "C:\\git.exe" : "/usr/bin/git",
                new ExecutableFingerprint(1, 1)),
            version);
        var root = CreatePath(OperatingSystem.IsWindows() ? "C:\\repository" : "/repository");
        var repository = new RepositoryLocation(
            root,
            root,
            root,
            Prefix: null,
            RepositoryObjectFormat.Sha1,
            IsBare: false);
        State = new StatusWorkspaceState(new RepositoryStatusSnapshot(
            new OperationGeneration(1),
            repository,
            HeadObjectId: null,
            HeadName: RefName.FromBytes("main"u8),
            UpstreamName: null,
            AheadCount: 0,
            BehindCount: 0,
            [.. entries]));
    }

    /// <summary>
    /// Notifies the attached view when fake activity state changes.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the deterministic fake Git installation.
    /// </summary>
    public GitInstallation Installation { get; }

    /// <summary>
    /// Gets the controlled fake status state.
    /// </summary>
    public StatusWorkspaceState State { get; }

    /// <summary>
    /// Gets the latest fake operation description.
    /// </summary>
    public string Activity { get; private set; } = "Ready";

    /// <summary>
    /// Gets whether the fake session is presenting a busy state.
    /// </summary>
    public bool IsBusy { get; internal set; }

    /// <summary>
    /// Gets the number of refresh actions requested by the view.
    /// </summary>
    internal int RefreshCallCount { get; private set; }

    /// <summary>
    /// Gets the number of stage actions requested by the view.
    /// </summary>
    internal int StageCallCount { get; private set; }

    /// <summary>
    /// Gets the number of unstage actions requested by the view.
    /// </summary>
    internal int UnstageCallCount { get; private set; }

    /// <summary>
    /// Records one requested status refresh.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefreshCallCount++;
        Activity = "Status refreshed";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested stage action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task StageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StageCallCount++;
        Activity = "Staged";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested unstage action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task UnstageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnstageCallCount++;
        Activity = "Unstaged";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates one ordinary modified worktree entry for a test path.
    /// </summary>
    /// <param name="path">The repository-relative display path.</param>
    /// <returns>The fake lossless status entry.</returns>
    internal static RepositoryStatusEntry CreateUnstagedEntry(string path)
        => new(
            RepositoryStatusEntryKind.Ordinary,
            GitFileStatus.Unmodified,
            GitFileStatus.Modified,
            CreatePath(path),
            OriginalPath: null,
            SimilarityPercentage: null,
            IsSubmodule: false);

    /// <summary>
    /// Creates one ordinary modified index entry for a test path.
    /// </summary>
    /// <param name="path">The repository-relative display path.</param>
    /// <returns>The fake lossless status entry.</returns>
    internal static RepositoryStatusEntry CreateStagedEntry(string path)
        => new(
            RepositoryStatusEntryKind.Ordinary,
            GitFileStatus.Modified,
            GitFileStatus.Unmodified,
            CreatePath(path),
            OriginalPath: null,
            SimilarityPercentage: null,
            IsSubmodule: false);

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(System.Text.Encoding.UTF8.GetBytes(path));
}
