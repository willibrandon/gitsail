using GitSail.Domain;
using GitSail.Git.Execution;
using Hex1b.Widgets;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Owns controlled repository chooser state and its asynchronous Git-backed actions.
/// </summary>
internal sealed class RepositoryChooserSession : IDisposable
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly RepositoryManagementService _managementService;
    private readonly RecentRepositoryService _recentRepositoryService;
    private readonly CanonicalDirectory _launchDirectory;
    private readonly CancellationToken _shutdownToken;
    private readonly Lock _operationLock = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _disposed;

    private RepositoryChooserSession(
        RepositoryManagementService managementService,
        RecentRepositoryService recentRepositoryService,
        CredentialPromptCoordinator credentialPrompts,
        CanonicalDirectory launchDirectory,
        string initialStatus,
        CancellationToken shutdownToken)
    {
        _managementService = managementService;
        _recentRepositoryService = recentRepositoryService;
        CredentialPrompts = credentialPrompts;
        _launchDirectory = launchDirectory;
        _shutdownToken = shutdownToken;
        var launchPath = GetManagedPath(launchDirectory);
        OpenPath = new TextBoxState(launchPath);
        InitializePath = new TextBoxState(launchPath);
        CloneSource = new TextBoxState();
        CloneTarget = new TextBoxState(Path.EndsInDirectorySeparator(launchPath)
            ? launchPath
            : launchPath + Path.DirectorySeparatorChar);
        Status = initialStatus;
        CredentialPrompts.Changed += HandleCredentialPromptChanged;
    }

    /// <summary>
    /// Notifies the chooser view after controlled state changes.
    /// </summary>
    internal event EventHandler? Changed;

    /// <summary>
    /// Gets the active chooser workflow.
    /// </summary>
    internal RepositoryChooserPage Page { get; private set; } = RepositoryChooserPage.Open;

    /// <summary>
    /// Gets the controlled repository or directory input for open workflows.
    /// </summary>
    internal TextBoxState OpenPath { get; }

    /// <summary>
    /// Gets the controlled target input for repository initialization.
    /// </summary>
    internal TextBoxState InitializePath { get; }

    /// <summary>
    /// Gets the controlled local path or remote URL input for cloning.
    /// </summary>
    internal TextBoxState CloneSource { get; }

    /// <summary>
    /// Gets the controlled canonical-target input for cloning.
    /// </summary>
    internal TextBoxState CloneTarget { get; }

    /// <summary>
    /// Gets the selected local-object clone mode.
    /// </summary>
    internal RepositoryCloneMode CloneMode { get; private set; }

    /// <summary>
    /// Gets whether cloning recursively initializes active submodules.
    /// </summary>
    internal bool RecurseSubmodules { get; private set; }

    /// <summary>
    /// Gets the newest-first exact recent repository paths.
    /// </summary>
    internal ImmutableArray<GitPath> RecentRepositories { get; private set; } = [];

    /// <summary>
    /// Gets the controlled focused recent-repository index.
    /// </summary>
    internal int RecentFocusedIndex { get; private set; }

    /// <summary>
    /// Gets whether a Git or cleanup operation is active.
    /// </summary>
    internal bool IsBusy { get; private set; }

    /// <summary>
    /// Gets the control-safe status or actionable error shown by the chooser.
    /// </summary>
    internal string Status { get; private set; }

    /// <summary>
    /// Gets the exact partial target currently eligible for identity-checked cleanup.
    /// </summary>
    internal CreatedDirectoryCleanup? Cleanup { get; private set; }

    /// <summary>
    /// Gets the canonical directory selected for repository opening.
    /// </summary>
    internal CanonicalDirectory? SelectedDirectory { get; private set; }

    /// <summary>
    /// Gets the authenticated credential prompt coordinator used by clone transport.
    /// </summary>
    internal CredentialPromptCoordinator CredentialPrompts { get; }

    /// <summary>
    /// Gets the compatible Git installation used by the chooser.
    /// </summary>
    internal GitInstallation Installation => _managementService.Installation;

    /// <summary>
    /// Resolves Git, creates isolated chooser services, and loads recent repositories.
    /// </summary>
    /// <param name="launchDirectory">The canonical process or explicitly requested working directory.</param>
    /// <param name="processEnvironment">The classified startup environment.</param>
    /// <param name="initialStatus">The initial guidance or previous open failure.</param>
    /// <param name="cancellationToken">Signals chooser and child-process shutdown.</param>
    /// <returns>A ready controlled chooser session.</returns>
    internal static async Task<RepositoryChooserSession> CreateAsync(
        CanonicalDirectory launchDirectory,
        IProcessEnvironment processEnvironment,
        string initialStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchDirectory);
        ArgumentNullException.ThrowIfNull(processEnvironment);
        ArgumentNullException.ThrowIfNull(initialStatus);
        var runner = new ChildProcessRunner();
        var installation = await new GitVersionService(
            new ExecutableResolver(processEnvironment),
            runner).GetAsync(launchDirectory, cancellationToken).ConfigureAwait(false);
        var environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
        var prompts = new CredentialPromptCoordinator();
        var broker = new CredentialPromptBroker(prompts);
        var session = new RepositoryChooserSession(
            new RepositoryManagementService(
                installation,
                runner,
                environmentFactory,
                broker,
                launchDirectory),
            new RecentRepositoryService(
                installation,
                runner,
                environmentFactory,
                launchDirectory),
            prompts,
            launchDirectory,
            initialStatus,
            cancellationToken);
        try
        {
            await session.RefreshRecentAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Switches the visible chooser workflow without discarding controlled input.
    /// </summary>
    /// <param name="page">The workflow to display.</param>
    internal void SetPage(RepositoryChooserPage page)
    {
        ThrowIfDisposed();
        Page = page;
        NotifyChanged();
    }

    /// <summary>
    /// Updates the focused recent-repository index after keyboard or pointer navigation.
    /// </summary>
    /// <param name="index">The requested item index.</param>
    internal void FocusRecent(int index)
    {
        ThrowIfDisposed();
        RecentFocusedIndex = RecentRepositories.IsEmpty
            ? 0
            : Math.Clamp(index, 0, RecentRepositories.Length - 1);
        NotifyChanged();
    }

    /// <summary>
    /// Cycles standard, full-copy, and shared local clone behavior.
    /// </summary>
    internal void CycleCloneMode()
    {
        ThrowIfDisposed();
        CloneMode = CloneMode switch
        {
            RepositoryCloneMode.Standard => RepositoryCloneMode.FullCopy,
            RepositoryCloneMode.FullCopy => RepositoryCloneMode.Shared,
            RepositoryCloneMode.Shared => RepositoryCloneMode.Standard,
            _ => RepositoryCloneMode.Standard,
        };
        NotifyChanged();
    }

    /// <summary>
    /// Toggles recursive submodule initialization for the next clone.
    /// </summary>
    internal void ToggleRecursiveSubmodules()
    {
        ThrowIfDisposed();
        RecurseSubmodules = !RecurseSubmodules;
        NotifyChanged();
    }

    /// <summary>
    /// Selects the repository containing the entered existing directory.
    /// </summary>
    /// <returns>A completed task after canonical path validation.</returns>
    internal Task SelectOpenPathAsync()
    {
        ThrowIfDisposed();
        try
        {
            SelectedDirectory = ResolveExistingDirectory(OpenPath.Text);
            Status = "Opening selected repository";
            NotifyChanged();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Status = TerminalTextSanitizer.Sanitize(exception.Message);
            NotifyChanged();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Selects the focused exact recent repository when it still exists.
    /// </summary>
    /// <returns>A completed task after exact native path validation.</returns>
    internal Task SelectRecentAsync()
    {
        ThrowIfDisposed();
        if (RecentRepositories.IsEmpty)
        {
            Status = "No recent repository is selected.";
            NotifyChanged();
            return Task.CompletedTask;
        }

        try
        {
            SelectedDirectory = CanonicalDirectory.Create(RecentRepositories[RecentFocusedIndex]);
            Status = "Opening recent repository";
            NotifyChanged();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Status = TerminalTextSanitizer.Sanitize(exception.Message);
            NotifyChanged();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Initializes the entered target through Git and selects the created repository.
    /// </summary>
    /// <param name="bare">Whether Git creates a repository without a worktree.</param>
    /// <returns>A task that completes after Git finishes or reports an actionable failure.</returns>
    internal Task InitializeAsync(bool bare)
        => RunCreationAsync(
            bare ? "Initializing bare repository" : "Initializing repository",
            token => _managementService.InitializeAsync(InitializePath.Text, bare, token));

    /// <summary>
    /// Clones the entered source with the selected mode and selects the created worktree.
    /// </summary>
    /// <returns>A task that completes after Git finishes or reports an actionable failure.</returns>
    internal Task CloneAsync()
        => RunCreationAsync(
            "Cloning repository",
            token => _managementService.CloneAsync(
                new RepositoryCloneRequest(
                    CloneSource.Text,
                    CloneTarget.Text,
                    CloneMode,
                    RecurseSubmodules),
                token));

    /// <summary>
    /// Requests cancellation of the active Git process tree.
    /// </summary>
    internal void CancelOperation()
    {
        ThrowIfDisposed();
        lock (_operationLock)
        {
            _operationCancellation?.Cancel();
        }

        Status = "Cancelling repository operation";
        NotifyChanged();
    }

    /// <summary>
    /// Deletes the exact unchanged partial target captured after a failed or cancelled creation.
    /// </summary>
    /// <returns>A task that completes after deletion or an actionable identity failure.</returns>
    internal async Task CleanupAsync()
    {
        ThrowIfDisposed();
        if (Cleanup is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = $"Removing partial target {Cleanup.DisplayPath}";
        NotifyChanged();
        try
        {
            await Cleanup.DeleteAsync(_shutdownToken).ConfigureAwait(false);
            Cleanup = null;
            Status = "Removed the unchanged partial clone target.";
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Status = TerminalTextSanitizer.Sanitize(exception.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Removes the focused exact recent repository from global Git configuration.
    /// </summary>
    /// <returns>A task that completes after the remaining list is reloaded.</returns>
    internal async Task RemoveFocusedRecentAsync()
    {
        ThrowIfDisposed();
        if (RecentRepositories.IsEmpty || IsBusy)
        {
            return;
        }

        var path = RecentRepositories[RecentFocusedIndex];
        IsBusy = true;
        Status = $"Removing recent entry {path.DisplayText}";
        NotifyChanged();
        try
        {
            await _recentRepositoryService.RemoveAsync(path, _shutdownToken).ConfigureAwait(false);
            if (await RefreshRecentAsync(_shutdownToken).ConfigureAwait(false))
            {
                Status = "Removed recent repository entry.";
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Status = TerminalTextSanitizer.Sanitize(exception.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Reloads exact recent repository paths from global Git configuration.
    /// </summary>
    /// <returns>A task that completes after the controlled recent list is replaced.</returns>
    internal async Task ReloadRecentAsync()
    {
        ThrowIfDisposed();
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "Refreshing recent repositories";
        NotifyChanged();
        try
        {
            if (await RefreshRecentAsync(_shutdownToken).ConfigureAwait(false))
            {
                Status = RecentRepositories.IsEmpty
                    ? "No recent repositories are recorded."
                    : $"Loaded {RecentRepositories.Length} recent repositories.";
            }
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Unsubscribes prompt notifications and cancels any active chooser operation.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CredentialPrompts.Changed -= HandleCredentialPromptChanged;
        CredentialPrompts.Dispose();
        lock (_operationLock)
        {
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private async Task RunCreationAsync(
        string activity,
        Func<CancellationToken, Task<RepositoryCreationResult>> operation)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(operation);
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Cleanup = null;
        Status = activity;
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        lock (_operationLock)
        {
            if (_disposed)
            {
                operationCancellation.Dispose();
                throw new ObjectDisposedException(nameof(RepositoryChooserSession));
            }

            _operationCancellation = operationCancellation;
        }

        NotifyChanged();
        try
        {
            var result = await operation(operationCancellation.Token).ConfigureAwait(false);
            SelectedDirectory = result.Directory;
            Status = result.IsBare ? "Opening initialized bare repository" : "Opening created repository";
        }
        catch (RepositoryCreationCancelledException exception) when (!_shutdownToken.IsCancellationRequested)
        {
            Cleanup = exception.Cleanup;
            Status = Cleanup is null
                ? "Repository operation cancelled. No new target requires cleanup."
                : $"Repository operation cancelled. Partial target available for cleanup: {Cleanup.DisplayPath}";
        }
        catch (RepositoryCreationException exception)
        {
            Cleanup = exception.Cleanup;
            Status = TerminalTextSanitizer.Sanitize(exception.Message);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Status = TerminalTextSanitizer.Sanitize(exception.Message);
        }
        finally
        {
            IsBusy = false;
            lock (_operationLock)
            {
                if (ReferenceEquals(_operationCancellation, operationCancellation))
                {
                    _operationCancellation = null;
                    operationCancellation.Dispose();
                }
            }

            NotifyChanged();
        }
    }

    private async Task<bool> RefreshRecentAsync(CancellationToken cancellationToken)
    {
        try
        {
            RecentRepositories = await _recentRepositoryService.LoadAsync(cancellationToken).ConfigureAwait(false);
            RecentFocusedIndex = RecentRepositories.IsEmpty
                ? 0
                : Math.Clamp(RecentFocusedIndex, 0, RecentRepositories.Length - 1);
            return true;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            RecentRepositories = [];
            RecentFocusedIndex = 0;
            Status = TerminalTextSanitizer.Sanitize(exception.Message);
            return false;
        }
    }

    private CanonicalDirectory ResolveExistingDirectory(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A repository path cannot contain NUL.", nameof(value));
        }

        return CanonicalDirectory.Create(Path.GetFullPath(value, GetManagedPath(_launchDirectory)));
    }

    private void HandleCredentialPromptChanged()
        => NotifyChanged();

    private void NotifyChanged()
        => Changed?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private static bool IsExpectedFailure(Exception exception)
        => exception is ArgumentException or
            GitCommandException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException;

    private static string GetManagedPath(CanonicalDirectory directory)
        => directory.Kind == NativePathKind.WindowsUtf16
            ? directory.GetWindowsPath()
            : s_strictUtf8.GetString(directory.GetUnixBytes());
}
