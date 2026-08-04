using GitSail.CommandLine;
using GitSail.Domain;
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
    private readonly GitSailShellOptions _options;
    private readonly ApplicationMode _mode;
    private readonly IRepositoryWorkspaceSession _workspace;
    private readonly CancellationToken _cancellationToken;
    private readonly GitDiffDecorationProvider _diffDecorationProvider = new();
    private Hex1bApp? _application;

    /// <summary>
    /// Initializes a repository workspace view over controlled session state.
    /// </summary>
    /// <param name="options">The selected top-level workflow and single-transaction behavior.</param>
    /// <param name="workspace">The repository state and action source.</param>
    /// <param name="cancellationToken">Signals application shutdown.</param>
    internal RepositoryWorkspaceView(
        GitSailShellOptions options,
        IRepositoryWorkspaceSession workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(workspace);
        _options = options;
        _mode = options.Mode;
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
        if (_options.Citool?.OpenCommitMessage == true)
        {
            application.RequestFocus(node =>
                node is EditorNode editor && ReferenceEquals(editor.State, _workspace.CommitMessage.Editor));
            application.Invalidate();
        }
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
    /// <returns>The controlled repository workspace and bounded dialog host.</returns>
    internal WindowPanelWidget Build(RootContext context)
        => context.WindowPanel()
            .Background(background => BuildWorkspace(background))
            .Fill();

    private VStackWidget BuildWorkspace<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
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
            bindings.Key(Hex1bKey.P).Action(
                _ => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken),
                "Prepare the focused untracked path for hunk and line staging");
            bindings.Key(Hex1bKey.F4).Action(
                _ => RunPrimaryActionAsync(),
                GetPrimaryActionDescription());
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
                bindings.Key(Hex1bKey.L).Action(
                    _ => RunSelectedLineActionAsync(),
                    "Stage or unstage selected changed lines");
                bindings.Key(Hex1bKey.R).Action(
                    actionContext => ShowRevertConfirmation(actionContext.Windows),
                    "Choose and confirm an exact worktree revert scope");
                bindings.Ctrl().Key(Hex1bKey.Z).Action(
                    _ => _workspace.UndoRevertAsync(_cancellationToken),
                    "Undo the most recent eligible worktree revert");
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
                bindings.Key(Hex1bKey.P).Action(
                    _ => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken),
                    "Prepare the focused untracked path for hunk and line staging");
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
                    _ => RunPrimaryActionAsync(),
                    GetPrimaryActionDescription());
                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            });
        return context.Border(context.VStack(builder => BuildCommitPaneContent(builder, editor)).Fill())
            .Title("Commit message")
            .Fill();
    }

    private Hex1bWidget[] BuildCommitPaneContent<TParent>(
        WidgetContext<TParent> context,
        EditorWidget editor)
        where TParent : Hex1bWidget
    {
        var content = new List<Hex1bWidget> { editor.Fill(), BuildCommitOptionsBar(context) };
        if (_workspace.CommitOptions.IsExpanded)
        {
            content.Add(BuildCommitIdentityBar(context));
        }

        return [.. content];
    }

    private Hex1bWidget BuildCommitOptionsBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var options = _workspace.CommitOptions;
        if (!options.IsExpanded)
        {
            return context.HStack(builder =>
            [
                builder.Button("Options").OnClick(_ => ToggleCommitOptions()),
                builder.Text($" {GetCommitOptionsSummary()}"),
            ]).FillWidth();
        }

        return context.WrapPanel(builder => BuildExpandedCommitOptions(builder, options)).FillWidth();
    }

    private Hex1bWidget[] BuildExpandedCommitOptions<TParent>(
        WidgetContext<TParent> context,
        CommitOptionsState options)
        where TParent : Hex1bWidget
    {
        var controls = new List<Hex1bWidget>
        {
            context.Button("Options").OnClick(_ => ToggleCommitOptions()),
            context.Button($"Amend [{FormatToggle(options.Amend)}]")
                .OnClick(_ => ToggleAmend()),
            context.Button($"Signoff [{FormatToggle(options.Signoff)}]")
                .OnClick(_ => ToggleSignoff()),
            context.Button($"Sign [{FormatToggle(options.SignCommit)}]")
                .OnClick(_ => ToggleSignCommit()),
            context.Button($"Cleanup: {FormatCleanupMode(options.CleanupMode)}")
                .OnClick(_ => CycleCleanupMode()),
        };
        if (_options.Citool?.NoCommit != true && _workspace.CanCommit)
        {
            controls.Add(context.Button("Without hooks...")
                .OnClick(eventArgs => ShowCommitWithoutHooksConfirmation(eventArgs.Windows)));
        }

        return [.. controls];
    }

    private HStackWidget BuildCommitIdentityBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var options = _workspace.CommitOptions;
        return options.SignCommit
            ? context.HStack(builder =>
            [
                builder.Text("Author: "),
                builder.TextBox().State(options.Author),
                builder.Text(" Signing key: "),
                builder.TextBox().State(options.SigningKey),
            ]).FillWidth()
            : context.HStack(builder =>
            [
                builder.Text("Author: "),
                builder.TextBox().State(options.Author),
            ]).FillWidth();
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
            CanRunPrimaryAction()
                ? actions.Button(GetPrimaryActionLabel()).OnClick(_ => RunPrimaryActionAsync())
                : actions.Text($"{GetPrimaryActionLabel()} unavailable"),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.UnstagedItems.Length == 0
                ? actions.Text("Stage unavailable")
                : actions.Button("Stage").OnClick(_ => _workspace.StageAsync(_cancellationToken)),
            actions.Text(" "),
            BuildPrepareUntrackedPatchAction(actions, compact: false),
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
            BuildSelectedLineAction(actions, compact: false),
            actions.Text(" "),
            BuildRevertAction(actions, compact: false),
            actions.Text(" "),
            BuildUndoRevertAction(actions, compact: false),
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
            CanRunPrimaryAction()
                ? actions.Button(GetPrimaryActionLabel()).OnClick(_ => RunPrimaryActionAsync())
                : actions.Text($" {GetPrimaryActionLabel()} "),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.UnstagedItems.Length == 0
                ? actions.Text(" S ")
                : actions.Button("S").OnClick(_ => _workspace.StageAsync(_cancellationToken)),
            actions.Text(" "),
            BuildPrepareUntrackedPatchAction(actions, compact: true),
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
            BuildSelectedLineAction(actions, compact: true),
            actions.Text(" "),
            BuildRevertAction(actions, compact: true),
            actions.Text(" "),
            BuildUndoRevertAction(actions, compact: true),
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
            info.Section($"F4 {GetPrimaryActionLabel()}"),
            info.Section("S Stage"),
            info.Section("U Unstage"),
            info.Section("A Stage all"),
            info.Section("Shift+U Unstage all"),
            info.Section($"[/] Context ({_workspace.DiffContextLines})"),
            info.Section("F5 Refresh"),
            info.Section("Space Check"),
            info.Section("P Prepare untracked hunks"),
            info.Section("S/U Hunk in diff"),
            info.Section("L Selected lines"),
            info.Section("R Revert"),
            info.Section("Ctrl+Z Undo revert"),
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
    {
        if (_mode == ApplicationMode.Citool && _workspace.IsCitoolCompleted)
        {
            _application?.RequestStop();
            return;
        }

        _application?.Invalidate();
    }

    private bool CanRunPrimaryAction()
        => _options.Citool?.NoCommit == true
            ? _workspace.CanCompleteWithoutCommit
            : _workspace.CanCommit;

    private string GetPrimaryActionLabel()
        => _options.Citool?.NoCommit == true ? "Done" : "Commit";

    private string GetPrimaryActionDescription()
        => _options.Citool?.NoCommit == true
            ? "Finish after validating the prepared index"
            : "Commit the prepared transaction";

    private Task RunPrimaryActionAsync()
        => _options.Citool?.NoCommit == true
            ? _workspace.CompleteWithoutCommitAsync(_cancellationToken)
            : _workspace.CommitAsync(_cancellationToken);

    private Hex1bWidget BuildSelectedLineAction<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
    {
        if (_workspace.CanStageSelectedLines)
        {
            return context.Button(compact ? "L" : "Stage lines")
                .OnClick(_ => _workspace.StageSelectedLinesAsync(_cancellationToken));
        }

        if (_workspace.CanUnstageSelectedLines)
        {
            return context.Button(compact ? "L" : "Unstage lines")
                .OnClick(_ => _workspace.UnstageSelectedLinesAsync(_cancellationToken));
        }

        return context.Text(string.Empty);
    }

    private Task RunSelectedLineActionAsync()
    {
        if (_workspace.CanStageSelectedLines)
        {
            return _workspace.StageSelectedLinesAsync(_cancellationToken);
        }

        return _workspace.CanUnstageSelectedLines
            ? _workspace.UnstageSelectedLinesAsync(_cancellationToken)
            : Task.CompletedTask;
    }

    private Hex1bWidget BuildRevertAction<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
        => CanRevert()
            ? context.Button(compact ? "R" : "Revert...")
                .OnClick(eventArgs => ShowRevertConfirmation(eventArgs.Windows))
            : context.Text(string.Empty);

    private Hex1bWidget BuildPrepareUntrackedPatchAction<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
        => _workspace.CanPrepareUntrackedPatch
            ? context.Button(compact ? "P" : "Prepare hunks")
                .OnClick(_ => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken))
            : context.Text(string.Empty);

    private Hex1bWidget BuildUndoRevertAction<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
        => _workspace.CanUndoRevert
            ? context.Button(compact ? "Undo" : "Undo revert")
                .OnClick(_ => _workspace.UndoRevertAsync(_cancellationToken))
            : context.Text(string.Empty);

    private bool CanRevert()
        => _workspace.CanRevertSelectedLines ||
            _workspace.CanRevertFocusedHunk ||
            _workspace.CanRevertFocusedFile;

    private void ShowRevertConfirmation(WindowManager windows)
    {
        if (!CanRevert())
        {
            return;
        }

        windows.Window(window => window.VStack(builder =>
        [
            builder.Text("Restore worktree content from the index."),
            builder.Text("The chosen scope discards current worktree bytes."),
            builder.Text("Undo remains available while preconditions match."),
            builder.Text(string.Empty),
            builder.WrapPanel(buttons => BuildRevertConfirmationButtons(buttons, window.Window)),
        ]))
        .Title("Revert worktree changes?")
        .Size(62, 10)
        .Modal()
        .Open(windows);
    }

    private Hex1bWidget[] BuildRevertConfirmationButtons<TParent>(
        WidgetContext<TParent> context,
        WindowHandle window)
        where TParent : Hex1bWidget
    {
        var buttons = new List<Hex1bWidget>
        {
            context.Button("Cancel").OnClick(_ => window.Cancel()),
        };
        if (_workspace.CanRevertSelectedLines)
        {
            buttons.Add(context.Button("Revert lines").OnClick(async _ =>
            {
                window.CloseWithResult("selected lines");
                await _workspace.RevertSelectedLinesAsync(_cancellationToken).ConfigureAwait(false);
            }));
        }

        if (_workspace.CanRevertFocusedHunk)
        {
            buttons.Add(context.Button("Revert hunk").OnClick(async _ =>
            {
                window.CloseWithResult("hunk");
                await _workspace.RevertFocusedHunkAsync(_cancellationToken).ConfigureAwait(false);
            }));
        }

        if (_workspace.CanRevertFocusedFile)
        {
            buttons.Add(context.Button("Revert file").OnClick(async _ =>
            {
                window.CloseWithResult("file");
                await _workspace.RevertFocusedFileAsync(_cancellationToken).ConfigureAwait(false);
            }));
        }

        return [.. buttons];
    }

    private void ToggleCommitOptions()
    {
        _workspace.CommitOptions.ToggleExpanded();
        _application?.Invalidate();
    }

    private void ToggleAmend()
    {
        _workspace.CommitOptions.ToggleAmend();
        _application?.Invalidate();
    }

    private void ToggleSignoff()
    {
        _workspace.CommitOptions.ToggleSignoff();
        _application?.Invalidate();
    }

    private void ToggleSignCommit()
    {
        _workspace.CommitOptions.ToggleSignCommit();
        _application?.Invalidate();
    }

    private void CycleCleanupMode()
    {
        _workspace.CommitOptions.CycleCleanupMode();
        _application?.Invalidate();
    }

    private void ShowCommitWithoutHooksConfirmation(WindowManager windows)
    {
        windows.Window(window => window.VStack(builder =>
        [
            builder.Text("Git will run this commit with --no-verify."),
            builder.Text("This bypasses pre-commit and commit-msg only."),
            builder.Text("Prepare and post hooks still run."),
            builder.Text(string.Empty),
            builder.HStack(buttons =>
            [
                buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                buttons.Text(" "),
                buttons.Button("Commit without hooks").OnClick(async _ =>
                {
                    window.Window.CloseWithResult(true);
                    await _workspace.CommitWithoutHooksAsync(_cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Commit without hooks?")
        .Size(62, 9)
        .Modal()
        .Open(windows);
    }

    private string GetCommitOptionsSummary()
    {
        var options = _workspace.CommitOptions;
        var enabled = new List<string>();
        if (options.Amend)
        {
            enabled.Add("amend");
        }

        if (options.Signoff)
        {
            enabled.Add("signoff");
        }

        if (options.SignCommit)
        {
            enabled.Add("signed commit");
        }

        if (options.CleanupMode != CommitCleanupMode.Default)
        {
            enabled.Add($"cleanup {FormatCleanupMode(options.CleanupMode)}");
        }

        if (!string.IsNullOrWhiteSpace(options.Author.Text))
        {
            enabled.Add("author override");
        }

        return enabled.Count == 0 ? "default transaction" : string.Join(", ", enabled);
    }

    private static string FormatToggle(bool enabled)
        => enabled ? "x" : " ";

    private static string FormatCleanupMode(CommitCleanupMode cleanupMode)
        => cleanupMode.ToString().ToLowerInvariant();

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
