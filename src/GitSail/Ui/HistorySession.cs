using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Coordinates structured history capture, filtering, focus, and exact commit previews.
/// </summary>
internal sealed class HistorySession
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly CanonicalDirectory _workingDirectory;
    private readonly HistoryService _service;
    private readonly HistoryQuery _query;
    private int _previewRequest;

    private HistorySession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        HistoryService service,
        HistoryQuery query)
    {
        _workingDirectory = workingDirectory;
        Repository = repository;
        Installation = installation;
        _service = service;
        _query = query;
        State = new HistoryWorkspaceState();
        Activity = "Ready to load commit history";
    }

    /// <summary>
    /// Notifies the view that controlled history state has changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the discovered repository displayed by the history workflow.
    /// </summary>
    internal RepositoryLocation Repository { get; }

    /// <summary>
    /// Gets the resolved Git installation used by this history workflow.
    /// </summary>
    internal GitInstallation Installation { get; }

    /// <summary>
    /// Gets the controlled structured history and preview state.
    /// </summary>
    internal HistoryWorkspaceState State { get; }

    /// <summary>
    /// Gets the current or most recent history activity description.
    /// </summary>
    internal string Activity { get; private set; }

    /// <summary>
    /// Gets whether a history capture or preview operation is active.
    /// </summary>
    internal bool IsBusy { get; private set; }

    /// <summary>
    /// Gets whether the most recent structured history capture failed.
    /// </summary>
    internal bool HasLoadFailure { get; private set; }

    /// <summary>
    /// Opens a repository and creates its structured history workflow.
    /// </summary>
    /// <param name="launchDirectory">The canonical directory supplied by the user.</param>
    /// <param name="options">The typed history command operands.</param>
    /// <param name="processEnvironment">The classified startup environment.</param>
    /// <param name="cancellationToken">Signals repository discovery cancellation.</param>
    /// <returns>The ready history session before its first bounded capture.</returns>
    internal static async Task<HistorySession> OpenAsync(
        CanonicalDirectory launchDirectory,
        HistoryOptions options,
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
        var pathspecs = ConvertPathspecs(options.Pathspecs).ToBuilder();
        if (options.PathspecFile is not null)
        {
            pathspecs.AddRange(await PathspecFileReader.ReadAsync(
                options.PathspecFile,
                options.PathspecFileNul,
                cancellationToken).ConfigureAwait(false));
        }

        var query = new HistoryQuery(
            options.RevisionRange is null ? null : Revision.Create(options.RevisionRange),
            pathspecs.ToImmutable(),
            2_000);
        return new HistorySession(
            workingDirectory,
            repository,
            installation,
            new HistoryService(installation, runner, environmentFactory),
            query);
    }

    /// <summary>
    /// Reloads the bounded structured history and the focused exact commit preview.
    /// </summary>
    /// <param name="cancellationToken">Signals history capture cancellation.</param>
    /// <returns>A task that completes after controlled history state is current.</returns>
    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        State.Clear();
        Activity = "Loading structured commit history...";
        NotifyChanged();
        try
        {
            var catalog = await _service.CaptureAsync(
                _workingDirectory,
                _query,
                cancellationToken).ConfigureAwait(false);
            State.ApplyCatalog(catalog);
            HasLoadFailure = false;
            await CaptureFocusedPreviewAsync(cancellationToken).ConfigureAwait(false);
            Activity = catalog.Commits.IsEmpty
                ? "No commits match this history request"
                : $"Loaded {catalog.Commits.Length} {(catalog.Commits.Length == 1 ? "commit" : "commits")}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            HasLoadFailure = true;
            State.SetPreviewMessage(TerminalTextSanitizer.Sanitize(exception.Message));
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Applies an incremental history filter and updates the focused exact preview.
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
    /// Focuses one visible history row and loads its exact immutable commit preview.
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
    /// Moves commit focus by one bounded relative offset.
    /// </summary>
    /// <param name="offset">The signed visible-row offset.</param>
    /// <param name="cancellationToken">Signals preview capture cancellation.</param>
    /// <returns>A task that completes after focus and preview state are current.</returns>
    internal Task MoveFocusAsync(int offset, CancellationToken cancellationToken)
    {
        if (State.VisibleItems.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var index = Math.Clamp(State.FocusedIndex + offset, 0, State.VisibleItems.Length - 1);
        return FocusAsync(index, cancellationToken);
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
        var commit = State.FocusedItem?.Commit;
        if (commit is null)
        {
            State.SetPreviewMessage(
                State.Catalog?.Commits.IsEmpty == true
                    ? "No commits match this history request."
                    : "No commit matches the current filter.");
            return;
        }

        try
        {
            var bytes = await _service.ShowAsync(
                _workingDirectory,
                commit.ObjectId,
                cancellationToken).ConfigureAwait(false);
            if (request == Volatile.Read(ref _previewRequest) &&
                State.FocusedItem?.Commit.ObjectId.Equals(commit.ObjectId) == true)
            {
                State.SetPreview(commit, RawPatchPresentationDecoder.Decode(bytes.Span, isTruncated: false));
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

    private static ImmutableArray<GitPath> ConvertPathspecs(ImmutableArray<string> pathspecs)
    {
        if (pathspecs.IsDefaultOrEmpty)
        {
            return [];
        }

        return OperatingSystem.IsWindows()
            ? [.. pathspecs.Select(GitPath.FromWindowsPath)]
            : [.. pathspecs.Select(path => GitPath.FromUnixBytes(s_strictUtf8.GetBytes(path)))];
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
