using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Coordinates revision resolution, lazy tree navigation, filtering, and exact object previews.
/// </summary>
internal sealed class TreeSession
{
    private const int MaximumPresentedBlobBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly CanonicalDirectory _workingDirectory;
    private readonly TreeService _service;
    private readonly List<TreeCatalog> _parents = [];
    private GitPath? _requestedDirectory;
    private int _previewRequest;

    private TreeSession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        TreeService service,
        string revision,
        GitPath? requestedDirectory)
    {
        _workingDirectory = workingDirectory;
        Repository = repository;
        Installation = installation;
        _service = service;
        _requestedDirectory = requestedDirectory;
        State = new TreeWorkspaceState(revision);
        Activity = "Ready to browse a repository tree";
    }

    /// <summary>
    /// Notifies the view that controlled tree-browser state has changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the discovered repository displayed by the tree browser.
    /// </summary>
    internal RepositoryLocation Repository { get; }

    /// <summary>
    /// Gets the resolved Git installation used by this tree browser.
    /// </summary>
    internal GitInstallation Installation { get; }

    /// <summary>
    /// Gets the controlled tree listing, filter, revision, focus, and preview state.
    /// </summary>
    internal TreeWorkspaceState State { get; }

    /// <summary>
    /// Gets the current or most recent tree-browser activity description.
    /// </summary>
    internal string Activity { get; private set; }

    /// <summary>
    /// Gets whether a tree or object capture is active.
    /// </summary>
    internal bool IsBusy { get; private set; }

    /// <summary>
    /// Gets whether the most recent revision or tree capture failed.
    /// </summary>
    internal bool HasLoadFailure { get; private set; }

    /// <summary>
    /// Gets whether navigation can return to a parent tree.
    /// </summary>
    internal bool CanNavigateUp => _parents.Count > 0;

    /// <summary>
    /// Opens a repository and creates its exact immutable tree-browser workflow.
    /// </summary>
    /// <param name="launchDirectory">The canonical directory supplied by the user.</param>
    /// <param name="options">The typed browser command operands.</param>
    /// <param name="processEnvironment">The classified startup environment.</param>
    /// <param name="cancellationToken">Signals repository discovery cancellation.</param>
    /// <returns>The ready browser session before its first tree capture.</returns>
    internal static async Task<TreeSession> OpenAsync(
        CanonicalDirectory launchDirectory,
        BrowserOptions options,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processEnvironment);
        var resolver = new ExecutableResolver(processEnvironment);
        var runner = new ChildProcessRunner();
        var environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
        var installation = await new GitVersionService(resolver, runner)
            .GetAsync(launchDirectory, cancellationToken)
            .ConfigureAwait(false);
        var repository = await new RepositoryDiscoveryService(installation, runner, environmentFactory)
            .DiscoverAsync(launchDirectory, cancellationToken)
            .ConfigureAwait(false);
        var workingDirectory = CanonicalDirectory.Create(repository.WorkTree ?? repository.GitDirectory);
        var directories = ConvertDirectories(options.Directories).ToBuilder();
        if (options.PathspecFile is not null)
        {
            directories.AddRange(await PathspecFileReader.ReadAsync(
                options.PathspecFile,
                options.PathspecFileNul,
                cancellationToken).ConfigureAwait(false));
        }

        if (directories.Count > 1)
        {
            throw new ArgumentException("The tree browser accepts at most one starting directory.", nameof(options));
        }

        var revision = string.IsNullOrEmpty(options.Revision) ? "HEAD" : options.Revision;
        var requestedDirectory = directories.Count == 0
            ? null
            : GitPathOperations.NormalizeDirectory(directories[0]);
        return new TreeSession(
            workingDirectory,
            repository,
            installation,
            new TreeService(installation, runner, environmentFactory),
            revision,
            requestedDirectory);
    }

    /// <summary>
    /// Loads the revision currently entered by the user and resets navigation to the requested directory.
    /// </summary>
    /// <param name="cancellationToken">Signals tree capture cancellation.</param>
    /// <returns>A task that completes after the first exact tree is current.</returns>
    internal Task LoadRevisionAsync(CancellationToken cancellationToken)
        => LoadRevisionAsync(State.Revision.Text, _requestedDirectory, cancellationToken);

    /// <summary>
    /// Reloads the current exact directory against the currently entered revision.
    /// </summary>
    /// <param name="cancellationToken">Signals tree capture cancellation.</param>
    /// <returns>A task that completes after the current directory is recaptured.</returns>
    internal Task RefreshAsync(CancellationToken cancellationToken)
        => LoadRevisionAsync(State.Revision.Text, State.Catalog?.Directory, cancellationToken);

    /// <summary>
    /// Applies an incremental tree filter and updates the focused exact object preview.
    /// </summary>
    /// <param name="filter">The latest user-entered filter text.</param>
    /// <param name="cancellationToken">Signals preview capture cancellation.</param>
    /// <returns>A task that completes after filter and preview state are current.</returns>
    internal Task FilterAsync(string filter, CancellationToken cancellationToken)
    {
        State.SetFilter(filter);
        NotifyChanged();
        return ReloadFocusedPreviewAsync(cancellationToken);
    }

    /// <summary>
    /// Focuses one visible tree row and loads its exact immutable object preview.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    /// <param name="cancellationToken">Signals preview capture cancellation.</param>
    /// <returns>A task that completes after focus and preview state are current.</returns>
    internal Task FocusAsync(int index, CancellationToken cancellationToken)
    {
        State.Focus(index);
        NotifyChanged();
        return ReloadFocusedPreviewAsync(cancellationToken);
    }

    /// <summary>
    /// Opens the focused tree directory or leaves non-tree objects selected for inspection.
    /// </summary>
    /// <param name="cancellationToken">Signals nested tree capture cancellation.</param>
    /// <returns>A task that completes after optional lazy directory navigation.</returns>
    internal async Task ActivateFocusedAsync(CancellationToken cancellationToken)
    {
        var current = State.Catalog;
        var entry = State.FocusedItem?.Entry;
        if (current is null || entry is null || entry.Kind != TreeEntryKind.Tree || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Activity = $"Opening {entry.Name.DisplayText}...";
        NotifyChanged();
        try
        {
            var directory = GitPathOperations.Combine(current.Directory, entry.Name);
            var nested = await _service.ListAsync(
                _workingDirectory,
                current.CommitObjectId,
                entry.ObjectId,
                directory,
                cancellationToken).ConfigureAwait(false);
            _parents.Add(current);
            State.ApplyCatalog(nested);
            await CaptureFocusedPreviewAsync(cancellationToken).ConfigureAwait(false);
            Activity = $"Opened {directory.DisplayText}";
            HasLoadFailure = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            HasLoadFailure = true;
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Returns to the exact parent tree retained in the navigation stack.
    /// </summary>
    /// <param name="cancellationToken">Signals focused preview capture cancellation.</param>
    /// <returns>A task that completes after the parent listing and preview are current.</returns>
    internal async Task NavigateUpAsync(CancellationToken cancellationToken)
    {
        if (_parents.Count == 0 || IsBusy)
        {
            return;
        }

        var index = _parents.Count - 1;
        var parent = _parents[index];
        _parents.RemoveAt(index);
        State.ApplyCatalog(parent);
        await CaptureFocusedPreviewAsync(cancellationToken).ConfigureAwait(false);
        Activity = parent.Directory is null ? "Opened repository root" : $"Opened {parent.Directory.DisplayText}";
        NotifyChanged();
    }

    private async Task LoadRevisionAsync(
        string revisionText,
        GitPath? directory,
        CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(revisionText))
        {
            Activity = "Enter a revision to browse";
            HasLoadFailure = true;
            NotifyChanged();
            return;
        }

        IsBusy = true;
        Activity = $"Loading tree at {TerminalTextSanitizer.Sanitize(revisionText)}...";
        NotifyChanged();
        try
        {
            var catalog = await _service.OpenAsync(
                _workingDirectory,
                Revision.Create(revisionText),
                directory,
                cancellationToken).ConfigureAwait(false);
            _parents.Clear();
            _requestedDirectory = directory;
            State.ApplyCatalog(catalog);
            await CaptureFocusedPreviewAsync(cancellationToken).ConfigureAwait(false);
            Activity = $"Loaded {catalog.Entries.Length} tree {(catalog.Entries.Length == 1 ? "entry" : "entries")}";
            HasLoadFailure = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            State.SetPreviewMessage(TerminalTextSanitizer.Sanitize(exception.Message));
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
            HasLoadFailure = true;
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    private async Task ReloadFocusedPreviewAsync(CancellationToken cancellationToken)
    {
        var request = Interlocked.Increment(ref _previewRequest);
        await CaptureFocusedPreviewAsync(cancellationToken, request).ConfigureAwait(false);
        NotifyChanged();
    }

    private Task CaptureFocusedPreviewAsync(CancellationToken cancellationToken)
        => CaptureFocusedPreviewAsync(cancellationToken, Interlocked.Increment(ref _previewRequest));

    private async Task CaptureFocusedPreviewAsync(
        CancellationToken cancellationToken,
        int request)
    {
        var entry = State.FocusedItem?.Entry;
        if (entry is null)
        {
            State.SetPreviewMessage("This tree contains no matching entries.");
            return;
        }

        if (entry.Kind == TreeEntryKind.Tree)
        {
            State.SetPreview(entry, "Directory\n\nPress Enter or select Open to load its immediate entries.");
            return;
        }

        if (entry.Kind == TreeEntryKind.GitLink)
        {
            State.SetPreview(
                entry,
                $"Submodule commit\n\nObject: {entry.ObjectId}\n\nOpen the submodule repository to inspect this commit.");
            return;
        }

        try
        {
            using var spool = await _service.ReadBlobAsync(
                _workingDirectory,
                entry,
                cancellationToken).ConfigureAwait(false);
            var length = (int)Math.Min(spool.Length, MaximumPresentedBlobBytes);
            var bytes = await spool.ReadSliceAsync(0, length, cancellationToken).ConfigureAwait(false);
            if (request == Volatile.Read(ref _previewRequest) && State.FocusedItem?.Entry.Equals(entry) == true)
            {
                var content = RawPatchPresentationDecoder.Decode(bytes, spool.Length > length);
                var heading = entry.Kind == TreeEntryKind.SymbolicLink ? "Symbolic link target\n\n" : string.Empty;
                State.SetPreview(entry, heading + content);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (request == Volatile.Read(ref _previewRequest))
            {
                State.SetPreviewMessage(TerminalTextSanitizer.Sanitize(exception.Message));
            }
        }
    }

    private static ImmutableArray<GitPath> ConvertDirectories(ImmutableArray<string> directories)
    {
        if (directories.IsDefaultOrEmpty)
        {
            return [];
        }

        return OperatingSystem.IsWindows()
            ? [.. directories.Select(GitPath.FromWindowsPath)]
            : [.. directories.Select(path => GitPath.FromUnixBytes(s_strictUtf8.GetBytes(path)))];
    }

    private static bool IsExpectedFailure(Exception exception)
        => exception is ArgumentException or
            ExecutableResolutionException or
            GitCommandException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException;

    private void NotifyChanged()
        => Changed?.Invoke();
}
