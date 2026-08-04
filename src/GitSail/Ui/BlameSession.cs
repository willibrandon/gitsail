using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Globalization;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Coordinates exact blame capture, search, line focus, parent navigation, and commit context.
/// </summary>
internal sealed class BlameSession
{
    private readonly CanonicalDirectory _workingDirectory;
    private readonly BlameService _service;
    private readonly HistoryService _historyService;
    private readonly Stack<(BlameRequest Request, int Line)> _backStack = new();
    private BlameRequest _request;
    private int? _preferredLine;
    private int _previewRequest;

    private BlameSession(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        GitInstallation installation,
        BlameService service,
        HistoryService historyService,
        BlameRequest request,
        int? preferredLine)
    {
        _workingDirectory = workingDirectory;
        Repository = repository;
        Installation = installation;
        _service = service;
        _historyService = historyService;
        _request = request;
        _preferredLine = preferredLine;
        State = new BlameWorkspaceState();
        Activity = "Ready to load line history";
    }

    /// <summary>
    /// Notifies the view that controlled line-history state has changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the discovered repository displayed by the blame workflow.
    /// </summary>
    internal RepositoryLocation Repository { get; }

    /// <summary>
    /// Gets the resolved Git installation used by this blame workflow.
    /// </summary>
    internal GitInstallation Installation { get; }

    /// <summary>
    /// Gets the controlled exact blame and commit-context state.
    /// </summary>
    internal BlameWorkspaceState State { get; }

    /// <summary>
    /// Gets the current or most recent line-history activity description.
    /// </summary>
    internal string Activity { get; private set; }

    /// <summary>
    /// Gets whether a blame capture or commit-context operation is active.
    /// </summary>
    internal bool IsBusy { get; private set; }

    /// <summary>
    /// Gets whether the most recent blame capture failed.
    /// </summary>
    internal bool HasLoadFailure { get; private set; }

    /// <summary>
    /// Gets whether the current request enables moved-line detection.
    /// </summary>
    internal bool DetectMoves => _request.DetectMoves;

    /// <summary>
    /// Gets whether the current request enables copied-line detection.
    /// </summary>
    internal bool DetectCopies => _request.DetectCopies;

    /// <summary>
    /// Gets whether a previously viewed blame location can be restored.
    /// </summary>
    internal bool CanNavigateBack => _backStack.Count != 0;

    /// <summary>
    /// Gets whether the focused line exposes a prior commit and path.
    /// </summary>
    internal bool CanNavigateParent => State.FocusedItem?.Attribution.Previous is not null;

    /// <summary>
    /// Gets the control-safe path currently being blamed.
    /// </summary>
    internal string PathDisplay => _request.Path.DisplayText;

    /// <summary>
    /// Gets the literal revision label or a worktree label for the current request.
    /// </summary>
    internal string RevisionDisplay => _request.Revision?.Value ?? "worktree";

    /// <summary>
    /// Opens a repository and creates its typed line-history workflow.
    /// </summary>
    /// <param name="launchDirectory">The canonical directory supplied by the user.</param>
    /// <param name="options">The typed blame command operands.</param>
    /// <param name="processEnvironment">The classified startup environment.</param>
    /// <param name="cancellationToken">Signals repository discovery and path-input cancellation.</param>
    /// <returns>The ready blame session before its first capture.</returns>
    internal static async Task<BlameSession> OpenAsync(
        CanonicalDirectory launchDirectory,
        BlameOptions options,
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
        var paths = await CommandPathspecResolver.ResolveAsync(
            options.Paths,
            options.NativePaths,
            options.PathspecFile,
            options.PathspecFileNul,
            cancellationToken).ConfigureAwait(false);

        if (paths.Length != 1)
        {
            throw new ArgumentException("Blame requires exactly one file path from the command line or pathspec file.", nameof(options));
        }

        var path = GitPathOperations.NormalizeFile(paths[0]);

        BlameRange? range = null;
        if (options.Range is not null && !BlameRange.TryParse(options.Range, out range))
        {
            throw new ArgumentException("The blame range must use start:end with positive line numbers.", nameof(options));
        }


        if (options.Line is not null && range is not null &&
            (options.Line < range.Start || options.Line > range.End))
        {
            throw new ArgumentException("The focused blame line must be inside the requested range.", nameof(options));
        }

        var request = new BlameRequest(
            options.Revision is null ? null : Revision.Create(options.Revision),
            path,
            range,
            options.DetectMoves,
            options.DetectCopies);
        return new BlameSession(
            workingDirectory,
            repository,
            installation,
            new BlameService(installation, runner, environmentFactory),
            new HistoryService(installation, runner, environmentFactory),
            request,
            options.Line);
    }

