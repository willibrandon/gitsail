using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using Hex1b.Documents;
using Hex1b.Widgets;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Supplies deterministic controlled repository state to headless workspace view tests.
/// </summary>
internal sealed class FakeRepositoryWorkspaceSession : IRepositoryWorkspaceSession
{
    private ImmutableArray<RefName> _localTags = [];
    private PushPlan? _pushPlan;
    private ImmutableArray<RefName> _remoteBranches = [];
    private RemoteInitializationPlan? _remoteInitializationPlan;
    private CredentialPromptKind? _remoteInitializationPromptKind;
    private string? _remoteInitializationPrompt;

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
        Branches = new BranchWorkspaceState();
        Worktrees = new WorktreeWorkspaceState();
        Remotes = new RemoteWorkspaceState();
        Stashes = new StashWorkspaceState();
        TransportOutput = new TransportOutputState();
        CredentialPrompts = new CredentialPromptCoordinator();
        CredentialPrompts.Changed += HandleCredentialPromptChanged;
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
    /// Gets controlled fake branch-window state.
    /// </summary>
    public BranchWorkspaceState Branches { get; }

    /// <summary>
    /// Gets controlled fake worktree-window state.
    /// </summary>
    public WorktreeWorkspaceState Worktrees { get; }

    /// <summary>
    /// Gets controlled fake remote-window state.
    /// </summary>
    public RemoteWorkspaceState Remotes { get; }

    /// <summary>
    /// Gets controlled fake stash-window state.
    /// </summary>
    public StashWorkspaceState Stashes { get; }

    /// <summary>
    /// Gets the deterministic fake transport-output presentation.
    /// </summary>
    public TransportOutputState TransportOutput { get; }

    /// <summary>
    /// Gets deterministic nonpersistent credential prompt state for view interaction tests.
    /// </summary>
    public CredentialPromptCoordinator CredentialPrompts { get; }

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
    /// Gets or sets the deterministic local publication warning used by amend confirmation tests.
    /// </summary>
    public PublishedAmendWarning? PublishedAmendWarning { get; internal set; }

    /// <summary>
    /// Gets or sets the deterministic detached HEAD warning used by commit confirmation tests.
    /// </summary>
    public DetachedHeadWarning? DetachedHeadWarning { get; internal set; }

    /// <summary>
    /// Gets or sets the deterministic active merge warning used by abort confirmation tests.
    /// </summary>
    public MergeAbortWarning? MergeAbortWarning { get; internal set; }

    /// <summary>
    /// Gets the canonical fake worktree requested for opening.
    /// </summary>
    public CanonicalDirectory? RequestedOpenDirectory { get; private set; }

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
        !NeedsCommitTemplateEdit &&
        (State.StagedItems.Length > 0 ||
            (CommitOptions.Amend && State.Snapshot.HeadObjectId is not null));

    /// <summary>
    /// Gets whether a deterministic in-progress merge is currently available to abort.
    /// </summary>
    public bool CanAbortMerge => !IsBusy && MergeAbortWarning is not null;

    /// <summary>
    /// Gets whether the fake configured template remains exactly unchanged and prevents commit.
    /// </summary>
    public bool NeedsCommitTemplateEdit => CommitMessage.IsInitialTemplateUnchanged;

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
    /// Gets the number of fake branch-catalog loads requested by the view.
    /// </summary>
    internal int LoadBranchesCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake linked-worktree catalog loads requested by the view.
    /// </summary>
    internal int LoadWorktreesCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake linked-worktree creations requested by the view.
    /// </summary>
    internal int AddWorktreeCallCount { get; private set; }

    /// <summary>
    /// Gets the most recent exact fake worktree action target.
    /// </summary>
    internal WorktreeInfo? LastWorktree { get; private set; }

    /// <summary>
    /// Gets the number of fake local branch switches requested by the view.
    /// </summary>
    internal int SwitchBranchCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake branch creations requested by the view.
    /// </summary>
    internal int CreateBranchCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake detached checkouts requested by the view.
    /// </summary>
    internal int DetachBranchCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake branch renames requested by the view.
    /// </summary>
    internal int RenameBranchCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake branch deletions requested by the view.
    /// </summary>
    internal int DeleteBranchCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake current-branch resets requested by the view.
    /// </summary>
    internal int ResetBranchCallCount { get; private set; }

    /// <summary>
    /// Gets the number of exact fake merge plans requested by the view.
    /// </summary>
    internal int PrepareMergeCallCount { get; private set; }

