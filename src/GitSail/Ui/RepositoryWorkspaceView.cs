using GitSail.CommandLine;
using Hex1b;
using Hex1b.Input;
using Hex1b.LanguageServer;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Composes the responsive, first-class keyboard and mouse repository workspace.
/// </summary>
internal sealed class RepositoryWorkspaceView
{
    private readonly ApplicationMode _mode;
    private readonly IRepositoryWorkspaceSession _workspace;
    private readonly CancellationToken _cancellationToken;
    private readonly GitDiffDecorationProvider _diffDecorationProvider = new();
    private Hex1bApp? _application;

    /// <summary>
    /// Initializes a repository workspace view over controlled session state.
    /// </summary>
    /// <param name="mode">The selected top-level application workflow.</param>
    /// <param name="workspace">The repository state and action source.</param>
    /// <param name="cancellationToken">Signals application shutdown.</param>
    internal RepositoryWorkspaceView(
        ApplicationMode mode,
        IRepositoryWorkspaceSession workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _mode = mode;
        _workspace = workspace;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Connects invalidation notifications to the application rendering this view.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    internal void Attach(Hex1bApp application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The repository workspace view is already attached.");
        }

        _application = application;
        _workspace.Changed += HandleWorkspaceChanged;
    }

    /// <summary>
    /// Disconnects repository invalidation notifications from the owning application.
    /// </summary>
    internal void Detach()
    {
        if (_application is null)
        {
            return;
        }

        _workspace.Changed -= HandleWorkspaceChanged;
        _application = null;
    }

