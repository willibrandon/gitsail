using GitSail.Domain;
using GitSail.Localization.Generated;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Composes the responsive keyboard-and-mouse structured history workflow.
/// </summary>
internal sealed class HistoryView
{
    private static readonly ActionId[] s_previewViewportActions =
    [
        EditorWidget.MoveLeft,
        EditorWidget.MoveRight,
        EditorWidget.MoveUp,
        EditorWidget.MoveDown,
        EditorWidget.MoveToLineStart,
        EditorWidget.MoveToLineEnd,
        EditorWidget.MoveToDocumentStart,
        EditorWidget.MoveToDocumentEnd,
        EditorWidget.MoveWordLeft,
        EditorWidget.MoveWordRight,
        EditorWidget.PageUp,
        EditorWidget.PageDown,
        EditorWidget.SelectLeft,
        EditorWidget.SelectRight,
        EditorWidget.SelectUp,
        EditorWidget.SelectDown,
        EditorWidget.SelectToLineStart,
        EditorWidget.SelectToLineEnd,
        EditorWidget.SelectPageUp,
        EditorWidget.SelectPageDown,
        EditorWidget.SelectToDocumentStart,
        EditorWidget.SelectToDocumentEnd,
        EditorWidget.SelectWordLeft,
        EditorWidget.SelectWordRight,
        EditorWidget.Click,
        EditorWidget.CtrlClick,
        EditorWidget.DoubleClick,
        EditorWidget.TripleClick,
        EditorWidget.ScrollUp,
        EditorWidget.ScrollDown,
        EditorWidget.ScrollLeft,
        EditorWidget.ScrollRight,
    ];
    private readonly HistorySession _session;
    private readonly CancellationToken _cancellationToken;
    private Hex1bApp? _application;
    private WindowManager? _popupWindowManager;
    private readonly List<WindowHandle> _popupWindows = [];
    private readonly PopupViewport _popupViewport = new();
    private ObjectId? _previewObjectId;
    private Action? _requestCleanRepaint;

    /// <summary>
    /// Initializes a structured history view over controlled session state.
    /// </summary>
    /// <param name="session">The structured history state and action source.</param>
    /// <param name="cancellationToken">Signals application shutdown.</param>
    internal HistoryView(HistorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Connects history invalidation notifications to the owning terminal application.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    /// <param name="requestCleanRepaint">Requests a clean physical repaint after preview viewport changes.</param>
    internal void Attach(Hex1bApp application, Action? requestCleanRepaint = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The history view is already attached.");
        }

        _application = application;
        _requestCleanRepaint = requestCleanRepaint;
        _previewObjectId = _session.State.FocusedItem?.Commit.ObjectId;
        _session.Changed += HandleChanged;
    }

    /// <summary>
    /// Disconnects history invalidation notifications from the owning application.
    /// </summary>
    internal void Detach()
    {
        if (_application is null)
        {
            return;
        }

        _session.Changed -= HandleChanged;
        _application = null;
        _requestCleanRepaint = null;
        _previewObjectId = null;
        _popupWindowManager = null;
        _popupWindows.Clear();
    }

    /// <summary>
    /// Builds the complete responsive history widget tree for one render generation.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The structured history workspace.</returns>
    internal Hex1bWidget Build(RootContext context)
        => context.Responsive(responsive =>
        [
            responsive.When(
                _popupViewport.Capture,
                builder => BuildWindowPanel(builder)),
        ]);

    private WindowPanelWidget BuildWindowPanel<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.WindowPanel()
            .Background(background => background.ZStack(layers =>
            [
                BuildWorkspace(layers),
                _popupWindows.Count > 0
                    ? layers.Backdrop()
                        .Transparent()
                        .OnClickAway(CloseActivePopup)
                    : null,
            ]).Fill())
            .Fill();

