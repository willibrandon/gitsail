using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using Hex1b.Documents;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Coordinates asynchronous status reads and serialized index mutations for one open repository.
/// </summary>
internal sealed class RepositoryWorkspaceSession : IRepositoryWorkspaceSession, IAsyncDisposable
{
    private const int MaximumPresentedPatchBytes = 4 * 1024 * 1024;
    private readonly CanonicalDirectory _workingDirectory;
    private readonly RepositoryLocation _repository;
    private readonly RepositoryStatusService _statusService;
    private readonly IndexMutationService _indexMutationService;
    private readonly RepositoryPatchService _patchService;
    private readonly BranchService _branchService;
    private readonly WorktreeService _worktreeService;
    private readonly MergeService _mergeService;
    private readonly RemoteService _remoteService;
    private readonly RemoteInitializationService _remoteInitializationService;
    private readonly PushService _pushService;
    private readonly StashService _stashService;
    private readonly RepositoryMaintenanceService _maintenanceService;
    private readonly RevisionResolver _revisionResolver;
    private readonly CommitService _commitService;
    private readonly PublishedAmendService _publishedAmendService;
    private readonly DetachedHeadWarningService _detachedHeadWarningService;
    private readonly MergeAbortService _mergeAbortService;
    private readonly CommitMessageInitializationService _commitMessageInitializationService;
    private readonly IReadOnlyList<GitPath> _commitMessageRecoveryPaths;
    private readonly GitPath _mergeMessagePath;
    private readonly GitPath _squashMessagePath;
    private readonly CommitDraftStore _commitDraftStore;
    private readonly RevertUndoStore _revertUndoStore;
    private readonly RawDiffService _rawDiffService;
    private readonly ConflictStageContentService _conflictStageContentService;
    private readonly ConflictMergeService _conflictMergeService;
    private readonly ConflictResolutionService _conflictResolutionService;
    private readonly RepositoryMutationCoordinator _mutationCoordinator;
    private readonly ImmutableArray<GitPath> _pathspecs;
    private RepositoryChangeWatcher? _changeWatcher;
    private RawDiffDocument? _workTreeDiff;
    private RawDiffDocument? _indexDiff;
    private RawDiffFile? _focusedPatchFile;
    private RawDiffTarget? _focusedPatchTarget;
    private OperationGeneration _focusedPatchGeneration;
    private OperationGeneration _generation;
    private int _diffContextLines = 3;
    private int _operationInProgress;
    private int _stashPreviewRequest;
    private string? _completionActivityOverride;
    private RevertUndoState? _revertUndoState;

