using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using Hex1b.Documents;
using Hex1b.Widgets;

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
        CommitMessage = new CommitMessageState();
        CommitOptions = new CommitOptionsState(amend: false);
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
    /// Gets the persistent fake commit-message editor state.
    /// </summary>
    public CommitMessageState CommitMessage { get; }

    /// <summary>
    /// Gets the lifted fake commit options used by view interaction tests.
    /// </summary>
    public CommitOptionsState CommitOptions { get; }

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
    /// Gets whether fake worktree changed lines are selected for staging.
    /// </summary>
    public bool CanStageSelectedLines =>
        !IsBusy && HasSelectedLines && State.ActivePane == StatusWorkspacePane.Unstaged;

    /// <summary>
    /// Gets whether fake index changed lines are selected for unstaging.
    /// </summary>
    public bool CanUnstageSelectedLines =>
        !IsBusy && HasSelectedLines && State.ActivePane == StatusWorkspacePane.Staged;

    /// <summary>
    /// Gets whether a fake focused worktree file is available to revert.
    /// </summary>
    public bool CanRevertFocusedFile =>
        !IsBusy && State.FocusedItem is not null && State.ActivePane == StatusWorkspacePane.Unstaged;

    /// <summary>
    /// Gets whether a fake focused worktree hunk is available to revert.
    /// </summary>
    public bool CanRevertFocusedHunk => CanStageFocusedHunk;

    /// <summary>
    /// Gets whether fake selected worktree changed lines are available to revert.
    /// </summary>
    public bool CanRevertSelectedLines => CanStageSelectedLines;

    /// <summary>
    /// Gets whether one successful fake revert remains available to undo.
    /// </summary>
    public bool CanUndoRevert { get; private set; }

    /// <summary>
    /// Gets whether the focused fake path is untracked and available for patch preparation.
    /// </summary>
    public bool CanPrepareUntrackedPatch => !IsBusy &&
        State.ActivePane == StatusWorkspacePane.Unstaged &&
        State.FocusedItem?.Entry.Kind == RepositoryStatusEntryKind.Untracked;

    /// <summary>
    /// Gets whether the fake repository exposes staged changes for commit.
    /// </summary>
    public bool CanCommit => !IsBusy &&
        !State.Snapshot.Entries.Any(static entry => entry.Kind == RepositoryStatusEntryKind.Unmerged) &&
        (State.StagedItems.Length > 0 ||
            (CommitOptions.Amend && State.Snapshot.HeadObjectId is not null));

    /// <summary>
    /// Gets whether fake citool completion has been requested successfully.
    /// </summary>
    public bool IsCitoolCompleted { get; private set; }

    /// <summary>
    /// Gets whether fake no-commit completion is currently available.
    /// </summary>
    public bool CanCompleteWithoutCommit => !IsBusy &&
        !State.Snapshot.Entries.Any(static entry => entry.Kind == RepositoryStatusEntryKind.Unmerged);

    /// <summary>
    /// Gets the fake explicit unchanged-line count around changes.
    /// </summary>
    public int DiffContextLines { get; private set; } = 3;

    /// <summary>
    /// Gets whether the fake diff pane is presenting an editable conflict result.
    /// </summary>
    public bool IsConflictResolutionActive { get; private set; }

    /// <summary>
    /// Gets whether the fake result cursor is inside an unresolved conflict block.
    /// </summary>
    public bool CanChooseFocusedConflictChunk => !IsBusy &&
        IsConflictResolutionActive &&
        HasFocusedConflictChunk &&
        ResolvedConflictChunkCount < ConflictChunkCount;

    /// <summary>
    /// Gets whether the fake marker-free conflict result is ready to stage.
    /// </summary>
    public bool CanStageConflictResolution => !IsBusy &&
        IsConflictResolutionActive &&
        ResolvedConflictChunkCount == ConflictChunkCount;

    /// <summary>
    /// Gets whether the fake active conflict supports executable-bit selection.
    /// </summary>
    public bool CanToggleConflictExecutable => !IsBusy && IsConflictResolutionActive;

    /// <summary>
    /// Gets whether the fake active result is selected as executable.
    /// </summary>
    public bool ConflictResultIsExecutable { get; private set; }

    /// <summary>
    /// Gets the number of fake conflict chunks already resolved.
    /// </summary>
    public int ResolvedConflictChunkCount { get; private set; }

    /// <summary>
    /// Gets the number of original chunks in the fake conflict result.
    /// </summary>
    public int ConflictChunkCount { get; private set; }

    /// <summary>
    /// Gets or sets whether the fake diff cursor is inside a complete hunk.
    /// </summary>
    internal bool HasFocusedHunk { get; set; } = true;

    /// <summary>
    /// Gets or sets whether fake changed diff lines are selected.
    /// </summary>
    internal bool HasSelectedLines { get; set; }

    /// <summary>
    /// Gets or sets whether the fake result cursor is inside an unresolved conflict block.
    /// </summary>
    internal bool HasFocusedConflictChunk { get; set; } = true;

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
    /// Gets the number of commit actions requested by the view.
    /// </summary>
    internal int CommitCallCount { get; private set; }

    /// <summary>
    /// Gets the number of separately confirmed hook-bypass commit actions requested by the view.
    /// </summary>
    internal int CommitWithoutHooksCallCount { get; private set; }

    /// <summary>
    /// Gets the number of focused-hunk stage actions requested by the view.
    /// </summary>
    internal int StageFocusedHunkCallCount { get; private set; }

    /// <summary>
    /// Gets the number of focused-hunk unstage actions requested by the view.
    /// </summary>
    internal int UnstageFocusedHunkCallCount { get; private set; }

    /// <summary>
    /// Gets the number of selected-line stage actions requested by the view.
    /// </summary>
    internal int StageSelectedLinesCallCount { get; private set; }

    /// <summary>
    /// Gets the number of selected-line unstage actions requested by the view.
    /// </summary>
    internal int UnstageSelectedLinesCallCount { get; private set; }

    /// <summary>
    /// Gets the number of complete-file revert actions requested by the view.
    /// </summary>
    internal int RevertFocusedFileCallCount { get; private set; }

    /// <summary>
    /// Gets the number of focused-hunk revert actions requested by the view.
    /// </summary>
    internal int RevertFocusedHunkCallCount { get; private set; }

    /// <summary>
    /// Gets the number of selected-line revert actions requested by the view.
    /// </summary>
    internal int RevertSelectedLinesCallCount { get; private set; }

    /// <summary>
    /// Gets the number of successful revert-undo actions requested by the view.
    /// </summary>
    internal int UndoRevertCallCount { get; private set; }

    /// <summary>
    /// Gets the number of untracked intent-to-add preparation actions requested by the view.
    /// </summary>
    internal int PrepareUntrackedPatchCallCount { get; private set; }

    /// <summary>
    /// Gets the number of next-hunk navigation actions requested by the view.
    /// </summary>
    internal int FocusNextHunkCallCount { get; private set; }

    /// <summary>
    /// Gets the number of previous-hunk navigation actions requested by the view.
    /// </summary>
    internal int FocusPreviousHunkCallCount { get; private set; }

    /// <summary>
    /// Gets the number of decrease-context actions requested by the view.
    /// </summary>
    internal int DecreaseDiffContextCallCount { get; private set; }

    /// <summary>
    /// Gets the number of increase-context actions requested by the view.
    /// </summary>
    internal int IncreaseDiffContextCallCount { get; private set; }

    /// <summary>
    /// Gets the number of focused conflict choices requested by the view.
    /// </summary>
    internal int ChooseConflictChunkCallCount { get; private set; }

    /// <summary>
    /// Gets the most recent exact fake conflict choice requested by the view.
    /// </summary>
    internal ConflictResolutionChoice? LastConflictChoice { get; private set; }

    /// <summary>
    /// Gets the number of next-unresolved-conflict actions requested by the view.
    /// </summary>
    internal int FocusNextUnresolvedConflictCallCount { get; private set; }

    /// <summary>
    /// Gets the number of executable-bit toggles requested by the view.
    /// </summary>
    internal int ToggleConflictExecutableCallCount { get; private set; }

    /// <summary>
    /// Gets the number of complete conflict-result stage actions requested by the view.
    /// </summary>
    internal int StageConflictResolutionCallCount { get; private set; }

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
    /// Records one exact side choice for the fake focused conflict block.
    /// </summary>
    /// <param name="choice">The exact base, ours, theirs, or both choice.</param>
    /// <returns>A completed task after fake progress publication.</returns>
    public Task ChooseFocusedConflictChunkAsync(ConflictResolutionChoice choice)
    {
        if (CanChooseFocusedConflictChunk)
        {
            ChooseConflictChunkCallCount++;
            LastConflictChoice = choice;
            ResolvedConflictChunkCount++;
            Activity = $"Resolved conflict {ResolvedConflictChunkCount}/{ConflictChunkCount}";
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake next-unresolved-conflict navigation action.
    /// </summary>
    /// <returns>A completed task after fake navigation publication.</returns>
    public Task FocusNextUnresolvedConflictAsync()
    {
        if (IsConflictResolutionActive && ResolvedConflictChunkCount < ConflictChunkCount)
        {
            FocusNextUnresolvedConflictCallCount++;
            Activity = "Focused next unresolved conflict";
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Toggles the fake regular-file executable result bit.
    /// </summary>
    /// <returns>A completed task after fake mode publication.</returns>
    public Task ToggleConflictExecutableAsync()
    {
        if (CanToggleConflictExecutable)
        {
            ToggleConflictExecutableCallCount++;
            ConflictResultIsExecutable = !ConflictResultIsExecutable;
            Activity = ConflictResultIsExecutable
                ? "Conflict result mode: executable"
                : "Conflict result mode: regular";
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake marker-free conflict-result staging action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake staging publication.</returns>
    public Task StageConflictResolutionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanStageConflictResolution)
        {
            StageConflictResolutionCallCount++;
            Activity = "Conflict resolution staged";
            Changed?.Invoke();
        }

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
    /// Records one selected-line stage action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task StageSelectedLinesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StageSelectedLinesCallCount++;
        Activity = "Selected lines staged";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one selected-line unstage action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task UnstageSelectedLinesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnstageSelectedLinesCallCount++;
        Activity = "Selected lines unstaged";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one complete-file revert and enables one-level fake undo.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task RevertFocusedFileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevertFocusedFileCallCount++;
        CanUndoRevert = true;
        Activity = "Reverted file; undo available";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one focused-hunk revert and enables one-level fake undo.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task RevertFocusedHunkAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevertFocusedHunkCallCount++;
        CanUndoRevert = true;
        Activity = "Reverted hunk; undo available";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one selected-line revert and enables one-level fake undo.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task RevertSelectedLinesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevertSelectedLinesCallCount++;
        CanUndoRevert = true;
        Activity = "Reverted selected lines; undo available";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one successful fake revert undo and consumes its one-level state.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task UndoRevertAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanUndoRevert)
        {
            UndoRevertCallCount++;
            CanUndoRevert = false;
            Activity = "Revert undone";
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one untracked intent-to-add patch preparation action.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task PrepareFocusedUntrackedPatchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanPrepareUntrackedPatch)
        {
            PrepareUntrackedPatchCallCount++;
            Activity = "Untracked patch ready for hunk and line staging";
            Changed?.Invoke();
        }

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
    /// Records one requested decrease in diff context.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task DecreaseDiffContextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DecreaseDiffContextCallCount++;
        DiffContextLines = Math.Max(0, DiffContextLines - 1);
        Activity = $"Diff context: {DiffContextLines}";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested increase in diff context.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task IncreaseDiffContextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IncreaseDiffContextCallCount++;
        DiffContextLines++;
        Activity = $"Diff context: {DiffContextLines}";
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
    /// Records one requested commit action and clears the successful fake draft.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitCallCount++;
        CommitMessage.Clear();
        IsCitoolCompleted = true;
        Activity = "Commit completed";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one separately confirmed hook-bypass commit and clears the successful fake draft.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task CommitWithoutHooksAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitWithoutHooksCallCount++;
        CommitMessage.Clear();
        IsCitoolCompleted = true;
        Activity = "Commit completed without bypassable hooks";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records successful no-commit completion when no fake unmerged entry remains.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake validation.</returns>
    public Task CompleteWithoutCommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanCompleteWithoutCommit)
        {
            IsCitoolCompleted = true;
            Activity = "Index preparation completed";
            Changed?.Invoke();
        }

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
    /// Creates one untracked worktree entry for a test path.
    /// </summary>
    /// <param name="path">The repository-relative display path.</param>
    /// <returns>The fake lossless untracked status entry.</returns>
    internal static RepositoryStatusEntry CreateUntrackedEntry(string path)
        => new(
            RepositoryStatusEntryKind.Untracked,
            GitFileStatus.Unmodified,
            GitFileStatus.Untracked,
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

    /// <summary>
    /// Activates a deterministic editable conflict result for view interaction tests.
    /// </summary>
    /// <param name="chunkCount">The positive number of fake unresolved chunks.</param>
    internal void ConfigureConflict(int chunkCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkCount);
        IsConflictResolutionActive = true;
        ConflictChunkCount = chunkCount;
        ResolvedConflictChunkCount = 0;
        ConflictResultIsExecutable = false;
        HasFocusedConflictChunk = true;
        Diff.SetEditor(
            "Conflict: conflict.txt",
            new EditorState(new Hex1bDocument(
                "<<<<<<< ours\nours\n=======\ntheirs\n>>>>>>> theirs\n")),
            State.Snapshot.Generation);
        Changed?.Invoke();
    }

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