    /// <summary>
    /// Gets the number of confirmed fake merge transactions requested by the view.
    /// </summary>
    internal int MergeCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake remote-catalog loads requested by the view.
    /// </summary>
    internal int LoadRemotesCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake remote-add transactions requested by the view.
    /// </summary>
    internal int AddRemoteCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake remote-removal transactions requested by the view.
    /// </summary>
    internal int RemoveRemoteCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake selected-remote fetch transactions requested by the view.
    /// </summary>
    internal int FetchRemoteCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake fetch-all transactions requested by the view.
    /// </summary>
    internal int FetchAllRemotesCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake remote-prune previews requested by the view.
    /// </summary>
    internal int PreparePruneRemoteCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake confirmed remote-prune transactions requested by the view.
    /// </summary>
    internal int PruneRemoteCallCount { get; private set; }

    /// <summary>
    /// Gets the number of exact fake remote-initialization plans requested by the view.
    /// </summary>
    internal int PrepareRemoteInitializationCallCount { get; private set; }

    /// <summary>
    /// Gets the number of confirmed fake remote-initialization transactions requested by the view.
    /// </summary>
    internal int InitializeRemoteCallCount { get; private set; }

    /// <summary>
    /// Gets the number of exact fake push plans requested by the view.
    /// </summary>
    internal int PreparePushCallCount { get; private set; }

    /// <summary>
    /// Gets the number of confirmed fake push transactions requested by the view.
    /// </summary>
    internal int PushCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake local-tag selection loads requested by the view.
    /// </summary>
    internal int LoadLocalTagsCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake remote-branch selection loads requested by the view.
    /// </summary>
    internal int LoadRemoteBranchesCallCount { get; private set; }

    /// <summary>
    /// Gets the number of exact fake tag-push plans requested by the view.
    /// </summary>
    internal int PrepareTagPushCallCount { get; private set; }

    /// <summary>
    /// Gets the number of exact fake remote-branch deletion plans requested by the view.
    /// </summary>
    internal int PrepareRemoteBranchDeletionCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake stash-catalog loads requested by the view.
    /// </summary>
    internal int LoadStashesCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake stash-create actions requested by the view.
    /// </summary>
    internal int CreateStashCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake stash-apply actions requested by the view.
    /// </summary>
    internal int ApplyStashCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake stash-pop actions requested by the view.
    /// </summary>
    internal int PopStashCallCount { get; private set; }

    /// <summary>
    /// Gets the number of fake stash-drop actions requested by the view.
    /// </summary>
    internal int DropStashCallCount { get; private set; }

    /// <summary>
    /// Gets the most recent exact fake branch action target.
    /// </summary>
    internal BranchInfo? LastBranch { get; private set; }

    /// <summary>
    /// Gets the most recent fake branch name entered through a dialog.
    /// </summary>
    internal string? LastBranchName { get; private set; }

    /// <summary>
    /// Gets the most recent fake revision entered through the reset dialog.
    /// </summary>
    internal string? LastBranchRevision { get; private set; }

    /// <summary>
    /// Gets the most recent exact fake merge plan submitted by a dialog.
    /// </summary>
    internal MergePlan? LastMergePlan { get; private set; }

    /// <summary>
    /// Gets the most recent validated fake merge options submitted by a dialog.
    /// </summary>
    internal MergeOptions? LastMergeOptions { get; private set; }

    /// <summary>
    /// Gets the most recent exact fake remote action target.
    /// </summary>
    internal RemoteInfo? LastRemote { get; private set; }

    /// <summary>
    /// Gets the most recent validated fake fetch options submitted by the view.
    /// </summary>
    internal FetchOptions? LastFetchOptions { get; private set; }

    /// <summary>
    /// Gets the most recent configured push-URL index selected for fake initialization planning.
    /// </summary>
    internal int LastRemoteInitializationUrlIndex { get; private set; } = -1;

    /// <summary>
    /// Gets the most recent exact fake initialization plan submitted by a confirmation dialog.
    /// </summary>
    internal RemoteInitializationPlan? LastRemoteInitializationPlan { get; private set; }

    /// <summary>
    /// Gets the decoded fake response returned through the authenticated prompt coordinator.
    /// </summary>
    internal string? LastCredentialPromptResponse { get; private set; }

    /// <summary>
    /// Gets the most recent fake remote name entered through a dialog.
    /// </summary>
    internal string? LastRemoteName { get; private set; }

    /// <summary>
    /// Gets the most recent fake remote URL entered through a dialog.
    /// </summary>
    internal string? LastRemoteUrl { get; private set; }

    /// <summary>
    /// Gets the most recent exact fake push plan submitted by a dialog.
    /// </summary>
    internal PushPlan? LastPushPlan { get; private set; }

    /// <summary>
    /// Gets the most recent validated fake push options submitted by a dialog.
    /// </summary>
    internal PushOptions? LastPushOptions { get; private set; }

    /// <summary>
    /// Gets the most recent follow-tags behavior used for fake push planning.
    /// </summary>
    internal GitOptionOverride LastPushFollowTags { get; private set; }