    private RepositoryWorkspaceSession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        RepositoryStatusService statusService,
        IndexMutationService indexMutationService,
        RepositoryPatchService patchService,
        BranchService branchService,
        WorktreeService worktreeService,
        MergeService mergeService,
        RemoteService remoteService,
        RemoteInitializationService remoteInitializationService,
        PushService pushService,
        StashService stashService,
        RepositoryMaintenanceService maintenanceService,
        RevisionResolver revisionResolver,
        CommitService commitService,
        PublishedAmendService publishedAmendService,
        DetachedHeadWarningService detachedHeadWarningService,
        MergeAbortService mergeAbortService,
        CommitMessageInitializationService commitMessageInitializationService,
        IReadOnlyList<GitPath> commitMessageRecoveryPaths,
        GitPath mergeMessagePath,
        GitPath squashMessagePath,
        CommitDraftStore commitDraftStore,
        RevertUndoStore revertUndoStore,
        RawDiffService rawDiffService,
        ConflictStageContentService conflictStageContentService,
        ConflictMergeService conflictMergeService,
        ConflictResolutionService conflictResolutionService,
        RepositoryMutationCoordinator mutationCoordinator,
        CredentialPromptCoordinator credentialPrompts,
        RepositoryStatusSnapshot snapshot,
        PublishedAmendWarning? publishedAmendWarning,
        DetachedHeadWarning? detachedHeadWarning,
        MergeAbortWarning? mergeAbortWarning,
        CommitMessageInitialization commitMessageInitialization,
        RevertUndoState? revertUndoState,
        bool amend,
        ImmutableArray<GitPath> pathspecs,
        StatusWorkspaceScope statusScope)
    {
        _workingDirectory = workingDirectory;
        _repository = repository;
        _statusService = statusService;
        _indexMutationService = indexMutationService;
        _patchService = patchService;
        _branchService = branchService;
        _worktreeService = worktreeService;
        _mergeService = mergeService;
        _remoteService = remoteService;
        _remoteInitializationService = remoteInitializationService;
        _pushService = pushService;
        _stashService = stashService;
        _maintenanceService = maintenanceService;
        _revisionResolver = revisionResolver;
        _commitService = commitService;
        _publishedAmendService = publishedAmendService;
        _detachedHeadWarningService = detachedHeadWarningService;
        _mergeAbortService = mergeAbortService;
        _commitMessageInitializationService = commitMessageInitializationService;
        _commitMessageRecoveryPaths = commitMessageRecoveryPaths;
        _mergeMessagePath = mergeMessagePath;
        _squashMessagePath = squashMessagePath;
        _commitDraftStore = commitDraftStore;
        _revertUndoStore = revertUndoStore;
        _rawDiffService = rawDiffService;
        _conflictStageContentService = conflictStageContentService;
        _conflictMergeService = conflictMergeService;
        _conflictResolutionService = conflictResolutionService;
        _mutationCoordinator = mutationCoordinator;
        _pathspecs = pathspecs.IsDefault ? [] : pathspecs;
        ArgumentNullException.ThrowIfNull(credentialPrompts);
        CredentialPrompts = credentialPrompts;
        _revertUndoState = revertUndoState;
        _generation = snapshot.Generation;
        Installation = installation;
        State = new StatusWorkspaceState(snapshot, statusScope);
        Branches = new BranchWorkspaceState();
        Worktrees = new WorktreeWorkspaceState();
        Remotes = new RemoteWorkspaceState();
        Stashes = new StashWorkspaceState();
        TransportOutput = new TransportOutputState();
        Maintenance = new RepositoryMaintenanceState();
        Diff = new DiffViewState();
        Conflict = new ConflictResolutionState();
        CommitMessage = new CommitMessageState(
            commitMessageInitialization.Message,
            commitMessageInitialization.Kind);
        var hasPendingCommitOperation = mergeAbortWarning is not null ||
            commitMessageInitialization.Kind is
                CommitMessageInitializationKind.Merge or
                CommitMessageInitializationKind.Squash;
        CommitOptions = new CommitOptionsState(amend && !hasPendingCommitOperation);
        PublishedAmendWarning = publishedAmendWarning;
        DetachedHeadWarning = detachedHeadWarning;
        MergeAbortWarning = mergeAbortWarning;
        CommitMessage.Changed += HandleCommitMessageChanged;
        CredentialPrompts.Changed += NotifyChanged;
        _commitDraftStore.PersistenceFailed += HandleCommitDraftPersistenceFailed;
        Activity = GetInitialActivity(
            commitMessageInitialization.Kind,
            revertUndoState,
            revertUndoStore.Warning);
    }

    /// <summary>
    /// Notifies the shell that controlled workspace state has changed and should be rendered.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the resolved Git installation used for this repository session.
    /// </summary>
    public GitInstallation Installation { get; }

    /// <summary>
    /// Gets the controlled status-pane and selection state.
    /// </summary>
    public StatusWorkspaceState State { get; }

    /// <summary>
    /// Gets controlled searchable branch-window catalog, filter, and focus state.
    /// </summary>
    public BranchWorkspaceState Branches { get; }

    /// <summary>
    /// Gets controlled searchable worktree-window catalog, filter, and focus state.
    /// </summary>
    public WorktreeWorkspaceState Worktrees { get; }

    /// <summary>
    /// Gets controlled searchable remote-window catalog, filter, and focus state.
    /// </summary>
    public RemoteWorkspaceState Remotes { get; }

    /// <summary>
    /// Gets controlled searchable stash-window catalog, preview, filter, and focus state.
    /// </summary>
    public StashWorkspaceState Stashes { get; }

    /// <summary>
    /// Gets separate read-only standard-output and standard-error transport presentations.
    /// </summary>
    public TransportOutputState TransportOutput { get; }

    /// <summary>
    /// Gets repository object statistics and the latest maintenance or verification output.
    /// </summary>
    public RepositoryMaintenanceState Maintenance { get; }

    /// <summary>
    /// Gets the serialized nonpersistent credential prompt state for transport operations.
    /// </summary>
    public CredentialPromptCoordinator CredentialPrompts { get; }

    /// <summary>
    /// Gets the current generation-matched read-only diff editor presentation.
    /// </summary>
    public DiffViewState Diff { get; }

    /// <summary>
    /// Gets the lifted editable conflict result and exact stage identity state.
    /// </summary>
    internal ConflictResolutionState Conflict { get; }

    /// <summary>
    /// Gets the persistent writable commit-message editor state.
    /// </summary>
    public CommitMessageState CommitMessage { get; }

    /// <summary>
    /// Gets the lifted options used to construct the next commit transaction.
    /// </summary>
    public CommitOptionsState CommitOptions { get; }

    /// <summary>
    /// Gets the current local remote-tracking warning for amending HEAD, when one applies.
    /// </summary>
    public PublishedAmendWarning? PublishedAmendWarning { get; private set; }

    /// <summary>
    /// Gets the exact detached HEAD warning required by the current Git configuration.
    /// </summary>
    public DetachedHeadWarning? DetachedHeadWarning { get; private set; }

    /// <summary>
    /// Gets the exact active merge state requiring confirmation before Git-owned abort.
    /// </summary>
    public MergeAbortWarning? MergeAbortWarning { get; private set; }

    /// <summary>
    /// Gets the canonical worktree requested for opening after this view closes.
    /// </summary>
    public CanonicalDirectory? RequestedOpenDirectory { get; private set; }

    /// <summary>
    /// Gets the repository view requested after the current workspace closes.
    /// A missing value leaves the shell with no view-navigation request.
    /// </summary>
    public RepositoryWorkspaceDestination? RequestedDestination { get; private set; }

    /// <summary>
    /// Gets a short, control-safe description of the current or most recent operation.
    /// </summary>
    public string Activity { get; private set; }

    /// <summary>
    /// Gets whether an asynchronous refresh or mutation is currently active.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _operationInProgress) != 0;

    /// <summary>
    /// Gets whether the current worktree diff cursor identifies an exact applicable hunk.
    /// </summary>
    public bool CanStageFocusedHunk => !IsBusy && GetFocusedHunk(RawDiffTarget.WorkTree) is not null;

    /// <summary>
    /// Gets whether the current index diff cursor identifies an exact applicable hunk.
    /// </summary>
    public bool CanUnstageFocusedHunk => !IsBusy && GetFocusedHunk(RawDiffTarget.Index) is not null;

    /// <summary>
    /// Gets whether the current worktree diff cursor set selects applicable changed lines.
    /// </summary>
    public bool CanStageSelectedLines => !IsBusy && HasSelectedChangedLines(RawDiffTarget.WorkTree);

    /// <summary>
    /// Gets whether the current index diff cursor set selects applicable changed lines.
    /// </summary>
    public bool CanUnstageSelectedLines => !IsBusy && HasSelectedChangedLines(RawDiffTarget.Index);

    /// <summary>
    /// Gets whether the current worktree patch can be reverted as a complete file.
    /// </summary>
    public bool CanRevertFocusedFile => !IsBusy && HasCurrentFocusedPatch(RawDiffTarget.WorkTree);

    /// <summary>
    /// Gets whether the current worktree diff cursor identifies an exact revertible hunk.
    /// </summary>
    public bool CanRevertFocusedHunk => CanStageFocusedHunk;

    /// <summary>
    /// Gets whether the current worktree cursor set selects exact revertible changed lines.
    /// </summary>
    public bool CanRevertSelectedLines => CanStageSelectedLines;

    /// <summary>
    /// Gets whether the most recent successful revert remains eligible for one-level undo.
    /// </summary>
    public bool CanUndoRevert => !IsBusy &&
        _revertUndoState is not null &&
        Equals(_revertUndoState.Precondition.HeadObjectId, State.Snapshot.HeadObjectId) &&
        _revertUndoState.Precondition.MatchesStatusHeadName(State.Snapshot.HeadName);

    /// <summary>
    /// Gets whether the focused untracked path can be prepared for exact hunk and line staging.
    /// </summary>
    public bool CanPrepareUntrackedPatch => !IsBusy &&
        State.ActivePane == StatusWorkspacePane.Unstaged &&
        State.FocusedItem?.Entry.Kind == RepositoryStatusEntryKind.Untracked;

    /// <summary>
    /// Gets whether staged changes or an existing commit are available for the selected transaction.
    /// </summary>
    public bool CanCommit => !IsBusy &&
        !HasUnmergedEntries &&
        !NeedsCommitTemplateEdit &&
        (State.StagedItems.Length > 0 ||
            (CommitOptions.Amend && State.Snapshot.HeadObjectId is not null) ||
            MergeAbortWarning is not null);

    /// <summary>
    /// Gets whether an exact in-progress merge is currently available to abort.
    /// </summary>
    public bool CanAbortMerge => !IsBusy && MergeAbortWarning is not null;

    /// <summary>
    /// Gets whether the configured commit template remains exactly unchanged and prevents commit.
    /// </summary>
    public bool NeedsCommitTemplateEdit => CommitMessage.IsInitialTemplateUnchanged;

    /// <summary>
    /// Gets whether the requested single-transaction workflow completed successfully.
    /// </summary>
    public bool IsCitoolCompleted { get; private set; }

    /// <summary>
    /// Gets whether the current index can complete no-commit citool successfully.
    /// </summary>
    public bool CanCompleteWithoutCommit => !IsBusy &&
        !HasUnmergedEntries;

    /// <summary>
    /// Gets the explicit unchanged-line count surrounding each captured change.
    /// </summary>
    public int DiffContextLines => _diffContextLines;

    /// <summary>
    /// Gets whether the diff pane currently owns an editable, generation-matched conflict result.
    /// </summary>
    public bool IsConflictResolutionActive => Conflict.IsActive &&
        Conflict.Generation == State.Snapshot.Generation &&
        ReferenceEquals(Conflict.Editor, Diff.Editor);

    /// <summary>
    /// Gets whether the result-editor cursor is inside an unresolved conflict marker block.
    /// </summary>
    public bool CanChooseFocusedConflictChunk => !IsBusy && GetFocusedConflictChunk() >= 0;

    /// <summary>
    /// Gets whether the marker-free conflict result can be staged through verified index rollback.
    /// </summary>
    public bool CanStageConflictResolution => !IsBusy &&
        IsConflictResolutionActive &&
        Conflict.IsComplete;

    /// <summary>
    /// Gets whether the active blob-backed conflict may toggle its staged executable bit.
    /// </summary>
    public bool CanToggleConflictExecutable => !IsBusy &&
        IsConflictResolutionActive &&
        Conflict.CanToggleExecutable;

    /// <summary>
    /// Gets whether the active conflict result will be staged as an executable regular file.
    /// </summary>
    public bool ConflictResultIsExecutable => IsConflictResolutionActive &&
        Conflict.ResultMode == GitFileMode.ExecutableFile;

    /// <summary>
    /// Gets the number of original conflict chunks whose generated markers have been removed.
    /// </summary>
    public int ResolvedConflictChunkCount => IsConflictResolutionActive
        ? Conflict.ResolvedChunkCount
        : 0;

    /// <summary>
    /// Gets the number of original conflict chunks in the active editable merge result.
    /// </summary>
    public int ConflictChunkCount => IsConflictResolutionActive ? Conflict.ChunkCount : 0;

    /// <summary>
    /// Requests another repository view from the shell that owns this session.
    /// The shell returns to the same repository after that view closes.
    /// </summary>
    /// <param name="destination">The repository view to open next.</param>
    public void RequestDestination(RepositoryWorkspaceDestination destination)
        => RequestedDestination = destination;

    /// <summary>
    /// Opens a non-bare repository and captures its first complete status generation.
    /// </summary>
    /// <param name="workingDirectory">The canonical directory supplied by the user.</param>
    /// <param name="amend">Whether the first commit transaction begins in amend mode.</param>
    /// <param name="cancellationToken">Signals startup cancellation.</param>
    /// <returns>The discovered repository, Git installation, and a workspace unless the repository is bare.</returns>
    internal static Task<(
        RepositoryWorkspaceSession? Session,
        RepositoryLocation Repository,
        GitInstallation Installation)> OpenAsync(
        CanonicalDirectory workingDirectory,
        bool amend,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        return OpenAsync(
            workingDirectory,
            amend,
            new RuntimeProcessEnvironment(),
            TimeProvider.System,
            cancellationToken);
    }

    /// <summary>
    /// Opens a repository with explicit environment and clock boundaries for deterministic verification.
    /// </summary>
    /// <param name="workingDirectory">The canonical directory supplied by the user.</param>
    /// <param name="amend">Whether the first commit transaction begins in amend mode.</param>
    /// <param name="processEnvironment">The classified startup-environment source.</param>
    /// <param name="timeProvider">The UTC clock used for bounded recovery state.</param>
    /// <param name="cancellationToken">Signals startup cancellation.</param>
    /// <returns>The discovered repository, Git installation, and a workspace unless the repository is bare.</returns>
    internal static Task<(
        RepositoryWorkspaceSession? Session,
        RepositoryLocation Repository,
        GitInstallation Installation)> OpenAsync(
        CanonicalDirectory workingDirectory,
        bool amend,
        IProcessEnvironment processEnvironment,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => OpenCoreAsync(
            workingDirectory,
            amend,
            processEnvironment,
            timeProvider,
            [],
            StatusWorkspaceScope.AllChanges,
            cancellationToken);

    /// <summary>
    /// Opens conflict-resolution mode with exact command and file path restrictions.
    /// </summary>
    /// <param name="workingDirectory">The canonical directory supplied by the user.</param>
    /// <param name="options">The typed merge-mode path operands.</param>
    /// <param name="processEnvironment">The classified startup-environment source.</param>
    /// <param name="timeProvider">The UTC clock used for bounded recovery state.</param>
    /// <param name="cancellationToken">Signals startup cancellation.</param>
    /// <returns>The discovered repository, Git installation, and conflict workspace unless the repository is bare.</returns>
    internal static async Task<(
        RepositoryWorkspaceSession? Session,
        RepositoryLocation Repository,
        GitInstallation Installation)> OpenMergeAsync(
        CanonicalDirectory workingDirectory,
        MergeCommandOptions options,
        IProcessEnvironment processEnvironment,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var pathspecs = await CommandPathspecResolver.ResolveAsync(
            options.Paths,
            options.NativePaths,
            options.PathspecFile,
            options.PathspecFileNul,
            cancellationToken).ConfigureAwait(false);
        return await OpenCoreAsync(
            workingDirectory,
            amend: false,
            processEnvironment,
            timeProvider,
            pathspecs,
            StatusWorkspaceScope.UnmergedOnly,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(
        RepositoryWorkspaceSession? Session,
        RepositoryLocation Repository,
        GitInstallation Installation)> OpenCoreAsync(
        CanonicalDirectory workingDirectory,
        bool amend,
        IProcessEnvironment processEnvironment,
        TimeProvider timeProvider,
        ImmutableArray<GitPath> pathspecs,
        StatusWorkspaceScope statusScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(processEnvironment);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var resolver = new ExecutableResolver(processEnvironment);
        var runner = new ChildProcessRunner();
        var environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
        var installation = await new GitVersionService(resolver, runner)
            .GetAsync(workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        var repository = await new RepositoryDiscoveryService(installation, runner, environmentFactory)
            .DiscoverAsync(workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (repository.IsBare)
        {
            return (Session: null, repository, installation);
        }

        var repositoryWorkingDirectory = CanonicalDirectory.Create(repository.WorkTree!);
        var statusService = new RepositoryStatusService(
            installation,
            runner,
            environmentFactory,
            new PorcelainV2StatusParser());
        var generation = new OperationGeneration(1);
        var snapshot = await statusService
            .ScanAsync(repository, repositoryWorkingDirectory, generation, pathspecs, cancellationToken)
            .ConfigureAwait(false);
        var snapshotPrecondition = snapshot.Precondition
            ?? throw new InvalidDataException("The initial status has no repository precondition.");
        var mutationCoordinator = new RepositoryMutationCoordinator();
        var credentialPrompts = new CredentialPromptCoordinator();
        var credentialPromptBroker = new CredentialPromptBroker(credentialPrompts);
        var indexMutationService = new IndexMutationService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator);
        var patchService = new RepositoryPatchService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator);
        var branchService = new BranchService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator);
        var worktreeService = new WorktreeService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator,
            branchService);
        var mergeService = new MergeService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator);
        var remoteService = new RemoteService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator,
            credentialPromptBroker);
        var pushService = new PushService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator,
            remoteService,
            credentialPromptBroker);
        var remoteInitializationService = new RemoteInitializationService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator,
            remoteService,
            resolver,
            repository.ObjectFormat,
            credentialPromptBroker);
        var stashService = new StashService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator);
        var maintenanceService = new RepositoryMaintenanceService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator,
            credentialPromptBroker);
        var revisionResolver = new RevisionResolver(installation, runner, environmentFactory);
        var revertUndoStore = await RevertUndoStore.CreateAsync(
            repository,
            processEnvironment,
            timeProvider,
            cancellationToken).ConfigureAwait(false);
        var revertUndoState = await revertUndoStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (revertUndoState is not null &&
            !await patchService.IsUndoRevertEligibleAsync(
                repositoryWorkingDirectory,
                revertUndoState.Patch,
                revertUndoState.Precondition,
                cancellationToken).ConfigureAwait(false))
        {
            await revertUndoStore.DiscardAsync(cancellationToken).ConfigureAwait(false);
            revertUndoState = null;
        }

        var rawDiffService = new RawDiffService(installation, runner, environmentFactory);
        var conflictStageContentService = new ConflictStageContentService(
            installation,
            runner,
            environmentFactory);
        var conflictMergeService = new ConflictMergeService(
            installation,
            runner,
            environmentFactory,
            processEnvironment);
        var conflictResolutionService = new ConflictResolutionService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator);
        var statePathService = new RepositoryStatePathService(installation, runner, environmentFactory);
        var commitService = new CommitService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator,
            statePathService);
        var publishedAmendService = new PublishedAmendService(
            installation,
            runner,
            environmentFactory);
        var publishedAmendWarning = amend
            ? await publishedAmendService.FindAsync(
                repositoryWorkingDirectory,
                snapshot.HeadObjectId,
                cancellationToken).ConfigureAwait(false)
            : null;
        var detachedHeadWarningService = new DetachedHeadWarningService(
            installation,
            runner,
            environmentFactory);
        var detachedHeadWarning = await detachedHeadWarningService.FindAsync(
            repositoryWorkingDirectory,
            snapshotPrecondition,
            cancellationToken).ConfigureAwait(false);
        var editMessagePath = await statePathService.ResolveAsync(
            repositoryWorkingDirectory,
            RepositoryStateFile.EditMessage,
            cancellationToken).ConfigureAwait(false);
        var messagePath = await statePathService.ResolveAsync(
            repositoryWorkingDirectory,
            RepositoryStateFile.Message,
            cancellationToken).ConfigureAwait(false);
        var backupPath = await statePathService.ResolveAsync(
            repositoryWorkingDirectory,
            RepositoryStateFile.MessageBackup,
            cancellationToken).ConfigureAwait(false);
        var mergeMessagePath = await statePathService.ResolveAsync(
            repositoryWorkingDirectory,
            RepositoryStateFile.MergeMessage,
            cancellationToken).ConfigureAwait(false);
        var squashMessagePath = await statePathService.ResolveAsync(
            repositoryWorkingDirectory,
            RepositoryStateFile.SquashMessage,
            cancellationToken).ConfigureAwait(false);
        var mergeHeadPath = await statePathService.ResolveAsync(
            repositoryWorkingDirectory,
            RepositoryStateFile.MergeHead,
            cancellationToken).ConfigureAwait(false);
        var mergeAbortService = new MergeAbortService(
            installation,
            runner,
            environmentFactory,
            mutationCoordinator,
            mergeHeadPath);
        var mergeAbortWarning = await mergeAbortService.FindWarningAsync(
            repositoryWorkingDirectory,
            snapshotPrecondition,
            cancellationToken).ConfigureAwait(false);
        var commitMessageInitializationService = new CommitMessageInitializationService(
            installation,
            runner,
            environmentFactory);
        GitPath[] commitMessageRecoveryPaths = [editMessagePath, messagePath, backupPath];
        var commitMessageInitialization = await commitMessageInitializationService.LoadAsync(
            repositoryWorkingDirectory,
            commitMessageRecoveryPaths,
            mergeMessagePath,
            squashMessagePath,
            mergeAbortWarning is not null,
            amend ? snapshot.HeadObjectId : null,
            cancellationToken).ConfigureAwait(false);
        var commitDraftStore = new CommitDraftStore(
            messagePath,
            backupPath,
            commitMessageInitialization.Message,
            TimeSpan.FromMilliseconds(500));
        var session = new RepositoryWorkspaceSession(
            repositoryWorkingDirectory,
            repository,
            installation,
            statusService,
            indexMutationService,
            patchService,
            branchService,
            worktreeService,
            mergeService,
            remoteService,
            remoteInitializationService,
            pushService,
            stashService,
            maintenanceService,
            revisionResolver,
            commitService,
            publishedAmendService,
            detachedHeadWarningService,
            mergeAbortService,
            commitMessageInitializationService,
            commitMessageRecoveryPaths,
            mergeMessagePath,
            squashMessagePath,
            commitDraftStore,
            revertUndoStore,
            rawDiffService,
            conflictStageContentService,
            conflictMergeService,
            conflictResolutionService,
            mutationCoordinator,
            credentialPrompts,
            snapshot,
            publishedAmendWarning,
            detachedHeadWarning,
            mergeAbortWarning,
            commitMessageInitialization,
            revertUndoState,
            amend,
            pathspecs,
            statusScope);
        try
        {
            await session.CaptureDiffsAsync(generation, cancellationToken).ConfigureAwait(false);
            await session.LoadActiveDiffAsync(cancellationToken).ConfigureAwait(false);
            session.StartAutomaticRefresh();
            return (session, repository, installation);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Focuses one worktree row and loads its exact raw patch into the read-only editor presentation.
    /// </summary>
    /// <param name="index">The absolute worktree row index.</param>
    /// <param name="cancellationToken">Signals patch loading cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    public async Task FocusUnstagedAsync(int index, CancellationToken cancellationToken)
    {
        State.FocusUnstaged(index);
        await LoadFocusedDiffAsync(RawDiffTarget.WorkTree, cancellationToken).ConfigureAwait(false);
        NotifyChanged();
    }

    /// <summary>
    /// Focuses one index row and loads its exact raw patch into the read-only editor presentation.
    /// </summary>
    /// <param name="index">The absolute index row index.</param>
    /// <param name="cancellationToken">Signals patch loading cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    public async Task FocusStagedAsync(int index, CancellationToken cancellationToken)
    {
        State.FocusStaged(index);
        await LoadFocusedDiffAsync(RawDiffTarget.Index, cancellationToken).ConfigureAwait(false);
        NotifyChanged();
    }

    /// <summary>
    /// Replaces the unresolved marker block under the result-editor cursor with one exact side choice.
    /// </summary>
    /// <param name="choice">The exact base, ours, theirs, or both content choice.</param>
    /// <returns>A completed task after editor replacement, next-conflict focus, and invalidation.</returns>
    public Task ChooseFocusedConflictChunkAsync(ConflictResolutionChoice choice)
    {
        var chunkIndex = GetFocusedConflictChunk();
        if (IsBusy || chunkIndex < 0)
        {
            return ReportNoSelectionAsync("Place the result cursor inside an unresolved conflict block");
        }

        Conflict.SetChoice(chunkIndex, choice);
        var nextChunk = Conflict.FindNextUnresolvedChunk(chunkIndex);
        if (nextChunk >= 0)
        {
            MoveToConflictChunk(nextChunk);
        }

        Activity = $"Resolved conflict {chunkIndex + 1}/{Conflict.ChunkCount} with {FormatConflictChoice(choice)}";
        NotifyChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves the editable result cursor to the next unresolved generated conflict marker block.
    /// </summary>
    /// <returns>A completed task after cursor movement and invalidation.</returns>
    public Task FocusNextUnresolvedConflictAsync()
    {
        var current = GetFocusedConflictChunk();
        var next = IsConflictResolutionActive
            ? Conflict.FindNextUnresolvedChunk(current)
            : -1;
        if (next < 0)
        {
            return ReportNoSelectionAsync(
                IsConflictResolutionActive && Conflict.IsComplete
                    ? "Every conflict marker is resolved"
                    : "No unresolved conflict block is available");
        }

        MoveToConflictChunk(next);
        Activity = $"Focused conflict {next + 1}/{Conflict.ChunkCount}";
        NotifyChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Toggles the regular-file executable bit selected for the active conflict result.
    /// </summary>
    /// <returns>A completed task after result-mode mutation and invalidation.</returns>
    public Task ToggleConflictExecutableAsync()
    {
        if (!CanToggleConflictExecutable)
        {
            return ReportNoSelectionAsync("The active conflict has no regular-file result mode");
        }

        Conflict.ToggleExecutable();
        Activity = Conflict.ResultMode == GitFileMode.ExecutableFile
            ? "Conflict result mode: executable"
            : "Conflict result mode: regular";
        NotifyChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stages the marker-free editable conflict result after exact live-stage validation.
    /// </summary>
    /// <param name="cancellationToken">Signals conflict staging cancellation.</param>
    /// <returns>A task that completes after rollback-capable mutation and reconciliation.</returns>
    public Task StageConflictResolutionAsync(CancellationToken cancellationToken)
    {
        var entry = Conflict.Entry;
        if (!CanStageConflictResolution || entry is null)
        {
            return ReportNoSelectionAsync(
                IsConflictResolutionActive
                    ? "Remove every generated conflict marker before staging the result"
                    : "No editable conflict result is active");
        }

        var content = Conflict.BuildResolvedContent();
        var resultMode = Conflict.ResultMode;
        return RunAsync(
            "Staging resolved conflict...",
            "Conflict resolution staged",
            token => _conflictResolutionService.ResolveAsync(
                _repository,
                _workingDirectory,
                entry,
                resultMode,
                content,
                token),
            cancellationToken);
    }

    /// <summary>
    /// Stages the complete exact hunk under the read-only diff editor cursor.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task StageFocusedHunkAsync(CancellationToken cancellationToken)
        => RunFocusedHunkMutationAsync(RawDiffTarget.WorkTree, cancellationToken);

    /// <summary>
    /// Unstages the complete exact hunk under the read-only diff editor cursor.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task UnstageFocusedHunkAsync(CancellationToken cancellationToken)
        => RunFocusedHunkMutationAsync(RawDiffTarget.Index, cancellationToken);

    /// <summary>
    /// Stages every exact changed line selected by the worktree diff editor cursor set.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task StageSelectedLinesAsync(CancellationToken cancellationToken)
        => RunSelectedLineMutationAsync(RawDiffTarget.WorkTree, cancellationToken);

    /// <summary>
    /// Unstages every exact changed line selected by the index diff editor cursor set.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task UnstageSelectedLinesAsync(CancellationToken cancellationToken)
        => RunSelectedLineMutationAsync(RawDiffTarget.Index, cancellationToken);

    /// <summary>
    /// Reverts the complete focused worktree file after destructive confirmation by the view.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public async Task RevertFocusedFileAsync(CancellationToken cancellationToken)
    {
        var file = _focusedPatchFile;
        var document = _workTreeDiff;
        if (!CanRevertFocusedFile || file is null || document is null)
        {
            await ReportNoSelectionAsync("No unstaged file available to revert").ConfigureAwait(false);
            return;
        }

        var patch = await document.ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        await RunRevertPatchAsync("file", patch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts the focused worktree hunk after destructive confirmation by the view.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public async Task RevertFocusedHunkAsync(CancellationToken cancellationToken)
    {
        var hunk = GetFocusedHunk(RawDiffTarget.WorkTree);
        var file = _focusedPatchFile;
        var document = _workTreeDiff;
        if (hunk is null || file is null || document is null)
        {
            await ReportNoSelectionAsync("No unstaged hunk under the diff cursor").ConfigureAwait(false);
            return;
        }

        var patch = await document.ReadHunkPatchAsync(file, hunk, cancellationToken).ConfigureAwait(false);
        await RunRevertPatchAsync("hunk", patch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts selected worktree changed lines after destructive confirmation by the view.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public async Task RevertSelectedLinesAsync(CancellationToken cancellationToken)
    {
        var selectedLineNumbers = GetSelectedChangedLineNumbers(RawDiffTarget.WorkTree);
        var file = _focusedPatchFile;
        var document = _workTreeDiff;
        if (selectedLineNumbers.Count == 0 || file is null || document is null)
        {
            await ReportNoSelectionAsync("No unstaged changed lines selected").ConfigureAwait(false);
            return;
        }

        var patch = await document.ReadSelectedLinesPatchAsync(
            file,
            selectedLineNumbers,
            RawPatchSelectionSide.PreserveNewSide,
            cancellationToken).ConfigureAwait(false);
        await RunRevertPatchAsync("selected lines", patch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reapplies the most recent exact reverted patch while its HEAD and content preconditions match.
    /// </summary>
    /// <param name="cancellationToken">Signals patch mutation cancellation.</param>
    /// <returns>A task that completes after undo and reconciliation.</returns>
    public Task UndoRevertAsync(CancellationToken cancellationToken)
    {
        var undoState = _revertUndoState;
        if (!CanUndoRevert || undoState is null)
        {
            return ReportNoSelectionAsync("No revert is available to undo");
        }

        return RunAsync(
            "Undoing the most recent revert...",
            "Revert undone",
            async token =>
            {
                if (!Equals(undoState.Precondition.HeadObjectId, State.Snapshot.HeadObjectId))
                {
                    throw new RepositoryPreconditionException(
                        "HEAD changed after the revert; refresh and review before restoring discarded worktree content.");
                }

                if (!undoState.Precondition.MatchesStatusHeadName(State.Snapshot.HeadName))
                {
                    throw new RepositoryPreconditionException(
                        "HEAD attachment changed after the revert; refresh and review before restoring discarded worktree content.");
                }

                var result = await _patchService.UndoRevertAsync(
                    _workingDirectory,
                    undoState.Patch,
                    undoState.Precondition,
                    token).ConfigureAwait(false);
                _revertUndoState = null;
                try
                {
                    await _revertUndoStore.DiscardAsync(token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    _completionActivityOverride =
                        $"Revert undone; cached recovery cleanup failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
                }

                return result;
            },
            cancellationToken,
            preserveRevertUndo: true);
    }

    /// <summary>
    /// Records intent-to-add for the focused untracked path and loads its exact unstaged patch.
    /// </summary>
    /// <param name="cancellationToken">Signals index mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task PrepareFocusedUntrackedPatchAsync(CancellationToken cancellationToken)
    {
        var item = State.FocusedItem;
        return !CanPrepareUntrackedPatch || item is null
            ? ReportNoSelectionAsync("No focused untracked path is available to prepare")
            : RunAsync(
                "Preparing untracked patch...",
                "Untracked patch ready for hunk and line staging",
                token => _indexMutationService.PrepareIntentToAddAsync(
                    _workingDirectory,
                    [item.Path],
                    token),
                cancellationToken);
    }

    /// <summary>
    /// Moves the read-only diff cursor to the next exact hunk header.
    /// </summary>
    /// <returns>A completed task after cursor movement and view invalidation.</returns>
    public Task FocusNextHunkAsync()
        => MoveFocusedHunkAsync(moveForward: true);

    /// <summary>
    /// Moves the read-only diff cursor to the preceding or containing exact hunk header.
    /// </summary>
    /// <returns>A completed task after cursor movement and view invalidation.</returns>
    public Task FocusPreviousHunkAsync()
        => MoveFocusedHunkAsync(moveForward: false);

    /// <summary>
    /// Decreases diff context by one line without mutating repository content.
    /// </summary>
    /// <param name="cancellationToken">Signals patch recapture cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    public Task DecreaseDiffContextAsync(CancellationToken cancellationToken)
        => ChangeDiffContextAsync(-1, cancellationToken);

    /// <summary>
    /// Increases diff context by one line without mutating repository content.
    /// </summary>
    /// <param name="cancellationToken">Signals patch recapture cancellation.</param>
    /// <returns>A task that completes after the presentation is current.</returns>
    public Task IncreaseDiffContextAsync(CancellationToken cancellationToken)
        => ChangeDiffContextAsync(1, cancellationToken);

    /// <summary>
    /// Refreshes status without changing the repository or discarding controlled selection.
    /// </summary>
    /// <param name="cancellationToken">Signals refresh cancellation.</param>
    /// <returns>A task that completes after the workspace is current.</returns>
    public Task RefreshAsync(CancellationToken cancellationToken)
        => RunAsync("Refreshing status...", "Status refreshed", mutation: null, cancellationToken);

    /// <summary>
    /// Loads Git's complete object-storage statistics without exposing alternate database paths.
    /// </summary>
    /// <param name="cancellationToken">Signals statistics loading cancellation.</param>
    /// <returns>A task that completes after the statistics presentation is current.</returns>
    public async Task LoadRepositoryStatisticsAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return;
        }

        Activity = "Loading repository statistics...";
        NotifyChanged();
        try
        {
            var statistics = await _maintenanceService.CaptureStatisticsAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            Maintenance.SetStatistics(statistics);
            Activity = "Repository statistics loaded";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Runs every foreground repository maintenance task selected by Git configuration.
    /// </summary>
    /// <param name="cancellationToken">Signals configured maintenance cancellation.</param>
    /// <returns>A task that completes after maintenance, statistics refresh, and reconciliation.</returns>
    public Task RunConfiguredMaintenanceAsync(CancellationToken cancellationToken)
        => RunRepositoryCareAsync(
            "Running configured repository maintenance...",
            "Configured repository maintenance completed",
            "Configured maintenance",
            token => _maintenanceService.RunConfiguredMaintenanceAsync(_workingDirectory, token),
            reconcileRepository: true,
            cancellationToken);

    /// <summary>
    /// Runs one foreground Git garbage collection after explicit user confirmation.
    /// </summary>
    /// <param name="cancellationToken">Signals garbage-collection cancellation.</param>
    /// <returns>A task that completes after collection, statistics refresh, and reconciliation.</returns>
    public Task RunGarbageCollectionAsync(CancellationToken cancellationToken)
        => RunRepositoryCareAsync(
            "Running repository garbage collection...",
            "Repository garbage collection completed",
            "Garbage collection",
            token => _maintenanceService.RunGarbageCollectionAsync(_workingDirectory, token),
            reconcileRepository: true,
            cancellationToken);

    /// <summary>
    /// Runs Git's complete object and reference integrity verification without writing lost-found files.
    /// </summary>
    /// <param name="cancellationToken">Signals verification cancellation.</param>
    /// <returns>A task that completes after the exact bounded verification output is presented.</returns>
    public Task VerifyRepositoryAsync(CancellationToken cancellationToken)
        => RunRepositoryCareAsync(
            "Verifying repository objects and references...",
            "Repository verification completed",
            "Repository verification",
            token => _maintenanceService.VerifyAsync(_workingDirectory, token),
            reconcileRepository: false,
            cancellationToken);

    /// <summary>
    /// Loads one stable exact branch and linked-worktree catalog without mutating repository state.
    /// </summary>
    /// <param name="cancellationToken">Signals catalog capture cancellation.</param>
    /// <returns>A task that completes after controlled branch state is current.</returns>
    public async Task LoadBranchesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return;
        }

        Branches.Clear();
        Activity = "Loading branches and linked worktrees...";
        NotifyChanged();
        try
        {
            var catalog = await _branchService.CaptureAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            Branches.ApplyCatalog(catalog);
            Activity = $"Loaded {catalog.Branches.Length} branches and {catalog.Worktrees.Length} worktrees";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Loads one stable exact linked-worktree catalog for the worktree window.
    /// </summary>
    /// <param name="cancellationToken">Signals catalog capture cancellation.</param>
    /// <returns>A task that completes after controlled worktree state is current.</returns>
    public async Task LoadWorktreesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return;
        }

        Worktrees.Clear();
        Activity = "Loading linked worktrees...";
        NotifyChanged();
        try
        {
            var catalog = await _branchService.CaptureAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            Worktrees.ApplyCatalog(catalog);
            Activity = $"Loaded {catalog.Worktrees.Length} worktrees";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Requests opening one exact existing worktree after the current view closes.
    /// </summary>
    /// <param name="worktree">The exact displayed worktree.</param>
    /// <returns>A completed task after path and catalog validation.</returns>
    public Task OpenWorktreeAsync(WorktreeInfo worktree)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        var catalog = Worktrees.Catalog;
        var displayed = catalog?.Worktrees.FirstOrDefault(item => item.Path.Equals(worktree.Path));
        if (displayed is null || !displayed.Matches(worktree))
        {
            return ReportNoSelectionAsync("Reload worktrees before opening the selected path");
        }

        if (worktree.IsBare)
        {
            return ReportNoSelectionAsync("A bare repository has no worktree to open");
        }

        try
        {
            var directory = CanonicalDirectory.Create(worktree.Path);
            if (directory.Equals(_workingDirectory))
            {
                return ReportNoSelectionAsync("The selected worktree is already open");
            }

            RequestedOpenDirectory = directory;
            Activity = $"Opening worktree {worktree.Path.DisplayText}";
            NotifyChanged();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            NotifyChanged();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a linked worktree from one exact displayed branch or object.
    /// </summary>
    /// <param name="source">The exact displayed starting branch and object.</param>
    /// <param name="targetDirectory">The absolute or current-worktree-relative target directory.</param>
    /// <param name="mode">How the new worktree obtains its HEAD.</param>
    /// <param name="newBranchName">The user-entered branch name required by new-branch mode.</param>
    /// <param name="trackSource">Whether a new branch directly tracks its remote source.</param>
    /// <param name="lockAfterCreation">Whether Git atomically locks the new worktree.</param>
    /// <param name="lockReason">The optional literal lock reason.</param>
    /// <param name="openAfterCreation">Whether the new worktree opens after successful creation.</param>
    /// <param name="cancellationToken">Signals creation cancellation.</param>
    /// <returns>A task that completes after Git-owned creation and reconciliation.</returns>
    public async Task AddWorktreeAsync(
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
        var catalog = Worktrees.Catalog;
        if (catalog is null)
        {
            await ReportNoSelectionAsync("Reload worktrees before creating one").ConfigureAwait(false);
            return;
        }

        CanonicalDirectory? createdDirectory = null;
        await RunAsync(
            $"Creating linked worktree at {TerminalTextSanitizer.Sanitize(targetDirectory)}...",
            "Created linked worktree",
            async token =>
            {
                var validatedName = mode == WorktreeAddMode.NewBranch
                    ? await _branchService.ValidateLocalNameAsync(
                        _workingDirectory,
                        newBranchName ?? string.Empty,
                        token).ConfigureAwait(false)
                    : null;
                var result = await _worktreeService.AddAsync(
                    _workingDirectory,
                    catalog,
                    new WorktreeAddRequest(
                        targetDirectory,
                        source,
                        mode,
                        validatedName,
                        trackSource,
                        lockAfterCreation,
                        lockReason),
                    token).ConfigureAwait(false);
                createdDirectory = result.Directory;
                return result.Operation;
            },
            cancellationToken,
            beforeScan: ClearWorktreeDependentCatalogs).ConfigureAwait(false);
        if (openAfterCreation && createdDirectory is not null)
        {
            RequestedOpenDirectory = createdDirectory;
            Activity = "Opening created linked worktree";
            NotifyChanged();
        }
    }

    /// <summary>
    /// Moves one exact linked worktree to a new canonical target.
    /// </summary>
    /// <param name="worktree">The exact displayed linked worktree.</param>
    /// <param name="targetDirectory">The absolute or current-worktree-relative new location.</param>
    /// <param name="cancellationToken">Signals movement cancellation.</param>
    /// <returns>A task that completes after Git-owned movement and reconciliation.</returns>
    public Task MoveWorktreeAsync(
        WorktreeInfo worktree,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(targetDirectory);
        var catalog = Worktrees.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload worktrees before moving one")
            : RunAsync(
                $"Moving linked worktree to {TerminalTextSanitizer.Sanitize(targetDirectory)}...",
                "Moved linked worktree",
                async token => (await _worktreeService.MoveAsync(
                    _workingDirectory,
                    catalog,
                    worktree,
                    targetDirectory,
                    token).ConfigureAwait(false)).Operation,
                cancellationToken,
                beforeScan: ClearWorktreeDependentCatalogs);
    }

    /// <summary>
    /// Locks one exact linked worktree with an optional literal reason.
    /// </summary>
    /// <param name="worktree">The exact displayed linked worktree.</param>
    /// <param name="reason">The optional literal lock reason.</param>
    /// <param name="cancellationToken">Signals lock cancellation.</param>
    /// <returns>A task that completes after Git-owned locking and reconciliation.</returns>
    public Task LockWorktreeAsync(
        WorktreeInfo worktree,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        var catalog = Worktrees.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload worktrees before locking one")
            : RunAsync(
                $"Locking {worktree.Path.DisplayText}...",
                "Locked linked worktree",
                token => _worktreeService.LockAsync(
                    _workingDirectory,
                    catalog,
                    worktree,
                    reason,
                    token),
                cancellationToken,
                beforeScan: ClearWorktreeDependentCatalogs);
    }

    /// <summary>
    /// Unlocks one exact linked worktree after catalog revalidation.
    /// </summary>
    /// <param name="worktree">The exact displayed linked worktree.</param>
    /// <param name="cancellationToken">Signals unlock cancellation.</param>
    /// <returns>A task that completes after Git-owned unlocking and reconciliation.</returns>
    public Task UnlockWorktreeAsync(
        WorktreeInfo worktree,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        var catalog = Worktrees.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload worktrees before unlocking one")
            : RunAsync(
                $"Unlocking {worktree.Path.DisplayText}...",
                "Unlocked linked worktree",
                token => _worktreeService.UnlockAsync(
                    _workingDirectory,
                    catalog,
                    worktree,
                    token),
                cancellationToken,
                beforeScan: ClearWorktreeDependentCatalogs);
    }

    /// <summary>
    /// Captures exact status and submodule data for linked-worktree removal confirmation.
    /// </summary>
    /// <param name="worktree">The exact displayed linked worktree.</param>
    /// <param name="cancellationToken">Signals removal inspection cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    public async Task<WorktreeRemovalPlan?> PrepareWorktreeRemovalAsync(
        WorktreeInfo worktree,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        var catalog = Worktrees.Catalog;
        if (catalog is null)
        {
            await ReportNoSelectionAsync("Reload worktrees before preparing removal").ConfigureAwait(false);
            return null;
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return null;
        }

        Activity = $"Inspecting {worktree.Path.DisplayText} before removal...";
        NotifyChanged();
        try
        {
            var plan = await _worktreeService.PrepareRemovalAsync(
                _workingDirectory,
                catalog,
                worktree,
                cancellationToken).ConfigureAwait(false);
            Activity = plan.RequiresForce
                ? "Removal requires confirmation to delete worktree files or submodules"
                : "Prepared clean linked-worktree removal";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Removes one exact linked worktree after the displayed plan is confirmed.
    /// </summary>
    /// <param name="plan">The exact reviewed worktree status and submodule plan.</param>
    /// <param name="force">Whether deletion of retained worktree content was explicitly confirmed.</param>
    /// <param name="cancellationToken">Signals removal cancellation.</param>
    /// <returns>A task that completes after Git-owned removal and reconciliation.</returns>
    public Task RemoveWorktreeAsync(
        WorktreeRemovalPlan plan,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return RunAsync(
            $"Removing {plan.Worktree.Path.DisplayText}...",
            "Removed linked worktree",
            token => _worktreeService.RemoveAsync(_workingDirectory, plan, force, token),
            cancellationToken,
            beforeScan: ClearWorktreeDependentCatalogs);
    }

    /// <summary>
    /// Captures Git's exact dry-run list of stale linked-worktree records.
    /// </summary>
    /// <param name="cancellationToken">Signals prune preview cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    public async Task<WorktreePrunePlan?> PrepareWorktreePruneAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return null;
        }

        Activity = "Inspecting stale linked-worktree records...";
        NotifyChanged();
        try
        {
            var plan = await _worktreeService.PreparePruneAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            Activity = plan.StandardOutput.IsEmpty && plan.StandardError.IsEmpty
                ? "No stale linked-worktree records are eligible for pruning"
                : "Prepared exact stale linked-worktree prune preview";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Prunes only the stale worktree records in the confirmed unchanged dry-run output.
    /// </summary>
    /// <param name="plan">The exact dry-run output reviewed by the user.</param>
    /// <param name="cancellationToken">Signals prune cancellation.</param>
    /// <returns>A task that completes after Git-owned pruning and reconciliation.</returns>
    public Task PruneWorktreesAsync(
        WorktreePrunePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return RunAsync(
            "Pruning reviewed stale linked-worktree records...",
            "Pruned stale linked-worktree records",
            token => _worktreeService.PruneAsync(_workingDirectory, plan, token),
            cancellationToken,
            beforeScan: ClearWorktreeDependentCatalogs);
    }

    /// <summary>
    /// Asks Git to repair one existing worktree path selected by the user.
    /// </summary>
    /// <param name="path">The absolute or current-worktree-relative existing directory.</param>
    /// <param name="cancellationToken">Signals repair cancellation.</param>
    /// <returns>A task that completes after Git-owned repair and reconciliation.</returns>
    public Task RepairWorktreeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return RunAsync(
            $"Repairing worktree connection at {TerminalTextSanitizer.Sanitize(path)}...",
            "Repaired linked-worktree connection",
            token => _worktreeService.RepairAsync(_workingDirectory, path, token),
            cancellationToken,
            beforeScan: ClearWorktreeDependentCatalogs);
    }

    /// <summary>
    /// Switches to an exact local branch selected from the displayed catalog.
    /// </summary>
    /// <param name="branch">The exact displayed local branch.</param>
    /// <param name="cancellationToken">Signals checkout cancellation.</param>
    /// <returns>A task that completes after Git-owned checkout and reconciliation.</returns>
    public Task SwitchBranchAsync(BranchInfo branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var catalog = Branches.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload branches before switching")
            : RunAsync(
                $"Switching to {branch.ShortName.DisplayText}...",
                $"Switched to {branch.ShortName.DisplayText}",
                token => _branchService.SwitchAsync(_workingDirectory, catalog, branch, token),
                cancellationToken,
                beforeScan: Branches.Clear);
    }

    /// <summary>
    /// Creates and switches to a local branch from an exact displayed source branch.
    /// </summary>
    /// <param name="source">The exact displayed source branch.</param>
    /// <param name="name">The user-entered local branch name validated by Git.</param>
    /// <param name="trackSource">Whether a remote source becomes the explicit direct upstream.</param>
    /// <param name="cancellationToken">Signals creation and checkout cancellation.</param>
    /// <returns>A task that completes after Git-owned creation and reconciliation.</returns>
    public Task CreateAndSwitchBranchAsync(
        BranchInfo source,
        string name,
        bool trackSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        var catalog = Branches.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload branches before creating a branch")
            : RunAsync(
                $"Creating {TerminalTextSanitizer.Sanitize(name)}...",
                $"Created and switched to {TerminalTextSanitizer.Sanitize(name)}",
                async token =>
                {
                    var validatedName = await _branchService.ValidateLocalNameAsync(
                        _workingDirectory,
                        name,
                        token).ConfigureAwait(false);
                    return await _branchService.CreateAndSwitchAsync(
                        _workingDirectory,
                        catalog,
                        validatedName,
                        source,
                        trackSource,
                        token).ConfigureAwait(false);
                },
                cancellationToken,
                beforeScan: Branches.Clear);
    }

    /// <summary>
    /// Detaches HEAD at the exact target of a displayed source branch.
    /// </summary>
    /// <param name="source">The exact displayed source branch.</param>
    /// <param name="cancellationToken">Signals detached checkout cancellation.</param>
    /// <returns>A task that completes after Git-owned checkout and reconciliation.</returns>
    public Task DetachBranchAsync(BranchInfo source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var catalog = Branches.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload branches before detaching HEAD")
            : RunAsync(
                $"Detaching at {source.TargetObjectId.ToString()[..12]}...",
                $"Detached HEAD at {source.TargetObjectId.ToString()[..12]}",
                token => _branchService.DetachAsync(_workingDirectory, catalog, source, token),
                cancellationToken,
                beforeScan: Branches.Clear);
    }

    /// <summary>
    /// Renames an exact displayed local branch to a Git-validated user-entered name.
    /// </summary>
    /// <param name="branch">The exact displayed local branch.</param>
    /// <param name="newName">The user-entered destination local branch name.</param>
    /// <param name="cancellationToken">Signals rename cancellation.</param>
    /// <returns>A task that completes after Git-owned rename and reconciliation.</returns>
    public Task RenameBranchAsync(
        BranchInfo branch,
        string newName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(newName);
        var catalog = Branches.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload branches before renaming")
            : RunAsync(
                $"Renaming {branch.ShortName.DisplayText}...",
                $"Renamed branch to {TerminalTextSanitizer.Sanitize(newName)}",
                async token =>
                {
                    var validatedName = await _branchService.ValidateLocalNameAsync(
                        _workingDirectory,
                        newName,
                        token).ConfigureAwait(false);
                    return await _branchService.RenameAsync(
                        _workingDirectory,
                        catalog,
                        branch,
                        validatedName,
                        token).ConfigureAwait(false);
                },
                cancellationToken,
                beforeScan: Branches.Clear);
    }

    /// <summary>
    /// Deletes an exact displayed unoccupied local branch with the selected mergedness policy.
    /// </summary>
    /// <param name="branch">The exact displayed local branch.</param>
    /// <param name="mode">The safe or explicitly confirmed force policy.</param>
    /// <param name="cancellationToken">Signals deletion cancellation.</param>
    /// <returns>A task that completes after Git-owned deletion and reconciliation.</returns>
    public Task DeleteBranchAsync(
        BranchInfo branch,
        BranchDeleteMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var catalog = Branches.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload branches before deleting")
            : RunAsync(
                $"Deleting {branch.ShortName.DisplayText}...",
                $"Deleted {branch.ShortName.DisplayText}",
                token => _branchService.DeleteAsync(_workingDirectory, catalog, branch, mode, token),
                cancellationToken,
                beforeScan: Branches.Clear);
    }

    /// <summary>
    /// Resolves a typed revision and resets the exact current branch with the confirmed mode.
    /// </summary>
    /// <param name="branch">The exact displayed current local branch.</param>
    /// <param name="revision">The untrusted user-entered revision expression.</param>
    /// <param name="mode">The confirmed soft, mixed, or hard reset mode.</param>
    /// <param name="cancellationToken">Signals resolution and reset cancellation.</param>
    /// <returns>A task that completes after Git-owned reset and reconciliation.</returns>
    public Task ResetCurrentBranchAsync(
        BranchInfo branch,
        string revision,
        BranchResetMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(revision);
        if (string.IsNullOrWhiteSpace(revision) || revision.Contains('\0', StringComparison.Ordinal))
        {
            return ReportNoSelectionAsync("Enter a nonempty revision without NUL characters");
        }

        var catalog = Branches.Catalog;
        var candidate = Revision.Create(revision.Trim());
        return catalog is null
            ? ReportNoSelectionAsync("Reload branches before resetting")
            : RunAsync(
                $"Resolving {TerminalTextSanitizer.Sanitize(candidate.Value)}...",
                $"Reset {branch.ShortName.DisplayText} with {mode.ToString().ToLowerInvariant()} mode",
                async token =>
                {
                    var resolved = await _revisionResolver.ResolveCommitAsync(
                        _workingDirectory,
                        candidate,
                        token).ConfigureAwait(false);
                    return await _branchService.ResetCurrentAsync(
                        _workingDirectory,
                        catalog,
                        branch,
                        resolved.CommitObjectId,
                        mode,
                        token).ConfigureAwait(false);
                },
                cancellationToken,
                beforeScan: Branches.Clear);
    }

    /// <summary>
    /// Prepares an exact selected-branch merge confirmation without mutating repository state.
    /// </summary>
    /// <param name="source">The exact displayed source branch.</param>
    /// <param name="cancellationToken">Signals merge-plan capture cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    public async Task<MergePlan?> PrepareMergeAsync(
        BranchInfo source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var catalog = Branches.Catalog;
        if (catalog is null)
        {
            await ReportNoSelectionAsync("Reload branches before preparing a merge").ConfigureAwait(false);
            return null;
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return null;
        }

        Activity = $"Preparing exact merge of {source.ShortName.DisplayText}...";
        NotifyChanged();
        try
        {
            var plan = await _mergeService.PrepareAsync(
                _workingDirectory,
                catalog,
                source,
                cancellationToken).ConfigureAwait(false);
            Activity = $"Prepared merge of {source.ShortName.DisplayText} {source.TargetObjectId.ToString()[..12]}";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Executes one exact confirmed merge with validated typed options.
    /// </summary>
    /// <param name="plan">The exact merge confirmation displayed to the user.</param>
    /// <param name="options">The validated typed merge options.</param>
    /// <param name="cancellationToken">Signals merge execution cancellation.</param>
    /// <returns>A task that completes after Git-owned merge and reconciliation.</returns>
    public Task MergeAsync(
        MergePlan plan,
        MergeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        return RunAsync(
            $"Merging exact {plan.Source.ShortName.DisplayText} {plan.Source.TargetObjectId.ToString()[..12]}...",
            $"Merged {plan.Source.ShortName.DisplayText}",
            async token =>
            {
                var result = await _mergeService.ExecuteAsync(
                    _workingDirectory,
                    plan,
                    options,
                    token).ConfigureAwait(false);
                if (result.Outcome != MergeOutcome.Completed)
                {
                    await ApplyPendingMergeMessageAsync(result.HasMergeHead, token).ConfigureAwait(false);
                }

                _completionActivityOverride = result.Outcome switch
                {
                    MergeOutcome.Completed => $"Merged {plan.Source.ShortName.DisplayText}",
                    MergeOutcome.StoppedBeforeCommit => "Merge prepared; review the staged result and commit",
                    MergeOutcome.SquashPrepared => "Squash result prepared; review the staged result and commit",
                    MergeOutcome.Conflicts => "Merge stopped with conflicts; resolve and stage each result",
                    _ => throw new InvalidOperationException("Git returned an unsupported merge outcome."),
                };
                return result.Operation;
            },
            cancellationToken,
            beforeScan: Branches.Clear);
    }

    /// <summary>
    /// Loads one stable exact configured remote catalog for the remote workspace.
    /// </summary>
    /// <param name="cancellationToken">Signals catalog capture cancellation.</param>
    /// <returns>A task that completes after controlled remote state is current.</returns>
    public async Task LoadRemotesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return;
        }

        Remotes.Clear();
        Activity = "Loading exact remote configuration...";
        NotifyChanged();
        try
        {
            var catalog = await _remoteService.CaptureAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            Remotes.ApplyCatalog(catalog);
            Activity = catalog.Remotes.IsEmpty
                ? "No configured remotes"
                : $"Loaded {catalog.Remotes.Length} {(catalog.Remotes.Length == 1 ? "remote" : "remotes")}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Focuses one visible exact remote row.
    /// </summary>
    /// <param name="index">The absolute filtered remote row index.</param>
    /// <returns>A completed task after controlled focus publication.</returns>
    public Task FocusRemoteAsync(int index)
    {
        Remotes.Focus(index);
        NotifyChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds one Git-validated remote name and exact user-entered URL.
    /// </summary>
    /// <param name="name">The user-entered remote name.</param>
    /// <param name="url">The user-entered remote URL.</param>
    /// <param name="cancellationToken">Signals remote-add cancellation.</param>
    /// <returns>A task that completes after Git-owned addition and reconciliation.</returns>
    public Task AddRemoteAsync(string name, string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(url);
        var catalog = Remotes.Catalog;
        if (catalog is null)
        {
            return ReportNoSelectionAsync("Reload remotes before adding one");
        }

        RemoteUrl remoteUrl;
        try
        {
            remoteUrl = RemoteUrl.FromText(url);
        }
        catch (ArgumentException exception)
        {
            return ReportNoSelectionAsync(exception.Message);
        }

        return RunAsync(
            $"Adding remote {TerminalTextSanitizer.Sanitize(name)}...",
            $"Added remote {TerminalTextSanitizer.Sanitize(name)}",
            async token =>
            {
                var validatedName = await _remoteService.ValidateNameAsync(
                    _workingDirectory,
                    name,
                    token).ConfigureAwait(false);
                var result = await _remoteService.AddAsync(
                    _workingDirectory,
                    catalog,
                    validatedName,
                    remoteUrl,
                    token).ConfigureAwait(false);
                var redactionCatalog = new RemoteCatalog(
                [
                    .. catalog.Remotes
                        .Append(new RemoteInfo(validatedName, [remoteUrl], [remoteUrl]))
                        .OrderBy(static remote => remote.Name),
                ]);
                SetTransportOutput($"Added {validatedName.DisplayText}", result, redactionCatalog);
                return result;
            },
            cancellationToken,
            beforeScan: ClearRemoteDependentCatalogs);
    }

    /// <summary>
    /// Removes one exact displayed remote after cancel-first user confirmation.
    /// </summary>
    /// <param name="remote">The exact displayed remote to remove.</param>
    /// <param name="cancellationToken">Signals remote-removal cancellation.</param>
    /// <returns>A task that completes after Git-owned removal and reconciliation.</returns>
    public Task RemoveRemoteAsync(RemoteInfo remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var catalog = Remotes.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload remotes before removing one")
            : RunAsync(
                $"Removing remote {remote.Name.DisplayText}...",
                $"Removed remote {remote.Name.DisplayText}",
                async token =>
                {
                    var result = await _remoteService.RemoveAsync(
                        _workingDirectory,
                        catalog,
                        remote,
                        token).ConfigureAwait(false);
                    SetTransportOutput($"Removed {remote.Name.DisplayText}", result, catalog);
                    return result;
                },
                cancellationToken,
                beforeScan: ClearRemoteDependentCatalogs);
    }

    /// <summary>
    /// Fetches one exact displayed remote with validated typed options.
    /// </summary>
    /// <param name="remote">The exact displayed remote to fetch.</param>
    /// <param name="options">The validated typed fetch options.</param>
    /// <param name="cancellationToken">Signals fetch cancellation.</param>
    /// <returns>A task that completes after Git-owned transport and reconciliation.</returns>
    public Task FetchRemoteAsync(
        RemoteInfo remote,
        FetchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(options);
        var catalog = Remotes.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload remotes before fetching")
            : RunAsync(
                $"Fetching {remote.Name.DisplayText}...",
                $"Fetched {remote.Name.DisplayText}",
                async token =>
                {
                    var result = await _remoteService.FetchAsync(
                        _workingDirectory,
                        catalog,
                        remote,
                        options,
                        token).ConfigureAwait(false);
                    SetTransportOutput($"Fetch {remote.Name.DisplayText}", result, catalog);
                    return result;
                },
                cancellationToken,
                beforeScan: ClearRemoteDependentCatalogs);
    }

    /// <summary>
    /// Fetches every exact displayed configured remote with validated typed options.
    /// </summary>
    /// <param name="options">The validated typed fetch options.</param>
    /// <param name="cancellationToken">Signals fetch-all cancellation.</param>
    /// <returns>A task that completes after Git-owned transport and reconciliation.</returns>
    public Task FetchAllRemotesAsync(FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var catalog = Remotes.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload remotes before fetching all")
            : catalog.Remotes.IsEmpty
                ? ReportNoSelectionAsync("No configured remotes to fetch")
                : RunAsync(
                    "Fetching all configured remotes...",
                    "Fetched all configured remotes",
                    async token =>
                    {
                        var result = await _remoteService.FetchAllAsync(
                            _workingDirectory,
                            catalog,
                            options,
                            token).ConfigureAwait(false);
                        SetTransportOutput("Fetch all remotes", result, catalog);
                        return result;
                    },
                    cancellationToken,
                    beforeScan: ClearRemoteDependentCatalogs);
    }

    /// <summary>
    /// Prepares exact Git dry-run output for one selected remote prune confirmation.
    /// </summary>
    /// <param name="remote">The exact displayed remote to preview.</param>
    /// <param name="cancellationToken">Signals prune-preview cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    public async Task<RemotePrunePlan?> PreparePruneRemoteAsync(
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var catalog = Remotes.Catalog;
        if (catalog is null)
        {
            await ReportNoSelectionAsync("Reload remotes before preparing prune").ConfigureAwait(false);
            return null;
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return null;
        }

        Activity = $"Preparing prune preview for {remote.Name.DisplayText}...";
        NotifyChanged();
        try
        {
            var plan = await _remoteService.PreparePruneAsync(
                _workingDirectory,
                catalog,
                remote,
                cancellationToken).ConfigureAwait(false);
            SetTransportOutput($"Prune preview {remote.Name.DisplayText}", plan.Preview, catalog);
            Activity = $"Prepared prune preview for {remote.Name.DisplayText}";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Prunes one exact confirmed remote against its displayed dry-run plan.
    /// </summary>
    /// <param name="plan">The exact prune confirmation displayed to the user.</param>
    /// <param name="cancellationToken">Signals prune cancellation.</param>
    /// <returns>A task that completes after Git-owned pruning and reconciliation.</returns>
    public Task PruneRemoteAsync(RemotePrunePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return RunAsync(
            $"Pruning stale refs for {plan.Remote.Name.DisplayText}...",
            $"Pruned stale refs for {plan.Remote.Name.DisplayText}",
            async token =>
            {
                var result = await _remoteService.PruneAsync(
                    _workingDirectory,
                    plan,
                    token).ConfigureAwait(false);
                SetTransportOutput(
                    $"Prune {plan.Remote.Name.DisplayText}",
                    result,
                    plan.Catalog);
                return result;
            },
            cancellationToken,
            beforeScan: ClearRemoteDependentCatalogs);
    }

    /// <summary>
    /// Resolves one configured push URL into an exact local or SSH initialization plan.
    /// </summary>
    /// <param name="remote">The exact displayed configured remote.</param>
    /// <param name="configuredUrlIndex">The selected configured push-URL index.</param>
    /// <param name="cancellationToken">Signals initialization planning cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    public async Task<RemoteInitializationPlan?> PrepareRemoteInitializationAsync(
        RemoteInfo remote,
        int configuredUrlIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var catalog = Remotes.Catalog;
        if (catalog is null)
        {
            await ReportNoSelectionAsync(
                "Reload remotes before preparing repository initialization").ConfigureAwait(false);
            return null;
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync(
                "Another repository operation is already running").ConfigureAwait(false);
            return null;
        }

        Activity = $"Inspecting initialization target for {remote.Name.DisplayText}...";
        NotifyChanged();
        try
        {
            var plan = await _remoteInitializationService.PrepareAsync(
                _workingDirectory,
                catalog,
                remote,
                configuredUrlIndex,
                cancellationToken).ConfigureAwait(false);
            Activity = $"Prepared exact bare-repository target for {remote.Name.DisplayText}";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Creates one exact confirmed local or SSH bare repository without changing the current repository.
    /// </summary>
    /// <param name="plan">The exact initialization plan displayed to the user.</param>
    /// <param name="cancellationToken">Signals initialization cancellation.</param>
    /// <returns>A task that completes after exact target creation and verification.</returns>
    public async Task InitializeRemoteAsync(
        RemoteInitializationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync(
                "Another repository operation is already running").ConfigureAwait(false);
            return;
        }

        Activity = $"Initializing exact bare repository for {plan.Remote.Name.DisplayText}...";
        NotifyChanged();
        try
        {
            var result = await _remoteInitializationService.InitializeAsync(
                _workingDirectory,
                plan,
                cancellationToken).ConfigureAwait(false);
            SetTransportOutput(
                $"Initialized {plan.Remote.Name.DisplayText}",
                result,
                plan.Catalog,
                [plan.Target.Url]);
            Activity = $"Initialized and verified bare repository for {plan.Remote.Name.DisplayText}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Prepares one exact Git-resolved default push confirmation for the selected remote.
    /// </summary>
    /// <param name="remote">The exact displayed destination remote.</param>
    /// <param name="followTags">The configured or explicit reachable annotated-tag behavior.</param>
    /// <param name="cancellationToken">Signals push planning cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    public async Task<PushPlan?> PreparePushAsync(
        RemoteInfo remote,
        GitOptionOverride followTags,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var catalog = Remotes.Catalog;
        if (catalog is null)
        {
            await ReportNoSelectionAsync("Reload remotes before preparing a push").ConfigureAwait(false);
            return null;
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return null;
        }

        Activity = $"Resolving exact default push for {remote.Name.DisplayText}...";
        NotifyChanged();
        try
        {
            var plan = await _pushService.PrepareAsync(
                _workingDirectory,
                catalog,
                remote,
                followTags,
                cancellationToken).ConfigureAwait(false);
            Activity = $"Prepared {plan.Updates.Length} exact push {(plan.Updates.Length == 1 ? "update" : "updates")}";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Executes one exact confirmed push with validated typed safety and upstream choices.
    /// </summary>
    /// <param name="plan">The exact push plan displayed to the user.</param>
    /// <param name="options">The validated typed push options.</param>
    /// <param name="cancellationToken">Signals push cancellation.</param>
    /// <returns>A task that completes after Git-owned push and reconciliation.</returns>
    public Task PushAsync(
        PushPlan plan,
        PushOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        return RunAsync(
            $"Pushing {plan.Updates.Length} exact {(plan.Updates.Length == 1 ? "update" : "updates")} to {plan.Remote.Name.DisplayText}...",
            $"Pushed to {plan.Remote.Name.DisplayText}",
            async token =>
            {
                var result = await _pushService.PushAsync(
                    _workingDirectory,
                    plan,
                    options,
                    token).ConfigureAwait(false);
                SetTransportOutput(
                    $"Push {plan.Remote.Name.DisplayText}",
                    result,
                    plan.Catalog,
                    [.. plan.Updates[0].Destinations.Select(static destination => destination.Url)]);
                return result;
            },
            cancellationToken,
            beforeScan: ClearRemoteDependentCatalogs);
    }

    /// <summary>
    /// Loads one stable complete list of exact local tag refs for tag-push selection.
    /// </summary>
    /// <param name="cancellationToken">Signals local-tag loading cancellation.</param>
    /// <returns>Every exact local tag ref in bytewise order.</returns>
    public async Task<System.Collections.Immutable.ImmutableArray<RefName>> LoadLocalTagsAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return [];
        }

        Activity = "Loading exact local tags...";
        NotifyChanged();
        try
        {
            var tags = await _pushService.CaptureLocalTagsAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            Activity = $"Loaded {tags.Length} local {(tags.Length == 1 ? "tag" : "tags")}";
            return tags;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return [];
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Loads the stable union of exact branch refs advertised by a selected remote's push URLs.
    /// </summary>
    /// <param name="remote">The exact displayed destination remote.</param>
    /// <param name="cancellationToken">Signals remote-branch loading cancellation.</param>
    /// <returns>Every exact advertised remote branch ref in bytewise order.</returns>
    public async Task<System.Collections.Immutable.ImmutableArray<RefName>> LoadRemoteBranchesAsync(
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var catalog = Remotes.Catalog;
        if (catalog is null)
        {
            await ReportNoSelectionAsync("Reload remotes before loading remote branches").ConfigureAwait(false);
            return [];
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return [];
        }

        Activity = $"Loading exact branches from {remote.Name.DisplayText}...";
        NotifyChanged();
        try
        {
            var branches = await _pushService.CaptureRemoteBranchesAsync(
                _workingDirectory,
                catalog,
                remote,
                cancellationToken).ConfigureAwait(false);
            Activity = $"Loaded {branches.Length} remote {(branches.Length == 1 ? "branch" : "branches")}";
            return branches;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return [];
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Prepares one exact selected local tag update for the displayed remote.
    /// </summary>
    /// <param name="remote">The exact displayed destination remote.</param>
    /// <param name="tag">The exact fully qualified local tag ref.</param>
    /// <param name="cancellationToken">Signals tag-push planning cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    public async Task<PushPlan?> PrepareTagPushAsync(
        RemoteInfo remote,
        RefName tag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(tag);
        var catalog = Remotes.Catalog;
        if (catalog is null)
        {
            await ReportNoSelectionAsync("Reload remotes before preparing a tag push").ConfigureAwait(false);
            return null;
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return null;
        }

        Activity = $"Preparing exact tag push for {tag.DisplayText}...";
        NotifyChanged();
        try
        {
            var plan = await _pushService.PrepareTagAsync(
                _workingDirectory,
                catalog,
                remote,
                tag,
                cancellationToken).ConfigureAwait(false);
            Activity = $"Prepared exact tag push for {tag.DisplayText}";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Prepares one exact selected advertised remote branch deletion.
    /// </summary>
    /// <param name="remote">The exact displayed destination remote.</param>
    /// <param name="branch">The exact fully qualified advertised branch ref.</param>
    /// <param name="cancellationToken">Signals deletion planning cancellation.</param>
    /// <returns>The exact plan, or <see langword="null"/> when preparation cannot complete.</returns>
    public async Task<PushPlan?> PrepareRemoteBranchDeletionAsync(
        RemoteInfo remote,
        RefName branch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(branch);
        var catalog = Remotes.Catalog;
        if (catalog is null)
        {
            await ReportNoSelectionAsync("Reload remotes before preparing a branch deletion").ConfigureAwait(false);
            return null;
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return null;
        }

        Activity = $"Preparing exact deletion of {branch.DisplayText}...";
        NotifyChanged();
        try
        {
            var plan = await _pushService.PrepareRemoteBranchDeletionAsync(
                _workingDirectory,
                catalog,
                remote,
                branch,
                cancellationToken).ConfigureAwait(false);
            Activity = $"Prepared exact deletion of {branch.DisplayText}";
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Loads one stable exact stash catalog and the focused entry's patch preview.
    /// </summary>
    /// <param name="cancellationToken">Signals catalog and preview capture cancellation.</param>
    /// <returns>A task that completes after controlled stash state is current.</returns>
    public async Task LoadStashesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return;
        }

        Stashes.Clear();
        Activity = "Loading stashes and exact worktree state...";
        NotifyChanged();
        try
        {
            var catalog = await _stashService.CaptureAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            Stashes.ApplyCatalog(catalog);
            await CaptureFocusedStashPreviewAsync(cancellationToken).ConfigureAwait(false);
            Activity = catalog.Entries.IsEmpty
                ? "No stashes"
                : $"Loaded {catalog.Entries.Length} {(catalog.Entries.Length == 1 ? "stash" : "stashes")}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Applies a filter and loads the newly focused exact stash patch when needed.
    /// </summary>
    /// <param name="filter">The latest user-entered incremental filter text.</param>
    /// <param name="cancellationToken">Signals patch preview capture cancellation.</param>
    /// <returns>A task that completes after filter, focus, and preview state are current.</returns>
    public Task FilterStashesAsync(string filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Stashes.SetFilter(filter);
        NotifyChanged();
        return ReloadFocusedStashPreviewAsync(cancellationToken);
    }

    /// <summary>
    /// Focuses one visible stash row and loads its exact patch preview.
    /// </summary>
    /// <param name="index">The absolute filtered stash row index.</param>
    /// <param name="cancellationToken">Signals patch preview capture cancellation.</param>
    /// <returns>A task that completes after focus and preview state are current.</returns>
    public Task FocusStashAsync(int index, CancellationToken cancellationToken)
    {
        Stashes.Focus(index);
        NotifyChanged();
        return ReloadFocusedStashPreviewAsync(cancellationToken);
    }

    /// <summary>
    /// Creates a stash from the current displayed repository generation and typed options.
    /// </summary>
    /// <param name="options">The validated noninteractive stash-create options.</param>
    /// <param name="cancellationToken">Signals stash creation cancellation.</param>
    /// <returns>A task that completes after Git-owned creation and reconciliation.</returns>
    public Task CreateStashAsync(StashCreateOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var precondition = State.Snapshot.Precondition;
        return precondition is null
            ? ReportNoSelectionAsync("Refresh status before creating a stash")
            : RunAsync(
                "Saving current changes to a stash...",
                "Saved current changes to a stash",
                token => _stashService.CreateAsync(_workingDirectory, precondition, options, token),
                cancellationToken,
                beforeScan: Stashes.Clear);
    }

    /// <summary>
    /// Applies one exact displayed stash without removing it from the reflog.
    /// </summary>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="restoreIndex">Whether Git should also restore its index state.</param>
    /// <param name="cancellationToken">Signals stash application cancellation.</param>
    /// <returns>A task that completes after Git-owned application and reconciliation.</returns>
    public Task ApplyStashAsync(
        StashInfo stash,
        bool restoreIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stash);
        var catalog = Stashes.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload stashes before applying")
            : RunAsync(
                $"Applying {stash.Selector} {stash.ObjectId.ToString()[..12]}...",
                $"Applied {stash.Selector} {stash.ObjectId.ToString()[..12]}",
                token => _stashService.ApplyAsync(
                    _workingDirectory,
                    catalog,
                    stash,
                    restoreIndex,
                    token),
                cancellationToken,
                beforeScan: Stashes.Clear);
    }

    /// <summary>
    /// Pops one exact displayed stash after a cancel-first user confirmation.
    /// </summary>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="restoreIndex">Whether Git should also restore its index state.</param>
    /// <param name="cancellationToken">Signals stash pop cancellation.</param>
    /// <returns>A task that completes after Git-owned pop and reconciliation.</returns>
    public Task PopStashAsync(
        StashInfo stash,
        bool restoreIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stash);
        var catalog = Stashes.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload stashes before popping")
            : RunAsync(
                $"Popping {stash.Selector} {stash.ObjectId.ToString()[..12]}...",
                $"Popped {stash.Selector} {stash.ObjectId.ToString()[..12]}",
                token => _stashService.PopAsync(
                    _workingDirectory,
                    catalog,
                    stash,
                    restoreIndex,
                    token),
                cancellationToken,
                beforeScan: Stashes.Clear);
    }

    /// <summary>
    /// Drops one exact displayed stash after a cancel-first user confirmation.
    /// </summary>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="cancellationToken">Signals stash deletion cancellation.</param>
    /// <returns>A task that completes after Git-owned deletion and reconciliation.</returns>
    public Task DropStashAsync(StashInfo stash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stash);
        var catalog = Stashes.Catalog;
        return catalog is null
            ? ReportNoSelectionAsync("Reload stashes before dropping")
            : RunAsync(
                $"Dropping {stash.Selector} {stash.ObjectId.ToString()[..12]}...",
                $"Dropped {stash.Selector} {stash.ObjectId.ToString()[..12]}",
                token => _stashService.DropAsync(_workingDirectory, catalog, stash, token),
                cancellationToken,
                beforeScan: Stashes.Clear);
    }

    /// <summary>
    /// Stages checked worktree paths, or the focused path when no rows are checked.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task StageAsync(CancellationToken cancellationToken)
    {
        var paths = State.GetPathsToStage();
        if (ContainsUnmergedPath(paths))
        {
            return ReportNoSelectionAsync("Use the conflict result editor to stage unresolved paths");
        }

        return paths.Count == 0
            ? ReportNoSelectionAsync("Nothing to stage")
            : RunAsync(
                $"Staging {FormatPathCount(paths.Count)}...",
                $"Staged {FormatPathCount(paths.Count)}",
                token => _indexMutationService.StageAsync(_workingDirectory, paths, token),
                cancellationToken);
    }

    /// <summary>
    /// Stages every worktree change regardless of presentation filtering or selection.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task StageAllAsync(CancellationToken cancellationToken)
        => HasUnmergedEntries
            ? ReportNoSelectionAsync("Resolve and stage every conflict before staging all changes")
            : State.UnstagedItems.Length == 0
            ? ReportNoSelectionAsync("Nothing to stage")
            : RunAsync(
                "Staging all changes...",
                "Staged all changes",
                token => _indexMutationService.StageAllAsync(_workingDirectory, token),
                cancellationToken);

    /// <summary>
    /// Unstages checked index paths, or the focused path when no rows are checked.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task UnstageAsync(CancellationToken cancellationToken)
    {
        var paths = State.GetPathsToUnstage();
        if (ContainsUnmergedPath(paths))
        {
            return ReportNoSelectionAsync("Abort or resolve the operation instead of unstaging conflict stages");
        }

        var snapshot = State.Snapshot;
        return paths.Count == 0
            ? ReportNoSelectionAsync("Nothing to unstage")
            : RunAsync(
                $"Unstaging {FormatPathCount(paths.Count)}...",
                $"Unstaged {FormatPathCount(paths.Count)}",
                token => _indexMutationService.UnstageAsync(snapshot, _workingDirectory, paths, token),
                cancellationToken);
    }

    /// <summary>
    /// Unstages every index entry to HEAD or clears the complete unborn index.
    /// </summary>
    /// <param name="cancellationToken">Signals mutation cancellation.</param>
    /// <returns>A task that completes after mutation and reconciliation.</returns>
    public Task UnstageAllAsync(CancellationToken cancellationToken)
    {
        if (HasUnmergedEntries)
        {
            return ReportNoSelectionAsync("Abort or resolve the operation instead of unstaging all conflict stages");
        }

        var snapshot = State.Snapshot;
        return State.StagedItems.Length == 0
            ? ReportNoSelectionAsync("Nothing to unstage")
            : RunAsync(
                "Unstaging all changes...",
                "Unstaged all changes",
                token => _indexMutationService.UnstageAllAsync(snapshot, _workingDirectory, token),
                cancellationToken);
    }

    /// <summary>
    /// Toggles amend mode and refreshes its local remote-tracking publication warning when enabling it.
    /// </summary>
    /// <param name="cancellationToken">Signals amend-safety inspection cancellation.</param>
    /// <returns>A task that completes after the lifted option and warning are current.</returns>
    public async Task ToggleAmendAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return;
        }

        if (CommitOptions.Amend)
        {
            CommitOptions.ToggleAmend();
            PublishedAmendWarning = null;
            Activity = "Amend disabled";
            NotifyChanged();
            return;
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            await ReportNoSelectionAsync("Another repository operation is already running").ConfigureAwait(false);
            return;
        }

        Activity = "Checking whether HEAD is locally published...";
        NotifyChanged();
        try
        {
            PublishedAmendWarning = await _publishedAmendService.FindAsync(
                _workingDirectory,
                State.Snapshot.HeadObjectId,
                cancellationToken).ConfigureAwait(false);
            CommitOptions.ToggleAmend();
            Activity = PublishedAmendWarning is null
                ? "Amend enabled"
                : "Amend enabled; confirmation required for locally published HEAD";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    /// <summary>
    /// Aborts the exact merge state displayed and confirmed by the view through Git porcelain.
    /// </summary>
    /// <param name="confirmedWarning">The exact merge warning displayed by the confirmation dialog.</param>
    /// <param name="cancellationToken">Signals abort cancellation.</param>
    /// <returns>A task that completes after Git-owned abort and repository reconciliation.</returns>
    public Task AbortMergeAsync(
        MergeAbortWarning confirmedWarning,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmedWarning);
        return !CanAbortMerge
            ? ReportNoSelectionAsync(
                IsBusy
                    ? "Another repository operation is already running"
                    : "No in-progress merge is available to abort")
            : RunAsync(
                "Aborting merge through Git...",
                "Merge aborted",
                token => _mergeAbortService.AbortAsync(
                    _workingDirectory,
                    confirmedWarning,
                    token),
                cancellationToken,
                beforeScan: Conflict.Clear);
    }

    /// <summary>
    /// Commits the current index through Git and retains the editor draft on failure.
    /// </summary>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    public Task CommitAsync(CancellationToken cancellationToken)
        => CommitAsync(
            skipHooks: false,
            confirmedPublishedAmendWarning: null,
            confirmedDetachedHeadWarning: null,
            cancellationToken);

    /// <summary>
    /// Commits after the view explicitly confirms every current detached or publication warning.
    /// </summary>
    /// <param name="confirmedPublishedAmendWarning">The exact publication warning displayed by the view.</param>
    /// <param name="confirmedDetachedHeadWarning">The exact detached HEAD warning displayed by the view.</param>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    public Task CommitAfterWarningsAsync(
        PublishedAmendWarning? confirmedPublishedAmendWarning,
        DetachedHeadWarning? confirmedDetachedHeadWarning,
        CancellationToken cancellationToken)
        => CommitAsync(
            skipHooks: false,
            confirmedPublishedAmendWarning,
            confirmedDetachedHeadWarning,
            cancellationToken);

    /// <summary>
    /// Commits through Git after a separate confirmation requested bypass of its bypassable hooks.
    /// </summary>
    /// <param name="confirmedPublishedAmendWarning">The exact publication warning displayed with the bypass warning.</param>
    /// <param name="confirmedDetachedHeadWarning">The exact detached HEAD warning displayed with the bypass warning.</param>
    /// <param name="cancellationToken">Signals commit cancellation.</param>
    /// <returns>A task that completes after commit verification and reconciliation.</returns>
    public Task CommitWithoutHooksAsync(
        PublishedAmendWarning? confirmedPublishedAmendWarning,
        DetachedHeadWarning? confirmedDetachedHeadWarning,
        CancellationToken cancellationToken)
        => CommitAsync(
            skipHooks: true,
            confirmedPublishedAmendWarning,
            confirmedDetachedHeadWarning,
            cancellationToken);

    private Task CommitAsync(
        bool skipHooks,
        PublishedAmendWarning? confirmedPublishedAmendWarning,
        DetachedHeadWarning? confirmedDetachedHeadWarning,
        CancellationToken cancellationToken)
        => !CanCommit
            ? ReportNoSelectionAsync(
                NeedsCommitTemplateEdit
                    ? "Edit the configured commit template before committing"
                    : "No commit transaction is available")
            : RunAsync(
                skipHooks ? "Committing without bypassable hooks..." : "Committing transaction...",
                "Commit completed",
                token => RunCommitAsync(
                    skipHooks,
                    confirmedPublishedAmendWarning,
                    confirmedDetachedHeadWarning,
                    token),
                cancellationToken);

    /// <summary>
    /// Completes no-commit citool only when the current index contains no unresolved entries.
    /// </summary>
    /// <param name="cancellationToken">Signals completion cancellation.</param>
    /// <returns>A completed task after validation and state publication.</returns>
    public Task CompleteWithoutCommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanCompleteWithoutCommit)
        {
            return ReportNoSelectionAsync(
                IsBusy
                    ? "Another repository operation is already running"
                    : "Resolve and stage every unmerged path before finishing");
        }

        IsCitoolCompleted = true;
        return ReportNoSelectionAsync("Index preparation completed");
    }

    /// <summary>
    /// Flushes the latest recoverable draft and releases repository-session resources.
    /// </summary>
    /// <returns>A value task that completes after pending recovery state is durable.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_changeWatcher is not null)
        {
            await _changeWatcher.DisposeAsync().ConfigureAwait(false);
            _changeWatcher = null;
        }

        CommitMessage.Changed -= HandleCommitMessageChanged;
        CredentialPrompts.Changed -= NotifyChanged;
        _commitDraftStore.PersistenceFailed -= HandleCommitDraftPersistenceFailed;
        try
        {
            await _commitDraftStore.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _revertUndoStore.DiscardAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Conflict.Clear();
                _workTreeDiff?.Dispose();
                _indexDiff?.Dispose();
                CredentialPrompts.Dispose();
                _mutationCoordinator.Dispose();
            }
        }
    }

    private async Task RunRepositoryCareAsync(
        string pendingActivity,
        string successActivity,
        string outputTitle,
        Func<CancellationToken, Task<GitOperationResult>> operation,
        bool reconcileRepository,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            Activity = "Another repository operation is already running";
            NotifyChanged();
            return;
        }

        Activity = pendingActivity;
        NotifyChanged();
        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            Maintenance.SetOutput(outputTitle, result.StandardOutput.Span, result.StandardError.Span);
            var statistics = await _maintenanceService.CaptureStatisticsAsync(
                _workingDirectory,
                cancellationToken).ConfigureAwait(false);
            Maintenance.SetStatistics(statistics);
            if (reconcileRepository)
            {
                await ScanAsync(cancellationToken).ConfigureAwait(false);
            }

            Activity = successActivity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RepositoryMaintenanceException exception)
        {
            Maintenance.SetOutput(
                outputTitle,
                exception.StandardOutput.Span,
                exception.StandardError.Span);
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    private async Task RunAsync(
        string pendingActivity,
        string successActivity,
        Func<CancellationToken, Task<GitOperationResult>>? mutation,
        CancellationToken cancellationToken,
        Action? beforeScan = null,
        bool preserveRevertUndo = false)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            Activity = "Another repository operation is already running";
            NotifyChanged();
            return;
        }

        Activity = pendingActivity;
        _completionActivityOverride = null;
        NotifyChanged();
        try
        {
            if (mutation is not null)
            {
                await mutation(cancellationToken).ConfigureAwait(false);
                if (!preserveRevertUndo)
                {
                    await ClearRevertUndoAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            beforeScan?.Invoke();
            await ScanAsync(cancellationToken).ConfigureAwait(false);
            Activity = _completionActivityOverride ?? successActivity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (exception is DetachedHeadConfirmationException detachedConfirmation)
            {
                DetachedHeadWarning = detachedConfirmation.Warning;
                Activity = "Confirmation required before committing on detached HEAD";
            }
            else if (exception is PublishedAmendConfirmationException publishedConfirmation)
            {
                PublishedAmendWarning = publishedConfirmation.Warning;
                Activity = "Confirmation required before amending a locally published commit";
            }
            else
            {
                Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            }

            if (mutation is not null)
            {
                await TryReconcileAfterFailureAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _completionActivityOverride = null;
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }
    }

    private void StartAutomaticRefresh()
    {
        _changeWatcher = new RepositoryChangeWatcher(
            _repository,
            TryAutomaticRefreshAsync,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(30));
    }

    private async Task<bool> TryAutomaticRefreshAsync(
        bool receivedFilesystemNotification,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            await ScanAsync(cancellationToken).ConfigureAwait(false);
            if (receivedFilesystemNotification)
            {
                Activity = "Updated after external changes";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Activity = $"Automatic refresh failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
            NotifyChanged();
        }

        return true;
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        _generation = _generation.Next();
        var snapshot = await _statusService
            .ScanAsync(_repository, _workingDirectory, _generation, _pathspecs, cancellationToken)
            .ConfigureAwait(false);
        await CaptureDiffsAsync(_generation, cancellationToken).ConfigureAwait(false);
        var snapshotPrecondition = snapshot.Precondition
            ?? throw new InvalidDataException("The refreshed status has no repository precondition.");
        MergeAbortWarning = await _mergeAbortService.FindWarningAsync(
            _workingDirectory,
            snapshotPrecondition,
            cancellationToken).ConfigureAwait(false);
        PublishedAmendWarning = CommitOptions.Amend
            ? await _publishedAmendService.FindAsync(
                _workingDirectory,
                snapshot.HeadObjectId,
                cancellationToken).ConfigureAwait(false)
            : null;
        DetachedHeadWarning = await _detachedHeadWarningService.FindAsync(
            _workingDirectory,
            snapshotPrecondition,
            cancellationToken).ConfigureAwait(false);
        State.ApplySnapshot(snapshot);
        await LoadActiveDiffAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureDiffsAsync(
        OperationGeneration generation,
        CancellationToken cancellationToken)
    {
        var workTreeTask = _rawDiffService.CaptureComparisonAsync(
            _workingDirectory,
            DiffRequest.IndexToWorkTree(_pathspecs),
            generation,
            _diffContextLines,
            cancellationToken);
        var indexTask = _rawDiffService.CaptureComparisonAsync(
            _workingDirectory,
            DiffRequest.HeadToIndex(_pathspecs),
            generation,
            _diffContextLines,
            cancellationToken);
        try
        {
            await Task.WhenAll(workTreeTask, indexTask).ConfigureAwait(false);
        }
        catch
        {
            if (workTreeTask.IsCompletedSuccessfully)
            {
                workTreeTask.Result.Dispose();
            }

            if (indexTask.IsCompletedSuccessfully)
            {
                indexTask.Result.Dispose();
            }

            throw;
        }

        var previousWorkTree = _workTreeDiff;
        var previousIndex = _indexDiff;
        _workTreeDiff = workTreeTask.Result;
        _indexDiff = indexTask.Result;
        previousWorkTree?.Dispose();
        previousIndex?.Dispose();
    }

    private Task LoadActiveDiffAsync(CancellationToken cancellationToken)
        => LoadFocusedDiffAsync(
            State.ActivePane == StatusWorkspacePane.Staged
                ? RawDiffTarget.Index
                : RawDiffTarget.WorkTree,
            cancellationToken);

    private async Task ReloadFocusedStashPreviewAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stashPreviewRequest);
        while (true)
        {
            if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
            {
                return;
            }

            var completedRequest = Volatile.Read(ref _stashPreviewRequest);
            try
            {
                while (true)
                {
                    completedRequest = Volatile.Read(ref _stashPreviewRequest);
                    Activity = "Loading exact stash patch...";
                    NotifyChanged();
                    await CaptureFocusedStashPreviewAsync(cancellationToken).ConfigureAwait(false);
                    if (completedRequest == Volatile.Read(ref _stashPreviewRequest))
                    {
                        break;
                    }
                }

                Activity = "Stash patch loaded";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                var message = TerminalTextSanitizer.Sanitize(exception.Message);
                Stashes.SetPreviewMessage($"Stash patch unavailable: {message}");
                Activity = $"Failed: {message}";
            }
            finally
            {
                Volatile.Write(ref _operationInProgress, 0);
                NotifyChanged();
            }

            if (completedRequest == Volatile.Read(ref _stashPreviewRequest))
            {
                return;
            }
        }
    }

    private async Task CaptureFocusedStashPreviewAsync(CancellationToken cancellationToken)
    {
        var catalog = Stashes.Catalog;
        var stash = Stashes.FocusedItem?.Stash;
        if (catalog is null)
        {
            Stashes.SetPreviewMessage("Reload stashes to inspect an exact patch.");
            return;
        }

        if (stash is null)
        {
            Stashes.SetPreviewMessage(
                catalog.Entries.IsEmpty
                    ? "No stashes are available."
                    : "No stash matches the current filter.");
            return;
        }

        using var patch = await _stashService.ShowAsync(
            _workingDirectory,
            catalog,
            stash,
            cancellationToken).ConfigureAwait(false);
        var length = checked((int)Math.Min(patch.Length, MaximumPresentedPatchBytes));
        var bytes = await patch.ReadSliceAsync(0, length, cancellationToken).ConfigureAwait(false);
        var text = bytes.Length == 0
            ? "This stash contains no patch content to present."
            : RawPatchPresentationDecoder.Decode(bytes, patch.Length > bytes.Length);
        var current = Stashes.FocusedItem?.Stash;
        if (ReferenceEquals(catalog, Stashes.Catalog) && current is not null && current.Matches(stash))
        {
            Stashes.SetPreview(stash, text);
        }
    }

    private async Task LoadFocusedDiffAsync(
        RawDiffTarget target,
        CancellationToken cancellationToken)
    {
        var item = State.FocusedItem;
        var document = target == RawDiffTarget.Index ? _indexDiff : _workTreeDiff;
        var generation = State.Snapshot.Generation;
        var side = target == RawDiffTarget.Index ? "Staged" : "Unstaged";
        if (item is null)
        {
            ClearFocusedPatch();
            Conflict.Clear();
            Diff.SetContent("Diff", "Select a changed path to inspect its patch.", generation);
            return;
        }

        var title = $"{side}: {item.Path.DisplayText}";
        if (item.Entry.Kind == RepositoryStatusEntryKind.Unmerged)
        {
            ClearFocusedPatch();
            await LoadConflictAsync(item.Entry, generation, cancellationToken).ConfigureAwait(false);
            return;
        }

        Conflict.Clear();
        if (document is null || document.Index.Generation != generation)
        {
            ClearFocusedPatch();
            Diff.SetContent(title, "Patch data is not current; refresh the repository.", generation);
            return;
        }

        var file = document.Index.Find(item.Path);
        if (file is null)
        {
            ClearFocusedPatch();
            var message = item.Entry.Kind == RepositoryStatusEntryKind.Untracked
                ? "This untracked path has no Git patch until it is staged or added with intent-to-add."
                : "Git emitted no patch content for this status entry.";
            Diff.SetContent(title, message, generation);
            return;
        }

        if (file.IsBinary)
        {
            _focusedPatchFile = file;
            _focusedPatchTarget = target;
            _focusedPatchGeneration = generation;
            Diff.SetContent(
                title,
                $"Binary patch data retained ({file.Length} exact bytes). Text presentation is disabled.",
                generation);
            return;
        }

        var bytes = await document.ReadFilePrefixAsync(
            file,
            MaximumPresentedPatchBytes,
            cancellationToken).ConfigureAwait(false);
        var text = RawPatchPresentationDecoder.Decode(bytes, file.Length > bytes.Length);
        _focusedPatchFile = file;
        _focusedPatchTarget = target;
        _focusedPatchGeneration = generation;

        Diff.SetContent(title, text, generation);
    }

    private async Task LoadConflictAsync(
        RepositoryStatusEntry entry,
        OperationGeneration generation,
        CancellationToken cancellationToken)
    {
        var title = $"Conflict: {entry.Path.DisplayText}";
        var previousEditor = Conflict.Editor;
        try
        {
            var stages = entry.ConflictStages
                ?? throw new InvalidDataException("Git did not provide exact stages for this unmerged path.");
            if (new[] { stages.Base, stages.Ours, stages.Theirs }
                .Any(static stage => stage is not null &&
                    stage.Mode is not (GitFileMode.RegularFile or GitFileMode.ExecutableFile)))
            {
                throw new InvalidDataException(
                    "Built-in conflict editing supports regular files; use an approved external mergetool for this path.");
            }

            var contents = await _conflictStageContentService
                .LoadAsync(_workingDirectory, stages, cancellationToken)
                .ConfigureAwait(false);
            var document = await _conflictMergeService
                .MergeAsync(_workingDirectory, contents, cancellationToken)
                .ConfigureAwait(false);
            Conflict.SetDocument(entry, document, generation);
            var editor = Conflict.Editor
                ?? throw new InvalidOperationException("Conflict state did not create its required result editor.");
            Diff.SetEditor(title, editor, generation);
            if (!ReferenceEquals(previousEditor, editor))
            {
                var first = Conflict.FindNextUnresolvedChunk(-1);
                if (first >= 0)
                {
                    MoveToConflictChunk(first);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Conflict.Clear();
            var message = TerminalTextSanitizer.Sanitize(exception.Message);
            Diff.SetContent(title, $"Conflict editor unavailable: {message}", generation);
            Activity = $"Conflict editor unavailable: {message}";
        }
    }

    private async Task RunFocusedHunkMutationAsync(
        RawDiffTarget target,
        CancellationToken cancellationToken)
    {
        var hunk = GetFocusedHunk(target);
        var file = _focusedPatchFile;
        var document = target == RawDiffTarget.Index ? _indexDiff : _workTreeDiff;
        if (hunk is null || file is null || document is null || document.Index.Generation != _focusedPatchGeneration)
        {
            await ReportNoSelectionAsync(target == RawDiffTarget.Index
                ? "No staged hunk under the diff cursor"
                : "No unstaged hunk under the diff cursor").ConfigureAwait(false);
            return;
        }

        var selectedPatch = await document.ReadHunkPatchAsync(
            file,
            hunk,
            cancellationToken).ConfigureAwait(false);
        if (target == RawDiffTarget.Index)
        {
            await RunAsync(
                "Unstaging hunk...",
                "Hunk unstaged",
                token => _patchService.UnstageAsync(
                    _workingDirectory,
                    selectedPatch,
                    token),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunAsync(
            "Staging hunk...",
            "Hunk staged",
            token => _patchService.StageAsync(
                _workingDirectory,
                selectedPatch,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private RawPatchHunk? GetFocusedHunk(RawDiffTarget target)
    {
        if (_focusedPatchTarget != target ||
            _focusedPatchGeneration != State.Snapshot.Generation ||
            _focusedPatchFile is null)
        {
            return null;
        }

        var position = Diff.Editor.Document.OffsetToPosition(Diff.Editor.Cursor.Position);
        return _focusedPatchFile.PatchIndex.FindHunkAtLine(position.Line);
    }

    private int GetFocusedConflictChunk()
    {
        if (!IsConflictResolutionActive)
        {
            return -1;
        }

        var position = Diff.Editor.Document.OffsetToPosition(Diff.Editor.Cursor.Position);
        return Conflict.FindChunkAtLine(position.Line - 1);
    }

    private void MoveToConflictChunk(int chunkIndex)
    {
        var editor = Conflict.Editor
            ?? throw new InvalidOperationException("No editable conflict result is active.");
        editor.SetCursorPosition(editor.Document.PositionToOffset(
            new DocumentPosition(Conflict.GetStartLine(chunkIndex) + 1, 1)));
    }

    private async Task RunSelectedLineMutationAsync(
        RawDiffTarget target,
        CancellationToken cancellationToken)
    {
        var selectedLineNumbers = GetSelectedChangedLineNumbers(target);
        var file = _focusedPatchFile;
        var document = target == RawDiffTarget.Index ? _indexDiff : _workTreeDiff;
        if (selectedLineNumbers.Count == 0 || file is null || document is null ||
            document.Index.Generation != _focusedPatchGeneration)
        {
            await ReportNoSelectionAsync(target == RawDiffTarget.Index
                ? "No staged changed lines selected"
                : "No unstaged changed lines selected").ConfigureAwait(false);
            return;
        }

        var selectedPatch = await document.ReadSelectedLinesPatchAsync(
            file,
            selectedLineNumbers,
            target == RawDiffTarget.Index
                ? RawPatchSelectionSide.PreserveNewSide
                : RawPatchSelectionSide.PreserveOldSide,
            cancellationToken).ConfigureAwait(false);
        if (target == RawDiffTarget.Index)
        {
            await RunAsync(
                "Unstaging selected lines...",
                "Selected lines unstaged",
                token => _patchService.UnstageAsync(
                    _workingDirectory,
                    selectedPatch,
                    token),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunAsync(
            "Staging selected lines...",
            "Selected lines staged",
            token => _patchService.StageAsync(
                _workingDirectory,
                selectedPatch,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private Task RunRevertPatchAsync(
        string scope,
        byte[] patch,
        CancellationToken cancellationToken)
        => RunAsync(
            $"Reverting {scope}...",
            $"Reverted {scope}; undo available",
            async token =>
            {
                var result = await _patchService.RevertAsync(
                    _workingDirectory,
                    patch,
                    token).ConfigureAwait(false);
                _revertUndoState = _revertUndoStore.CreateState(patch, result.Precondition);
                try
                {
                    await _revertUndoStore.SaveAsync(_revertUndoState, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    _completionActivityOverride =
                        $"Reverted {scope}; undo available this session only: {TerminalTextSanitizer.Sanitize(exception.Message)}";
                }

                return result.Operation;
            },
            cancellationToken,
            preserveRevertUndo: true);

    private bool HasSelectedChangedLines(RawDiffTarget target)
        => GetSelectedChangedLineNumbers(target).Count > 0;

    private bool HasCurrentFocusedPatch(RawDiffTarget target)
    {
        var document = target == RawDiffTarget.Index ? _indexDiff : _workTreeDiff;
        return _focusedPatchTarget == target &&
            _focusedPatchGeneration == State.Snapshot.Generation &&
            _focusedPatchFile is not null &&
            document is not null &&
            document.Index.Generation == _focusedPatchGeneration;
    }

    private HashSet<int> GetSelectedChangedLineNumbers(RawDiffTarget target)
    {
        var selectedLineNumbers = new HashSet<int>();
        if (_focusedPatchTarget != target ||
            _focusedPatchGeneration != State.Snapshot.Generation ||
            _focusedPatchFile is null)
        {
            return selectedLineNumbers;
        }

        return DiffLineSelectionMapper.GetChangedLineNumbers(
            Diff.Editor,
            _focusedPatchFile.PatchIndex);
    }

    private Task MoveFocusedHunkAsync(bool moveForward)
    {
        if (_focusedPatchFile is null ||
            _focusedPatchTarget is null ||
            _focusedPatchGeneration != State.Snapshot.Generation)
        {
            return ReportNoSelectionAsync("No current diff hunk to navigate");
        }

        var editor = Diff.Editor;
        var currentLine = editor.Document.OffsetToPosition(editor.Cursor.Position).Line;
        var hunk = moveForward
            ? _focusedPatchFile.PatchIndex.FindNextHunk(currentLine)
            : _focusedPatchFile.PatchIndex.FindPreviousHunk(currentLine);
        if (hunk is null)
        {
            return ReportNoSelectionAsync(moveForward ? "No later hunk" : "No earlier hunk");
        }

        editor.Cursor.Position = editor.Document.PositionToOffset(
            new DocumentPosition(hunk.StartLineNumber, 1));
        editor.Cursor.ClearSelection();
        Activity = moveForward ? "Focused next hunk" : "Focused previous hunk";
        NotifyChanged();
        return Task.CompletedTask;
    }

    private Task ChangeDiffContextAsync(int delta, CancellationToken cancellationToken)
    {
        var next = Math.Clamp(_diffContextLines + delta, 0, 100_000);
        if (next == _diffContextLines)
        {
            return ReportNoSelectionAsync($"Diff context remains {_diffContextLines}");
        }

        return RunAsync(
            $"Changing diff context to {next}...",
            $"Diff context: {next}",
            mutation: null,
            cancellationToken,
            beforeScan: () => _diffContextLines = next);
    }

    private async Task<GitOperationResult> RunCommitAsync(
        bool skipHooks,
        PublishedAmendWarning? confirmedPublishedAmendWarning,
        DetachedHeadWarning? confirmedDetachedHeadWarning,
        CancellationToken cancellationToken)
    {
        var commitMessage = CommitMessage.Message;
        var editorVersion = CommitMessage.Version;
        var draftVersion = _commitDraftStore.Version;
        var result = await _commitService.CommitAsync(
            State.Snapshot,
            _workingDirectory,
            CommitOptions.CreateRequest(
                commitMessage,
                skipHooks,
                confirmedPublishedAmendWarning,
                confirmedDetachedHeadWarning),
            cancellationToken).ConfigureAwait(false);
        var retainedNewerDraft = false;
        string? recoveryWarning = null;
        try
        {
            if (CommitMessage.Version == editorVersion &&
                await _commitDraftStore.TryDiscardAsync(
                    draftVersion,
                    CancellationToken.None).ConfigureAwait(false))
            {
                CommitMessage.Clear();
            }
            else
            {
                retainedNewerDraft = true;
                await _commitDraftStore.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            recoveryWarning = $"The commit succeeded, but draft recovery cleanup failed: {exception.Message}";
            if (CommitMessage.Version == editorVersion)
            {
                CommitMessage.Clear();
            }
        }

        var shortObjectId = result.NewHead.ToString()[..12];
        var completionParts = new List<string> { $"Committed {shortObjectId}" };
        if (skipHooks)
        {
            completionParts.Add("hook bypass explicitly requested");
        }

        if (retainedNewerDraft)
        {
            completionParts.Add("retained newer draft");
        }

        if (result.DraftCleanupWarning is not null)
        {
            completionParts.Add(TerminalTextSanitizer.Sanitize(result.DraftCleanupWarning));
        }

        if (recoveryWarning is not null)
        {
            completionParts.Add(TerminalTextSanitizer.Sanitize(recoveryWarning));
        }

        _completionActivityOverride = string.Join("; ", completionParts);
        IsCitoolCompleted = true;
        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private void ClearFocusedPatch()
    {
        _focusedPatchFile = null;
        _focusedPatchTarget = null;
        _focusedPatchGeneration = default;
    }

    private async Task TryReconcileAfterFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ScanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed and status could not refresh: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
    }

    private Task ReportNoSelectionAsync(string activity)
    {
        Activity = activity;
        NotifyChanged();
        return Task.CompletedTask;
    }

    private void NotifyChanged()
        => Changed?.Invoke();

    private void HandleCommitMessageChanged()
        => _commitDraftStore.ScheduleSave(CommitMessage.Message);

    private void HandleCommitDraftPersistenceFailed(Exception exception)
    {
        Activity = $"Draft autosave failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        NotifyChanged();
    }

    private void SetTransportOutput(
        string title,
        GitOperationResult result,
        RemoteCatalog catalog,
        IReadOnlyList<RemoteUrl>? additionalUrls = null)
        => TransportOutput.Set(
            title,
            TransportTextFormatter.Format(result.StandardOutput.Span, catalog, additionalUrls ?? []),
            TransportTextFormatter.Format(result.StandardError.Span, catalog, additionalUrls ?? []));

    private void ClearRemoteDependentCatalogs()
    {
        Branches.Clear();
        Remotes.Clear();
    }

    private void ClearWorktreeDependentCatalogs()
    {
        Branches.Clear();
        Worktrees.Clear();
    }

    private async Task ApplyPendingMergeMessageAsync(
        bool hasMergeHead,
        CancellationToken cancellationToken)
    {
        var initialization = await _commitMessageInitializationService.LoadAsync(
            _workingDirectory,
            _commitMessageRecoveryPaths,
            _mergeMessagePath,
            _squashMessagePath,
            hasMergeHead,
            amendHead: null,
            cancellationToken).ConfigureAwait(false);
        CommitOptions.DisableAmend();
        PublishedAmendWarning = null;
        if (initialization.Kind is
            CommitMessageInitializationKind.Recovery or
            CommitMessageInitializationKind.Merge or
            CommitMessageInitializationKind.Squash)
        {
            _ = CommitMessage.TryApplyPendingOperationMessage(initialization);
        }
    }

    private async Task ClearRevertUndoAsync(CancellationToken cancellationToken)
    {
        _revertUndoState = null;
        try
        {
            await _revertUndoStore.DiscardAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            _completionActivityOverride =
                $"Operation completed; cached revert recovery cleanup failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
    }

    private static string GetInitialActivity(
        CommitMessageInitializationKind commitMessageKind,
        RevertUndoState? revertUndoState,
        string? recoveryWarning)
    {
        var activities = new List<string>();
        var commitMessageActivity = commitMessageKind switch
        {
            CommitMessageInitializationKind.Empty => null,
            CommitMessageInitializationKind.Recovery => "Loaded saved commit message text",
            CommitMessageInitializationKind.Merge => "Loaded Git merge message",
            CommitMessageInitializationKind.Squash => "Loaded Git squash message",
            CommitMessageInitializationKind.Amend => "Loaded HEAD message for amend",
            CommitMessageInitializationKind.Template => "Loaded configured commit template; edit it before committing",
            _ => throw new ArgumentOutOfRangeException(nameof(commitMessageKind)),
        };
        if (commitMessageActivity is not null)
        {
            activities.Add(commitMessageActivity);
        }

        if (revertUndoState is not null)
        {
            activities.Add("Recovered revert undo");
        }

        var activity = activities.Count == 0
            ? "Ready"
            : string.Join("; ", activities);
        return recoveryWarning is null
            ? activity
            : $"{activity}; {TerminalTextSanitizer.Sanitize(recoveryWarning)}";
    }

    private static bool IsExpectedFailure(Exception exception)
        => exception is GitCommandException or RepositoryPreconditionException or
            BranchOperationException or WorktreeOperationException or
            MergeOperationException or RemoteOperationException or
            PushOperationException or RemoteInitializationException or
            RepositoryMaintenanceException or
            PublishedAmendConfirmationException or DetachedHeadConfirmationException or
            InvalidDataException or
            IOException or UnauthorizedAccessException;

    private bool HasUnmergedEntries
        => State.Snapshot.Entries.Any(static entry => entry.Kind == RepositoryStatusEntryKind.Unmerged);

    private bool ContainsUnmergedPath(IReadOnlyList<GitPath> paths)
        => paths.Any(path => State.Snapshot.Entries.Any(
            entry => entry.Kind == RepositoryStatusEntryKind.Unmerged && entry.Path.Equals(path)));

    private static string FormatPathCount(int count)
        => count == 1 ? "1 path" : $"{count} paths";

    private static string FormatConflictChoice(ConflictResolutionChoice choice)
        => choice switch
        {
            ConflictResolutionChoice.Ours => "ours",
            ConflictResolutionChoice.Theirs => "theirs",
            ConflictResolutionChoice.Base => "base",
            ConflictResolutionChoice.Both => "ours then theirs",
            _ => throw new ArgumentOutOfRangeException(nameof(choice)),
        };
}