    /// <summary>
    /// Reloads exact content, line attribution, and focused commit context.
    /// </summary>
    /// <param name="cancellationToken">Signals blame capture cancellation.</param>
    /// <returns>A task that completes after controlled blame state is current.</returns>
    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        State.Clear();
        Activity = "Loading exact content and line history...";
        NotifyChanged();
        try
        {
            var catalog = await _service.CaptureAsync(
                _workingDirectory,
                Repository,
                _request,
                cancellationToken).ConfigureAwait(false);
            State.ApplyCatalog(catalog, _preferredLine);
            _preferredLine = null;
            HasLoadFailure = false;
            await CaptureFocusedPreviewAsync(cancellationToken).ConfigureAwait(false);
            Activity = catalog.Attributions.IsEmpty
                ? "The selected range contains no attributable lines"
                : $"Loaded {catalog.Attributions.Length} {(catalog.Attributions.Length == 1 ? "line" : "lines")} as {State.EncodingLabel}";
            if (State.EncodingWarning is not null)
            {
                Activity = State.EncodingWarning;
            }
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
    /// Applies an incremental line-history search and updates focused commit context.
    /// </summary>
    /// <param name="filter">The latest user-entered search text.</param>
    /// <param name="cancellationToken">Signals commit-context capture cancellation.</param>
    /// <returns>A task that completes after search and preview state are current.</returns>
    internal Task FilterAsync(string filter, CancellationToken cancellationToken)
    {
        State.SetFilter(filter);
        NotifyChanged();
        return ReloadFocusedPreviewAsync(cancellationToken);
    }

    /// <summary>
    /// Focuses one visible attributed line and loads its commit and file-history context.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    /// <param name="cancellationToken">Signals context capture cancellation.</param>
    /// <returns>A task that completes after focus and preview state are current.</returns>
    internal Task FocusAsync(int index, CancellationToken cancellationToken)
    {
        State.Focus(index);
        NotifyChanged();
        return ReloadFocusedPreviewAsync(cancellationToken);
    }

    /// <summary>
    /// Focuses the one-based line entered in the lifted line-navigation field.
    /// </summary>
    /// <param name="cancellationToken">Signals context capture cancellation.</param>
    /// <returns>A task that completes after line navigation and preview loading.</returns>
    internal Task GoToLineAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(
                State.GoToLine.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var lineNumber) || lineNumber <= 0)
        {
            Activity = "Enter a positive one-based line number";
            NotifyChanged();
            return Task.CompletedTask;
        }

        if (!State.GoTo(lineNumber))
        {
            Activity = "That line is outside the loaded range";
            NotifyChanged();
            return Task.CompletedTask;
        }