    /// <summary>
    /// Gets the most recent exact local tag selected through the fake tag-push workflow.
    /// </summary>
    internal RefName? LastTag { get; private set; }

    /// <summary>
    /// Gets the most recent exact advertised branch selected through the fake deletion workflow.
    /// </summary>
    internal RefName? LastRemoteBranch { get; private set; }

    /// <summary>
    /// Gets the most recent fake stash-create options submitted by a dialog.
    /// </summary>
    internal StashCreateOptions? LastStashCreateOptions { get; private set; }

    /// <summary>
    /// Gets the most recent exact fake stash action target.
    /// </summary>
    internal StashInfo? LastStash { get; private set; }

    /// <summary>
    /// Gets whether the most recent fake apply or pop requested index restoration.
    /// </summary>
    internal bool LastStashRestoreIndex { get; private set; }

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
    /// Gets the number of explicitly confirmed merge-abort actions requested by the view.
    /// </summary>
    internal int AbortMergeCallCount { get; private set; }

    /// <summary>
    /// Gets the number of commit actions requested after confirming every current warning.
    /// </summary>
    internal int CommitAfterWarningsCallCount { get; private set; }

    /// <summary>
    /// Gets the exact publication warning last submitted from a confirmation dialog.
    /// </summary>
    internal PublishedAmendWarning? LastConfirmedPublishedAmendWarning { get; private set; }

    /// <summary>
    /// Gets the exact detached HEAD warning last submitted from a confirmation dialog.
    /// </summary>
    internal DetachedHeadWarning? LastConfirmedDetachedHeadWarning { get; private set; }