    /// <summary>
    /// Builds the complete responsive workspace widget tree for one render generation.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The controlled repository workspace widget.</returns>
    internal VStackWidget Build(RootContext context)
        => context.VStack(builder =>
        [
            BuildHeader(builder),
            builder.Responsive(responsive =>
            [
                responsive.When(
                    static (width, height) => width < 60 || height < 18,
                    compact => BuildResizeView(compact)),
                responsive.WhenMinWidth(
                    100,
                    wide => wide.HSplitter(
                        BuildChangesPane(wide),
                        BuildDetailPane(wide),
                        44).Fill()),
                responsive.Otherwise(medium => medium.VSplitter(
                    BuildChangesPane(medium),
                    BuildDetailPane(medium),
                    11).Fill()),
            ]).Fill(),
            BuildActionBar(builder),
            BuildShortcutBar(builder),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.S).Action(
                _ => _workspace.StageAsync(_cancellationToken),
                "Stage checked or focused paths");
            bindings.Key(Hex1bKey.A).Action(
                _ => _workspace.StageAllAsync(_cancellationToken),
                "Stage all changes");
            bindings.Key(Hex1bKey.U).Action(
                _ => _workspace.UnstageAsync(_cancellationToken),
                "Unstage checked or focused paths");
            bindings.Shift().Key(Hex1bKey.U).Action(
                _ => _workspace.UnstageAllAsync(_cancellationToken),
                "Unstage all changes");
            bindings.Key(Hex1bKey.Oem4).Action(
                _ => _workspace.DecreaseDiffContextAsync(_cancellationToken),
                "Show less diff context");
            bindings.Key(Hex1bKey.Oem6).Action(
                _ => _workspace.IncreaseDiffContextAsync(_cancellationToken),
                "Show more diff context");
            bindings.Key(Hex1bKey.F5).Action(
                _ => _workspace.RefreshAsync(_cancellationToken),
                "Refresh repository status");
            bindings.Key(Hex1bKey.F4).Action(
                _ => _workspace.CommitAsync(_cancellationToken),
                "Commit staged changes");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }).Fill();

    private InfoBarWidget BuildHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var snapshot = _workspace.State.Snapshot;
        var branch = snapshot.HeadName?.DisplayText ??
            (snapshot.HeadObjectId is null ? "unborn" : "detached");
        var repository = snapshot.Repository.WorkTree?.DisplayText ?? snapshot.Repository.GitDirectory.DisplayText;
        var tracking = snapshot.UpstreamName is null
            ? string.Empty
            : $" | {snapshot.UpstreamName.DisplayText} +{snapshot.AheadCount}/-{snapshot.BehindCount}";
        return context.InfoBar(info =>
        [
            info.Section(" GitSail "),
            info.Section(_mode.ToString().ToLowerInvariant()),
            info.Section(branch + tracking),
            info.Spacer(),
            info.Section(repository),
            info.Section($"Git {_workspace.Installation.Version}"),
        ]).Divider(" | ");
    }

    private SplitterWidget BuildChangesPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VSplitter(
            BuildUnstagedPane(context),
            BuildStagedPane(context),
            9).Fill();

    private BorderWidget BuildUnstagedPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var state = _workspace.State;
        var list = context.List(state.UnstagedItems)
            .ItemKey(static item => item.Path)
            .MultiSelect()
            .FocusedIndex(state.UnstagedFocusedIndex)
            .SelectedIndices(state.UnstagedSelectedIndices)
            .OnFocusChanged(async eventArgs =>
            {
                await _workspace.FocusUnstagedAsync(
                    eventArgs.FocusedIndex,
                    _cancellationToken).ConfigureAwait(false);
            })
            .OnSelectionChanged(eventArgs =>
            {
                state.SetUnstagedSelection(eventArgs.SelectedIndices, eventArgs.ToggledIndex);
                _application?.Invalidate();
            })
            .Empty(empty => empty.Text("Working tree clean."))
            .InputBindings(bindings =>
            {
                bindings.Mouse(MouseButton.Left).Ctrl().Action(async actionContext =>
                {
                    var index = GetPointerItemIndex(actionContext);
                    if (index >= 0)
                    {
                        state.ToggleUnstagedSelection(index);
                        await _workspace.FocusUnstagedAsync(index, _cancellationToken).ConfigureAwait(false);
                        actionContext.Invalidate();
                    }
                }, "Toggle worktree row selection");
                bindings.Mouse(MouseButton.Left).Shift().Action(async actionContext =>
                {
                    var index = GetPointerItemIndex(actionContext);
                    if (index >= 0)
                    {
                        state.ExtendUnstagedSelection(index);
                        await _workspace.FocusUnstagedAsync(index, _cancellationToken).ConfigureAwait(false);
                        actionContext.Invalidate();
                    }
                }, "Extend worktree row selection");
            });
        return context.Border(list.Fill())
            .Title($"Unstaged ({state.UnstagedItems.Length})")
            .Fill();
    }

    private BorderWidget BuildStagedPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var state = _workspace.State;
        var list = context.List(state.StagedItems)
            .ItemKey(static item => item.Path)
            .MultiSelect()
            .FocusedIndex(state.StagedFocusedIndex)
            .SelectedIndices(state.StagedSelectedIndices)
            .OnFocusChanged(async eventArgs =>
            {
                await _workspace.FocusStagedAsync(
                    eventArgs.FocusedIndex,
                    _cancellationToken).ConfigureAwait(false);
            })
            .OnSelectionChanged(eventArgs =>
            {
                state.SetStagedSelection(eventArgs.SelectedIndices, eventArgs.ToggledIndex);
                _application?.Invalidate();
            })
            .Empty(empty => empty.Text("No staged changes."))
            .InputBindings(bindings =>
            {
                bindings.Mouse(MouseButton.Left).Ctrl().Action(async actionContext =>
                {
                    var index = GetPointerItemIndex(actionContext);
                    if (index >= 0)
                    {
                        state.ToggleStagedSelection(index);
                        await _workspace.FocusStagedAsync(index, _cancellationToken).ConfigureAwait(false);
                        actionContext.Invalidate();
                    }
                }, "Toggle index row selection");
                bindings.Mouse(MouseButton.Left).Shift().Action(async actionContext =>
                {
                    var index = GetPointerItemIndex(actionContext);
                    if (index >= 0)
                    {
                        state.ExtendStagedSelection(index);
                        await _workspace.FocusStagedAsync(index, _cancellationToken).ConfigureAwait(false);
                        actionContext.Invalidate();
                    }
                }, "Extend index row selection");
            });
        return context.Border(list.Fill())
            .Title($"Staged ({state.StagedItems.Length})")
            .Fill();
    }

    private ResponsiveWidget BuildDetailPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.When(
                static (_, height) => height >= 20,
                spacious => BuildDetailLayout(spacious, diffRows: 14)),
            responsive.Otherwise(compact => BuildDetailLayout(compact, diffRows: 8)),
        ]).Fill();

    private SplitterWidget BuildDetailLayout<TParent>(
        WidgetContext<TParent> context,
        int diffRows)
        where TParent : Hex1bWidget
        => context.VSplitter(
            BuildDiffPane(context),
            BuildCommitPane(context),
            diffRows).Fill();

    private BorderWidget BuildDiffPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var editor = context.Editor(_workspace.Diff.Editor)
            .LineNumbers()
            .WordWrap(false)
            .Decorations(_diffDecorationProvider)
            .InputBindings(bindings =>
            {
                bindings.Remove(EditorWidget.Undo);
                bindings.Remove(EditorWidget.Redo);
                bindings.Remove(EditorWidget.DeleteBackward);
                bindings.Remove(EditorWidget.DeleteForward);
                bindings.Remove(EditorWidget.DeleteWordBackward);
                bindings.Remove(EditorWidget.DeleteWordForward);
                bindings.Remove(EditorWidget.DeleteLine);
                bindings.Remove(EditorWidget.InsertNewline);
                bindings.Remove(EditorWidget.InsertTab);
                bindings.Remove(Hex1bKey.Spacebar, Hex1bModifiers.Control);
                bindings.Remove(Hex1bKey.K, Hex1bModifiers.Control);
                bindings.Remove(Hex1bKey.F12);
                bindings.Remove(Hex1bKey.F12, Hex1bModifiers.Shift);
                bindings.Key(Hex1bKey.S).Action(
                    _ => _workspace.StageFocusedHunkAsync(_cancellationToken),
                    "Stage hunk under diff cursor");
                bindings.Key(Hex1bKey.U).Action(
                    _ => _workspace.UnstageFocusedHunkAsync(_cancellationToken),
                    "Unstage hunk under diff cursor");
                bindings.Key(Hex1bKey.J).Action(
                    _ => _workspace.FocusNextHunkAsync(),
                    "Focus next diff hunk");
                bindings.Key(Hex1bKey.K).Action(
                    _ => _workspace.FocusPreviousHunkAsync(),
                    "Focus previous diff hunk");
                bindings.Key(Hex1bKey.A).Action(
                    _ => _workspace.StageAllAsync(_cancellationToken),
                    "Stage all changes");
                bindings.Shift().Key(Hex1bKey.U).Action(
                    _ => _workspace.UnstageAllAsync(_cancellationToken),
                    "Unstage all changes");
                bindings.Key(Hex1bKey.Oem4).Action(
                    _ => _workspace.DecreaseDiffContextAsync(_cancellationToken),
                    "Show less diff context");
                bindings.Key(Hex1bKey.Oem6).Action(
                    _ => _workspace.IncreaseDiffContextAsync(_cancellationToken),
                    "Show more diff context");
                bindings.Key(Hex1bKey.F5).Action(
                    _ => _workspace.RefreshAsync(_cancellationToken),
                    "Refresh repository status");
                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            });
        return context.Border(editor.Fill())
            .Title(_workspace.Diff.Title)
            .Fill();
    }

    private BorderWidget BuildCommitPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var editor = context.Editor(_workspace.CommitMessage.Editor)
            .WordWrap(true)
            .InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.F4).Action(
                    _ => _workspace.CommitAsync(_cancellationToken),
                    "Commit staged changes");
                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            });
        return context.Border(editor.Fill())
            .Title("Commit message")
            .Fill();
    }

    private ResponsiveWidget BuildActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(120, wide => BuildFullActionBar(wide)),
            responsive.Otherwise(compact => BuildCompactActionBar(compact)),
        ]);

    private HStackWidget BuildFullActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            _workspace.CanCommit
                ? actions.Button("Commit").OnClick(_ => _workspace.CommitAsync(_cancellationToken))
                : actions.Text("Commit unavailable"),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.UnstagedItems.Length == 0
                ? actions.Text("Stage unavailable")
                : actions.Button("Stage").OnClick(_ => _workspace.StageAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.StagedItems.Length == 0
                ? actions.Text("Unstage unavailable")
                : actions.Button("Unstage").OnClick(_ => _workspace.UnstageAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.CanStageFocusedHunk
                ? actions.Button("Stage hunk").OnClick(_ => _workspace.StageFocusedHunkAsync(_cancellationToken))
                : _workspace.CanUnstageFocusedHunk
                    ? actions.Button("Unstage hunk").OnClick(
                        _ => _workspace.UnstageFocusedHunkAsync(_cancellationToken))
                    : actions.Text("Hunk unavailable"),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text("Refresh unavailable")
                : actions.Button("Refresh").OnClick(_ => _workspace.RefreshAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.UnstagedItems.Length == 0
                ? actions.Text("Stage all unavailable")
                : actions.Button("Stage all").OnClick(_ => _workspace.StageAllAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.StagedItems.Length == 0
                ? actions.Text("Unstage all unavailable")
                : actions.Button("Unstage all").OnClick(_ => _workspace.UnstageAllAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.DiffContextLines == 0
                ? actions.Text("Less context unavailable")
                : actions.Button("Less context").OnClick(
                    _ => _workspace.DecreaseDiffContextAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text("More context unavailable")
                : actions.Button("More context").OnClick(
                    _ => _workspace.IncreaseDiffContextAsync(_cancellationToken)),
            actions.Text(" "),
            actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private HStackWidget BuildCompactActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            _workspace.CanCommit
                ? actions.Button("Commit").OnClick(_ => _workspace.CommitAsync(_cancellationToken))
                : actions.Text(" Commit "),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.UnstagedItems.Length == 0
                ? actions.Text(" S ")
                : actions.Button("S").OnClick(_ => _workspace.StageAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.StagedItems.Length == 0
                ? actions.Text(" U ")
                : actions.Button("U").OnClick(_ => _workspace.UnstageAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.UnstagedItems.Length == 0
                ? actions.Text(" A ")
                : actions.Button("A").OnClick(_ => _workspace.StageAllAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.StagedItems.Length == 0
                ? actions.Text(" U* ")
                : actions.Button("U*").OnClick(_ => _workspace.UnstageAllAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.CanStageFocusedHunk
                ? actions.Button("H").OnClick(_ => _workspace.StageFocusedHunkAsync(_cancellationToken))
                : _workspace.CanUnstageFocusedHunk
                    ? actions.Button("H").OnClick(_ => _workspace.UnstageFocusedHunkAsync(_cancellationToken))
                    : actions.Text(" H "),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.DiffContextLines == 0
                ? actions.Text(" [ ")
                : actions.Button("[").OnClick(_ => _workspace.DecreaseDiffContextAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text(" ] ")
                : actions.Button("]").OnClick(_ => _workspace.IncreaseDiffContextAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text(" Refresh ")
                : actions.Button("Refresh").OnClick(_ => _workspace.RefreshAsync(_cancellationToken)),
            actions.Text(" "),
            actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private InfoBarWidget BuildShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section("F4 Commit"),
            info.Section("S Stage"),
            info.Section("U Unstage"),
            info.Section("A Stage all"),
            info.Section("Shift+U Unstage all"),
            info.Section($"[/] Context ({_workspace.DiffContextLines})"),
            info.Section("F5 Refresh"),
            info.Section("Space Check"),
            info.Section("S/U Hunk in diff"),
            info.Section("J/K Navigate hunks"),
            info.Section("Mouse Select/Scroll Diff"),
            info.Spacer(),
            info.Section(_workspace.Activity),
            info.Section("Ctrl+Q Quit"),
        ]).Divider(" | ");

    private static BorderWidget BuildResizeView<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.Text("GitSail needs a terminal at least 60 columns wide and 18 rows high."),
            builder.Text("Resize the terminal to return to the repository workspace."),
            builder.Text("Ctrl+Q remains available."),
        ])).Title("Terminal too small").Fill();

    private void HandleWorkspaceChanged()
        => _application?.Invalidate();

    private static int GetPointerItemIndex(InputBindingActionContext actionContext)
    {
        var node = actionContext.Focusables
            .OfType<ListNode<StatusWorkspaceItem>>()
            .FirstOrDefault(candidate => candidate.Bounds.Contains(actionContext.MouseX, actionContext.MouseY));
        if (node is null)
        {
            return -1;
        }

        var row = (actionContext.MouseY - node.Bounds.Y) / node.ItemHeight;
        var index = node.ScrollOffset + row;
        return index >= 0 && index < node.EffectiveItemCount ? index : -1;
    }

}