    private VStackWidget BuildWorkspace<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
            [
                BuildHeader(builder),
                builder.Responsive(responsive =>
                [
                    responsive.When(
                        static (width, height) => width < 60 || height < 15,
                        compact => compact.Border(compact.Text(
                            "Resize the terminal to at least 60 columns by 18 rows. Ctrl+Q remains available.").Wrap())
                            .Title("More room needed")
                            .Fill()),
                    responsive.WhenMinWidth(
                        100,
                        wide => BuildHistoryContent(wide, 52)),
                    responsive.Otherwise(medium => BuildHistoryContent(medium, 38)),
                ]).Fill(),
                BuildActions(builder),
                BuildShortcuts(builder),
            ]).InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.F5).Action(
                    _ => _session.LoadAsync(_cancellationToken),
                    "Refresh structured commit history");
                bindings.Ctrl().Key(Hex1bKey.R).Action(
                    _ => _session.LoadAsync(_cancellationToken),
                    "Refresh structured commit history");
                bindings.Key(Hex1bKey.F7).Action(
                    _ => FocusFilter(),
                    "Focus history search");
                bindings.Key(Hex1bKey.J).Action(
                    _ => _session.MoveFocusAsync(1, _cancellationToken),
                    "Focus the next commit");
                bindings.Key(Hex1bKey.K).Action(
                    _ => _session.MoveFocusAsync(-1, _cancellationToken),
                    "Focus the previous commit");
                if (_session.PendingOperation is null)
                {
                    bindings.Key(Hex1bKey.C).Action(
                        actionContext => ShowOperationDialogAsync(
                            actionContext.Windows,
                            HistoryCommitOperation.CherryPick),
                        "Review and cherry-pick the focused commit");
                    bindings.Key(Hex1bKey.R).Action(
                        actionContext => ShowOperationDialogAsync(
                            actionContext.Windows,
                            HistoryCommitOperation.Revert),
                        "Review and revert the focused commit");
                }
                else
                {
                    bindings.Key(Hex1bKey.C).Action(
                        _ => _session.ContinueOperationAsync(_cancellationToken),
                        "Continue the stopped history operation");
                    bindings.Key(Hex1bKey.S).Action(
                        actionContext => ShowControlConfirmation(
                            actionContext.Windows,
                            abort: false),
                        "Review and skip the stopped history operation");
                    bindings.Key(Hex1bKey.A).Action(
                        actionContext => ShowControlConfirmation(
                            actionContext.Windows,
                            abort: true),
                        "Review and abort the stopped history operation");
                }

                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            }).Fill();

    private HStackWidget BuildHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var repository = RepositoryLabel.Create(_session.Repository);
        return context.HStack(header =>
        [
            header.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("history"),
                info.Spacer(),
                info.Section($" | {repository}"),
                info.Section($"Git {_session.Installation.Version}"),
            ]).Divider(" | ").FillWidth(),
        ]).FillWidth();
    }

    private SplitterWidget BuildHistoryContent<TParent>(WidgetContext<TParent> context, int split)
        where TParent : Hex1bWidget
        => context.HSplitter(
            context.VStack(left =>
            [
                left.HStack(filter =>
                [
                    filter.Text("Find: "),
                    filter.TextBox()
                        .State(_session.State.Filter)
                        .OnTextChanged(eventArgs => _session.FilterAsync(
                            eventArgs.NewText,
                            _cancellationToken))
                        .FillWidth(),
                ]).FillWidth(),
                left.List(_session.State.VisibleItems)
                    .ItemKey(static item => item.Commit.ObjectId)
                    .FocusedIndex(_session.State.FocusedIndex)
                    .OnFocusChanged(eventArgs => _session.FocusAsync(
                        eventArgs.FocusedIndex,
                        _cancellationToken))
                    .InputBindings(ConfigureClampedListNavigation)
                    .Empty(empty => empty.Text(
                        _session.State.Catalog?.Commits.IsEmpty == true
                            ? "No commits match this history request."
                            : "No commit matches the current search."))
                    .Fill(),
                left.VStack(details => BuildDetails(details)),
            ]).Fill(),
            context.Border(
                context.Editor(_session.State.Preview)
                    .LineNumbers()
                    .WordWrap(false)
                    .Decorations(_session.State.PreviewDecorationProvider)
                    .InputBindings(ConfigurePreviewViewportBindings)
                    .Fill())
                .Title(_session.State.PreviewTitle)
                .Fill(),
            split).Fill();

    private void ConfigureClampedListNavigation(InputBindingsBuilder bindings)
    {
        bindings.Remove(ListWidget<HistoryWorkspaceItem>.MoveUp);
        bindings.Remove(ListWidget<HistoryWorkspaceItem>.MoveDown);
        bindings.Remove(ListWidget<HistoryWorkspaceItem>.ScrollUp);
        bindings.Remove(ListWidget<HistoryWorkspaceItem>.ScrollDown);
        bindings.Key(Hex1bKey.UpArrow).Action(
            _ => _session.MoveFocusAsync(-1, _cancellationToken),
            "Move toward the newest commit");
        bindings.Key(Hex1bKey.DownArrow).Action(
            _ => _session.MoveFocusAsync(1, _cancellationToken),
            "Move toward the oldest commit");
        bindings.Mouse(MouseButton.ScrollUp).Action(
            _ => _session.MoveFocusAsync(-1, _cancellationToken),
            "Scroll toward the newest commit");
        bindings.Mouse(MouseButton.ScrollDown).Action(
            _ => _session.MoveFocusAsync(1, _cancellationToken),
            "Scroll toward the oldest commit");
    }

    private void ConfigurePreviewViewportBindings(InputBindingsBuilder bindings)
    {
        foreach (var actionId in s_previewViewportActions)
        {
            var keyBindings = bindings.Bindings
                .Where(binding => binding.ActionId == actionId)
                .ToArray();
            var mouseBindings = bindings.MouseBindings
                .Where(binding => binding.ActionId == actionId)
                .ToArray();
            if (keyBindings.Length == 0 && mouseBindings.Length == 0)
            {
                continue;
            }

            bindings.Remove(actionId);
            foreach (var binding in keyBindings)
            {
                bindings.Add(new InputBinding(
                    binding.Steps,
                    context => ExecutePreviewViewportActionAsync(binding.ExecuteAsync, context),
                    binding.Description,
                    binding.IsGlobal,
                    actionId,
                    binding.OverridesCapture));
            }

            foreach (var binding in mouseBindings)
            {
                bindings.Add(new MouseBinding(
                    binding.Button,
                    binding.Action,
                    binding.Modifiers,
                    binding.ClickCount,
                    context => ExecutePreviewViewportActionAsync(binding.ExecuteAsync, context),
                    binding.Description,
                    actionId,
                    binding.OverridesCapture));
            }
        }
    }

    private async Task ExecutePreviewViewportActionAsync(
        Func<InputBindingActionContext, Task> action,
        InputBindingActionContext context)
    {
        var editor = FindPreviewEditor();
        var before = editor is null
            ? default
            : (editor.ScrollOffset, editor.HorizontalScrollOffset);
        await action(context).ConfigureAwait(false);
        if (editor is not null &&
            before != (editor.ScrollOffset, editor.HorizontalScrollOffset))
        {
            _requestCleanRepaint?.Invoke();
        }
    }

    private Hex1bWidget[] BuildDetails<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var commit = _session.State.FocusedItem?.Commit;
        if (commit is null)
        {
            return [context.Text(AppMessages.HistoryPromptSelectCommit).Wrap()];
        }

        var author = Decode(commit.AuthorName.Span, AppMessages.HistoryValueUnknownAuthor);
        var email = Decode(commit.AuthorEmail.Span, AppMessages.HistoryValueNoEmail);
        var decorations = Decode(commit.Decorations.Span, string.Empty);
        var parents = commit.Parents.IsEmpty
            ? AppMessages.HistoryValueRootParents
            : string.Join(' ', commit.Parents.Select(static parent => parent.ToString()[..12]));
        var details = new List<Hex1bWidget>
        {
            context.Text(AppMessages.HistoryDetailAuthor(author: author, email: email)).Wrap(),
            context.Text(AppMessages.HistoryDetailDate($"{commit.AuthoredAt.ToLocalTime():F}")).Wrap(),
        };
        if (decorations.Length > 0)
        {
            details.Add(context.Text(AppMessages.HistoryDetailReferences(decorations)).Wrap());
        }

        details.Add(context.Text(AppMessages.HistoryDetailParents(parents)).Wrap());
        details.Add(context.Text(AppMessages.HistoryDetailSignature(
            FormatSignature(commit.SignatureStatus))).Wrap());
        return [.. details];
    }

    private ResponsiveWidget BuildActions<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(
                100,
                wide => wide.HStack(actions => BuildActionItems(actions, compact: false)).FillWidth()),
            responsive.Otherwise(
                compact => compact.HStack(actions => BuildActionItems(actions, compact: true)).FillWidth()),
        ]);

    private Hex1bWidget[] BuildActionItems<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
    {
        var actions = new List<Hex1bWidget>
        {
            BuildRefreshAction(context),
            context.Text(" "),
            context.Button("Find").OnClick(_ => FocusFilter()),
        };
        if (_session.PendingOperation is null)
        {
            actions.Add(context.Text(" "));
            actions.Add(context.Button(compact ? "Pick..." : "Cherry-pick...").OnClick(eventArgs =>
                ShowOperationDialogAsync(
                    eventArgs.Context.Windows,
                    HistoryCommitOperation.CherryPick)));
            actions.Add(context.Text(" "));
            actions.Add(context.Button(compact ? "Revert..." : "Revert commit...").OnClick(eventArgs =>
                ShowOperationDialogAsync(
                    eventArgs.Context.Windows,
                    HistoryCommitOperation.Revert)));
        }
        else
        {
            actions.Add(context.Text(" "));
            actions.Add(context.Button("Continue").OnClick(
                _ => _session.ContinueOperationAsync(_cancellationToken)));
            actions.Add(context.Text(" "));
            actions.Add(context.Button("Skip...").OnClick(eventArgs =>
                ShowControlConfirmation(eventArgs.Context.Windows, abort: false)));
            actions.Add(context.Text(" "));
            actions.Add(context.Button("Abort...").OnClick(eventArgs =>
                ShowControlConfirmation(eventArgs.Context.Windows, abort: true)));
        }

        actions.Add(context.Text(compact ? string.Empty : $" {_session.Activity} ").FillWidth());
        actions.Add(context.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()));
        return [.. actions];
    }

    private ResponsiveWidget BuildShortcuts<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(
                100,
                wide => BuildShortcutInfo(wide, compact: false)),
            responsive.Otherwise(
                compact => BuildShortcutInfo(compact, compact: true)),
        ]);

    private InfoBarWidget BuildShortcutInfo<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        {
            var sections = new List<IInfoBarChild>
            {
                info.Section(_session.PendingOperation is null ? "C Pick" : "C Continue"),
                info.Section(_session.PendingOperation is null ? "R Revert" : "S Skip"),
                info.Section(_session.PendingOperation is null ? "F5 Refresh" : "A Abort"),
            };
            if (!compact)
            {
                sections.Add(info.Section("F7 Find"));
                sections.Add(info.Section("J/K Select"));
                sections.Add(info.Section("Mouse Select/Scroll/Resize"));
            }

            sections.Add(info.Spacer());
            sections.Add(info.Section("Ctrl+Q Quit"));
            return [.. sections];
        }).Divider(" | ");

    private void OpenPopup(WindowManager windows, WindowHandle popup)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(popup);
        _popupWindowManager = windows;
        _popupWindows.Add(popup);
        popup.OnClose(() =>
        {
            _popupWindows.Remove(popup);
            if (_popupWindows.Count == 0)
            {
                _popupWindowManager = null;
            }
        });
        popup.Open(windows);
    }

    private void CloseActivePopup()
    {
        if (_popupWindowManager is not { } windows)
        {
            return;
        }

        var activeWindow = windows.ActiveWindow;
        if (activeWindow is null)
        {
            return;
        }

        for (var index = _popupWindows.Count - 1; index >= 0; index--)
        {
            var popup = _popupWindows[index];
            if (ReferenceEquals(windows.Get(popup), activeWindow))
            {
                windows.Close(popup);
                return;
            }
        }
    }

    private Task ShowOperationDialogAsync(
        WindowManager windows,
        HistoryCommitOperation operation)
    {
        var commit = _session.State.FocusedItem?.Commit;
        if (commit is null || _session.IsBusy || _session.PendingOperation is not null)
        {
            return Task.CompletedTask;
        }

        if (commit.Parents.Length > 1)
        {
            ShowMainlineParentDialog(windows, commit, operation);
            return Task.CompletedTask;
        }

        return ShowOperationConfirmationAsync(windows, commit, operation, mainlineParent: null);
    }

    private void ShowMainlineParentDialog(
        WindowManager windows,
        HistoryCommit commit,
        HistoryCommitOperation operation)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text(
                $"{commit.ObjectId.ToString()[..12]} is a merge commit with {commit.Parents.Length} parents.").Wrap(),
            builder.Text("Choose the parent that represents the history line to keep.").Wrap(),
            builder.Text("The change relative to that parent will be applied.").Wrap(),
            builder.VScrollPanel(parents =>
                [.. commit.Parents.Select((parent, index) =>
                    parents.Button($"Parent {index + 1}: {parent.ToString()[..12]}")
                        .OnClick(async _ =>
                        {
                            window.Window.CloseWithResult(index + 1);
                            await ShowOperationConfirmationAsync(
                                windows,
                                commit,
                                operation,
                                index + 1).ConfigureAwait(false);
                        }))],
                showScrollbar: true).Fill(),
            builder.Button("Cancel").OnClick(_ => window.Window.Cancel()),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close the mainline parent selector")))
        .Title("Choose merge parent")
        .Size(
            _popupViewport.FitWidth(68),
            _popupViewport.FitHeight(Math.Min(18, 8 + commit.Parents.Length)))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(50, 10, 100, 32));
    }

    private async Task ShowOperationConfirmationAsync(
        WindowManager windows,
        HistoryCommit commit,
        HistoryCommitOperation operation,
        int? mainlineParent)
    {
        var plan = await _session.PrepareOperationAsync(
            operation,
            mainlineParent,
            _cancellationToken).ConfigureAwait(false);
        if (plan is null || !plan.Commit.Equals(commit.ObjectId))
        {
            return;
        }

        var subject = Decode(commit.Subject.Span, "(no subject)");
        var operationLabel = operation == HistoryCommitOperation.CherryPick
            ? "Cherry-pick"
            : "Revert commit";
        var branch = FormatHeadName(plan.Precondition.HeadName);
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var content = new List<Hex1bWidget>
            {
                builder.Text($"Commit: {plan.Commit}"),
                builder.Text($"Subject: {subject}").Wrap(),
                builder.Text($"Current target: {branch}"),
            };
            if (mainlineParent is not null)
            {
                content.Add(builder.Text($"Merge mainline: parent {mainlineParent}"));
            }

            content.Add(builder.Text(operation == HistoryCommitOperation.CherryPick
                ? "Git will apply this change and create a new commit on the current target."
                : "Git will apply the inverse change and create a new commit on the current target.").Wrap());
            content.Add(builder.Text("If Git stops, this view will offer Continue, Skip, and Abort."));
            content.Add(builder.HStack(buttons =>
            [
                buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                buttons.Text(" "),
                buttons.Button(operationLabel).OnClick(async _ =>
                {
                    window.Window.CloseWithResult(true);
                    await _session.ExecuteOperationAsync(plan, _cancellationToken).ConfigureAwait(false);
                }),
            ]));
            return [.. content];
        }).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close the history operation confirmation")))
        .Title($"{operationLabel} this commit?")
        .Size(
            _popupViewport.FitWidth(76),
            _popupViewport.FitHeight(mainlineParent is null ? 12 : 13))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(54, 11, 100, 22));
    }

    private void ShowControlConfirmation(WindowManager windows, bool abort)
    {
        var state = _session.PendingOperation;
        if (state is null || _session.IsBusy)
        {
            return;
        }

        var operationName = state.Operation == HistoryCommitOperation.CherryPick
            ? "cherry-pick"
            : "commit revert";
        var action = abort ? "Abort" : "Skip";
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Stopped operation: {operationName}"),
            builder.Text($"Commit: {state.Commit}"),
            builder.Text(abort
                ? "Git will restore the repository state from before this operation started."
                : "Git will discard this commit's partial application and advance past it.").Wrap(),
            builder.HStack(buttons =>
            [
                buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                buttons.Text(" "),
                buttons.Button(action).OnClick(async _ =>
                {
                    window.Window.CloseWithResult(true);
                    if (abort)
                    {
                        await _session.AbortOperationAsync(_cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _session.SkipOperationAsync(_cancellationToken).ConfigureAwait(false);
                    }
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close the stopped operation confirmation")))
        .Title($"{action} {operationName}?")
        .Size(_popupViewport.FitWidth(72), _popupViewport.FitHeight(9))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(52, 9, 96, 16));
    }

    private void FocusFilter()
    {
        _application?.RequestFocus(node =>
            node is TextBoxNode);
        _application?.Invalidate();
    }

    private Hex1bWidget BuildRefreshAction<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.IsBusy
            ? context.Text(" Refresh ")
            : context.Button("Refresh").OnClick(_ => _session.LoadAsync(_cancellationToken));

    private void HandleChanged()
    {
        var focusedObjectId = _session.State.FocusedItem?.Commit.ObjectId;
        if (focusedObjectId is not null && !focusedObjectId.Equals(_previewObjectId))
        {
            _previewObjectId = focusedObjectId;
            ResetPreviewViewport();
            _requestCleanRepaint?.Invoke();
        }

        _application?.Invalidate();
    }

    private void ResetPreviewViewport()
    {
        var editor = FindPreviewEditor();
        if (editor is null)
        {
            return;
        }

        var bindings = new InputBindingsBuilder();
        editor.ConfigureDefaultBindings(bindings);
        bindings.GetBindings(EditorWidget.MoveToDocumentStart)
            .Single()
            .ExecuteAsync(null!)
            .GetAwaiter()
            .GetResult();
    }

    private EditorNode? FindPreviewEditor()
        => _application?.Focusables
            .OfType<EditorNode>()
            .FirstOrDefault(node => ReferenceEquals(node.State, _session.State.Preview));

    private static string Decode(ReadOnlySpan<byte> bytes, string emptyValue)
        => bytes.IsEmpty
            ? emptyValue
            : TerminalTextSanitizer.Sanitize(GitPath.FromUnixBytes(bytes).DisplayText);

    private static string FormatSignature(CommitSignatureStatus status)
        => status switch
        {
            CommitSignatureStatus.None => AppMessages.HistorySignatureUnsigned,
            CommitSignatureStatus.Good => AppMessages.HistorySignatureValid,
            CommitSignatureStatus.Bad => AppMessages.HistorySignatureBad,
            CommitSignatureStatus.UnknownValidity => AppMessages.HistorySignatureValidTrustUnknown,
            CommitSignatureStatus.ExpiredSignature => AppMessages.HistorySignatureExpiredSignature,
            CommitSignatureStatus.ExpiredKey => AppMessages.HistorySignatureExpiredKey,
            CommitSignatureStatus.RevokedKey => AppMessages.HistorySignatureRevokedKey,
            CommitSignatureStatus.CannotCheck => AppMessages.HistorySignatureCannotVerify,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static string FormatHeadName(RefName? headName)
    {
        const string localBranchPrefix = "refs/heads/";
        if (headName is null)
        {
            return "detached HEAD";
        }

        var displayText = headName.DisplayText;
        return displayText.StartsWith(localBranchPrefix, StringComparison.Ordinal)
            ? $"branch {displayText[localBranchPrefix.Length..]}"
            : displayText;
    }
}
