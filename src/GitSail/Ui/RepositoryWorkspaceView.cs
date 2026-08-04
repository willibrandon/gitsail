using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using Hex1b;
using Hex1b.Input;
using Hex1b.LanguageServer;
using Hex1b.Widgets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

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
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
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
            bindings.Ctrl().Key(Hex1bKey.R).Action(
                _ => _workspace.RefreshAsync(_cancellationToken),
                "Refresh repository status");
            bindings.Key(Hex1bKey.F1).Action(
                actionContext => ShowHelp(actionContext.Windows),
                "Open context help and the live keyboard reference");
            bindings.Key(Hex1bKey.F2).Action(
                actionContext => ShowCommandPalette(actionContext.Windows),
                "Open the searchable command palette");
            bindings.Key(Hex1bKey.F8).Action(
                actionContext => ShowBranchesAsync(actionContext.Windows),
                "Open the searchable branch and worktree window");
            bindings.Key(Hex1bKey.F9).Action(
                actionContext => ShowStashesAsync(actionContext.Windows),
                "Open the searchable stash and patch window");
            bindings.Key(Hex1bKey.P).Action(
                _ => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken),
                "Prepare the focused untracked path for hunk and line staging");
            bindings.Key(Hex1bKey.F4).Action(
                actionContext => RunPrimaryActionAsync(actionContext.Windows),
                GetPrimaryActionDescription());
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                actionContext => Complete(() => actionContext.Windows.ActiveWindow?.Close()),
                "Close the active window");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }).Fill();

    private HStackWidget BuildHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var snapshot = _workspace.State.Snapshot;
        var branch = snapshot.HeadName?.DisplayText ??
            (snapshot.HeadObjectId is null ? "unborn" : "detached");
        var repository = snapshot.Repository.WorkTree?.DisplayText ?? snapshot.Repository.GitDirectory.DisplayText;
        var tracking = snapshot.UpstreamName is null
            ? string.Empty
            : $" | {snapshot.UpstreamName.DisplayText} +{snapshot.AheadCount}/-{snapshot.BehindCount}";
        return context.HStack(header =>
        [
            header.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section(_mode.ToString().ToLowerInvariant()),
                info.Section(branch + tracking),
                info.Spacer(),
                info.Section(repository),
                info.Section($"Git {_workspace.Installation.Version}"),
            ]).Divider(" | ").FillWidth(),
            header.HStack(actions =>
            [
                actions.Button("Commands").OnClick(eventArgs => ShowCommandPalette(eventArgs.Windows)),
                actions.Text(" "),
                _workspace.IsBusy
                    ? actions.Text("Branches  Stashes")
                    : actions.HStack(repositoryActions =>
                    [
                        repositoryActions.Button("Branches").OnClick(
                            eventArgs => ShowBranchesAsync(eventArgs.Windows)),
                        repositoryActions.Text(" "),
                        repositoryActions.Button("Stashes").OnClick(
                            eventArgs => ShowStashesAsync(eventArgs.Windows)),
                    ]),
            ]),
        ]).FillWidth();
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
                bindings.Remove(Hex1bKey.Spacebar, Hex1bModifiers.Control);
                bindings.Remove(Hex1bKey.K, Hex1bModifiers.Control);
                bindings.Remove(Hex1bKey.F12);
                bindings.Remove(Hex1bKey.F12, Hex1bModifiers.Shift);
                if (_workspace.IsConflictResolutionActive)
                {
                    bindings.Alt().Key(Hex1bKey.O).Action(
                        _ => _workspace.ChooseFocusedConflictChunkAsync(ConflictResolutionChoice.Ours),
                        "Replace the focused conflict block with ours");
                    bindings.Alt().Key(Hex1bKey.T).Action(
                        _ => _workspace.ChooseFocusedConflictChunkAsync(ConflictResolutionChoice.Theirs),
                        "Replace the focused conflict block with theirs");
                    bindings.Alt().Key(Hex1bKey.B).Action(
                        _ => _workspace.ChooseFocusedConflictChunkAsync(ConflictResolutionChoice.Base),
                        "Replace the focused conflict block with base");
                    bindings.Alt().Key(Hex1bKey.A).Action(
                        _ => _workspace.ChooseFocusedConflictChunkAsync(ConflictResolutionChoice.Both),
                        "Replace the focused conflict block with ours then theirs");
                    bindings.Alt().Key(Hex1bKey.N).Action(
                        _ => _workspace.FocusNextUnresolvedConflictAsync(),
                        "Focus the next unresolved conflict block");
                    bindings.Alt().Key(Hex1bKey.X).Action(
                        _ => _workspace.ToggleConflictExecutableAsync(),
                        "Toggle the conflict result executable bit");
                    bindings.Alt().Key(Hex1bKey.S).Action(
                        _ => _workspace.StageConflictResolutionAsync(_cancellationToken),
                        "Stage the marker-free conflict result");
                }
                else
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
                }

                bindings.Key(Hex1bKey.F5).Action(
                    _ => _workspace.RefreshAsync(_cancellationToken),
                    "Refresh repository status");
                if (!_workspace.IsConflictResolutionActive)
                {
                    bindings.Key(Hex1bKey.P).Action(
                        _ => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken),
                        "Prepare the focused untracked path for hunk and line staging");
                }

                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            });
        return context.Border(editor.Fill())
            .Title(GetDiffPaneTitle())
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
                    actionContext => RunPrimaryActionAsync(actionContext.Windows),
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
                .OnClick(_ => ToggleAmendAsync()),
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
        => _workspace.IsConflictResolutionActive
            ? context.Responsive(responsive =>
            [
                responsive.WhenMinWidth(180, wide => BuildFullConflictActionBar(wide)),
                responsive.Otherwise(compact => BuildCompactConflictActionBar(compact)),
            ])
            : context.Responsive(responsive =>
            [
                responsive.WhenMinWidth(120, wide => BuildFullActionBar(wide)),
                responsive.Otherwise(compact => BuildCompactActionBar(compact)),
            ]);

    private HStackWidget BuildFullConflictActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            actions.Text($"Resolved {_workspace.ResolvedConflictChunkCount}/{_workspace.ConflictChunkCount}"),
            actions.Text(" "),
            BuildAbortMergeAction(actions, compact: false),
            actions.Text(" "),
            BuildConflictChoiceAction(actions, "Use ours", ConflictResolutionChoice.Ours),
            actions.Text(" "),
            BuildConflictChoiceAction(actions, "Use theirs", ConflictResolutionChoice.Theirs),
            actions.Text(" "),
            BuildConflictChoiceAction(actions, "Use base", ConflictResolutionChoice.Base),
            actions.Text(" "),
            BuildConflictChoiceAction(actions, "Use both", ConflictResolutionChoice.Both),
            actions.Text(" "),
            !_workspace.IsBusy && _workspace.ResolvedConflictChunkCount < _workspace.ConflictChunkCount
                ? actions.Button("Next conflict").OnClick(_ => _workspace.FocusNextUnresolvedConflictAsync())
                : actions.Text("All markers resolved"),
            actions.Text(" "),
            _workspace.CanToggleConflictExecutable
                ? actions.Button(GetConflictModeLabel()).OnClick(_ => _workspace.ToggleConflictExecutableAsync())
                : actions.Text("Mode unavailable"),
            actions.Text(" "),
            _workspace.CanStageConflictResolution
                ? actions.Button("Stage resolution").OnClick(
                    _ => _workspace.StageConflictResolutionAsync(_cancellationToken))
                : actions.Text("Stage after resolving markers"),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text("Refresh unavailable")
                : actions.Button("Refresh").OnClick(_ => _workspace.RefreshAsync(_cancellationToken)),
            actions.Text(" "),
            actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private HStackWidget BuildCompactConflictActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            actions.Text($"{_workspace.ResolvedConflictChunkCount}/{_workspace.ConflictChunkCount}"),
            actions.Text(" "),
            BuildAbortMergeAction(actions, compact: true),
            actions.Text(" "),
            BuildConflictChoiceAction(actions, "O", ConflictResolutionChoice.Ours),
            actions.Text(" "),
            BuildConflictChoiceAction(actions, "T", ConflictResolutionChoice.Theirs),
            actions.Text(" "),
            BuildConflictChoiceAction(actions, "B", ConflictResolutionChoice.Base),
            actions.Text(" "),
            BuildConflictChoiceAction(actions, "O+T", ConflictResolutionChoice.Both),
            actions.Text(" "),
            !_workspace.IsBusy && _workspace.ResolvedConflictChunkCount < _workspace.ConflictChunkCount
                ? actions.Button("Next").OnClick(_ => _workspace.FocusNextUnresolvedConflictAsync())
                : actions.Text("Done"),
            actions.Text(" "),
            _workspace.CanToggleConflictExecutable
                ? actions.Button(_workspace.ConflictResultIsExecutable ? "755" : "644")
                    .OnClick(_ => _workspace.ToggleConflictExecutableAsync())
                : actions.Text("---"),
            actions.Text(" "),
            _workspace.CanStageConflictResolution
                ? actions.Button("Stage").OnClick(_ => _workspace.StageConflictResolutionAsync(_cancellationToken))
                : actions.Text("Stage"),
            actions.Text(" "),
            actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private HStackWidget BuildFullActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            CanRunPrimaryAction()
                ? actions.Button(GetPrimaryActionLabel()).OnClick(
                    eventArgs => RunPrimaryActionAsync(eventArgs.Windows))
                : actions.Text(GetPrimaryActionUnavailableLabel()),
            actions.Text(" "),
            BuildAbortMergeAction(actions, compact: false),
            actions.Text(" "),
            !CanStagePaths()
                ? actions.Text("Stage unavailable")
                : actions.Button("Stage").OnClick(_ => _workspace.StageAsync(_cancellationToken)),
            actions.Text(" "),
            BuildPrepareUntrackedPatchAction(actions, compact: false),
            actions.Text(" "),
            !CanUnstagePaths()
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
            !CanStageAll()
                ? actions.Text("Stage all unavailable")
                : actions.Button("Stage all").OnClick(_ => _workspace.StageAllAsync(_cancellationToken)),
            actions.Text(" "),
            !CanUnstageAll()
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
                ? actions.Button(GetPrimaryActionLabel()).OnClick(
                    eventArgs => RunPrimaryActionAsync(eventArgs.Windows))
                : actions.Text(_workspace.NeedsCommitTemplateEdit
                    ? " Edit template "
                    : $" {GetPrimaryActionLabel()} "),
            actions.Text(" "),
            BuildAbortMergeAction(actions, compact: true),
            actions.Text(" "),
            !CanStagePaths()
                ? actions.Text(" S ")
                : actions.Button("S").OnClick(_ => _workspace.StageAsync(_cancellationToken)),
            actions.Text(" "),
            BuildPrepareUntrackedPatchAction(actions, compact: true),
            actions.Text(" "),
            !CanUnstagePaths()
                ? actions.Text(" U ")
                : actions.Button("U").OnClick(_ => _workspace.UnstageAsync(_cancellationToken)),
            actions.Text(" "),
            !CanStageAll()
                ? actions.Text(" A ")
                : actions.Button("A").OnClick(_ => _workspace.StageAllAsync(_cancellationToken)),
            actions.Text(" "),
            !CanUnstageAll()
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
        => _workspace.IsConflictResolutionActive
            ? BuildConflictShortcutBar(context)
            : BuildRepositoryShortcutBar(context);

    private InfoBarWidget BuildConflictShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section("F1 Help"),
            info.Section("F2 Commands"),
            info.Section("Alt+O Ours"),
            info.Section("Alt+T Theirs"),
            info.Section("Alt+B Base"),
            info.Section("Alt+A Both"),
            info.Section("Alt+N Next"),
            info.Section("Alt+X Toggle mode"),
            info.Section("Alt+S Stage result"),
            info.Section("Ctrl+Z/Y Undo/redo"),
            info.Section("Mouse Edit/Select/Scroll/Act"),
            info.Spacer(),
            info.Section(_workspace.Activity),
            info.Section("Ctrl+Q Quit"),
        ]).Divider(" | ");

    private InfoBarWidget BuildRepositoryShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section($"F4 {GetPrimaryActionLabel()}"),
            info.Section("F1 Help"),
            info.Section("F2 Commands"),
            info.Section("F8 Branches"),
            info.Section("F9 Stashes"),
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
            builder.Text("F1 Help, F2 Commands, and Ctrl+Q Quit remain available."),
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

    private bool CanStagePaths()
    {
        var paths = _workspace.State.GetPathsToStage();
        return !_workspace.IsBusy &&
            paths.Count > 0 &&
            !ContainsUnmergedPath(paths);
    }

    private bool CanUnstagePaths()
    {
        var paths = _workspace.State.GetPathsToUnstage();
        return !_workspace.IsBusy &&
            paths.Count > 0 &&
            !ContainsUnmergedPath(paths);
    }

    private bool CanStageAll()
        => !_workspace.IsBusy &&
            _workspace.State.UnstagedItems.Length > 0 &&
            !HasUnmergedEntries();

    private bool CanUnstageAll()
        => !_workspace.IsBusy &&
            _workspace.State.StagedItems.Length > 0 &&
            !HasUnmergedEntries();

    private bool HasUnmergedEntries()
        => _workspace.State.Snapshot.Entries.Any(
            static entry => entry.Kind == RepositoryStatusEntryKind.Unmerged);

    private bool ContainsUnmergedPath(IReadOnlyList<GitPath> paths)
        => paths.Any(path => _workspace.State.Snapshot.Entries.Any(
            entry => entry.Kind == RepositoryStatusEntryKind.Unmerged && entry.Path.Equals(path)));

    private string GetPrimaryActionLabel()
        => _options.Citool?.NoCommit == true ? "Done" : "Commit";

    private string GetPrimaryActionDescription()
        => _options.Citool?.NoCommit == true
            ? "Finish after validating the prepared index"
            : "Commit the prepared transaction";

    private string GetPrimaryActionUnavailableLabel()
        => _workspace.NeedsCommitTemplateEdit
            ? "Edit template before commit"
            : $"{GetPrimaryActionLabel()} unavailable";

    private Task RunPrimaryActionAsync(WindowManager windows)
    {
        if (_options.Citool?.NoCommit == true)
        {
            return _workspace.CompleteWithoutCommitAsync(_cancellationToken);
        }

        var publishedWarning = _workspace.CommitOptions.Amend
            ? _workspace.PublishedAmendWarning
            : null;
        var detachedWarning = _workspace.DetachedHeadWarning;
        if (publishedWarning is not null || detachedWarning is not null)
        {
            ShowCommitWarningsConfirmation(windows, publishedWarning, detachedWarning);
            return Task.CompletedTask;
        }

        return _workspace.CommitAsync(_cancellationToken);
    }

    private Hex1bWidget BuildConflictChoiceAction<TParent>(
        WidgetContext<TParent> context,
        string label,
        ConflictResolutionChoice choice)
        where TParent : Hex1bWidget
        => _workspace.CanChooseFocusedConflictChunk
            ? context.Button(label).OnClick(_ => _workspace.ChooseFocusedConflictChunkAsync(choice))
            : context.Text(label);

    private string GetDiffPaneTitle()
        => _workspace.IsConflictResolutionActive
            ? $"{_workspace.Diff.Title} " +
                $"[{_workspace.ResolvedConflictChunkCount}/{_workspace.ConflictChunkCount}; " +
                $"{(_workspace.ConflictResultIsExecutable ? "executable" : "regular")}]"
            : _workspace.Diff.Title;

    private string GetConflictModeLabel()
        => _workspace.ConflictResultIsExecutable
            ? "Mode: executable (100755)"
            : "Mode: regular (100644)";

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

    private Hex1bWidget BuildAbortMergeAction<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
        => _workspace.CanAbortMerge
            ? context.Button(compact ? "Abort" : "Abort merge...")
                .OnClick(eventArgs => ShowAbortMergeConfirmation(eventArgs.Windows))
            : context.Text(string.Empty);

    private bool CanRevert()
        => _workspace.CanRevertSelectedLines ||
            _workspace.CanRevertFocusedHunk ||
            _workspace.CanRevertFocusedFile;

    private void ShowCommandPalette(WindowManager windows)
    {
        var filterState = new TextBoxState();
        string? focusedId = null;
        windows.Window(window => window.VStack(builder =>
        {
            var filter = filterState.Text.Trim();
            var commands = BuildWorkspaceCommands(windows);
            var visible = string.IsNullOrEmpty(filter)
                ? commands
                : [.. commands.Where(command => command.Matches(filter))];
            var focusedIndex = focusedId is null
                ? 0
                : Math.Max(0, visible.FindIndex(command => string.Equals(
                    command.Id,
                    focusedId,
                    StringComparison.Ordinal)));
            if (focusedIndex >= visible.Count)
            {
                focusedIndex = 0;
            }

            var focused = visible.Count == 0 ? null : visible[focusedIndex];
            focusedId = focused?.Id;
            return
            [
                builder.HStack(search =>
                [
                    search.Text("Find action: "),
                    search.TextBox()
                        .State(filterState)
                        .OnTextChanged(_ =>
                        {
                            focusedId = null;
                            _application?.Invalidate();
                        })
                        .OnSubmit(_ => ExecutePaletteCommandAsync(focused, window.Window))
                        .FillWidth(),
                ]).FillWidth(),
                builder.List(visible)
                    .ItemKey(static command => command.Id)
                    .FocusedIndex(focusedIndex)
                    .OnFocusChanged(eventArgs =>
                    {
                        if (eventArgs.FocusedIndex >= 0 && eventArgs.FocusedIndex < visible.Count)
                        {
                            focusedId = visible[eventArgs.FocusedIndex].Id;
                            _application?.Invalidate();
                        }
                    })
                    .Empty(empty => empty.Text("No command matches the current filter."))
                    .InputBindings(bindings => bindings.Key(Hex1bKey.Enter).Action(
                        _ => ExecutePaletteCommandAsync(focused, window.Window),
                        "Run the focused available action"))
                    .Fill(),
                builder.Text(focused?.Description ?? "Type to search every implemented workspace action."),
                builder.Text(GetCommandAvailabilityText(focused)),
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    focused?.IsAvailable == true
                        ? actions.Button("Run selected").OnClick(
                            _ => ExecutePaletteCommandAsync(focused, window.Window))
                        : actions.Text("Run selected unavailable"),
                ]),
                builder.Text("Type to filter | Up/Down select | Enter or mouse button runs | Esc closes"),
            ];
        }).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close the command palette");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close the command palette");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }))
        .Title("Command palette")
        .Size(94, 26)
        .Resizable(58, 16, 130, 48)
        .Modal()
        .Open(windows);
    }

    private List<WorkspaceCommandItem> BuildWorkspaceCommands(WindowManager windows)
    {
        var commands = new List<WorkspaceCommandItem>();
        var busy = _workspace.IsBusy ? "Another repository operation is running." : null;
        Add("help.context", "Help", "Context help", "Open the live keyboard, pointer, and workflow reference.", "F1", null,
            () => Complete(() => ShowHelp(windows)));
        Add("help.doctor", "Help", "Doctor and runtime", "Inspect the current build, runtime, Git, and repository capabilities.", string.Empty, null,
            () => Complete(() => ShowDoctor(windows)));
        Add("view.branches", "Branch", "Branches and worktrees", "Open searchable local and remote-tracking branches with linked-worktree state.", "F8", busy,
            () => ShowBranchesAsync(windows));
        Add("view.stashes", "Stash", "Stashes and exact patches", "Open searchable stash entries, exact patch previews, and lifecycle actions.", "F9", busy,
            () => ShowStashesAsync(windows));
        Add("repository.refresh", "Repository", "Refresh", "Rescan repository status, exact diffs, warnings, and conflict state.", "F5 / Ctrl+R",
            busy, () => _workspace.RefreshAsync(_cancellationToken));
        Add("commit.primary", "Commit", GetPrimaryActionLabel(), GetPrimaryActionDescription(), "F4",
            CanRunPrimaryAction() ? null : GetPrimaryActionUnavailableLabel(),
            () => RunPrimaryActionAsync(windows));
        Add("index.stage", "Commit", "Stage selected paths", "Stage checked worktree paths, or the focused path when none are checked.", "S",
            CanStagePaths() ? null : "No stageable checked or focused path is available.",
            () => _workspace.StageAsync(_cancellationToken));
        Add("index.stage-all", "Commit", "Stage all", "Stage every eligible worktree change without presentation filtering.", "A",
            CanStageAll() ? null : "No eligible unstaged paths are available.",
            () => _workspace.StageAllAsync(_cancellationToken));
        Add("index.unstage", "Commit", "Unstage selected paths", "Unstage checked index paths, or the focused path when none are checked.", "U",
            CanUnstagePaths() ? null : "No unstageable checked or focused path is available.",
            () => _workspace.UnstageAsync(_cancellationToken));
        Add("index.unstage-all", "Commit", "Unstage all", "Unstage every eligible index change without presentation filtering.", "Shift+U",
            CanUnstageAll() ? null : "No eligible staged paths are available.",
            () => _workspace.UnstageAllAsync(_cancellationToken));
        Add("diff.prepare-untracked", "Diff", "Prepare untracked hunks", "Add intent-to-add for the focused untracked path so hunk and line staging becomes available.", "P",
            _workspace.CanPrepareUntrackedPatch ? null : "Focus one eligible untracked file first.",
            () => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken));
        Add("diff.stage-hunk", "Diff", "Stage focused hunk", "Stage the exact focused worktree hunk.", "S in diff",
            _workspace.CanStageFocusedHunk ? null : "No stageable worktree hunk is focused.",
            () => _workspace.StageFocusedHunkAsync(_cancellationToken));
        Add("diff.unstage-hunk", "Diff", "Unstage focused hunk", "Unstage the exact focused index hunk.", "U in diff",
            _workspace.CanUnstageFocusedHunk ? null : "No unstageable index hunk is focused.",
            () => _workspace.UnstageFocusedHunkAsync(_cancellationToken));
        Add("diff.stage-lines", "Diff", "Stage selected lines", "Stage the exact selected added and context lines from the focused worktree patch.", "L",
            _workspace.CanStageSelectedLines ? null : "The current editor selection cannot be staged.",
            () => _workspace.StageSelectedLinesAsync(_cancellationToken));
        Add("diff.unstage-lines", "Diff", "Unstage selected lines", "Unstage the exact selected added and context lines from the focused index patch.", "L",
            _workspace.CanUnstageSelectedLines ? null : "The current editor selection cannot be unstaged.",
            () => _workspace.UnstageSelectedLinesAsync(_cancellationToken));
        Add("diff.revert", "Diff", "Revert changes", "Review the available file, hunk, or selected-line destructive revert choices.", "R",
            CanRevert() ? null : "No revertible file, hunk, or line selection is available.",
            () => Complete(() => ShowRevertConfirmation(windows)));
        Add("diff.undo-revert", "Diff", "Undo last revert", "Restore the most recent exact revert while its repository preconditions still match.", "Ctrl+Z",
            _workspace.CanUndoRevert ? null : "No current revert undo transaction is available.",
            () => _workspace.UndoRevertAsync(_cancellationToken));
        Add("diff.less-context", "View", "Decrease diff context", "Regenerate exact repository diffs with one fewer context line.", "[",
            _workspace.IsBusy || _workspace.DiffContextLines == 0 ? "Diff context cannot be decreased now." : null,
            () => _workspace.DecreaseDiffContextAsync(_cancellationToken));
        Add("diff.more-context", "View", "Increase diff context", "Regenerate exact repository diffs with one more context line.", "]",
            busy, () => _workspace.IncreaseDiffContextAsync(_cancellationToken));
        Add("merge.abort", "Merge", "Abort merge", "Review the exact merge, worktree, index, and autostash state before asking Git to abort.", string.Empty,
            _workspace.CanAbortMerge ? null : "No verified active merge can be aborted.",
            () => Complete(() => ShowAbortMergeConfirmation(windows)));
        var mergeSource = _workspace.Branches.FocusedItem?.Branch;
        Add("merge.selected-branch", "Merge", "Merge selected branch", "Prepare an exact confirmation for the selected nonsymbolic branch and target object.", string.Empty,
            mergeSource is null || mergeSource.IsCurrent || mergeSource.SymbolicTarget is not null
                ? "Open Branches and select a noncurrent nonsymbolic branch first."
                : busy,
            () => mergeSource is null
                ? Task.CompletedTask
                : ShowMergeBranchDialogAsync(windows, branchWindow: null, mergeSource));
        AddConflictCommand("merge.use-ours", "Use ours", "Replace the focused conflict chunk with our side.", "Alt+O", ConflictResolutionChoice.Ours);
        AddConflictCommand("merge.use-theirs", "Use theirs", "Replace the focused conflict chunk with their side.", "Alt+T", ConflictResolutionChoice.Theirs);
        AddConflictCommand("merge.use-base", "Use base", "Replace the focused conflict chunk with the merge base.", "Alt+B", ConflictResolutionChoice.Base);
        AddConflictCommand("merge.use-both", "Use both", "Replace the focused conflict chunk with ours followed by theirs.", "Alt+A", ConflictResolutionChoice.Both);
        Add("merge.next-conflict", "Merge", "Next unresolved conflict", "Move result focus to the next unresolved conflict marker.", "Alt+N",
            !_workspace.IsBusy && _workspace.IsConflictResolutionActive &&
                _workspace.ResolvedConflictChunkCount < _workspace.ConflictChunkCount
                ? null
                : "No later unresolved conflict marker is available.",
            () => _workspace.FocusNextUnresolvedConflictAsync());
        Add("merge.toggle-mode", "Merge", "Toggle result executable mode", "Toggle the conflict result between regular and executable file modes.", "Alt+X",
            _workspace.CanToggleConflictExecutable ? null : "The focused conflict result cannot change executable mode.",
            () => _workspace.ToggleConflictExecutableAsync());
        Add("merge.stage-result", "Merge", "Stage conflict result", "Save the complete resolved result atomically and stage it through Git.", "Alt+S",
            _workspace.CanStageConflictResolution ? null : "Resolve every marker before staging this result.",
            () => _workspace.StageConflictResolutionAsync(_cancellationToken));
        Add("commit.options", "Commit", "Expand or collapse commit options", "Show or hide author, amend, signoff, signing, cleanup, and hook-bypass controls.", string.Empty,
            busy, () => Complete(ToggleCommitOptions));
        Add("commit.toggle-amend", "Commit", "Toggle amend", "Toggle whether the next commit amends the exact current HEAD.", string.Empty,
            busy, () => ToggleAmendAsync());
        Add("commit.toggle-signoff", "Commit", "Toggle signoff", "Toggle Git's signoff trailer for the next commit.", string.Empty,
            busy, () => Complete(ToggleSignoff));
        Add("commit.toggle-signing", "Commit", "Toggle commit signing", "Toggle commit signing for the next commit transaction.", string.Empty,
            busy, () => Complete(ToggleSignCommit));
        Add("commit.cycle-cleanup", "Commit", "Cycle cleanup mode", "Cycle through Git-owned commit message cleanup modes.", string.Empty,
            busy, () => Complete(CycleCleanupMode));
        Add("commit.without-hooks", "Commit", "Commit without bypassable hooks", "Review a warning before asking Git to bypass pre-commit and commit-msg.", string.Empty,
            _options.Citool?.NoCommit == true || !_workspace.CanCommit
                ? "No commit-without-hooks transaction is available."
                : null,
            () => Complete(() => ShowCommitWithoutHooksConfirmation(windows)));
        Add("application.quit", "Repository", "Quit", "Close the current repository workspace.", "Ctrl+Q", null,
            () => Complete(() => _application?.RequestStop()));
        return commands;

        void Add(
            string id,
            string category,
            string label,
            string description,
            string binding,
            string? unavailableReason,
            Func<Task> executeAsync)
            => commands.Add(new WorkspaceCommandItem(
                id,
                category,
                label,
                description,
                binding,
                unavailableReason,
                executeAsync));

        void AddConflictCommand(
            string id,
            string label,
            string description,
            string binding,
            ConflictResolutionChoice choice)
            => Add(
                id,
                "Merge",
                label,
                description,
                binding,
                _workspace.CanChooseFocusedConflictChunk
                    ? null
                    : "No unresolved conflict chunk is focused.",
                () => _workspace.ChooseFocusedConflictChunkAsync(choice));
    }

    private static async Task ExecutePaletteCommandAsync(
        WorkspaceCommandItem? command,
        WindowHandle paletteWindow)
    {
        if (command?.IsAvailable != true)
        {
            return;
        }

        paletteWindow.CloseWithResult(command.Id);
        await command.ExecuteAsync().ConfigureAwait(false);
    }

    private static string GetCommandAvailabilityText(WorkspaceCommandItem? command)
        => command is null
            ? "Availability: no matching action"
            : command.IsAvailable
                ? $"Available now{(command.Binding.Length == 0 ? string.Empty : $" | Binding: {command.Binding}")}"
                : $"Unavailable: {command.UnavailableReason}";

    private void ShowHelp(WindowManager windows)
    {
        windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Doctor").OnClick(_ => ShowDoctor(windows)),
            ]),
            builder.VScrollPanel(help =>
            [
                help.Text($"{BuildInformation.DisplayVersion} | {_mode.ToString().ToLowerInvariant()} mode"),
                help.Text("F1 Help | F2 searchable commands | F4 primary action | F5 refresh"),
                help.Text("F8 branches/worktrees | F9 stashes/patches | Ctrl+Q quit"),
                help.Text("S stage | U unstage | A stage all | Shift+U unstage all"),
                help.Text("[ / ] diff context | P prepare untracked hunks | L selected lines | R revert"),
                help.Text("J/K navigate hunks | Ctrl+Z undo the last exact revert"),
                help.Text("Conflict: Alt+O ours | Alt+T theirs | Alt+B base | Alt+A both"),
                help.Text("Conflict: Alt+N next | Alt+X file mode | Alt+S stage resolved result"),
                help.Text("Mouse: focus and select list rows, activate buttons, select editor text, scroll, and resize windows."),
                help.Text("The command palette lists unavailable actions too and explains the live reason."),
                help.Text("Destructive actions open cancel-first confirmations showing the exact target identity."),
                help.Text("Diff and stash editors are read-only; repository mutations use retained exact Git bytes."),
                help.Text("Use git tui doctor --json outside the TUI for the stable machine-readable report."),
            ], showScrollbar: true).Fill(),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close help");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close help");
        }))
        .Title("Help and keyboard reference")
        .Size(88, 24)
        .Resizable(58, 16, 120, 42)
        .Modal()
        .Open(windows);
    }

    private void ShowDoctor(WindowManager windows)
    {
        var repository = _workspace.State.Snapshot.Repository.WorkTree?.DisplayText ??
            _workspace.State.Snapshot.Repository.GitDirectory.DisplayText;
        windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
            ]),
            builder.Text($"Product: {BuildInformation.DisplayVersion}"),
            builder.Text($"Runtime identifier: {RuntimeInformation.RuntimeIdentifier}"),
            builder.Text($"Operating system: {TerminalTextSanitizer.Sanitize(RuntimeInformation.OSDescription)}"),
            builder.Text($"Architecture: {RuntimeInformation.ProcessArchitecture}"),
            builder.Text($"Native AOT: {!RuntimeFeature.IsDynamicCodeSupported}"),
            builder.Text($"Git: {_workspace.Installation.Version}"),
            builder.Text($"Git executable: {TerminalTextSanitizer.Sanitize(_workspace.Installation.Executable.Path)}"),
            builder.Text($"Repository: {repository}"),
            builder.Text($"Mode: {_mode.ToString().ToLowerInvariant()}"),
            builder.Text("Stable JSON: git tui doctor --json"),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close Doctor");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close Doctor");
        }))
        .Title("Doctor and runtime capabilities")
        .Size(92, 16)
        .Resizable(58, 14, 120, 30)
        .Modal()
        .Open(windows);
    }

    private static Task Complete(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }

    private async Task ShowStashesAsync(WindowManager windows)
    {
        await _workspace.LoadStashesAsync(_cancellationToken).ConfigureAwait(false);
        if (_workspace.Stashes.Catalog is null)
        {
            return;
        }

        windows.Window(window => window.VStack(builder =>
        [
            builder.VSplitter(
                builder.VStack(top =>
                [
                    top.HStack(filter =>
                    [
                        filter.Text("Filter: "),
                        filter.TextBox()
                            .State(_workspace.Stashes.Filter)
                            .OnTextChanged(eventArgs => _workspace.FilterStashesAsync(
                                eventArgs.NewText,
                                _cancellationToken))
                            .FillWidth(),
                    ]).FillWidth(),
                    top.List(_workspace.Stashes.VisibleItems)
                        .ItemKey(static item => item.Key)
                        .FocusedIndex(_workspace.Stashes.FocusedIndex)
                        .OnFocusChanged(eventArgs => _workspace.FocusStashAsync(
                            eventArgs.FocusedIndex,
                            _cancellationToken))
                        .Empty(empty => empty.Text(
                            _workspace.Stashes.Catalog.Entries.IsEmpty
                                ? "No stashes. Save current changes to create one."
                                : "No stash matches the filter."))
                        .InputBindings(bindings =>
                        {
                            bindings.Key(Hex1bKey.Enter).Action(
                                _ => ShowApplyFocusedStashDialog(windows, window.Window, pop: false),
                                "Review and apply the focused stash");
                            bindings.Key(Hex1bKey.F5).Action(
                                _ => _workspace.LoadStashesAsync(_cancellationToken),
                                "Refresh stashes and exact worktree state");
                            bindings.Key(Hex1bKey.N).Action(
                                _ => ShowCreateStashDialog(windows, window.Window),
                                "Save current changes to a new stash");
                        }).Fill(),
                    top.VStack(details => BuildStashDetails(details)),
                    top.WrapPanel(actions => BuildStashActions(actions, windows, window.Window)),
                ]).Fill(),
                builder.Border(
                    builder.Editor(_workspace.Stashes.Preview)
                        .LineNumbers()
                        .WordWrap(false)
                        .Decorations(_diffDecorationProvider)
                        .Fill())
                    .Title(_workspace.Stashes.PreviewTitle)
                    .Fill(),
                11).Fill(),
            builder.Text("Enter apply | N new | F5 refresh | Mouse select, inspect, scroll, resize, and activate"),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close the stash window");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }))
        .Title("Stashes and exact patches")
        .Size(96, 27)
        .Resizable(64, 19, 130, 48)
        .Modal()
        .Open(windows);
    }

    private Hex1bWidget[] BuildStashDetails<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var stash = _workspace.Stashes.FocusedItem?.Stash;
        if (stash is null)
        {
            return [context.Text("Select a stash to inspect its exact identity and patch.")];
        }

        return
        [
            context.Text($"{stash.Selector} | {stash.CreatedAt.ToLocalTime():F}"),
            context.Text($"Object: {stash.ObjectId}"),
            context.Text($"Subject: {stash.DisplayMessage}"),
        ];
    }

    private Hex1bWidget[] BuildStashActions<TParent>(
        WidgetContext<TParent> context,
        WindowManager windows,
        WindowHandle stashWindow)
        where TParent : Hex1bWidget
    {
        var actions = new List<Hex1bWidget>
        {
            context.Button("Cancel").OnClick(_ => stashWindow.Cancel()),
            context.Button("Refresh").OnClick(_ => _workspace.LoadStashesAsync(_cancellationToken)),
            context.Button("New...").OnClick(_ => ShowCreateStashDialog(windows, stashWindow)),
        };
        if (_workspace.Stashes.FocusedItem is not null)
        {
            actions.Add(context.Button("Apply...").OnClick(
                _ => ShowApplyFocusedStashDialog(windows, stashWindow, pop: false)));
            actions.Add(context.Button("Pop...").OnClick(
                _ => ShowApplyFocusedStashDialog(windows, stashWindow, pop: true)));
            actions.Add(context.Button("Drop...").OnClick(
                _ => ShowDropFocusedStashDialog(windows, stashWindow)));
        }

        return [.. actions];
    }

    private void ShowCreateStashDialog(WindowManager windows, WindowHandle stashWindow)
    {
        var messageState = new TextBoxState();
        var fileScope = StashFileScope.Tracked;
        var keepIndex = false;
        var stagedOnly = false;
        windows.Window(window => window.VStack(builder =>
        [
            builder.Text("Save current repository changes and restore the selected paths through Git."),
            builder.HStack(message =>
            [
                message.Text("Message: "),
                message.TextBox().State(messageState).FillWidth(),
            ]).FillWidth(),
            builder.WrapPanel(options =>
            [
                options.Button(GetStashFileScopeLabel(fileScope)).OnClick(_ =>
                {
                    stagedOnly = false;
                    fileScope = fileScope switch
                    {
                        StashFileScope.Tracked => StashFileScope.IncludeUntracked,
                        StashFileScope.IncludeUntracked => StashFileScope.IncludeIgnored,
                        StashFileScope.IncludeIgnored => StashFileScope.Tracked,
                        _ => throw new InvalidOperationException("The stash file scope is invalid."),
                    };
                    _application?.Invalidate();
                }),
                options.Button(keepIndex ? "Keep index [x]" : "Keep index [ ]").OnClick(_ =>
                {
                    keepIndex = !keepIndex;
                    if (keepIndex)
                    {
                        stagedOnly = false;
                    }

                    _application?.Invalidate();
                }),
                options.Button(stagedOnly ? "Staged only [x]" : "Staged only [ ]").OnClick(_ =>
                {
                    stagedOnly = !stagedOnly;
                    if (stagedOnly)
                    {
                        fileScope = StashFileScope.Tracked;
                        keepIndex = false;
                    }

                    _application?.Invalidate();
                }),
            ]),
            builder.Text(GetStashCreateScopeSummary(fileScope, keepIndex, stagedOnly)),
            builder.Text(GetCurrentChangeSummary()),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Save stash").OnClick(async _ =>
                {
                    var options = new StashCreateOptions(
                        messageState.Text,
                        fileScope,
                        keepIndex,
                        stagedOnly);
                    window.Window.CloseWithResult("create");
                    stashWindow.CloseWithResult("create");
                    await _workspace.CreateStashAsync(options, _cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Save current changes to a stash")
        .Size(86, 13)
        .Modal()
        .Open(windows);
    }

    private void ShowApplyFocusedStashDialog(
        WindowManager windows,
        WindowHandle stashWindow,
        bool pop)
    {
        var stash = _workspace.Stashes.FocusedItem?.Stash;
        if (stash is null)
        {
            return;
        }

        var restoreIndex = false;
        windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"{(pop ? "Pop" : "Apply")}: {stash.Selector}"),
            builder.Text($"Exact object: {stash.ObjectId}"),
            builder.Text($"Subject: {stash.DisplayMessage}"),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button(pop ? "Pop stash" : "Apply stash").OnClick(async _ =>
                {
                    window.Window.CloseWithResult(pop ? "pop" : "apply");
                    stashWindow.CloseWithResult(pop ? "pop" : "apply");
                    if (pop)
                    {
                        await _workspace.PopStashAsync(
                            stash,
                            restoreIndex,
                            _cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _workspace.ApplyStashAsync(
                            stash,
                            restoreIndex,
                            _cancellationToken).ConfigureAwait(false);
                    }
                }),
            ]),
            builder.Text(pop
                ? "Git applies the state and drops this reflog entry only after a clean application."
                : "Git applies the state and retains this stash reflog entry."),
            builder.Text("Conflicts remain in the worktree for resolution; a failed pop retains the stash."),
            builder.Button(restoreIndex ? "Restore index [x]" : "Restore index [ ]").OnClick(_ =>
            {
                restoreIndex = !restoreIndex;
                _application?.Invalidate();
            }),
        ]))
        .Title(pop ? "Pop stash?" : "Apply stash?")
        .Size(88, 13)
        .Modal()
        .Open(windows);
    }

    private void ShowDropFocusedStashDialog(WindowManager windows, WindowHandle stashWindow)
    {
        var stash = _workspace.Stashes.FocusedItem?.Stash;
        if (stash is null)
        {
            return;
        }

        windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Drop permanently from the stash reflog: {stash.Selector}"),
            builder.Text($"Exact object: {stash.ObjectId}"),
            builder.Text($"Subject: {stash.DisplayMessage}"),
            builder.Text("The commit may later be pruned and become unrecoverable."),
            builder.Text("No worktree or index content is applied by this action."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Drop stash").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("drop");
                    stashWindow.CloseWithResult("drop");
                    await _workspace.DropStashAsync(stash, _cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Drop stash?")
        .Size(88, 12)
        .Modal()
        .Open(windows);
    }

    private string GetCurrentChangeSummary()
    {
        var staged = _workspace.State.StagedItems.Length;
        var unstaged = _workspace.State.UnstagedItems.Length;
        var untracked = _workspace.State.Snapshot.Entries.Count(
            static entry => entry.Kind == RepositoryStatusEntryKind.Untracked);
        return $"Current view: {staged} staged, {unstaged} unstaged, {untracked} untracked paths.";
    }

    private static string GetStashFileScopeLabel(StashFileScope scope)
        => scope switch
        {
            StashFileScope.Tracked => "Files: tracked",
            StashFileScope.IncludeUntracked => "Files: +untracked",
            StashFileScope.IncludeIgnored => "Files: +ignored",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    private static string GetStashCreateScopeSummary(
        StashFileScope scope,
        bool keepIndex,
        bool stagedOnly)
    {
        if (stagedOnly)
        {
            return "Only staged changes are saved; unstaged and untracked paths remain.";
        }

        var included = scope switch
        {
            StashFileScope.Tracked => "tracked changes",
            StashFileScope.IncludeUntracked => "tracked and untracked changes",
            StashFileScope.IncludeIgnored => "tracked, untracked, and ignored changes",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        return keepIndex
            ? $"Save {included}; staged changes remain in the index and worktree."
            : $"Save {included}; Git restores those paths to HEAD.";
    }

    private async Task ShowBranchesAsync(WindowManager windows)
    {
        await _workspace.LoadBranchesAsync(_cancellationToken).ConfigureAwait(false);
        if (_workspace.Branches.Catalog is null)
        {
            return;
        }

        windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(filter =>
            [
                filter.Text("Filter: "),
                filter.TextBox()
                    .State(_workspace.Branches.Filter)
                    .OnTextChanged(eventArgs =>
                    {
                        _workspace.Branches.SetFilter(eventArgs.NewText);
                        _application?.Invalidate();
                    })
                    .FillWidth(),
            ]).FillWidth(),
            builder.List(_workspace.Branches.VisibleItems)
                .ItemKey(static item => item.Key)
                .FocusedIndex(_workspace.Branches.FocusedIndex)
                .OnFocusChanged(eventArgs =>
                {
                    if (eventArgs.FocusedIndex >= 0 &&
                        eventArgs.FocusedIndex < _workspace.Branches.VisibleItems.Length)
                    {
                        _workspace.Branches.Focus(eventArgs.FocusedIndex);
                        _application?.Invalidate();
                    }
                })
                .Empty(empty => empty.Text("No branch matches the filter."))
                .InputBindings(bindings =>
                {
                    bindings.Key(Hex1bKey.Enter).Action(
                        _ => RunFocusedBranchPrimaryActionAsync(windows, window.Window),
                        "Switch to the local branch or create from the remote branch");
                    bindings.Key(Hex1bKey.F5).Action(
                        _ => _workspace.LoadBranchesAsync(_cancellationToken),
                        "Refresh branches and linked worktrees");
                }).Fill(),
            builder.VStack(details => BuildBranchDetails(details)),
            builder.WrapPanel(actions => BuildBranchActions(actions, windows, window.Window)),
            builder.Text("Enter primary action | F5 refresh | Mouse select, scroll, resize, and activate buttons"),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close the branch window");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }))
        .Title("Branches and linked worktrees")
        .Size(84, 20)
        .Resizable(58, 16, 120, 40)
        .Modal()
        .Open(windows);
    }

    private Hex1bWidget[] BuildBranchDetails<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var branch = _workspace.Branches.FocusedItem?.Branch;
        if (branch is null)
        {
            return [context.Text("Select a branch to inspect exact state and available actions.")];
        }

        var target = branch.TargetObjectId.ToString();
        var lines = new List<Hex1bWidget>
        {
            context.Text($"Target: {target[..12]} ({(branch.IsCurrent ? "current" : branch.Kind.ToString())})"),
        };
        if (branch.SymbolicTarget is not null)
        {
            lines.Add(context.Text($"Symbolic target: {branch.SymbolicTarget.DisplayText}"));
        }
        else if (branch.UpstreamName is not null)
        {
            var tracking = branch.IsUpstreamGone
                ? "gone"
                : $"ahead {branch.AheadCount}, behind {branch.BehindCount}";
            lines.Add(context.Text($"Upstream: {branch.UpstreamName.DisplayText} ({tracking})"));
        }
        else
        {
            lines.Add(context.Text("Upstream: none"));
        }

        if (!branch.OccupiedWorktrees.IsEmpty)
        {
            var firstPath = branch.OccupiedWorktrees[0].DisplayText;
            var remaining = branch.OccupiedWorktrees.Length == 1
                ? string.Empty
                : $" and {branch.OccupiedWorktrees.Length - 1} more";
            lines.Add(context.Text($"Checked out at: {firstPath}{remaining}"));
        }

        return [.. lines];
    }

    private Hex1bWidget[] BuildBranchActions<TParent>(
        WidgetContext<TParent> context,
        WindowManager windows,
        WindowHandle branchWindow)
        where TParent : Hex1bWidget
    {
        var branch = _workspace.Branches.FocusedItem?.Branch;
        var actions = new List<Hex1bWidget>
        {
            context.Button("Cancel").OnClick(_ => branchWindow.Cancel()),
            context.Button("Refresh").OnClick(_ => _workspace.LoadBranchesAsync(_cancellationToken)),
        };
        if (branch is null)
        {
            return [.. actions];
        }

        if (branch.Kind == BranchKind.Local)
        {
            if (branch.IsCurrent)
            {
                actions.Add(context.Text("Current branch"));
            }
            else if (branch.OccupiedWorktrees.IsEmpty)
            {
                actions.Add(context.Button("Switch").OnClick(async _ =>
                {
                    var selectedBranch = _workspace.Branches.FocusedItem?.Branch;
                    if (selectedBranch is null ||
                        selectedBranch.Kind != BranchKind.Local ||
                        selectedBranch.IsCurrent ||
                        !selectedBranch.OccupiedWorktrees.IsEmpty)
                    {
                        return;
                    }

                    branchWindow.CloseWithResult("switch");
                    await _workspace.SwitchBranchAsync(
                        selectedBranch,
                        _cancellationToken).ConfigureAwait(false);
                }));
            }
            else
            {
                actions.Add(context.Text("Switch unavailable: linked worktree"));
            }
        }

        if (branch.SymbolicTarget is null)
        {
            actions.Add(context.Button("New...").OnClick(
                _ => ShowCreateFocusedBranchDialog(windows, branchWindow)));
            if (!branch.IsCurrent)
            {
                actions.Add(context.Button("Merge...").OnClick(
                    _ => ShowMergeFocusedBranchDialogAsync(windows, branchWindow)));
            }
        }

        actions.Add(context.Button("Detach").OnClick(async _ =>
        {
            var selectedBranch = _workspace.Branches.FocusedItem?.Branch;
            if (selectedBranch is null)
            {
                return;
            }

            branchWindow.CloseWithResult("detach");
            await _workspace.DetachBranchAsync(
                selectedBranch,
                _cancellationToken).ConfigureAwait(false);
        }));
        if (branch.Kind == BranchKind.Local &&
            (branch.IsCurrent || branch.OccupiedWorktrees.IsEmpty))
        {
            actions.Add(context.Button("Rename...").OnClick(
                _ => ShowRenameFocusedBranchDialog(windows, branchWindow)));
        }

        if (branch.Kind == BranchKind.Local &&
            !branch.IsCurrent &&
            branch.OccupiedWorktrees.IsEmpty)
        {
            actions.Add(context.Button("Delete...").OnClick(
                _ => ShowDeleteFocusedBranchDialog(windows, branchWindow)));
        }

        if (branch.Kind == BranchKind.Local && branch.IsCurrent)
        {
            actions.Add(context.Button("Reset...").OnClick(
                _ => ShowResetFocusedBranchDialog(windows, branchWindow)));
        }

        return [.. actions];
    }

    private Task RunFocusedBranchPrimaryActionAsync(
        WindowManager windows,
        WindowHandle branchWindow)
    {
        var branch = _workspace.Branches.FocusedItem?.Branch;
        if (branch is null)
        {
            return Task.CompletedTask;
        }

        if (branch.Kind == BranchKind.Local)
        {
            if (branch.IsCurrent || !branch.OccupiedWorktrees.IsEmpty)
            {
                return Task.CompletedTask;
            }

            branchWindow.CloseWithResult("switch");
            return _workspace.SwitchBranchAsync(branch, _cancellationToken);
        }

        if (branch.SymbolicTarget is null)
        {
            ShowCreateBranchDialog(windows, branchWindow, branch);
        }

        return Task.CompletedTask;
    }

    private void ShowCreateFocusedBranchDialog(WindowManager windows, WindowHandle branchWindow)
    {
        var branch = _workspace.Branches.FocusedItem?.Branch;
        if (branch is not null && branch.SymbolicTarget is null)
        {
            ShowCreateBranchDialog(windows, branchWindow, branch);
        }
    }

    private void ShowRenameFocusedBranchDialog(WindowManager windows, WindowHandle branchWindow)
    {
        var branch = _workspace.Branches.FocusedItem?.Branch;
        if (branch is not null &&
            branch.Kind == BranchKind.Local &&
            (branch.IsCurrent || branch.OccupiedWorktrees.IsEmpty))
        {
            ShowRenameBranchDialog(windows, branchWindow, branch);
        }
    }

    private void ShowDeleteFocusedBranchDialog(WindowManager windows, WindowHandle branchWindow)
    {
        var branch = _workspace.Branches.FocusedItem?.Branch;
        if (branch is not null &&
            branch.Kind == BranchKind.Local &&
            !branch.IsCurrent &&
            branch.OccupiedWorktrees.IsEmpty)
        {
            ShowDeleteBranchDialog(windows, branchWindow, branch);
        }
    }

    private void ShowResetFocusedBranchDialog(WindowManager windows, WindowHandle branchWindow)
    {
        var branch = _workspace.Branches.FocusedItem?.Branch;
        if (branch is not null && branch.Kind == BranchKind.Local && branch.IsCurrent)
        {
            ShowResetBranchDialog(windows, branchWindow, branch);
        }
    }

    private Task ShowMergeFocusedBranchDialogAsync(WindowManager windows, WindowHandle branchWindow)
    {
        var branch = _workspace.Branches.FocusedItem?.Branch;
        return branch is null || branch.IsCurrent || branch.SymbolicTarget is not null
            ? Task.CompletedTask
            : ShowMergeBranchDialogAsync(windows, branchWindow, branch);
    }

    private async Task ShowMergeBranchDialogAsync(
        WindowManager windows,
        WindowHandle? branchWindow,
        BranchInfo source)
    {
        var plan = await _workspace.PrepareMergeAsync(
            source,
            _cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return;
        }

        var fastForward = MergeFastForwardMode.Default;
        var strategy = MergeStrategy.Default;
        var conflictPreference = MergeConflictPreference.Default;
        var squash = false;
        var stopBeforeCommit = false;
        var autoStash = GitOptionOverride.Configured;
        var rerere = GitOptionOverride.Configured;
        var verifySignatures = GitOptionOverride.Configured;
        windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Merge source: {plan.Source.FullName.DisplayText}"),
            builder.Text($"Incoming object: {plan.Source.TargetObjectId}"),
            builder.Text($"Current HEAD: {plan.Precondition.HeadObjectId}"),
            builder.Text(GetMergeRelationshipText(plan)),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Merge exact object").OnClick(async _ =>
                {
                    var options = new MergeOptions(
                        fastForward,
                        strategy,
                        conflictPreference,
                        squash,
                        stopBeforeCommit,
                        autoStash,
                        rerere,
                        verifySignatures);
                    window.Window.CloseWithResult("merge");
                    branchWindow?.CloseWithResult("merge");
                    await _workspace.MergeAsync(
                        plan,
                        options,
                        _cancellationToken).ConfigureAwait(false);
                }),
            ]),
            builder.WrapPanel(options =>
            [
                options.Button(GetFastForwardLabel(fastForward)).OnClick(_ =>
                {
                    fastForward = CycleFastForwardMode(fastForward);
                    _application?.Invalidate();
                }),
                options.Button(GetMergeStrategyLabel(strategy)).OnClick(_ =>
                {
                    strategy = CycleMergeStrategy(strategy);
                    if (strategy is not MergeStrategy.Default and not MergeStrategy.Ort)
                    {
                        conflictPreference = MergeConflictPreference.Default;
                    }

                    _application?.Invalidate();
                }),
                options.Button(GetConflictPreferenceLabel(conflictPreference)).OnClick(_ =>
                {
                    if (strategy is MergeStrategy.Default or MergeStrategy.Ort)
                    {
                        conflictPreference = CycleConflictPreference(conflictPreference);
                        _application?.Invalidate();
                    }
                }),
            ]),
            builder.WrapPanel(options =>
            [
                options.Button(squash ? "Squash [x]" : "Squash [ ]").OnClick(_ =>
                {
                    squash = !squash;
                    if (squash)
                    {
                        stopBeforeCommit = false;
                    }

                    _application?.Invalidate();
                }),
                options.Button(stopBeforeCommit ? "Stop before commit [x]" : "Stop before commit [ ]").OnClick(_ =>
                {
                    stopBeforeCommit = !stopBeforeCommit;
                    if (stopBeforeCommit)
                    {
                        squash = false;
                    }

                    _application?.Invalidate();
                }),
            ]),
            builder.WrapPanel(options =>
            [
                options.Button(GetOverrideLabel("Autostash", autoStash)).OnClick(_ =>
                {
                    autoStash = CycleOverride(autoStash);
                    _application?.Invalidate();
                }),
                options.Button(GetOverrideLabel("Rerere update", rerere)).OnClick(_ =>
                {
                    rerere = CycleOverride(rerere);
                    _application?.Invalidate();
                }),
                options.Button(GetOverrideLabel("Verify signatures", verifySignatures)).OnClick(_ =>
                {
                    verifySignatures = CycleOverride(verifySignatures);
                    _application?.Invalidate();
                }),
            ]),
            builder.Text(GetMergeOptionsExplanation(
                plan,
                fastForward,
                strategy,
                conflictPreference,
                squash,
                stopBeforeCommit,
                autoStash)),
            builder.Text("Git runs hooks, strategy machinery, rerere, autostash, index updates, refs, and conflict setup."),
        ]))
        .Title("Merge exact selected branch?")
        .Size(104, 19)
        .Resizable(64, 17, 130, 34)
        .Modal()
        .Open(windows);
    }

    private void ShowCreateBranchDialog(
        WindowManager windows,
        WindowHandle branchWindow,
        BranchInfo source)
    {
        var nameState = new TextBoxState(GetInitialBranchName(source));
        var trackSource = source.Kind == BranchKind.RemoteTracking;
        var validationMessage = string.Empty;
        windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Source: {source.FullName.DisplayText}"),
            builder.Text($"Exact commit: {source.TargetObjectId}"),
            builder.HStack(name =>
            [
                name.Text("Local name: "),
                name.TextBox().State(nameState).OnTextChanged(_ => validationMessage = string.Empty),
            ]).FillWidth(),
            source.Kind == BranchKind.RemoteTracking
                ? builder.Button(trackSource ? "Tracking [x] direct" : "Tracking [ ] none").OnClick(_ =>
                {
                    trackSource = !trackSource;
                    _application?.Invalidate();
                })
                : builder.Text("Tracking: none"),
            builder.Text(validationMessage),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Create and switch").OnClick(async _ =>
                {
                    if (string.IsNullOrWhiteSpace(nameState.Text))
                    {
                        validationMessage = "Enter a local branch name.";
                        _application?.Invalidate();
                        return;
                    }

                    window.Window.CloseWithResult("create");
                    branchWindow.CloseWithResult("create");
                    await _workspace.CreateAndSwitchBranchAsync(
                        source,
                        nameState.Text,
                        trackSource,
                        _cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Create local branch")
        .Size(72, 11)
        .Modal()
        .Open(windows);
    }

    private void ShowRenameBranchDialog(
        WindowManager windows,
        WindowHandle branchWindow,
        BranchInfo branch)
    {
        var nameState = new TextBoxState(TryGetEditableRefText(branch.ShortName, out var name)
            ? name
            : string.Empty);
        var validationMessage = string.Empty;
        windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Rename: {branch.ShortName.DisplayText}"),
            builder.Text($"Exact commit remains: {branch.TargetObjectId}"),
            builder.HStack(nameRow =>
            [
                nameRow.Text("New name: "),
                nameRow.TextBox().State(nameState).OnTextChanged(_ => validationMessage = string.Empty),
            ]).FillWidth(),
            builder.Text(validationMessage),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Rename").OnClick(async _ =>
                {
                    if (string.IsNullOrWhiteSpace(nameState.Text))
                    {
                        validationMessage = "Enter a destination branch name.";
                        _application?.Invalidate();
                        return;
                    }

                    window.Window.CloseWithResult("rename");
                    branchWindow.CloseWithResult("rename");
                    await _workspace.RenameBranchAsync(
                        branch,
                        nameState.Text,
                        _cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Rename local branch")
        .Size(70, 9)
        .Modal()
        .Open(windows);
    }

    private void ShowDeleteBranchDialog(
        WindowManager windows,
        WindowHandle branchWindow,
        BranchInfo branch)
    {
        windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Delete local branch: {branch.ShortName.DisplayText}"),
            builder.Text($"Current target: {branch.TargetObjectId}"),
            builder.Text("Safe delete asks Git to verify mergedness."),
            builder.Text("Force delete removes the ref even when commits are unmerged."),
            builder.Text("The branch is not checked out in any linked worktree."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Safe delete").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("safe delete");
                    branchWindow.CloseWithResult("delete");
                    await _workspace.DeleteBranchAsync(
                        branch,
                        BranchDeleteMode.Safe,
                        _cancellationToken).ConfigureAwait(false);
                }),
                actions.Text(" "),
                actions.Button("Force delete").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("force delete");
                    branchWindow.CloseWithResult("delete");
                    await _workspace.DeleteBranchAsync(
                        branch,
                        BranchDeleteMode.Force,
                        _cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Delete branch?")
        .Size(76, 11)
        .Modal()
        .Open(windows);
    }

    private void ShowResetBranchDialog(
        WindowManager windows,
        WindowHandle branchWindow,
        BranchInfo branch)
    {
        var revisionState = new TextBoxState();
        var validationMessage = string.Empty;
        windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Current branch: {branch.ShortName.DisplayText}"),
            builder.Text($"Current commit: {branch.TargetObjectId}"),
            builder.HStack(revision =>
            [
                revision.Text("Target revision: "),
                revision.TextBox().State(revisionState).OnTextChanged(_ => validationMessage = string.Empty),
            ]).FillWidth(),
            builder.Text("Soft keeps index and worktree; mixed resets index; hard also discards tracked worktree changes."),
            builder.Text(validationMessage),
            builder.WrapPanel(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Button("Soft reset").OnClick(
                    _ => RunResetBranchAsync(
                        window.Window,
                        branchWindow,
                        branch,
                        revisionState,
                        BranchResetMode.Soft,
                        message => validationMessage = message)),
                actions.Button("Mixed reset").OnClick(
                    _ => RunResetBranchAsync(
                        window.Window,
                        branchWindow,
                        branch,
                        revisionState,
                        BranchResetMode.Mixed,
                        message => validationMessage = message)),
                actions.Button("Hard reset").OnClick(
                    _ => RunResetBranchAsync(
                        window.Window,
                        branchWindow,
                        branch,
                        revisionState,
                        BranchResetMode.Hard,
                        message => validationMessage = message)),
            ]),
        ]))
        .Title("Reset current branch")
        .Size(88, 11)
        .Modal()
        .Open(windows);
    }

    private async Task RunResetBranchAsync(
        WindowHandle resetWindow,
        WindowHandle branchWindow,
        BranchInfo branch,
        TextBoxState revisionState,
        BranchResetMode mode,
        Action<string> setValidationMessage)
    {
        if (string.IsNullOrWhiteSpace(revisionState.Text))
        {
            setValidationMessage("Enter a revision for Git to resolve to an exact commit.");
            _application?.Invalidate();
            return;
        }

        resetWindow.CloseWithResult(mode);
        branchWindow.CloseWithResult("reset");
        await _workspace.ResetCurrentBranchAsync(
            branch,
            revisionState.Text,
            mode,
            _cancellationToken).ConfigureAwait(false);
    }

    private static string GetInitialBranchName(BranchInfo source)
    {
        if (source.Kind == BranchKind.RemoteTracking)
        {
            try
            {
                var proposal = BranchService.GetLocalNameProposal(source);
                if (TryGetEditableRefText(proposal, out var proposalText))
                {
                    return proposalText;
                }
            }
            catch (InvalidDataException)
            {
                return string.Empty;
            }
        }
        else if (TryGetEditableRefText(source.ShortName, out var localName))
        {
            return localName + "-new";
        }

        return string.Empty;
    }

    private static bool TryGetEditableRefText(RefName referenceName, out string text)
    {
        try
        {
            text = s_strictUtf8.GetString(referenceName.GetBytes());
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string GetMergeRelationshipText(MergePlan plan)
        => plan.Relationship switch
        {
            MergeRelationship.AlreadyIntegrated =>
                $"Already integrated; incoming-only commits: {plan.IncomingCommitCount}.",
            MergeRelationship.FastForward =>
                $"Fast-forward available; {plan.IncomingCommitCount} incoming commits.",
            MergeRelationship.Diverged =>
                $"Diverged: {plan.CurrentOnlyCommitCount} current-only, {plan.IncomingCommitCount} incoming-only commits.",
            _ => throw new ArgumentOutOfRangeException(nameof(plan)),
        };

    private static string GetFastForwardLabel(MergeFastForwardMode mode)
        => mode switch
        {
            MergeFastForwardMode.Default => "Fast-forward: Git config",
            MergeFastForwardMode.FastForwardOnly => "Fast-forward: only",
            MergeFastForwardMode.NoFastForward => "Fast-forward: create merge commit",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static MergeFastForwardMode CycleFastForwardMode(MergeFastForwardMode mode)
        => mode switch
        {
            MergeFastForwardMode.Default => MergeFastForwardMode.FastForwardOnly,
            MergeFastForwardMode.FastForwardOnly => MergeFastForwardMode.NoFastForward,
            MergeFastForwardMode.NoFastForward => MergeFastForwardMode.Default,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static string GetMergeStrategyLabel(MergeStrategy strategy)
        => strategy switch
        {
            MergeStrategy.Default => "Strategy: Git default",
            MergeStrategy.Ort => "Strategy: ort",
            MergeStrategy.Resolve => "Strategy: resolve",
            MergeStrategy.Ours => "Strategy: ours (discard incoming tree)",
            MergeStrategy.Subtree => "Strategy: subtree",
            _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
        };

    private static MergeStrategy CycleMergeStrategy(MergeStrategy strategy)
        => strategy switch
        {
            MergeStrategy.Default => MergeStrategy.Ort,
            MergeStrategy.Ort => MergeStrategy.Resolve,
            MergeStrategy.Resolve => MergeStrategy.Ours,
            MergeStrategy.Ours => MergeStrategy.Subtree,
            MergeStrategy.Subtree => MergeStrategy.Default,
            _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
        };

    private static string GetConflictPreferenceLabel(MergeConflictPreference preference)
        => preference switch
        {
            MergeConflictPreference.Default => "Conflicts: normal",
            MergeConflictPreference.Ours => "Conflicts: favor ours",
            MergeConflictPreference.Theirs => "Conflicts: favor theirs",
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };

    private static MergeConflictPreference CycleConflictPreference(MergeConflictPreference preference)
        => preference switch
        {
            MergeConflictPreference.Default => MergeConflictPreference.Ours,
            MergeConflictPreference.Ours => MergeConflictPreference.Theirs,
            MergeConflictPreference.Theirs => MergeConflictPreference.Default,
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };

    private static string GetOverrideLabel(string name, GitOptionOverride option)
        => option switch
        {
            GitOptionOverride.Configured => $"{name}: Git config",
            GitOptionOverride.Enabled => $"{name}: on",
            GitOptionOverride.Disabled => $"{name}: off",
            _ => throw new ArgumentOutOfRangeException(nameof(option)),
        };

    private static GitOptionOverride CycleOverride(GitOptionOverride option)
        => option switch
        {
            GitOptionOverride.Configured => GitOptionOverride.Enabled,
            GitOptionOverride.Enabled => GitOptionOverride.Disabled,
            GitOptionOverride.Disabled => GitOptionOverride.Configured,
            _ => throw new ArgumentOutOfRangeException(nameof(option)),
        };

    private static string GetMergeOptionsExplanation(
        MergePlan plan,
        MergeFastForwardMode fastForward,
        MergeStrategy strategy,
        MergeConflictPreference conflictPreference,
        bool squash,
        bool stopBeforeCommit,
        GitOptionOverride autoStash)
    {
        if (strategy == MergeStrategy.Ours)
        {
            return "Warning: the ours strategy records incoming history but discards its entire tree.";
        }

        if (conflictPreference != MergeConflictPreference.Default)
        {
            return "The ours/theirs preference affects conflicting hunks only; nonconflicting incoming changes remain.";
        }

        if (squash)
        {
            return "Squash prepares index/worktree changes without MERGE_HEAD or merge ancestry; review and commit separately.";
        }

        if (stopBeforeCommit &&
            plan.Relationship == MergeRelationship.FastForward &&
            fastForward != MergeFastForwardMode.NoFastForward)
        {
            return "A fast-forward cannot stop before a merge commit; select create merge commit to guarantee review before commit.";
        }

        if (autoStash == GitOptionOverride.Enabled)
        {
            return "Autostash may produce additional conflicts when Git reapplies local changes after the merge.";
        }

        return "The exact incoming object is used after revalidating HEAD, index, worktree, and the selected source ref.";
    }

    private void ShowAbortMergeConfirmation(WindowManager windows)
    {
        var warning = _workspace.MergeAbortWarning;
        if (!_workspace.CanAbortMerge || warning is null)
        {
            return;
        }

        var headObjectId = warning.Precondition.HeadObjectId
            ?? throw new InvalidDataException("An active merge warning has no HEAD object.");
        windows.Window(window => window.VStack(builder =>
        {
            var content = new List<Hex1bWidget>
            {
                builder.Text($"Current HEAD ({GetHeadAttachmentLabel(warning.Precondition.HeadName)}):"),
                builder.Text(headObjectId.ToString()),
                builder.HStack(buttons =>
                [
                    buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    buttons.Text(" "),
                    buttons.Button("Abort merge").OnClick(async _ =>
                    {
                        window.Window.CloseWithResult(true);
                        await _workspace.AbortMergeAsync(warning, _cancellationToken).ConfigureAwait(false);
                    }),
                ]),
                builder.Text("Incoming MERGE_HEAD objects:"),
                builder.VScrollPanel(
                    heads => [.. warning.MergeHeads.Select(head => heads.Text(head.ToString()))],
                    showScrollbar: warning.MergeHeads.Length > 4).Fill(),
            };
            if (warning.MergeAutostash is null)
            {
                content.Add(builder.Text("No merge autostash will be applied."));
            }
            else
            {
                content.Add(builder.Text("MERGE_AUTOSTASH object Git will apply during abort:"));
                content.Add(builder.Text(warning.MergeAutostash.ToString()));
            }

            content.Add(builder.Text("Git will run merge --abort and attempt to restore the pre-merge state."));
            content.Add(builder.Text("Uncommitted changes that Git cannot reconstruct may cause the abort to fail."));
            return [.. content];
        }))
        .Title("Abort merge?")
        .Size(
            86,
            14 + Math.Min(warning.MergeHeads.Length, 4) + (warning.MergeAutostash is null ? 0 : 1))
        .Modal()
        .Open(windows);
    }

    private static string GetHeadAttachmentLabel(RefName? headName)
    {
        const string localBranchPrefix = "refs/heads/";
        if (headName is null)
        {
            return "detached HEAD";
        }

        var displayText = headName.DisplayText;
        return displayText.StartsWith(localBranchPrefix, StringComparison.Ordinal)
            ? displayText[localBranchPrefix.Length..]
            : displayText;
    }

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

    private Task ToggleAmendAsync()
        => _workspace.ToggleAmendAsync(_cancellationToken);

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

    private void ShowCommitWarningsConfirmation(
        WindowManager windows,
        PublishedAmendWarning? publishedWarning,
        DetachedHeadWarning? detachedWarning)
    {
        windows.Window(window => window.VStack(builder =>
        {
            var content = new List<Hex1bWidget>();
            if (detachedWarning is not null)
            {
                content.Add(builder.Text(
                    $"HEAD is detached at {detachedWarning.HeadObjectId.ToString()[..12]}."));
                content.Add(builder.Text("The new commit will not belong to a branch."));
                content.Add(builder.Text(
                    "Create or switch to a branch first unless this detached commit is intentional."));
            }

            if (publishedWarning is not null)
            {
                content.Add(builder.Text("HEAD is contained by these local remote-tracking refs:"));
            }

            content.Add(builder.HStack(buttons =>
            [
                buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                buttons.Text(" "),
                buttons.Button(publishedWarning is null ? "Commit anyway" : "Amend anyway")
                    .OnClick(async _ =>
                    {
                        window.Window.CloseWithResult(true);
                        await _workspace.CommitAfterWarningsAsync(
                            publishedWarning,
                            detachedWarning,
                            _cancellationToken).ConfigureAwait(false);
                    }),
            ]));
            if (publishedWarning is not null)
            {
                var referenceLabels = GetRemoteTrackingReferenceLabels(publishedWarning);
                content.Add(builder.VScrollPanel(references =>
                    [.. referenceLabels.Select(label => references.Text(label))],
                    showScrollbar: false).Fill());
                content.Add(builder.Text("Amending rewrites HEAD and may require a force push."));
                content.Add(builder.Text("This is a local heuristic; remote servers may differ from these refs."));
            }

            if (detachedWarning is not null)
            {
                content.Add(builder.Text(
                    "The new commit may become unreachable after HEAD moves away from it."));
            }

            return [.. content];
        }))
        .Title(GetCommitWarningTitle(publishedWarning, detachedWarning))
        .Size(
            publishedWarning is null ? 78 : 86,
            9 + (publishedWarning is null ? 0 : 6) + (detachedWarning is null ? 0 : 4))
        .Modal()
        .Open(windows);
    }

    private void ShowCommitWithoutHooksConfirmation(WindowManager windows)
    {
        var publishedWarning = _workspace.CommitOptions.Amend
            ? _workspace.PublishedAmendWarning
            : null;
        var detachedWarning = _workspace.DetachedHeadWarning;
        windows.Window(window => window.VStack(builder =>
        {
            var content = new List<Hex1bWidget>
            {
                builder.Text("Git will run this commit with --no-verify."),
                builder.Text("This bypasses pre-commit and commit-msg only."),
                builder.Text("Prepare and post hooks still run."),
                builder.HStack(buttons =>
                [
                    buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    buttons.Text(" "),
                    buttons.Button("Commit without hooks").OnClick(async _ =>
                    {
                        window.Window.CloseWithResult(true);
                        await _workspace.CommitWithoutHooksAsync(
                            publishedWarning,
                            detachedWarning,
                            _cancellationToken).ConfigureAwait(false);
                    }),
                ]),
            };
            if (publishedWarning is not null)
            {
                content.Add(builder.Text("HEAD is also contained by these local remote-tracking refs:"));
                var referenceLabels = GetRemoteTrackingReferenceLabels(publishedWarning);
                content.Add(builder.VScrollPanel(references =>
                    [.. referenceLabels.Select(label => references.Text(label))],
                    showScrollbar: false).Fill());
                content.Add(builder.Text("This is a local heuristic; remote servers may differ from these refs."));
            }

            if (detachedWarning is not null)
            {
                content.Add(builder.Text(
                    $"HEAD is detached at {detachedWarning.HeadObjectId.ToString()[..12]}."));
                content.Add(builder.Text("The new commit will not belong to a branch."));
                content.Add(builder.Text(
                    "The new commit may become unreachable after HEAD moves away from it."));
            }

            return [.. content];
        }))
        .Title("Commit without hooks?")
        .Size(
            publishedWarning is null && detachedWarning is null ? 62 : 86,
            9 + (publishedWarning is null ? 0 : 6) + (detachedWarning is null ? 0 : 4))
        .Modal()
        .Open(windows);
    }

    private static string GetCommitWarningTitle(
        PublishedAmendWarning? publishedWarning,
        DetachedHeadWarning? detachedWarning)
        => publishedWarning is not null
            ? detachedWarning is null
                ? "Amend published commit?"
                : "Amend published detached HEAD?"
            : "Commit detached HEAD?";

    private static string[] GetRemoteTrackingReferenceLabels(PublishedAmendWarning warning)
    {
        const string prefix = "refs/remotes/";
        return [.. warning.RemoteTrackingRefs
            .Select(static reference => reference.DisplayText)
            .Select(static display => display.StartsWith(prefix, StringComparison.Ordinal)
                ? display[prefix.Length..]
                : display)];
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
