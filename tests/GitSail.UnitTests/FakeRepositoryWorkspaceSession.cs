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
        Diff = new DiffViewState();
        SetFakeDiff(State.FocusedItem, "Unstaged");
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
    /// Gets the deterministic read-only diff editor presentation.
    /// </summary>
    public DiffViewState Diff { get; }

    /// <summary>
    /// Gets the latest fake operation description.
    /// </summary>
    public string Activity { get; private set; } = "Ready";

    /// <summary>
    /// Gets whether the fake session is presenting a busy state.
    /// </summary>
    public bool IsBusy { get; internal set; }

    /// <summary>
    /// Gets whether the fake worktree diff cursor identifies a stageable hunk.
    /// </summary>
    public bool CanStageFocusedHunk =>
        !IsBusy && HasFocusedHunk && State.ActivePane == StatusWorkspacePane.Unstaged;

    /// <summary>
    /// Gets whether the fake index diff cursor identifies an unstageable hunk.
    /// </summary>
    public bool CanUnstageFocusedHunk =>
        !IsBusy && HasFocusedHunk && State.ActivePane == StatusWorkspacePane.Staged;

    /// <summary>
    /// Gets or sets whether the fake diff cursor is inside a complete hunk.
    /// </summary>
    internal bool HasFocusedHunk { get; set; } = true;

    /// <summary>
    /// Gets the number of refresh actions requested by the view.
    /// </summary>
    internal int RefreshCallCount { get; private set; }

    /// <summary>
    /// Gets the number of stage actions requested by the view.
    /// </summary>
    internal int StageCallCount { get; private set; }

    /// <summary>
    /// Gets the number of stage-all actions requested by the view.
    /// </summary>
    internal int StageAllCallCount { get; private set; }

    /// <summary>
    /// Gets the number of unstage actions requested by the view.
    /// </summary>
    internal int UnstageCallCount { get; private set; }

    /// <summary>
    /// Gets the number of unstage-all actions requested by the view.
    /// </summary>
    internal int UnstageAllCallCount { get; private set; }

    /// <summary>
    /// Gets the number of focused-hunk stage actions requested by the view.
    /// </summary>
    internal int StageFocusedHunkCallCount { get; private set; }

    /// <summary>
    /// Gets the number of focused-hunk unstage actions requested by the view.
    /// </summary>
    internal int UnstageFocusedHunkCallCount { get; private set; }

    /// <summary>
    /// Gets the number of next-hunk navigation actions requested by the view.
    /// </summary>
    internal int FocusNextHunkCallCount { get; private set; }

    /// <summary>
    /// Gets the number of previous-hunk navigation actions requested by the view.
    /// </summary>
    internal int FocusPreviousHunkCallCount { get; private set; }

    /// <summary>
    /// Focuses one fake worktree row and replaces the deterministic patch presentation.
    /// </summary>
    /// <param name="index">The absolute worktree row index.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake presentation replacement.</returns>
    public Task FocusUnstagedAsync(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State.FocusUnstaged(index);
        SetFakeDiff(State.FocusedItem, "Unstaged");
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Focuses one fake index row and replaces the deterministic patch presentation.
    /// </summary>
    /// <param name="index">The absolute index row index.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake presentation replacement.</returns>
    public Task FocusStagedAsync(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State.FocusStaged(index);
        SetFakeDiff(State.FocusedItem, "Staged");
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested focused-hunk stage action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task StageFocusedHunkAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StageFocusedHunkCallCount++;
        Activity = "Hunk staged";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested focused-hunk unstage action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task UnstageFocusedHunkAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnstageFocusedHunkCallCount++;
        Activity = "Hunk unstaged";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested next-hunk navigation action.
    /// </summary>
    /// <returns>A completed task.</returns>
    public Task FocusNextHunkAsync()
    {
        FocusNextHunkCallCount++;
        Activity = "Focused next hunk";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested previous-hunk navigation action.
    /// </summary>
    /// <returns>A completed task.</returns>
    public Task FocusPreviousHunkAsync()
    {
        FocusPreviousHunkCallCount++;
        Activity = "Focused previous hunk";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

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
    /// Records one requested stage-all action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task StageAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StageAllCallCount++;
        Activity = "Staged all";
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
    /// Records one requested unstage-all action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task UnstageAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnstageAllCallCount++;
        Activity = "Unstaged all";
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

    private void SetFakeDiff(StatusWorkspaceItem? item, string side)
    {
        if (item is null)
        {
            Diff.SetContent("Diff", "Select a changed path to inspect its patch.", State.Snapshot.Generation);
            return;
        }

        var path = item.Path.DisplayText;
        var lines = Enumerable.Range(1, 40)
            .Select(index => index % 2 == 0 ? $"+new line {index}" : $"-old line {index}");
        var patch = $"diff --git a/{path} b/{path}\n" +
            $"--- a/{path}\n" +
            $"+++ b/{path}\n" +
            "@@ -1,20 +1,20 @@\n" +
            string.Join('\n', lines);
        Diff.SetContent($"{side}: {path}", patch, State.Snapshot.Generation);
    }
}