    /// <summary>
    /// Gets the exact merge-abort warning last submitted from a confirmation dialog.
    /// </summary>
    internal MergeAbortWarning? LastConfirmedMergeAbortWarning { get; private set; }

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
    /// Records one requested branch-catalog load while retaining configured fake data.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task LoadBranchesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadBranchesCallCount++;
        Activity = $"Loaded {Branches.VisibleItems.Length} branches";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested worktree-catalog load while retaining configured fake data.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task LoadWorktreesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadWorktreesCallCount++;
        Activity = $"Loaded {Worktrees.VisibleItems.Length} worktrees";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake worktree open when its directory exists.
    /// </summary>
    /// <param name="worktree">The exact displayed fake worktree.</param>
    /// <returns>A completed task.</returns>
    public Task OpenWorktreeAsync(WorktreeInfo worktree)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        LastWorktree = worktree;
        RequestedOpenDirectory = CanonicalDirectory.Create(worktree.Path);
        Activity = "Opening fake worktree";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake linked-worktree creation.
    /// </summary>
    /// <param name="source">The exact displayed starting branch and object.</param>
    /// <param name="targetDirectory">The entered target directory.</param>
    /// <param name="mode">How the fake worktree obtains its HEAD.</param>
    /// <param name="newBranchName">The optional entered new branch name.</param>
    /// <param name="trackSource">Whether direct upstream tracking was selected.</param>
    /// <param name="lockAfterCreation">Whether atomic locking was selected.</param>
    /// <param name="lockReason">The optional lock reason.</param>
    /// <param name="openAfterCreation">Whether opening after creation was selected.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task AddWorktreeAsync(
        BranchInfo source,
        string targetDirectory,
        WorktreeAddMode mode,
        string? newBranchName,
        bool trackSource,
        bool lockAfterCreation,
        string? lockReason,
        bool openAfterCreation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        AddWorktreeCallCount++;
        LastBranch = source;
        LastBranchName = newBranchName;
        Activity = $"Created fake {mode} worktree";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake linked-worktree movement.
    /// </summary>
    /// <param name="worktree">The exact displayed fake worktree.</param>
    /// <param name="targetDirectory">The entered destination directory.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task MoveWorktreeAsync(
        WorktreeInfo worktree,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(targetDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        LastWorktree = worktree;
        Activity = "Moved fake worktree";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake linked-worktree lock.
    /// </summary>
    /// <param name="worktree">The exact displayed fake worktree.</param>
    /// <param name="reason">The optional literal lock reason.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task LockWorktreeAsync(
        WorktreeInfo worktree,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        cancellationToken.ThrowIfCancellationRequested();
        LastWorktree = worktree;
        Activity = "Locked fake worktree";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake linked-worktree unlock.
    /// </summary>
    /// <param name="worktree">The exact displayed fake worktree.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task UnlockWorktreeAsync(
        WorktreeInfo worktree,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        cancellationToken.ThrowIfCancellationRequested();
        LastWorktree = worktree;
        Activity = "Unlocked fake worktree";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates one deterministic fake clean linked-worktree removal plan.
    /// </summary>
    /// <param name="worktree">The exact displayed fake worktree.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>The fake exact removal plan, or <see langword="null"/> without a catalog.</returns>
    public Task<WorktreeRemovalPlan?> PrepareWorktreeRemovalAsync(
        WorktreeInfo worktree,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        cancellationToken.ThrowIfCancellationRequested();
        LastWorktree = worktree;
        return Task.FromResult(Worktrees.Catalog is null
            ? null
            : new WorktreeRemovalPlan(Worktrees.Catalog, worktree, [], []));
    }

    /// <summary>
    /// Records one requested fake linked-worktree removal.
    /// </summary>
    /// <param name="plan">The exact fake removal plan.</param>
    /// <param name="force">Whether force removal was confirmed.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task RemoveWorktreeAsync(
        WorktreeRemovalPlan plan,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        LastWorktree = plan.Worktree;
        Activity = force ? "Force removed fake worktree" : "Removed fake worktree";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates one deterministic empty fake worktree-prune preview.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>The fake plan, or <see langword="null"/> without a catalog.</returns>
    public Task<WorktreePrunePlan?> PrepareWorktreePruneAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Worktrees.Catalog is null
            ? null
            : new WorktreePrunePlan(Worktrees.Catalog.Precondition, [], []));
    }

    /// <summary>
    /// Records one requested fake stale-worktree prune.
    /// </summary>
    /// <param name="plan">The exact fake prune preview.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task PruneWorktreesAsync(
        WorktreePrunePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        Activity = "Pruned fake worktrees";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake worktree repair path.
    /// </summary>
    /// <param name="path">The entered worktree repair path.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task RepairWorktreeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        Activity = "Repaired fake worktree";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested switch to an exact fake local branch.
    /// </summary>
    /// <param name="branch">The exact displayed fake branch.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task SwitchBranchAsync(BranchInfo branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        cancellationToken.ThrowIfCancellationRequested();
        SwitchBranchCallCount++;
        LastBranch = branch;
        Activity = $"Switched to {branch.ShortName.DisplayText}";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake branch creation and tracking choice.
    /// </summary>
    /// <param name="source">The exact displayed fake source branch.</param>
    /// <param name="name">The entered local branch name.</param>
    /// <param name="trackSource">Whether direct tracking was selected.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task CreateAndSwitchBranchAsync(
        BranchInfo source,
        string name,
        bool trackSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        CreateBranchCallCount++;
        LastBranch = source;
        LastBranchName = name;
        Activity = trackSource ? "Created tracked branch" : "Created untracked branch";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake detached checkout.
    /// </summary>
    /// <param name="source">The exact displayed fake source branch.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task DetachBranchAsync(BranchInfo source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        DetachBranchCallCount++;
        LastBranch = source;
        Activity = "Detached HEAD";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake local branch rename.
    /// </summary>
    /// <param name="branch">The exact displayed fake local branch.</param>
    /// <param name="newName">The entered destination branch name.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task RenameBranchAsync(
        BranchInfo branch,
        string newName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(newName);
        cancellationToken.ThrowIfCancellationRequested();
        RenameBranchCallCount++;
        LastBranch = branch;
        LastBranchName = newName;
        Activity = "Renamed branch";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake branch deletion.
    /// </summary>
    /// <param name="branch">The exact displayed fake local branch.</param>
    /// <param name="mode">The selected fake mergedness policy.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task DeleteBranchAsync(
        BranchInfo branch,
        BranchDeleteMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        cancellationToken.ThrowIfCancellationRequested();
        DeleteBranchCallCount++;
        LastBranch = branch;
        Activity = $"Deleted branch with {mode} policy";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake current-branch reset.
    /// </summary>
    /// <param name="branch">The exact displayed fake current branch.</param>
    /// <param name="revision">The entered revision expression.</param>
    /// <param name="mode">The selected fake reset mode.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task ResetCurrentBranchAsync(
        BranchInfo branch,
        string revision,
        BranchResetMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(revision);
        cancellationToken.ThrowIfCancellationRequested();
        ResetBranchCallCount++;
        LastBranch = branch;
        LastBranchRevision = revision;
        Activity = $"Reset branch with {mode} mode";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates one deterministic exact fake merge plan for the selected branch.
    /// </summary>
    /// <param name="source">The exact displayed fake source branch.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>The deterministic fake divergent merge plan.</returns>
    public Task<MergePlan?> PrepareMergeAsync(
        BranchInfo source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        PrepareMergeCallCount++;
        LastBranch = source;
        Assert.IsNotNull(Branches.Catalog);
        Assert.IsTrue(ObjectId.TryParseHex(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"u8,
            out var headObjectId));
        var fingerprint = new byte[32];
        var precondition = new RepositoryPrecondition(
            headObjectId,
            Branches.Catalog.Precondition.HeadName,
            fingerprint);
        var plan = new MergePlan(
            precondition,
            new RepositoryWorktreeFingerprint(fingerprint),
            source,
            MergeRelationship.Diverged,
            currentOnlyCommitCount: 2,
            incomingCommitCount: 3);
        Activity = "Prepared merge";
        Changed?.Invoke();
        return Task.FromResult<MergePlan?>(plan);
    }

    /// <summary>
    /// Records one confirmed exact fake merge transaction and typed options.
    /// </summary>
    /// <param name="plan">The exact displayed fake merge plan.</param>
    /// <param name="options">The validated typed fake merge options.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake merge publication.</returns>
    public Task MergeAsync(
        MergePlan plan,
        MergeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        MergeCallCount++;
        LastMergePlan = plan;
        LastMergeOptions = options;
        Activity = "Merged branch";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one requested fake remote-catalog load while retaining configured fake data.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake load publication.</returns>
    public Task LoadRemotesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadRemotesCallCount++;
        Activity = $"Loaded {Remotes.VisibleItems.Length} remotes";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Focuses one visible fake remote row.
    /// </summary>
    /// <param name="index">The absolute filtered fake remote row index.</param>
    /// <returns>A completed task after fake focus publication.</returns>
    public Task FocusRemoteAsync(int index)
    {
        Remotes.Focus(index);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake remote-add transaction and user-entered values.
    /// </summary>
    /// <param name="name">The entered fake remote name.</param>
    /// <param name="url">The entered fake remote URL.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake addition publication.</returns>
    public Task AddRemoteAsync(string name, string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(url);
        cancellationToken.ThrowIfCancellationRequested();
        AddRemoteCallCount++;
        LastRemoteName = name;
        LastRemoteUrl = url;
        Activity = "Added remote";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake exact remote-removal transaction.
    /// </summary>
    /// <param name="remote">The exact displayed fake remote.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake removal publication.</returns>
    public Task RemoveRemoteAsync(RemoteInfo remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();
        RemoveRemoteCallCount++;
        LastRemote = remote;
        Activity = "Removed remote";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake selected-remote fetch transaction and typed options.
    /// </summary>
    /// <param name="remote">The exact displayed fake remote.</param>
    /// <param name="options">The validated typed fake fetch options.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake fetch publication.</returns>
    public Task FetchRemoteAsync(
        RemoteInfo remote,
        FetchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        FetchRemoteCallCount++;
        LastRemote = remote;
        LastFetchOptions = options;
        TransportOutput.Set("Fetch remote", "fake stdout", "fake stderr");
        Activity = "Fetched remote";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake fetch-all transaction and typed options.
    /// </summary>
    /// <param name="options">The validated typed fake fetch options.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake fetch-all publication.</returns>
    public Task FetchAllRemotesAsync(FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        FetchAllRemotesCallCount++;
        LastFetchOptions = options;
        TransportOutput.Set("Fetch all", "fake stdout", "fake stderr");
        Activity = "Fetched all remotes";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates one deterministic exact fake remote-prune plan.
    /// </summary>
    /// <param name="remote">The exact displayed fake remote.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>The deterministic fake prune plan.</returns>
    public Task<RemotePrunePlan?> PreparePruneRemoteAsync(
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();
        PreparePruneRemoteCallCount++;
        LastRemote = remote;
        Assert.IsNotNull(Remotes.Catalog);
        var plan = new RemotePrunePlan(
            Remotes.Catalog,
            remote,
            new GitOperationResult(" * [would prune] origin/stale\n"u8.ToArray(), ReadOnlyMemory<byte>.Empty));
        Activity = "Prepared remote prune";
        Changed?.Invoke();
        return Task.FromResult<RemotePrunePlan?>(plan);
    }

    /// <summary>
    /// Records one confirmed fake remote-prune transaction.
    /// </summary>
    /// <param name="plan">The exact displayed fake prune plan.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake prune publication.</returns>
    public Task PruneRemoteAsync(RemotePrunePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        PruneRemoteCallCount++;
        LastRemote = plan.Remote;
        Activity = "Pruned remote";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the configured exact fake remote-initialization plan.
    /// </summary>
    /// <param name="remote">The exact displayed fake remote.</param>
    /// <param name="configuredUrlIndex">The selected configured fake push-URL index.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>The configured exact fake initialization plan.</returns>
    public async Task<RemoteInitializationPlan?> PrepareRemoteInitializationAsync(
        RemoteInfo remote,
        int configuredUrlIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();
        PrepareRemoteInitializationCallCount++;
        LastRemote = remote;
        LastRemoteInitializationUrlIndex = configuredUrlIndex;
        if (_remoteInitializationPromptKind is { } promptKind &&
            _remoteInitializationPrompt is { } prompt)
        {
            var response = await CredentialPrompts.RequestAsync(
                "Prepare remote initialization",
                prompt,
                promptKind,
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                Activity = "Remote initialization credential prompt cancelled";
                Changed?.Invoke();
                return null;
            }

            try
            {
                LastCredentialPromptResponse = Encoding.UTF8.GetString(response);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response);
            }
        }

        Activity = "Prepared exact remote initialization";
        Changed?.Invoke();
        return _remoteInitializationPlan;
    }

    /// <summary>
    /// Records one confirmed exact fake remote-initialization transaction.
    /// </summary>
    /// <param name="plan">The exact displayed fake initialization plan.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake initialization publication.</returns>
    public Task InitializeRemoteAsync(
        RemoteInitializationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        InitializeRemoteCallCount++;
        LastRemoteInitializationPlan = plan;
        TransportOutput.Set("Initialize remote", "fake initialization stdout", string.Empty);
        Activity = "Initialized exact remote repository";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns one configured exact fake push plan for the selected remote.
    /// </summary>
    /// <param name="remote">The exact displayed fake destination remote.</param>
    /// <param name="followTags">The configured or explicit fake follow-tags behavior.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>The configured exact fake plan.</returns>
    public Task<PushPlan?> PreparePushAsync(
        RemoteInfo remote,
        GitOptionOverride followTags,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();
        PreparePushCallCount++;
        LastRemote = remote;
        LastPushFollowTags = followTags;
        Activity = "Prepared exact push";
        Changed?.Invoke();
        return Task.FromResult(_pushPlan);
    }

    /// <summary>
    /// Records one confirmed exact fake push transaction and typed options.
    /// </summary>
    /// <param name="plan">The exact displayed fake push plan.</param>
    /// <param name="options">The validated typed fake push options.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake push publication.</returns>
    public Task PushAsync(
        PushPlan plan,
        PushOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        PushCallCount++;
        LastPushPlan = plan;
        LastPushOptions = options;
        TransportOutput.Set("Push remote", "fake push stdout", "fake push stderr");
        Activity = "Pushed exact plan";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the configured exact fake local tag refs.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>Every configured exact fake local tag ref.</returns>
    public Task<ImmutableArray<RefName>> LoadLocalTagsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadLocalTagsCallCount++;
        Activity = $"Loaded {_localTags.Length} local tags";
        Changed?.Invoke();
        return Task.FromResult(_localTags);
    }

    /// <summary>
    /// Returns the configured exact fake advertised branch refs.
    /// </summary>
    /// <param name="remote">The exact displayed fake destination remote.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>Every configured exact fake advertised branch ref.</returns>
    public Task<ImmutableArray<RefName>> LoadRemoteBranchesAsync(
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();
        LoadRemoteBranchesCallCount++;
        LastRemote = remote;
        Activity = $"Loaded {_remoteBranches.Length} remote branches";
        Changed?.Invoke();
        return Task.FromResult(_remoteBranches);
    }

    /// <summary>
    /// Returns the configured exact fake tag-push plan.
    /// </summary>
    /// <param name="remote">The exact displayed fake destination remote.</param>
    /// <param name="tag">The exact fully qualified fake local tag ref.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>The configured exact fake plan.</returns>
    public Task<PushPlan?> PrepareTagPushAsync(
        RemoteInfo remote,
        RefName tag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(tag);
        cancellationToken.ThrowIfCancellationRequested();
        PrepareTagPushCallCount++;
        LastRemote = remote;
        LastTag = tag;
        Activity = "Prepared exact tag push";
        Changed?.Invoke();
        return Task.FromResult(_pushPlan);
    }

    /// <summary>
    /// Returns the configured exact fake remote-branch deletion plan.
    /// </summary>
    /// <param name="remote">The exact displayed fake destination remote.</param>
    /// <param name="branch">The exact fully qualified fake advertised branch ref.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>The configured exact fake plan.</returns>
    public Task<PushPlan?> PrepareRemoteBranchDeletionAsync(
        RemoteInfo remote,
        RefName branch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(branch);
        cancellationToken.ThrowIfCancellationRequested();
        PrepareRemoteBranchDeletionCallCount++;
        LastRemote = remote;
        LastRemoteBranch = branch;
        Activity = "Prepared exact remote branch deletion";
        Changed?.Invoke();
        return Task.FromResult(_pushPlan);
    }

    /// <summary>
    /// Records one requested stash-catalog load while retaining configured fake data.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake load publication.</returns>
    public Task LoadStashesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadStashesCallCount++;
        Activity = $"Loaded {Stashes.VisibleItems.Length} stashes";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies one fake stash filter and updates the deterministic preview.
    /// </summary>
    /// <param name="filter">The latest fake filter text.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake filter publication.</returns>
    public Task FilterStashesAsync(string filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        Stashes.SetFilter(filter);
        SetFakeStashPreview();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Focuses one fake stash row and updates the deterministic preview.
    /// </summary>
    /// <param name="index">The absolute filtered fake stash row index.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake focus publication.</returns>
    public Task FocusStashAsync(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stashes.Focus(index);
        SetFakeStashPreview();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake stash-create request and all typed options.
    /// </summary>
    /// <param name="options">The exact fake create options.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake creation publication.</returns>
    public Task CreateStashAsync(StashCreateOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        CreateStashCallCount++;
        LastStashCreateOptions = options;
        Activity = "Saved current changes to a stash";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake exact stash-apply request.
    /// </summary>
    /// <param name="stash">The exact displayed fake stash.</param>
    /// <param name="restoreIndex">Whether fake index restoration was requested.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake application publication.</returns>
    public Task ApplyStashAsync(
        StashInfo stash,
        bool restoreIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stash);
        cancellationToken.ThrowIfCancellationRequested();
        ApplyStashCallCount++;
        LastStash = stash;
        LastStashRestoreIndex = restoreIndex;
        Activity = "Applied stash";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake exact stash-pop request.
    /// </summary>
    /// <param name="stash">The exact displayed fake stash.</param>
    /// <param name="restoreIndex">Whether fake index restoration was requested.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake pop publication.</returns>
    public Task PopStashAsync(
        StashInfo stash,
        bool restoreIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stash);
        cancellationToken.ThrowIfCancellationRequested();
        PopStashCallCount++;
        LastStash = stash;
        LastStashRestoreIndex = restoreIndex;
        Activity = "Popped stash";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one fake exact stash-drop request.
    /// </summary>
    /// <param name="stash">The exact displayed fake stash.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task after fake deletion publication.</returns>
    public Task DropStashAsync(StashInfo stash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stash);
        cancellationToken.ThrowIfCancellationRequested();
        DropStashCallCount++;
        LastStash = stash;
        Activity = "Dropped stash";
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
    /// Toggles the fake amend option while retaining a configured deterministic publication warning.
    /// </summary>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task ToggleAmendAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitOptions.ToggleAmend();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one abort of the exact merge warning displayed by the view.
    /// </summary>
    /// <param name="confirmedWarning">The exact merge warning displayed by the confirmation dialog.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task AbortMergeAsync(
        MergeAbortWarning confirmedWarning,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmedWarning);
        cancellationToken.ThrowIfCancellationRequested();
        AbortMergeCallCount++;
        LastConfirmedMergeAbortWarning = confirmedWarning;
        MergeAbortWarning = null;
        Activity = "Merge aborted";
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
    /// Records one commit with explicitly confirmed detached or publication warnings.
    /// </summary>
    /// <param name="confirmedPublishedAmendWarning">The exact publication warning displayed by the view.</param>
    /// <param name="confirmedDetachedHeadWarning">The exact detached HEAD warning displayed by the view.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task CommitAfterWarningsAsync(
        PublishedAmendWarning? confirmedPublishedAmendWarning,
        DetachedHeadWarning? confirmedDetachedHeadWarning,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitAfterWarningsCallCount++;
        LastConfirmedPublishedAmendWarning = confirmedPublishedAmendWarning;
        LastConfirmedDetachedHeadWarning = confirmedDetachedHeadWarning;
        CommitMessage.Clear();
        IsCitoolCompleted = true;
        Activity = "Confirmed commit completed";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one separately confirmed hook-bypass commit and clears the successful fake draft.
    /// </summary>
    /// <param name="confirmedPublishedAmendWarning">The exact publication warning displayed with the bypass warning.</param>
    /// <param name="confirmedDetachedHeadWarning">The exact detached HEAD warning displayed with the bypass warning.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task CommitWithoutHooksAsync(
        PublishedAmendWarning? confirmedPublishedAmendWarning,
        DetachedHeadWarning? confirmedDetachedHeadWarning,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitWithoutHooksCallCount++;
        LastConfirmedPublishedAmendWarning = confirmedPublishedAmendWarning;
        LastConfirmedDetachedHeadWarning = confirmedDetachedHeadWarning;
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

    /// <summary>
    /// Publishes a deterministic exact branch catalog for branch-window interaction tests.
    /// </summary>
    /// <param name="branches">The complete fake branch records.</param>
    internal void ConfigureBranches(params BranchInfo[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        var fingerprint = new byte[32];
        var precondition = new RepositoryPrecondition(
            State.Snapshot.HeadObjectId,
            State.Snapshot.Precondition?.HeadName,
            fingerprint);
        Branches.ApplyCatalog(new BranchCatalog(precondition, [.. branches], []));
        Changed?.Invoke();
    }

    /// <summary>
    /// Publishes a deterministic exact branch and worktree catalog for worktree-window tests.
    /// </summary>
    /// <param name="branches">The complete fake branch records.</param>
    /// <param name="worktrees">The complete fake worktree records with the main worktree first.</param>
    internal void ConfigureWorktrees(BranchInfo[] branches, WorktreeInfo[] worktrees)
    {
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(worktrees);
        var fingerprint = new byte[32];
        var precondition = new RepositoryPrecondition(
            State.Snapshot.HeadObjectId,
            State.Snapshot.Precondition?.HeadName,
            fingerprint);
        Worktrees.ApplyCatalog(new BranchCatalog(precondition, [.. branches], [.. worktrees]));
        Changed?.Invoke();
    }

    /// <summary>
    /// Publishes a deterministic exact remote catalog for remote-workspace interaction tests.
    /// </summary>
    /// <param name="remotes">The complete fake remote records.</param>
    internal void ConfigureRemotes(params RemoteInfo[] remotes)
    {
        ArgumentNullException.ThrowIfNull(remotes);
        Remotes.ApplyCatalog(new RemoteCatalog(
            [.. remotes.OrderBy(static remote => remote.Name)]));
        Changed?.Invoke();
    }

    /// <summary>
    /// Publishes one deterministic exact push plan for push-dialog interaction tests.
    /// </summary>
    /// <param name="plan">The exact fake push plan returned by preparation.</param>
    internal void ConfigurePushPlan(PushPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _pushPlan = plan;
        Changed?.Invoke();
    }

    /// <summary>
    /// Publishes one deterministic exact remote-initialization plan for view interaction tests.
    /// </summary>
    /// <param name="plan">The exact fake initialization plan returned by preparation.</param>
    internal void ConfigureRemoteInitializationPlan(RemoteInitializationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _remoteInitializationPlan = plan;
        Changed?.Invoke();
    }

    /// <summary>
    /// Configures one fake credential prompt before remote-initialization planning can complete.
    /// </summary>
    /// <param name="kind">The visible, secret, or confirmation response treatment.</param>
    /// <param name="prompt">The control-safe fake prompt text.</param>
    internal void ConfigureRemoteInitializationPrompt(
        CredentialPromptKind kind,
        string prompt)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        _remoteInitializationPromptKind = kind;
        _remoteInitializationPrompt = prompt;
        LastCredentialPromptResponse = null;
        Changed?.Invoke();
    }

    private void HandleCredentialPromptChanged()
        => Changed?.Invoke();

    /// <summary>
    /// Publishes deterministic exact local tag refs for tag-selection interaction tests.
    /// </summary>
    /// <param name="tags">The complete fake local tag ref list.</param>
    internal void ConfigureLocalTags(params RefName[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _localTags = [.. tags];
        Changed?.Invoke();
    }

    /// <summary>
    /// Publishes deterministic exact advertised branch refs for deletion interaction tests.
    /// </summary>
    /// <param name="branches">The complete fake advertised branch ref list.</param>
    internal void ConfigureRemoteBranches(params RefName[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        _remoteBranches = [.. branches];
        Changed?.Invoke();
    }

    /// <summary>
    /// Replaces the fake diff with one exact presentation document and publishes the change.
    /// </summary>
    /// <param name="title">The fake diff-pane title.</param>
    /// <param name="text">The complete fake patch presentation.</param>
    internal void ConfigureDiff(string title, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(text);
        Diff.SetContent(title, text, State.Snapshot.Generation);
        Changed?.Invoke();
    }

    /// <summary>
    /// Publishes a deterministic exact stash catalog for stash-window interaction tests.
    /// </summary>
    /// <param name="stashes">The complete ordered fake stash records.</param>
    internal void ConfigureStashes(params StashInfo[] stashes)
    {
        ArgumentNullException.ThrowIfNull(stashes);
        var fingerprint = new byte[32];
        var precondition = new RepositoryPrecondition(
            State.Snapshot.HeadObjectId,
            headName: null,
            fingerprint);
        Stashes.ApplyCatalog(new StashCatalog(
            precondition,
            new RepositoryWorktreeFingerprint(fingerprint),
            [.. stashes]));
        SetFakeStashPreview();
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

    private void SetFakeStashPreview()
    {
        var stash = Stashes.FocusedItem?.Stash;
        if (stash is null)
        {
            Stashes.SetPreviewMessage("No stash matches the current filter.");
            return;
        }

        Stashes.SetPreview(
            stash,
            $"diff --git a/stashed.txt b/stashed.txt\n--- a/stashed.txt\n+++ b/stashed.txt\n@@ -1 +1 @@\n-old\n+{stash.DisplayMessage}\n");
    }
}