        Activity = $"Focused line {lineNumber.ToString(CultureInfo.InvariantCulture)}";
        NotifyChanged();
        return ReloadFocusedPreviewAsync(cancellationToken);
    }

    /// <summary>
    /// Reloads line history with moved-line detection toggled.
    /// </summary>
    /// <param name="cancellationToken">Signals blame capture cancellation.</param>
    /// <returns>A task that completes after the updated request is loaded.</returns>
    internal Task ToggleMoveDetectionAsync(CancellationToken cancellationToken)
    {
        _request = _request with { DetectMoves = !_request.DetectMoves };
        _preferredLine = State.FocusedItem?.Attribution.ResultLineNumber;
        return LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Reloads line history with cross-file copied-line detection toggled.
    /// </summary>
    /// <param name="cancellationToken">Signals blame capture cancellation.</param>
    /// <returns>A task that completes after the updated request is loaded.</returns>
    internal Task ToggleCopyDetectionAsync(CancellationToken cancellationToken)
    {
        _request = _request with { DetectCopies = !_request.DetectCopies };
        _preferredLine = State.FocusedItem?.Attribution.ResultLineNumber;
        return LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Opens the focused line's previous commit and path while retaining a back location.
    /// </summary>
    /// <param name="cancellationToken">Signals parent blame capture cancellation.</param>
    /// <returns>A task that completes after the parent location is loaded.</returns>
    internal Task NavigateParentAsync(CancellationToken cancellationToken)
    {
        var attribution = State.FocusedItem?.Attribution;
        if (attribution?.Previous is not { } previous)
        {
            return Task.CompletedTask;
        }

        _backStack.Push((_request, attribution.ResultLineNumber));
        _request = _request with
        {
            Revision = Revision.Create(previous.ObjectId.ToString()),
            Path = previous.Path,
            Range = null,
        };
        _preferredLine = attribution.SourceLineNumber;
        return LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Restores the most recently viewed blame request and line.
    /// </summary>
    /// <param name="cancellationToken">Signals restored blame capture cancellation.</param>
    /// <returns>A task that completes after the prior location is loaded.</returns>
    internal Task NavigateBackAsync(CancellationToken cancellationToken)
    {
        if (!_backStack.TryPop(out var location))
        {
            return Task.CompletedTask;
        }

        _request = location.Request;
        _preferredLine = location.Line;
        return LoadAsync(cancellationToken);
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
        var attribution = State.FocusedItem?.Attribution;
        if (attribution is null)
        {
            State.SetPreviewMessage("No line matches the current search.");
            return;
        }

        if (attribution.Commit.IsUncommitted)
        {
            State.SetPreview(
                attribution,
                "This line differs from the selected base commit and has not been committed yet.\n\n" +
                $"Path: {attribution.SourcePath.DisplayText}\n" +
                $"Line: {attribution.ResultLineNumber.ToString(CultureInfo.InvariantCulture)}");
            return;
        }

        try
        {
            var historyTask = _historyService.CaptureAsync(
                _workingDirectory,
                new HistoryQuery(
                    Revision.Create(attribution.Commit.ObjectId.ToString()),
                    [attribution.SourcePath],
                    12),
                cancellationToken);
            var patchTask = _historyService.ShowAsync(
                _workingDirectory,
                attribution.Commit.ObjectId,
                cancellationToken);
            await Task.WhenAll(historyTask, patchTask).ConfigureAwait(false);
            if (request != Volatile.Read(ref _previewRequest) ||
                State.FocusedItem?.Attribution.ResultLineNumber != attribution.ResultLineNumber)
            {
                return;
            }

            var history = await historyTask.ConfigureAwait(false);
            var patch = await patchTask.ConfigureAwait(false);
            State.SetPreview(
                attribution,
                BuildHistoryContext(history) + RawPatchPresentationDecoder.Decode(patch.Span, isTruncated: false));
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

    private static string BuildHistoryContext(HistoryCatalog history)
    {
        var builder = new StringBuilder("Recent commits for this path:\n");
        foreach (var commit in history.Commits)
        {
            builder.Append(commit.ObjectId.ToString()[..12])
                .Append("  ")
                .Append(commit.AuthoredAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append("  ")
                .Append(Decode(commit.Subject.Span, "(no subject)"))
                .Append('\n');
        }

        builder.Append("\nSelected commit and patch:\n\n");
        return builder.ToString();
    }

    private static string Decode(ReadOnlySpan<byte> bytes, string emptyValue)
        => bytes.IsEmpty ? emptyValue : GitPath.FromUnixBytes(bytes).DisplayText;

    private static bool IsExpectedFailure(Exception exception)
        => exception is ArgumentException or
            ExecutableResolutionException or
            GitCommandException or
            InvalidDataException or
            FileNotFoundException or
            IOException or
            UnauthorizedAccessException;

    private void NotifyChanged()
        => Changed?.Invoke();
}
