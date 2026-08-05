using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using GitSail.Localization.Generated;
using System.Collections.Immutable;
using System.Globalization;

namespace GitSail.Ui;

/// <summary>
/// Coordinates validated comparison capture, file filtering, focus, and bounded presentations.
/// </summary>
internal sealed class DiffSession : IDisposable
{
    private const int MaximumPresentationBytes = 16 * 1024 * 1024;
    private readonly CanonicalDirectory _workingDirectory;
    private readonly RawDiffService _service;
    private readonly DiffRequest _request;
    private GitDiffRuntimeConfiguration _configuration;
    private RawDiffDocument? _document;
    private int _contextLines;
    private int _previewRequest;
    private long _generation;

    private DiffSession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        RawDiffService service,
        DiffRequest request,
        GitDiffRuntimeConfiguration configuration,
        string leftLabel,
        string rightLabel)
    {
        _workingDirectory = workingDirectory;
        _service = service;
        _request = request;
        _configuration = configuration;
        _contextLines = configuration.ContextLines;
        Repository = repository;
        Installation = installation;
        LeftLabel = leftLabel;
        RightLabel = rightLabel;
        State = new DiffWorkspaceState(configuration.TabSize);
        Activity = "Ready to load comparison";
    }

    /// <summary>
    /// Notifies the view that controlled comparison state has changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the discovered repository displayed by the comparison workflow.
    /// </summary>
    internal RepositoryLocation Repository { get; }

    /// <summary>
    /// Gets the resolved Git installation used by this comparison workflow.
    /// </summary>
    internal GitInstallation Installation { get; }

    /// <summary>
    /// Gets the control-safe left repository-state label.
    /// </summary>
    internal string LeftLabel { get; }

    /// <summary>
    /// Gets the control-safe right repository-state label.
    /// </summary>
    internal string RightLabel { get; }

    /// <summary>
    /// Gets the concise control-safe comparison label.
    /// </summary>
    internal string ComparisonLabel => $"{LeftLabel} → {RightLabel}";

    /// <summary>
    /// Gets the controlled changed-file and presentation state.
    /// </summary>
    internal DiffWorkspaceState State { get; }

    /// <summary>
    /// Gets the current or most recent comparison activity description.
    /// </summary>
    internal string Activity { get; private set; }

    /// <summary>
    /// Gets whether a comparison capture or preview operation is active.
    /// </summary>
    internal bool IsBusy { get; private set; }

    /// <summary>
    /// Gets whether the most recent comparison capture failed.
    /// </summary>
    internal bool HasLoadFailure { get; private set; }

    /// <summary>
    /// Gets the number of unchanged lines requested around each textual hunk.
    /// </summary>
    internal int ContextLines => _contextLines;

    /// <summary>
    /// Opens a repository and validates every revision and native pathspec input.
    /// </summary>
    /// <param name="launchDirectory">The canonical directory supplied by the user.</param>
    /// <param name="options">The typed comparison command operands.</param>
    /// <param name="processEnvironment">The classified startup environment.</param>
    /// <param name="cancellationToken">Signals repository discovery cancellation.</param>
    /// <returns>The ready comparison session before its first bounded capture.</returns>
    internal static async Task<DiffSession> OpenAsync(
        CanonicalDirectory launchDirectory,
        DiffOptions options,
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
        var configuration = GitDiffRuntimeConfiguration.Resolve(
            await new GitConfigurationService(
                installation,
                runner,
                environmentFactory,
                new GitConfigurationParser()).LoadSnapshotAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false));
        var pathspecs = await CommandPathspecResolver.ResolveAsync(
            options.Pathspecs,
            options.NativePathspecs,
            options.PathspecFile,
            options.PathspecFileNul,
            cancellationToken).ConfigureAwait(false);

        var revisionResolver = new RevisionResolver(installation, runner, environmentFactory);
        var (request, leftLabel, rightLabel) = await BuildRequestAsync(
            workingDirectory,
            options,
            pathspecs,
            revisionResolver,
            cancellationToken).ConfigureAwait(false);
        return new DiffSession(
            workingDirectory,
            repository,
            installation,
            new RawDiffService(installation, runner, environmentFactory),
            request,
            configuration,
            leftLabel,
            rightLabel);
    }

    /// <summary>
    /// Reloads the complete bounded comparison and focused file presentation.
    /// </summary>
    /// <param name="cancellationToken">Signals comparison capture cancellation.</param>
    /// <returns>A task that completes after controlled comparison state is current.</returns>
    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        State.Clear();
        _document?.Dispose();
        _document = null;
        Activity = $"Loading {ComparisonLabel}...";
        NotifyChanged();
        try
        {
            var generation = new OperationGeneration(Interlocked.Increment(ref _generation));
            var document = await _service.CaptureComparisonAsync(
                _workingDirectory,
                _request,
                generation,
                _configuration,
                cancellationToken).ConfigureAwait(false);
            _document = document;
            State.ApplyFiles(document.Index.Files);
            HasLoadFailure = false;
            await CaptureFocusedPreviewAsync(cancellationToken).ConfigureAwait(false);
            Activity = document.Index.Files.IsEmpty
                ? $"No changes in {ComparisonLabel}"
                : AppMessages.DiffActivityLoadedChangedFiles(document.Index.Files.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            HasLoadFailure = true;
            State.SetMessage(TerminalTextSanitizer.Sanitize(exception.Message));
            Activity = $"Failed: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Applies an incremental path filter and updates the focused file presentation.
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
    /// Focuses one visible changed file and loads its bounded exact-byte-derived presentation.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    /// <param name="cancellationToken">Signals preview capture cancellation.</param>
    /// <returns>A task that completes after focus and presentation state are current.</returns>
    internal Task FocusAsync(int index, CancellationToken cancellationToken)
    {
        State.Focus(index);
        NotifyChanged();
        return ReloadFocusedPreviewAsync(cancellationToken);
    }

    /// <summary>
    /// Moves changed-file focus by one bounded relative offset.
    /// </summary>
    /// <param name="offset">The signed visible-row offset.</param>
    /// <param name="cancellationToken">Signals preview capture cancellation.</param>
    /// <returns>A task that completes after focus and presentation state are current.</returns>
    internal Task MoveFileAsync(int offset, CancellationToken cancellationToken)
    {
        if (State.VisibleItems.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var index = Math.Clamp(State.FocusedIndex + offset, 0, State.VisibleItems.Length - 1);
        return FocusAsync(index, cancellationToken);
    }

    /// <summary>
    /// Switches between aligned two-pane and unified layouts.
    /// </summary>
    internal void ToggleLayout()
    {
        State.ToggleLayout();
        Activity = State.IsSideBySide ? "Showing aligned side-by-side comparison" : "Showing unified comparison";
        NotifyChanged();
    }

    /// <summary>
    /// Moves to the previous or next hunk in every active presentation.
    /// </summary>
    /// <param name="offset">The signed hunk offset.</param>
    internal void MoveHunk(int offset)
    {
        Activity = State.MoveHunk(offset) ? "Focused comparison hunk" : "No textual hunk is available";
        NotifyChanged();
    }

    /// <summary>
    /// Selects the next or previous content match in the active comparison layout.
    /// </summary>
    /// <param name="reverse">Whether to search toward the start of the presentation.</param>
    internal void FindText(bool reverse)
    {
        Activity = State.FindText(reverse)
            ? $"Selected {(reverse ? "previous" : "next")} text match"
            : string.IsNullOrEmpty(State.Search.Text)
                ? "Enter comparison text to find"
                : "No comparison text matches the search";
        NotifyChanged();
    }

    /// <summary>
    /// Moves the active comparison layout to the one-based presentation line entered by the user.
    /// </summary>
    internal void GoToPresentationLine()
    {
        if (!int.TryParse(
                State.GoToLine.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var lineNumber) || lineNumber <= 0)
        {
            Activity = "Enter a positive one-based presentation line";
            NotifyChanged();
            return;
        }

        Activity = State.GoToPresentationLine(lineNumber)
            ? $"Focused presentation line {lineNumber.ToString(CultureInfo.InvariantCulture)}"
            : "That line is outside the active comparison";
        NotifyChanged();
    }

    /// <summary>
    /// Changes unified context by one bounded offset and recaptures the same exact comparison.
    /// </summary>
    /// <param name="offset">The signed context-line offset.</param>
    /// <param name="cancellationToken">Signals comparison recapture cancellation.</param>
    /// <returns>A task that completes after the requested context is visible.</returns>
    internal Task ChangeContextAsync(int offset, CancellationToken cancellationToken)
    {
        var next = Math.Clamp(_contextLines + offset, 0, 100_000);
        if (next == _contextLines || IsBusy)
        {
            Activity = $"Comparison context remains {_contextLines}";
            NotifyChanged();
            return Task.CompletedTask;
        }

        _contextLines = next;
        _configuration = _configuration.WithContextLines(next);
        return LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the complete current unified presentation for clipboard export.
    /// </summary>
    /// <returns>The current control-safe unified presentation text.</returns>
    internal string GetUnifiedPresentation()
        => State.UnifiedEditor.Document.GetText();

    /// <summary>
    /// Releases the owned raw comparison spool and any temporary file it uses.
    /// </summary>
    public void Dispose()
    {
        _document?.Dispose();
        _document = null;
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
        var item = State.FocusedItem;
        var document = _document;
        if (item is null || document is null)
        {
            State.SetMessage(
                document?.Index.Files.IsEmpty == true
                    ? "This comparison contains no changed files."
                    : "No changed file matches the current path filter.");
            return;
        }

        try
        {
            var bytes = await document.ReadFilePrefixAsync(
                item.File,
                MaximumPresentationBytes,
                cancellationToken).ConfigureAwait(false);
            if (request == Volatile.Read(ref _previewRequest) &&
                ReferenceEquals(document, _document) &&
                ReferenceEquals(item.File, State.FocusedItem?.File))
            {
                var presentation = ComparisonPresentationBuilder.Build(
                    bytes,
                    item.File,
                    item.File.Length > bytes.Length);
                State.SetPresentation(item.File, presentation, LeftLabel, RightLabel);
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
                State.SetMessage(TerminalTextSanitizer.Sanitize(exception.Message));
            }
        }
    }

    private static async Task<(DiffRequest Request, string LeftLabel, string RightLabel)> BuildRequestAsync(
        CanonicalDirectory workingDirectory,
        DiffOptions options,
        ImmutableArray<GitPath> pathspecs,
        RevisionResolver resolver,
        CancellationToken cancellationToken)
    {
        if (options.Cached && options.RightRevision is not null)
        {
            throw new ArgumentException("Option '--cached' accepts at most one revision.", nameof(options));
        }

        if (options.LeftRevision is null)
        {
            return options.Cached
                ? (DiffRequest.HeadToIndex(pathspecs), "HEAD", "Index")
                : (DiffRequest.IndexToWorkTree(pathspecs), "Index", "Worktree");
        }

        var left = await resolver.ResolveCommitAsync(
            workingDirectory,
            Revision.Create(options.LeftRevision),
            cancellationToken).ConfigureAwait(false);
        var leftLabel = CreateRevisionLabel(options.LeftRevision, left.CommitObjectId);
        if (options.RightRevision is null)
        {
            return options.Cached
                ? (DiffRequest.CommitToIndex(left.CommitObjectId, pathspecs), leftLabel, "Index")
                : (DiffRequest.CommitToWorkTree(left.CommitObjectId, pathspecs), leftLabel, "Worktree");
        }

        var right = await resolver.ResolveCommitAsync(
            workingDirectory,
            Revision.Create(options.RightRevision),
            cancellationToken).ConfigureAwait(false);
        return (
            DiffRequest.CommitToCommit(left.CommitObjectId, right.CommitObjectId, pathspecs),
            leftLabel,
            CreateRevisionLabel(options.RightRevision, right.CommitObjectId));
    }

    private static string CreateRevisionLabel(string revision, ObjectId objectId)
    {
        var safeRevision = TerminalTextSanitizer.Sanitize(revision);
        return $"{safeRevision} ({objectId.ToString()[..12]})";
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
