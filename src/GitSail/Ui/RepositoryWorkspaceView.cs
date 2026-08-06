using GitSail.CommandLine;
using GitSail.Diagnostics;
using GitSail.Domain;
using GitSail.Features.Help;
using GitSail.Git.Execution;
using GitSail.Localization.Generated;
using Hex1b;
using Hex1b.Documents;
using Hex1b.Input;
using Hex1b.Widgets;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly ImmutableArray<string> s_workspaceMenuCategories =
    [
        "Repository",
        "Edit",
        "View",
        "Branch",
        "Commit",
        "Merge",
        "Remote",
        "Stash",
        "History",
        "Tools",
        "Help",
    ];
    private readonly Lock _credentialPromptLock = new();
    private readonly Lock _executablePromptLock = new();
    private Hex1bApp? _application;
    private WindowManager? _credentialWindowManager;
    private WindowHandle? _credentialPromptWindow;
    private WindowManager? _executableWindowManager;
    private WindowHandle? _executablePromptWindow;
    private WindowManager? _popupWindowManager;
    private readonly List<WindowHandle> _popupWindows = [];
    private long _credentialPromptId;
    private long _executablePromptId;
    private int _workspaceRegion;
    private bool _isDiffSearchVisible;
    private EditorState? _commandEditor;
    private TextBoxWidget? _changedPathFilterWidget;
    private TextBoxWidget? _diffSearchWidget;
    private readonly PopupViewport _popupViewport = new();

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
        _workspace.Operations.CancelAll();
        _application = null;
        _commandEditor = null;
        _isDiffSearchVisible = false;
        _changedPathFilterWidget = null;
        _diffSearchWidget = null;
        _popupWindowManager = null;
        _popupWindows.Clear();
        _executableWindowManager = null;
        _executablePromptWindow = null;
        _executablePromptId = 0;
    }

    /// <summary>
    /// Builds the complete responsive workspace widget tree for one render generation.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The controlled repository workspace and bounded dialog host.</returns>
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
                        .InputBindings(bindings => bindings.Key(Hex1bKey.Escape)
                            .Global()
                            .OverridesCapture()
                            .Action(
                                _ => CloseActivePopup(),
                                "Close the active popup"))
                    : null,
            ]).Fill())
            .Fill();

    private VStackWidget BuildWorkspace<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            builder.Responsive(responsive =>
            [
                responsive.When(
                    static (width, height) => width < 60 || height < 18,
                    compact => BuildMinimumWorkspace(compact)),
                responsive.Otherwise(workspace => BuildStandardWorkspace(workspace)),
            ]).Fill(),
        ]).InputBindings(bindings =>
        {
            if (_mode != ApplicationMode.Merge)
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
            }
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
            bindings.Key(Hex1bKey.F6).Action(
                _ => CycleWorkspaceRegion(),
                "Cycle changes, diff, and commit regions");
            bindings.Key(Hex1bKey.F7).Action(
                _ => FocusChangedPathFilter(),
                "Focus changed-path filter");
            bindings.Ctrl().Key(Hex1bKey.F).Action(
                _ => FocusDiffSearch(),
                AppMessages.DiffBindingFocusTextSearch);
            bindings.Key(Hex1bKey.F3).Action(
                actionContext => FindDiffTextAsync(actionContext, reverse: false),
                AppMessages.DiffBindingNextTextMatch);
            bindings.Shift().Key(Hex1bKey.F3).Action(
                actionContext => FindDiffTextAsync(actionContext, reverse: true),
                AppMessages.DiffBindingPreviousTextMatch);
            bindings.Key(Hex1bKey.N).Action(
                actionContext => FindDiffTextAsync(actionContext, reverse: false),
                AppMessages.DiffBindingNextTextMatch);
            bindings.Shift().Key(Hex1bKey.N).Action(
                actionContext => FindDiffTextAsync(actionContext, reverse: true),
                AppMessages.DiffBindingPreviousTextMatch);
            if (!IsResolutionOnlyMode)
            {
                bindings.Key(Hex1bKey.F2).Action(
                    actionContext => ShowCommandPalette(actionContext.Windows),
                    "Open the searchable command palette");
                bindings.Key(Hex1bKey.F10).Action(
                    actionContext => ShowApplicationMenu(actionContext.Windows),
                    "Open the complete application menu");
                bindings.Key(Hex1bKey.F8).Action(
                    actionContext => ShowBranchesAsync(actionContext.Windows),
                    "Open the searchable branch and worktree window");
                bindings.Key(Hex1bKey.F9).Action(
                    actionContext => ShowStashesAsync(actionContext.Windows),
                    "Open the searchable stash and patch window");
                bindings.Key(Hex1bKey.P).Action(
                    _ => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken),
                    "Prepare the focused untracked path for hunk and line staging");
                bindings.Key(Hex1bKey.R).Action(
                    actionContext => ShowRevertConfirmation(actionContext.Windows),
                    "Choose and confirm an exact worktree revert scope");
                bindings.Shift().Key(Hex1bKey.R).Action(
                    actionContext => ShowRevertConfirmation(actionContext.Windows),
                    "Choose and confirm an exact worktree revert scope");
                bindings.Ctrl().Key(Hex1bKey.Z).Action(
                    _ => _workspace.UndoRevertAsync(_cancellationToken),
                    "Undo the most recent eligible worktree revert");
            }
            bindings.Key(Hex1bKey.F4).Action(
                actionContext => IsResolutionOnlyMode
                    ? Complete(actionContext.RequestStop)
                    : RunPrimaryActionAsync(actionContext.Windows),
                IsResolutionOnlyMode
                    ? GetResolutionExitDescription()
                    : GetPrimaryActionDescription());
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                actionContext => Complete(() => actionContext.Windows.ActiveWindow?.Close()),
                "Close the active window");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }).Fill();

    private VStackWidget BuildStandardWorkspace<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            BuildHeader(builder),
            BuildMenuBar(builder),
            BuildWorkspaceContent(builder),
            BuildActionBar(builder),
            BuildShortcutBar(builder),
        ]).Fill();

    private VStackWidget BuildMinimumWorkspace<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            BuildResizeView(builder),
            BuildResizeActionBar(builder),
            BuildResizeShortcutBar(builder),
        ]).Fill();

    private HStackWidget BuildResizeActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions => IsResolutionOnlyMode
            ?
            [
                actions.Button(AppMessages.WorkspaceActionHelp).OnClick(
                    eventArgs => ShowHelp(eventArgs.Windows)),
                actions.Text(" "),
                actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                    eventArgs => eventArgs.Context.RequestStop()),
            ]
            :
            [
                actions.Button(AppMessages.WorkspaceActionHelp).OnClick(
                    eventArgs => ShowHelp(eventArgs.Windows)),
                actions.Text(" "),
                actions.Button(AppMessages.WorkspaceActionCommands).OnClick(
                    eventArgs => ShowCommandPalette(eventArgs.Windows)),
                actions.Text(" "),
                actions.Button(AppMessages.WorkspaceActionMenu).OnClick(
                    eventArgs => ShowApplicationMenu(eventArgs.Windows)),
                actions.Text(" "),
                actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                    eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth();

    private InfoBarWidget BuildResizeShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info => IsResolutionOnlyMode
            ?
            [
                info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
                info.Spacer(),
                info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
            ]
            :
            [
                info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
                info.Section($"F2 {AppMessages.WorkspaceActionCommands}"),
                info.Section($"F10 {AppMessages.WorkspaceActionMenu}"),
                info.Spacer(),
                info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
            ]).Divider(" | ");

    private Hex1bWidget BuildMenuBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        RememberFocusedWorkspaceEditor();
        if (IsResolutionOnlyMode)
        {
            return context.HStack(_ => []);
        }

        return context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(
                120,
                wide =>
                {
                    var commands = BuildWorkspaceCommands();
                    return wide.MenuBar(menu =>
                    [
                        .. s_workspaceMenuCategories.Select(category => menu.Menu(
                            category,
                            items => BuildWorkspaceMenuItems(items, category, commands))
                            .NoAccelerator()),
                    ]).FillWidth();
                }),
            responsive.Otherwise(compact => compact.HStack(_ => [])),
        ]);
    }

    private static IEnumerable<IMenuChild> BuildWorkspaceMenuItems(
        MenuContext context,
        string category,
        IReadOnlyList<WorkspaceCommandItem> commands)
    {
        foreach (var command in commands.Where(command => command.MenuCategories.Contains(
            category,
            StringComparer.Ordinal)))
        {
            var label = command.Binding.Length == 0
                ? command.Label
                : $"{command.Label}   {command.Binding}";
            var item = context.MenuItem(label)
                .OnActivated(eventArgs => command.ExecuteAsync(eventArgs.Windows));
            yield return command.IsAvailable ? item : item.Disabled();
        }
    }

    private ResponsiveWidget BuildWorkspaceContent<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => IsResolutionOnlyMode
            ? context.Responsive(responsive =>
            [
                responsive.When(
                    static (width, height) => width < 60 || height < 14,
                    compact => BuildResizeView(compact)),
                responsive.Otherwise(workspace => workspace.HSplitter(
                    BuildChangesPane(workspace),
                    BuildDetailPane(workspace),
                    22).Fill()),
            ]).Fill()
            : context.Responsive(responsive =>
            [
                responsive.When(
                    static (width, height) => width < 60 || height < 14,
                    compact => BuildResizeView(compact)),
                responsive.When(
                    static (width, _) => width < 80,
                    compact => BuildCompactWorkspace(compact)),
                responsive.WhenMinWidth(
                    120,
                    wide => wide.HSplitter(
                        BuildChangesPane(wide),
                        BuildDetailPane(wide),
                        44).Fill()),
                responsive.Otherwise(medium => BuildMediumWorkspace(medium)),
            ]).Fill();

    private TabPanelWidget BuildCompactWorkspace<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.TabPanel(tabs =>
        [
            tabs.Tab(AppMessages.WorkspaceSectionChanges, content => [BuildChangesPane(content)])
                .Selected(_workspaceRegion == 0),
            tabs.Tab(AppMessages.WorkspaceSectionDiff, content => [BuildDiffPane(content)])
                .Selected(_workspaceRegion == 1),
            tabs.Tab(AppMessages.WorkspaceSectionCommit, content => [BuildCommitPane(content)])
                .Selected(_workspaceRegion == 2),
        ])
        .Compact()
        .OnSelectionChanged(eventArgs => SelectWorkspaceRegion(eventArgs.SelectedIndex))
        .Fill();

    private ResponsiveWidget BuildMediumWorkspace<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.When(
                static (_, height) => height >= 44,
                spacious => BuildMediumWorkspace(spacious, changesRows: 17)),
            responsive.When(
                static (_, height) => height >= 32,
                comfortable => BuildMediumWorkspace(comfortable, changesRows: 12)),
            responsive.When(
                static (_, height) => height >= 24,
                comfortable => BuildMediumWorkspace(comfortable, changesRows: 9)),
            responsive.Otherwise(compact => BuildMediumWorkspace(compact, changesRows: 6)),
        ]).Fill();

    private SplitterWidget BuildMediumWorkspace<TParent>(
        WidgetContext<TParent> context,
        int changesRows)
        where TParent : Hex1bWidget
        => context.VSplitter(
            BuildChangesPane(context),
            BuildDetailPane(context),
            changesRows).Fill();

    private void CycleWorkspaceRegion()
    {
        var regionCount = IsResolutionOnlyMode ? 2 : 3;
        SelectWorkspaceRegion((_workspaceRegion + 1) % regionCount);
    }

    private void FocusChangedPathFilter()
    {
        _workspaceRegion = 0;
        _application?.RequestFocus(node =>
            node is TextBoxNode textBox &&
            ReferenceEquals(textBox.SourceWidget, _changedPathFilterWidget));
        _application?.Invalidate();
    }

    private void FocusDiffSearch()
    {
        _workspaceRegion = 1;
        _isDiffSearchVisible = true;
        _diffSearchWidget = null;
        _application?.RequestFocus(node =>
            node is TextBoxNode textBox &&
            ReferenceEquals(textBox.SourceWidget, _diffSearchWidget));
        _application?.Invalidate();
    }

    private void HideDiffSearch()
    {
        _isDiffSearchVisible = false;
        _diffSearchWidget = null;
        _application?.RequestFocus(node =>
            node is EditorNode editor &&
            ReferenceEquals(editor.State, _workspace.Diff.Editor));
        _application?.Invalidate();
    }

    private async Task FindDiffTextAsync(
        InputBindingActionContext actionContext,
        bool reverse)
    {
        if (!_workspace.Diff.Find(reverse))
        {
            actionContext.Invalidate();
            return;
        }

        _workspaceRegion = 1;
        var editor = actionContext.Focusables
            .OfType<EditorNode>()
            .FirstOrDefault(node => ReferenceEquals(node.State, _workspace.Diff.Editor));
        if (editor is not null)
        {
            var cursors = editor.State.Cursors.Snapshot();
            await ExecuteEditorActionAsync(
                editor,
                actionContext,
                EditorWidget.MoveToLineStart).ConfigureAwait(false);
            editor.State.Cursors.Restore(cursors);
        }

        _application?.RequestFocus(node =>
            node is EditorNode candidate &&
            ReferenceEquals(candidate.State, _workspace.Diff.Editor));
        actionContext.Invalidate();
    }

    private static async Task ExecuteEditorActionAsync(
        EditorNode editor,
        InputBindingActionContext actionContext,
        ActionId actionId)
    {
        var bindings = new InputBindingsBuilder();
        editor.ConfigureDefaultBindings(bindings);
        var keyBinding = bindings.GetBindings(actionId).SingleOrDefault();
        if (keyBinding is not null)
        {
            await keyBinding.ExecuteAsync(actionContext).ConfigureAwait(false);
            return;
        }

        var mouseBinding = bindings.MouseBindings
            .Single(binding => binding.ActionId == actionId);
        await mouseBinding.ExecuteAsync(actionContext).ConfigureAwait(false);
    }

    private Task RequestDestinationAsync(RepositoryWorkspaceDestination destination)
    {
        _workspace.RequestDestination(destination);
        _application?.RequestStop();
        return Task.CompletedTask;
    }

    private void SelectWorkspaceRegion(int region)
    {
        var maximum = IsResolutionOnlyMode ? 1 : 2;
        _workspaceRegion = Math.Clamp(region, 0, maximum);
        switch (_workspaceRegion)
        {
            case 0:
                _commandEditor = null;
                _application?.RequestFocus(node =>
                    node is ListNode<StatusWorkspaceItem> list && IsActiveStatusList(list));
                break;
            case 1:
                _commandEditor = _workspace.Diff.Editor;
                _application?.RequestFocus(node =>
                    node is EditorNode editor && ReferenceEquals(editor.State, _workspace.Diff.Editor));
                break;
            case 2:
                _commandEditor = _workspace.CommitMessage.Editor;
                _application?.RequestFocus(node =>
                    node is EditorNode editor && ReferenceEquals(editor.State, _workspace.CommitMessage.Editor));
                break;
        }

        _application?.Invalidate();
    }

    private void RememberFocusedWorkspaceEditor()
    {
        if (_application?.FocusedNode is not EditorNode editor)
        {
            return;
        }

        if (ReferenceEquals(editor.State, _workspace.Diff.Editor) ||
            ReferenceEquals(editor.State, _workspace.CommitMessage.Editor))
        {
            _commandEditor = editor.State;
        }
    }

    private void CopyEditorSelection(EditorState editor)
    {
        var selections = editor.Cursors
            .Where(static cursor => cursor.HasSelection)
            .Select(cursor => editor.Document.GetText(cursor.SelectionRange));
        _application?.CopyToClipboard(string.Join('\n', selections));
    }

    private void MutateEditor(EditorState editor, Action<EditorState> mutation)
    {
        mutation(editor);
        _application?.Invalidate();
    }

    private bool IsActiveStatusList(ListNode<StatusWorkspaceItem> list)
    {
        var expected = _workspace.State.ActivePane == StatusWorkspacePane.Staged
            ? _workspace.State.StagedItems
            : _workspace.State.UnstagedItems;
        if (list.Items.Count != expected.Length)
        {
            return false;
        }

        return expected.IsEmpty || ReferenceEquals(list.Items[0], expected[0]);
    }

    private void OpenPopup(WindowManager windows, WindowHandle popup, Action? onClose = null)
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
                SelectWorkspaceRegion(_workspaceRegion);
            }

            onClose?.Invoke();
        });
        popup.Open(windows);
    }

    private void CloseActivePopup()
    {
        if (_popupWindowManager is { } windows)
        {
            ClosePopupOnBackgroundClick(windows);
        }
    }

    private void ClosePopupOnBackgroundClick(WindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        for (var index = _popupWindows.Count - 1; index >= 0; index--)
        {
            var popup = _popupWindows[index];
            if (windows.Get(popup) is not null)
            {
                windows.Close(popup);
                return;
            }
        }
    }

    private static TWidget DismissOnEscape<TWidget>(TWidget widget, WindowHandle window)
        where TWidget : Hex1bWidget
        => widget.InputBindings(bindings =>
        {
            bindings.Remove(Hex1bKey.Escape);
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Cancel(),
                "Close the active window");
        });

    private ResponsiveWidget BuildHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(180, wide => BuildWideHeader(wide)),
            responsive.WhenMinWidth(120, medium => BuildMediumHeader(medium)),
            responsive.Otherwise(compact => BuildCompactHeader(compact)),
        ]);

    private HStackWidget BuildWideHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var snapshot = _workspace.State.Snapshot;
        var branch = CreateBranchHeader(snapshot, 48);
        var repository = RepositoryLabel.Create(snapshot.Repository);
        return context.HStack(header =>
        [
            header.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section(_mode.ToString().ToLowerInvariant()),
                info.Section(branch),
                info.Spacer(),
                info.Section(repository),
                info.Section(CreateVersionHeader(40)),
            ]).Divider(" | ").FillWidth(),
            BuildHeaderActions(header),
        ]).FillWidth();
    }

    private HStackWidget BuildMediumHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var snapshot = _workspace.State.Snapshot;
        return context.HStack(header =>
        [
            header.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section(CreateBranchHeader(snapshot, 24)),
                info.Spacer(),
                info.Section(CreateVersionHeader(32)),
            ]).Divider(" | ").FillWidth(),
            BuildHeaderActions(header),
        ]).FillWidth();
    }

    private VStackWidget BuildCompactHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var snapshot = _workspace.State.Snapshot;
        return context.VStack(header =>
        [
            header.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section(CreateBranchHeader(snapshot, 12)),
                info.Spacer(),
                info.Section(CreateVersionHeader(32)),
            ]).Divider(" | ").FillWidth(),
            BuildHeaderActions(header),
        ]).FillWidth();
    }

    private HStackWidget BuildHeaderActions<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => IsResolutionOnlyMode
            ? context.HStack(actions =>
            [
                actions.Text(_mode == ApplicationMode.Rebase
                    ? AppMessages.WorkspaceStatusResolveAndReturn
                    : AppMessages.WorkspaceStatusResolveUnmerged),
                ApplicationTrace.IsEnabled
                    ? actions.Button("Trace").OnClick(eventArgs => ShowTrace(eventArgs.Windows))
                    : actions.Text(string.Empty),
            ])
            : context.HStack(actions =>
        [
            actions.Button(AppMessages.WorkspaceActionMenu).OnClick(
                eventArgs => ShowApplicationMenu(eventArgs.Windows)),
            actions.Text(" "),
            actions.Button(AppMessages.WorkspaceActionCommands).OnClick(
                eventArgs => ShowCommandPalette(eventArgs.Windows)),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text($"{AppMessages.WorkspaceActionBranches}  " +
                    $"{AppMessages.WorkspaceActionRemotes}  {AppMessages.WorkspaceActionStashes}")
                : actions.HStack(repositoryActions =>
                [
                    repositoryActions.Button(AppMessages.WorkspaceActionBranches).OnClick(
                        eventArgs => ShowBranchesAsync(eventArgs.Windows)),
                    repositoryActions.Text(" "),
                    repositoryActions.Button(AppMessages.WorkspaceActionRemotes).OnClick(
                        eventArgs => ShowRemotesAsync(eventArgs.Windows)),
                    repositoryActions.Text(" "),
                    repositoryActions.Button(AppMessages.WorkspaceActionStashes).OnClick(
                        eventArgs => ShowStashesAsync(eventArgs.Windows)),
                ]),
            ApplicationTrace.IsEnabled
                ? actions.HStack(traceActions =>
                [
                    traceActions.Text(" "),
                    traceActions.Button("Trace").OnClick(eventArgs => ShowTrace(eventArgs.Windows)),
                ])
                : actions.Text(string.Empty),
        ]);

    private static string CreateBranchHeader(RepositoryStatusSnapshot snapshot, int maximumRunes)
    {
        var branch = snapshot.HeadName?.DisplayText ??
            (snapshot.HeadObjectId is null ? "unborn" : "detached");
        var tracking = snapshot.UpstreamName is null
            ? string.Empty
            : $" | {snapshot.UpstreamName.DisplayText} +{snapshot.AheadCount}/-{snapshot.BehindCount}";
        return ShortenHeaderText(branch + tracking, maximumRunes);
    }

    private string CreateVersionHeader(int maximumRunes)
        => ShortenHeaderText($"Git {_workspace.Installation.Version}", maximumRunes);

    private static string ShortenHeaderText(string text, int maximumRunes)
    {
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length <= maximumRunes)
        {
            return text;
        }

        var builder = new StringBuilder(maximumRunes + 1);
        for (var index = 0; index < maximumRunes - 1; index++)
        {
            builder.Append(runes[index]);
        }

        return builder.Append('…').ToString();
    }

    private VStackWidget BuildChangesPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(changes =>
        [
            changes.HStack(filter =>
            [
                filter.Text($"{AppMessages.WorkspaceLabelFind} "),
                BuildChangedPathFilter(filter),
            ]).FillWidth(),
            _mode == ApplicationMode.Merge
                ? BuildUnstagedPane(changes)
                : changes.VSplitter(
                    BuildUnstagedPane(changes),
                    BuildStagedPane(changes),
                    9).Fill(),
        ]).Fill();

    private TextBoxWidget BuildChangedPathFilter<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var filter = context.TextBox()
            .State(_workspace.State.Filter)
            .OnTextChanged(eventArgs => _workspace.FilterChangedPathsAsync(
                eventArgs.NewText,
                _cancellationToken))
            .OnSubmit(_ => SelectWorkspaceRegion(0))
            .FillWidth();
        _changedPathFilterWidget = filter;
        return filter;
    }

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
            .OnItemActivated(eventArgs =>
                state.ActivePane == StatusWorkspacePane.Unstaged
                    ? Task.CompletedTask
                    : _workspace.FocusUnstagedAsync(
                        eventArgs.ActivatedIndex,
                        _cancellationToken))
            .OnSelectionChanged(eventArgs =>
            {
                state.SetUnstagedSelection(eventArgs.SelectedIndices, eventArgs.ToggledIndex);
                _application?.Invalidate();
            })
            .Empty(empty => empty.Text(_mode == ApplicationMode.Merge
                ? state.IsFilterActive
                    ? "No unmerged path matches the filter."
                    : "No unmerged paths match this request."
                : state.IsFilterActive
                    ? "No unstaged path matches the filter."
                    : AppMessages.WorkspaceStatusClean))
            .InputBindings(bindings =>
            {
                ConfigureClampedListNavigation(
                    bindings,
                    state.UnstagedItems.Length,
                    () => state.UnstagedFocusedIndex,
                    (index, cancellationToken) => _workspace.FocusUnstagedAsync(
                        index,
                        cancellationToken),
                    state.ExtendUnstagedSelection);
                ConfigureDiffContextCharacterBindings(bindings);
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
            .Title(_mode == ApplicationMode.Merge
                ? CreateFilteredCountTitle(
                    "Unmerged",
                    state.UnstagedItems.Length,
                    state.UnstagedTotalCount,
                    state.IsFilterActive)
                : CreateFilteredCountTitle(
                    AppMessages.WorkspaceSectionUnstaged,
                    state.UnstagedItems.Length,
                    state.UnstagedTotalCount,
                    state.IsFilterActive))
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
            .OnItemActivated(eventArgs =>
                state.ActivePane == StatusWorkspacePane.Staged
                    ? Task.CompletedTask
                    : _workspace.FocusStagedAsync(
                        eventArgs.ActivatedIndex,
                        _cancellationToken))
            .OnSelectionChanged(eventArgs =>
            {
                state.SetStagedSelection(eventArgs.SelectedIndices, eventArgs.ToggledIndex);
                _application?.Invalidate();
            })
            .Empty(empty => empty.Text(state.IsFilterActive
                ? "No staged path matches the filter."
                : AppMessages.WorkspaceStatusNoStagedChanges))
            .InputBindings(bindings =>
            {
                ConfigureClampedListNavigation(
                    bindings,
                    state.StagedItems.Length,
                    () => state.StagedFocusedIndex,
                    (index, cancellationToken) => _workspace.FocusStagedAsync(
                        index,
                        cancellationToken),
                    state.ExtendStagedSelection);
                ConfigureDiffContextCharacterBindings(bindings);
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
            .Title(CreateFilteredCountTitle(
                AppMessages.WorkspaceSectionStaged,
                state.StagedItems.Length,
                state.StagedTotalCount,
                state.IsFilterActive))
            .Fill();
    }

    private static string CreateFilteredCountTitle(
        string label,
        int visibleCount,
        int totalCount,
        bool filterActive)
        => filterActive
            ? $"{label} ({visibleCount}/{totalCount})"
            : $"{label} ({totalCount})";

    private Hex1bWidget BuildDetailPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _mode == ApplicationMode.Merge
            ? BuildDiffPane(context)
            : context.Responsive(responsive =>
        [
            responsive.When(
                static (_, height) => height >= 144,
                veryTall => BuildDetailLayout(veryTall, diffRows: 96)),
            responsive.When(
                static (_, height) => height >= 108,
                veryTall => BuildDetailLayout(veryTall, diffRows: 72)),
            responsive.When(
                static (_, height) => height >= 84,
                tall => BuildDetailLayout(tall, diffRows: 56)),
            responsive.When(
                static (_, height) => height >= 64,
                tall => BuildDetailLayout(tall, diffRows: 44)),
            responsive.When(
                static (_, height) => height >= 48,
                spacious => BuildDetailLayout(spacious, diffRows: 33)),
            responsive.When(
                static (_, height) => height >= 36,
                spacious => BuildDetailLayout(spacious, diffRows: 25)),
            responsive.When(
                static (_, height) => height >= 28,
                comfortable => BuildDetailLayout(comfortable, diffRows: 19)),
            responsive.When(
                static (_, height) => height >= 20,
                comfortable => BuildDetailLayout(comfortable, diffRows: 14)),
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
            .WordWrap(false);
        if (_workspace.Diff.DecorationProvider is { } decorationProvider)
        {
            editor = editor.Decorations(decorationProvider);
        }

        editor = editor.InputBindings(bindings =>
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
                    RemoveCharacterBindings(bindings);
                    bindings.Ctrl().Key(Hex1bKey.Z).Action(
                        _ => _workspace.UndoRevertAsync(_cancellationToken),
                        "Undo the most recent eligible worktree revert");
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
                    if (!IsResolutionOnlyMode)
                    {
                        bindings.Key(Hex1bKey.F9).Action(
                            actionContext => ShowStashesAsync(actionContext.Windows),
                            "Open the searchable stash and patch window");
                        bindings.Key(Hex1bKey.R).Action(
                            actionContext => ShowRevertConfirmation(actionContext.Windows),
                            "Choose and confirm an exact worktree revert scope");
                        bindings.Shift().Key(Hex1bKey.R).Action(
                            actionContext => ShowRevertConfirmation(actionContext.Windows),
                            "Choose and confirm an exact worktree revert scope");
                    }
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
                    ConfigureDiffContextCharacterBindings(bindings);
                }

                bindings.Key(Hex1bKey.F5).Action(
                    _ => _workspace.RefreshAsync(_cancellationToken),
                    "Refresh repository status");
                if (!IsResolutionOnlyMode && !_workspace.IsConflictResolutionActive)
                {
                    bindings.Key(Hex1bKey.P).Action(
                        _ => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken),
                        "Prepare the focused untracked path for hunk and line staging");
                }

                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            });
        return context.Border(context.VStack(diff => BuildDiffPaneContent(diff, editor)).Fill())
            .Title(GetDiffPaneTitle())
            .Fill();
    }

    private void ConfigureDiffContextCharacterBindings(InputBindingsBuilder bindings)
    {
        bindings.Character(static text => text == "[").Action(
            (_, _) => _workspace.DecreaseDiffContextAsync(_cancellationToken),
            "Show less diff context");
        bindings.Character(static text => text == "]").Action(
            (_, _) => _workspace.IncreaseDiffContextAsync(_cancellationToken),
            "Show more diff context");
    }

    private static void RemoveCharacterBindings(InputBindingsBuilder bindings)
    {
        var keyBindings = bindings.Bindings.ToArray();
        var mouseBindings = bindings.MouseBindings.ToArray();
        var dragBindings = bindings.DragBindings.ToArray();
        bindings.RemoveAll();
        foreach (var binding in keyBindings)
        {
            bindings.Add(binding);
        }

        foreach (var binding in mouseBindings)
        {
            bindings.Add(binding);
        }

        foreach (var binding in dragBindings)
        {
            bindings.Add(binding);
        }
    }

    private Hex1bWidget[] BuildDiffPaneContent<TParent>(
        WidgetContext<TParent> context,
        EditorWidget editor)
        where TParent : Hex1bWidget
    {
        var content = new List<Hex1bWidget>();
        if (_isDiffSearchVisible)
        {
            content.Add(context.HStack(search =>
            [
                search.Text($"{AppMessages.DiffActionText}: "),
                BuildDiffSearch(search),
                search.Text(" "),
                search.Text(_workspace.Diff.SearchStatus).FixedWidth(10),
                search.Text(" "),
                search.Button(AppMessages.DiffActionPreviousShort).OnClick(
                    eventArgs => FindDiffTextAsync(eventArgs.Context, reverse: true)),
                search.Text(" "),
                search.Button(AppMessages.DiffActionNext).OnClick(
                    eventArgs => FindDiffTextAsync(eventArgs.Context, reverse: false)),
                search.Text(" "),
                search.Button(AppMessages.DiffActionHide).OnClick(_ => Complete(HideDiffSearch)),
            ]).FillWidth());
        }

        content.Add(editor.Fill());
        return [.. content];
    }

    private TextBoxWidget BuildDiffSearch<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var search = context.TextBox()
            .State(_workspace.Diff.Search)
            .InputBindings(bindings =>
            {
                bindings.Remove(Hex1bKey.Escape);
                bindings.Key(Hex1bKey.Escape).Action(
                    _ => HideDiffSearch(),
                    AppMessages.DiffBindingHideInput);
            })
            .OnTextChanged(eventArgs =>
            {
                _workspace.Diff.SetSearch(eventArgs.NewText);
                _application?.Invalidate();
            })
            .OnSubmit(eventArgs => FindDiffTextAsync(eventArgs.Context, reverse: false))
            .FillWidth();
        _diffSearchWidget = search;
        return search;
    }

    private BorderWidget BuildCommitPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var editor = context.Editor(_workspace.CommitMessage.Editor)
            .WordWrap(ShouldWrapCommitMessage())
            .Decorations(_workspace.CommitMessage.Spelling.DecorationProvider)
            .InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.F4).Action(
                    actionContext => IsResolutionOnlyMode
                        ? Complete(actionContext.RequestStop)
                        : RunPrimaryActionAsync(actionContext.Windows),
                    IsResolutionOnlyMode
                        ? GetResolutionExitDescription()
                        : GetPrimaryActionDescription());
                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
                bindings.Shift().Key(Hex1bKey.F7).Action(
                    actionContext => ShowNextSpellingSuggestion(actionContext.Windows),
                    "Show the next possible misspelling");
                bindings.Mouse(MouseButton.Right).Action(
                    ShowSpellingSuggestionAtPointerAsync,
                    "Show spelling suggestions at the pointer");
            });
        return context.Border(context.VStack(builder => BuildCommitPaneContent(builder, editor)).Fill())
            .Title(AppMessages.WorkspaceSectionCommitMessage)
            .Fill();
    }

    private bool ShouldWrapCommitMessage()
        => _workspace.Configuration.Resolve(
            "gitsail.wrapcommitmessage",
            GitConfigurationScope.Local).EffectiveParsedValue?.BooleanValue ?? false;

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

        if (ShouldShowPersistentPushAction())
        {
            content.Add(BuildPersistentPushAction(context));
        }

        return [.. content];
    }

    private async Task ShowSpellingSuggestionAtPointerAsync(
        InputBindingActionContext actionContext)
    {
        var editor = actionContext.Focusables
            .OfType<EditorNode>()
            .FirstOrDefault(node => ReferenceEquals(
                node.State,
                _workspace.CommitMessage.Editor));
        if (editor is not null)
        {
            actionContext.Focus(editor);
            await ExecuteEditorActionAsync(
                editor,
                actionContext,
                EditorWidget.Click).ConfigureAwait(false);
        }

        var issue = FindSpellingIssueAtCursor(findNext: false);
        if (issue is null)
        {
            ShowSpellingStatus(actionContext.Windows);
            return;
        }

        ShowSpellingSuggestion(actionContext.Windows, issue);
    }

    private void ShowNextSpellingSuggestion(WindowManager windows)
    {
        var issue = FindSpellingIssueAtCursor(findNext: true);
        if (issue is null)
        {
            ShowSpellingStatus(windows);
            return;
        }

        _workspace.CommitMessage.Editor.SetCursorPosition(new DocumentOffset(issue.Offset));
        _application?.RequestFocus(node =>
            node is EditorNode editor &&
            ReferenceEquals(editor.State, _workspace.CommitMessage.Editor));
        ShowSpellingSuggestion(windows, issue);
    }

    private SpellingIssue? FindSpellingIssueAtCursor(bool findNext)
    {
        var issues = _workspace.CommitMessage.Spelling.Issues;
        if (issues.IsEmpty)
        {
            return null;
        }

        var offset = _workspace.CommitMessage.Editor.Cursor.Position.Value;
        var current = issues.FirstOrDefault(issue =>
            offset >= issue.Offset && offset < issue.Offset + issue.Length);
        if (current is not null || !findNext)
        {
            return current;
        }

        return issues.FirstOrDefault(issue => issue.Offset > offset) ?? issues[0];
    }

    private void ShowSpellingStatus(WindowManager windows)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var spelling = _workspace.CommitMessage.Spelling;
            var content = new List<Hex1bWidget>
            {
                builder.Text(spelling.StatusText).Wrap(),
                builder.Text($"Possible misspellings: {spelling.Issues.Length}"),
                builder.WrapPanel(actions =>
                {
                    var buttons = new List<Hex1bWidget>
                    {
                        actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                        actions.Button(spelling.IsChecking ? "Check again" : "Check now").OnClick(
                            _ => _workspace.CheckSpellingAsync()),
                    };
                    if (!spelling.Issues.IsEmpty)
                    {
                        buttons.Add(actions.Button("Review next").OnClick(_ =>
                        {
                            window.Window.CloseWithResult("review");
                            ShowNextSpellingSuggestion(windows);
                        }));
                    }

                    return [.. buttons];
                }),
                builder.Text("Shift+F7 reviews | Right-click a marked word | Esc/click outside closes"),
            };
            return [.. content];
        }).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close commit spelling status");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close commit spelling status");
        }))
        .Title("Commit message spelling")
        .Size(_popupViewport.FitWidth(72), _popupViewport.FitHeight(10))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 10, 110, 24));
    }

    private void ShowSpellingSuggestion(WindowManager windows, SpellingIssue issue)
    {
        var safeWord = TerminalTextSanitizer.Sanitize(issue.Word);
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var content = new List<Hex1bWidget>
            {
                builder.Text($"Possible misspelling: {safeWord}").Wrap(),
            };
            if (issue.Suggestions.IsEmpty)
            {
                content.Add(builder.Text("No replacement suggestions were returned."));
            }
            else
            {
                content.Add(builder.Border(DismissOnEscape(
                    builder.List(issue.Suggestions)
                        .ItemKey(static suggestion => suggestion)
                        .OnItemActivated(eventArgs => ReplaceSpellingSuggestion(
                            window.Window,
                            issue,
                            eventArgs.ActivatedItem))
                        .Fill(),
                    window.Window))
                    .Title("Replacements")
                    .Fill());
            }

            content.Add(builder.WrapPanel(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                actions.Button("Check again").OnClick(_ =>
                {
                    window.Window.Cancel();
                    return _workspace.CheckSpellingAsync();
                }),
            ]));
            content.Add(builder.Text("Enter/mouse replaces | Esc/click outside closes"));
            return [.. content];
        }).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close spelling suggestions");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close spelling suggestions");
        }))
        .Title($"Spelling: {safeWord}")
        .Size(
            _popupViewport.FitWidth(58),
            _popupViewport.FitHeight(9 + Math.Min(issue.Suggestions.Length, 8)))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(46, 9, 96, 28));
    }

    private Task ReplaceSpellingSuggestion(
        WindowHandle window,
        SpellingIssue issue,
        string replacement)
    {
        if (_workspace.CommitMessage.TryReplaceSpellingIssue(issue, replacement))
        {
            window.CloseWithResult(replacement);
        }
        else
        {
            window.Cancel();
            _ = _workspace.CheckSpellingAsync();
        }

        _application?.Invalidate();
        return Task.CompletedTask;
    }

    private bool ShouldShowPersistentPushAction()
        => _mode == ApplicationMode.Gui &&
            (_workspace.Configuration.Resolve(
                "gitsail.showpushaction",
                GitConfigurationScope.Local).EffectiveParsedValue?.BooleanValue ?? false);

    private HStackWidget BuildPersistentPushAction<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            actions.Text($"{AppMessages.WorkspaceActionRemotes}: "),
            actions.Button(AppMessages.WorkspaceActionPush).OnClick(
                eventArgs => ShowPersistentPushActionAsync(eventArgs.Windows)),
        ]).FillWidth();

    private Hex1bWidget BuildCommitOptionsBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var options = _workspace.CommitOptions;
        if (!options.IsExpanded)
        {
            return context.HStack(builder =>
            [
                builder.Button(AppMessages.WorkspaceActionOptions).OnClick(_ => ToggleCommitOptions()),
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
            context.Button(AppMessages.WorkspaceActionOptions).OnClick(_ => ToggleCommitOptions()),
            context.Button($"Amend [{FormatToggle(options.Amend)}]")
                .OnClick(_ => ToggleAmendAsync()),
            context.Button($"Signoff [{FormatToggle(options.Signoff)}]")
                .OnClick(_ => ToggleSignoff()),
            context.Button($"Sign [{FormatToggle(options.SignCommit)}]")
                .OnClick(_ => ToggleSignCommit()),
            context.Button($"Cleanup: {FormatCleanupMode(options.CleanupMode)}")
                .OnClick(_ => CycleCleanupMode()),
        };
        if (!IsResolutionOnlyMode &&
            _options.Citool?.NoCommit != true &&
            _workspace.CanCommit)
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

    private Hex1bWidget BuildActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _workspace.IsConflictResolutionActive
            ? context.Responsive(responsive =>
            [
                responsive.WhenMinWidth(300, wide => BuildFullConflictActionBar(wide)),
                responsive.Otherwise(compact => BuildCompactConflictActionBar(compact)),
            ])
            : _mode == ApplicationMode.Rebase
                ? context.Responsive(responsive =>
                [
                    responsive.WhenMinWidth(76, wide => BuildRebaseWorkspaceActionBar(wide, compact: false)),
                    responsive.Otherwise(compact => BuildRebaseWorkspaceActionBar(compact, compact: true)),
                ])
                : _mode == ApplicationMode.Merge
                    ? BuildMergeModeActionBar(context)
                : ShouldShowCleanActionBar()
                    ? BuildCleanActionBar(context)
                : context.Responsive(responsive =>
            [
                responsive.WhenMinWidth(300, wide => BuildFullActionBar(wide)),
                responsive.Otherwise(compact => BuildCompactActionBar(compact)),
            ]);

    private HStackWidget BuildCleanActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            actions.Text(AppMessages.WorkspaceStatusClean),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text(AppMessages.WorkspaceStatusRefreshing)
                : actions.Button(AppMessages.WorkspaceActionRefresh).OnClick(
                    _ => _workspace.RefreshAsync(_cancellationToken)),
            actions.Text(" "),
            actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private bool ShouldShowCleanActionBar()
        => _mode == ApplicationMode.Gui &&
            !_workspace.IsConflictResolutionActive &&
            _workspace.State.UnstagedTotalCount == 0 &&
            _workspace.State.StagedTotalCount == 0 &&
            !CanRunPrimaryAction();

    private HStackWidget BuildRebaseWorkspaceActionBar<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            actions.Button(compact
                ? AppMessages.WorkspaceActionReturn
                : AppMessages.WorkspaceActionReturnToRebase).OnClick(
                eventArgs => eventArgs.Context.RequestStop()),
            actions.Text(" "),
            !CanStagePaths()
                ? actions.Text(compact ? " S " : AppMessages.WorkspaceActionStage)
                : actions.Button(compact ? "S" : AppMessages.WorkspaceActionStage).OnClick(
                    _ => _workspace.StageAsync(_cancellationToken)),
            actions.Text(" "),
            !CanUnstagePaths()
                ? actions.Text(compact ? " U " : AppMessages.WorkspaceActionUnstage)
                : actions.Button(compact ? "U" : AppMessages.WorkspaceActionUnstage).OnClick(
                    _ => _workspace.UnstageAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text(AppMessages.WorkspaceStatusRefreshing)
                : actions.Button(AppMessages.WorkspaceActionRefresh).OnClick(
                    _ => _workspace.RefreshAsync(_cancellationToken)),
        ]).FillWidth();

    private HStackWidget BuildMergeModeActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            actions.Text(_workspace.State.UnstagedTotalCount == 0
                ? AppMessages.WorkspaceStatusNoUnresolvedPaths
                : AppMessages.WorkspacePromptSelectUnmergedPath),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text(AppMessages.WorkspaceStatusRefreshing)
                : actions.Button(AppMessages.WorkspaceActionRefresh).OnClick(
                    _ => _workspace.RefreshAsync(_cancellationToken)),
            actions.Text(" "),
            actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private HStackWidget BuildFullConflictActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            actions.Text($"{AppMessages.WorkspaceLabelResolved} " +
                $"{_workspace.ResolvedConflictChunkCount}/{_workspace.ConflictChunkCount}"),
            actions.Text(" "),
            BuildAbortMergeAction(actions, compact: false),
            actions.Text(" "),
            BuildConflictChoiceAction(
                actions,
                AppMessages.WorkspaceActionUseOurs,
                ConflictResolutionChoice.Ours),
            actions.Text(" "),
            BuildConflictChoiceAction(
                actions,
                AppMessages.WorkspaceActionUseTheirs,
                ConflictResolutionChoice.Theirs),
            actions.Text(" "),
            BuildConflictChoiceAction(
                actions,
                AppMessages.WorkspaceActionUseBase,
                ConflictResolutionChoice.Base),
            actions.Text(" "),
            BuildConflictChoiceAction(
                actions,
                AppMessages.WorkspaceActionUseBoth,
                ConflictResolutionChoice.Both),
            actions.Text(" "),
            !_workspace.IsBusy && _workspace.ResolvedConflictChunkCount < _workspace.ConflictChunkCount
                ? actions.Button(AppMessages.WorkspaceActionNextConflict).OnClick(
                    _ => _workspace.FocusNextUnresolvedConflictAsync())
                : actions.Text(AppMessages.WorkspaceStatusAllMarkersResolved),
            actions.Text(" "),
            _workspace.CanToggleConflictExecutable
                ? actions.Button(GetConflictModeLabel()).OnClick(_ => _workspace.ToggleConflictExecutableAsync())
                : actions.Text(AppMessages.WorkspaceLabelMode),
            actions.Text(" "),
            _workspace.CanStageConflictResolution
                ? actions.Button(AppMessages.WorkspaceActionStageResolution).OnClick(
                    _ => _workspace.StageConflictResolutionAsync(_cancellationToken))
                : actions.Text(AppMessages.WorkspaceActionStageResolution),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text(AppMessages.WorkspaceStatusRefreshing)
                : actions.Button(AppMessages.WorkspaceActionRefresh).OnClick(
                    _ => _workspace.RefreshAsync(_cancellationToken)),
            actions.Text(" "),
            actions.Button(_mode == ApplicationMode.Rebase
                ? AppMessages.WorkspaceActionReturn
                : AppMessages.WorkspaceActionQuit).OnClick(
                eventArgs => eventArgs.Context.RequestStop()),
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
                : actions.Text(AppMessages.WorkspaceActionDone),
            actions.Text(" "),
            _workspace.CanToggleConflictExecutable
                ? actions.Button(_workspace.ConflictResultIsExecutable ? "755" : "644")
                    .OnClick(_ => _workspace.ToggleConflictExecutableAsync())
                : actions.Text("---"),
            actions.Text(" "),
            _workspace.CanStageConflictResolution
                ? actions.Button(AppMessages.WorkspaceActionStage).OnClick(
                    _ => _workspace.StageConflictResolutionAsync(_cancellationToken))
                : actions.Text(AppMessages.WorkspaceActionStage),
            actions.Text(" "),
            actions.Button(_mode == ApplicationMode.Rebase
                ? AppMessages.WorkspaceActionReturn
                : AppMessages.WorkspaceActionQuit).OnClick(
                eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private HStackWidget BuildFullActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            CanRunPrimaryAction()
                ? actions.Button(GetPrimaryActionLabel()).OnClick(
                    eventArgs => RunPrimaryActionAsync(eventArgs.Windows))
                : actions.Text(GetPrimaryActionLabel()),
            actions.Text(" "),
            BuildAbortMergeAction(actions, compact: false),
            actions.Text(" "),
            !CanStagePaths()
                ? actions.Text(AppMessages.WorkspaceActionStage)
                : actions.Button(AppMessages.WorkspaceActionStage).OnClick(
                    _ => _workspace.StageAsync(_cancellationToken)),
            actions.Text(" "),
            BuildPrepareUntrackedPatchAction(actions, compact: false),
            actions.Text(" "),
            !CanUnstagePaths()
                ? actions.Text(AppMessages.WorkspaceActionUnstage)
                : actions.Button(AppMessages.WorkspaceActionUnstage).OnClick(
                    _ => _workspace.UnstageAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.CanStageFocusedHunk
                ? actions.Button(AppMessages.WorkspaceActionStageHunk).OnClick(
                    _ => _workspace.StageFocusedHunkAsync(_cancellationToken))
                : _workspace.CanUnstageFocusedHunk
                    ? actions.Button(AppMessages.WorkspaceActionUnstageHunk).OnClick(
                        _ => _workspace.UnstageFocusedHunkAsync(_cancellationToken))
                    : actions.Text(AppMessages.WorkspaceActionHunk),
            actions.Text(" "),
            BuildSelectedLineAction(actions, compact: false),
            actions.Text(" "),
            BuildRevertAction(actions, compact: false),
            actions.Text(" "),
            BuildUndoRevertAction(actions, compact: false),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text(AppMessages.WorkspaceStatusRefreshing)
                : actions.Button(AppMessages.WorkspaceActionRefresh).OnClick(
                    _ => _workspace.RefreshAsync(_cancellationToken)),
            actions.Text(" "),
            !CanStageAll()
                ? actions.Text(AppMessages.WorkspaceActionStageAll)
                : actions.Button(AppMessages.WorkspaceActionStageAll).OnClick(
                    _ => _workspace.StageAllAsync(_cancellationToken)),
            actions.Text(" "),
            !CanUnstageAll()
                ? actions.Text(AppMessages.WorkspaceActionUnstageAll)
                : actions.Button(AppMessages.WorkspaceActionUnstageAll).OnClick(
                    _ => _workspace.UnstageAllAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.DiffContextLines == 0
                ? actions.Text(AppMessages.WorkspaceActionLessContext)
                : actions.Button(AppMessages.WorkspaceActionLessContext).OnClick(
                    _ => _workspace.DecreaseDiffContextAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text(AppMessages.WorkspaceActionMoreContext)
                : actions.Button(AppMessages.WorkspaceActionMoreContext).OnClick(
                    _ => _workspace.IncreaseDiffContextAsync(_cancellationToken)),
            actions.Text(" "),
            actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private HStackWidget BuildCompactActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            CanRunPrimaryAction()
                ? actions.Button(GetPrimaryActionLabel()).OnClick(
                    eventArgs => RunPrimaryActionAsync(eventArgs.Windows))
                : actions.Text($" {GetPrimaryActionLabel()} "),
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
                ? actions.Text($" {AppMessages.WorkspaceStatusRefreshing} ")
                : actions.Button(AppMessages.WorkspaceActionRefresh).OnClick(
                    _ => _workspace.RefreshAsync(_cancellationToken)),
            actions.Text(" "),
            actions.Button(AppMessages.WorkspaceActionQuit).OnClick(
                eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private ResponsiveWidget BuildShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _mode == ApplicationMode.Rebase
            ? context.Responsive(responsive =>
            [
                responsive.Otherwise(rebase => rebase.InfoBar(info =>
                [
                    info.Section($"F4 {AppMessages.WorkspaceActionReturnToRebase}"),
                    info.Section($"S {AppMessages.WorkspaceActionStage}"),
                    info.Section($"U {AppMessages.WorkspaceActionUnstage}"),
                    info.Section($"F5 {AppMessages.WorkspaceActionRefresh}"),
                    info.Spacer(),
                    info.Section(AppMessages.WorkspaceActionMouse),
                ]).Divider(" | ")),
            ])
            : _mode == ApplicationMode.Merge
                ? context.Responsive(responsive =>
                [
                    responsive.Otherwise(merge => merge.InfoBar(info => _workspace.IsConflictResolutionActive
                        ?
                        [
                            info.Section("F1"),
                            info.Section("Alt+O/T/B/A"),
                            info.Section("Alt+N"),
                            info.Section($"Alt+S {AppMessages.WorkspaceActionStage}"),
                            info.Spacer(),
                            info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
                        ]
                        :
                        [
                            info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
                            info.Section($"F5 {AppMessages.WorkspaceActionRefresh}"),
                            info.Spacer(),
                            info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
                            info.Section(AppMessages.WorkspaceActionMouse),
                        ]).Divider(" | ")),
                ])
            : context.Responsive(responsive =>
        [
            responsive.When(
                static (width, _) => width >= 180,
                roomy => _workspace.IsConflictResolutionActive
                    ? BuildRoomyConflictShortcutBar(roomy)
                    : BuildRoomyRepositoryShortcutBar(roomy)),
            responsive.WhenMinWidth(
                76,
                compact => _workspace.IsConflictResolutionActive
                    ? BuildCompactConflictShortcutBar(compact)
                    : BuildCompactRepositoryShortcutBar(compact)),
            responsive.Otherwise(narrow => _workspace.IsConflictResolutionActive
                ? BuildNarrowConflictShortcutBar(narrow)
                : BuildNarrowRepositoryShortcutBar(narrow)),
        ]);

    private VStackWidget BuildRoomyConflictShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(rows =>
        [
            rows.InfoBar(info =>
            [
                info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
                info.Section($"F2 {AppMessages.WorkspaceActionCommands}"),
                info.Section($"F10 {AppMessages.WorkspaceActionMenu}"),
                info.Section($"F5 {AppMessages.WorkspaceActionRefresh}"),
                info.Spacer(),
                info.Section(_workspace.Activity),
                info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
            ]).Divider(" | "),
            rows.InfoBar(info =>
            [
                info.Section($"Alt+O {AppMessages.WorkspaceActionUseOurs}"),
                info.Section($"Alt+T {AppMessages.WorkspaceActionUseTheirs}"),
                info.Section($"Alt+B {AppMessages.WorkspaceActionUseBase}"),
                info.Section($"Alt+A {AppMessages.WorkspaceActionUseBoth}"),
                info.Section($"Alt+N {AppMessages.WorkspaceActionNextConflict}"),
                info.Section($"Alt+X {AppMessages.WorkspaceActionToggleMode}"),
                info.Section($"Alt+S {AppMessages.WorkspaceActionStageResolution}"),
            ]).Divider(" | "),
            rows.InfoBar(info =>
            [
                info.Section("Ctrl+Z/Y Undo/redo"),
                info.Section(AppMessages.WorkspaceActionMouse),
            ]).Divider(" | "),
        ]);

    private VStackWidget BuildRoomyRepositoryShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(rows =>
        [
            rows.InfoBar(info =>
            [
                info.Section($"F4 {GetPrimaryActionLabel()}"),
                info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
                info.Section($"F2 {AppMessages.WorkspaceActionCommands}"),
                info.Section($"F10 {AppMessages.WorkspaceActionMenu}"),
                info.Section($"F8 {AppMessages.WorkspaceActionBranches}"),
                info.Section($"F9 {AppMessages.WorkspaceActionStashes}"),
                info.Section($"F5 {AppMessages.WorkspaceActionRefresh}"),
                info.Section($"F7 {AppMessages.WorkspaceActionPaths}"),
                info.Spacer(),
                info.Section(_workspace.Activity),
                info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
            ]).Divider(" | "),
            rows.InfoBar(info =>
            [
                info.Section($"S {AppMessages.WorkspaceActionStage}"),
                info.Section($"U {AppMessages.WorkspaceActionUnstage}"),
                info.Section($"A {AppMessages.WorkspaceActionStageAll}"),
                info.Section($"Shift+U {AppMessages.WorkspaceActionUnstageAll}"),
                info.Section($"Space {AppMessages.WorkspaceActionCheck}"),
                info.Section($"P {AppMessages.WorkspaceActionPrepareHunks}"),
            ]).Divider(" | "),
            rows.InfoBar(info =>
            [
                info.Section($"S/U {AppMessages.WorkspaceSectionDiff} {AppMessages.WorkspaceActionHunk}"),
                info.Section($"L {AppMessages.WorkspaceActionLines}"),
                info.Section($"R {AppMessages.WorkspaceActionRevert}"),
                info.Section($"Ctrl+Z {AppMessages.WorkspaceActionUndoRevert}"),
                info.Section("J/K Hunks"),
                info.Section("Ctrl+F/F3/N Find"),
                info.Section($"[/] {AppMessages.WorkspaceActionContext} ({_workspace.DiffContextLines})"),
                info.Section($"{AppMessages.WorkspaceActionMouse} {AppMessages.WorkspaceSectionDiff}"),
            ]).Divider(" | "),
        ]);

    private static InfoBarWidget BuildCompactConflictShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
            info.Section($"F2 {AppMessages.WorkspaceActionCommands}"),
            info.Section($"F10 {AppMessages.WorkspaceActionMenu}"),
            info.Section("Alt+O/T/B/A"),
            info.Section("Alt+N"),
            info.Section("Alt+S"),
            info.Section("Ctrl+Q"),
        ]).Divider(" | ");

    private InfoBarWidget BuildCompactRepositoryShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
            info.Section($"F2 {AppMessages.WorkspaceActionCommands}"),
            info.Section($"F4 {GetPrimaryActionLabel()}"),
            info.Section($"F5 {AppMessages.WorkspaceActionRefresh}"),
            info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
        ]).Divider(" | ");

    private static InfoBarWidget BuildNarrowConflictShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
            info.Section($"F2 {AppMessages.WorkspaceActionCommands}"),
            info.Section("Alt+O/T Choose"),
            info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
        ]).Divider(" | ");

    private InfoBarWidget BuildNarrowRepositoryShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section($"F1 {AppMessages.WorkspaceActionHelp}"),
            info.Section($"F2 {AppMessages.WorkspaceActionCommands}"),
            info.Section($"F4 {GetPrimaryActionLabel()}"),
            info.Section("S/U"),
            info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
        ]).Divider(" | ");

    private BorderWidget BuildResizeView<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        [
            builder.Text(AppMessages.WorkspaceResizeRequirement).Wrap(),
            builder.Text(AppMessages.WorkspaceResizeInstruction).Wrap(),
            builder.Text(IsResolutionOnlyMode
                ? AppMessages.WorkspaceResizeBindingsResolution
                : AppMessages.WorkspaceResizeBindingsNormal).Wrap(),
        ])).Title(AppMessages.WorkspaceResizeTitle).Fill();

    private void HandleWorkspaceChanged()
    {
        SynchronizeCredentialPromptWindow();
        SynchronizeExecutableCapabilityWindow();
        if (_mode == ApplicationMode.Citool && _workspace.IsCitoolCompleted)
        {
            _application?.RequestStop();
            return;
        }

        _application?.Invalidate();
    }

    private Task StartCredentialOperation(
        WindowManager windows,
        Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(operation);
        _credentialWindowManager = windows;
        _workspace.Operations.Start(
            "credential-transport",
            context => RunCredentialOperationAsync(
                windows,
                operation,
                context.CancellationToken),
            _cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunCredentialOperationAsync(
        WindowManager windows,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            OpenPopup(windows, windows.Window(window => window.VStack(builder =>
            [
                builder.Text(TerminalTextSanitizer.Sanitize(exception.Message)),
                builder.Button("Close").OnClick(_ => window.Window.Cancel()),
            ]))
            .Title("Transport operation failed")
            .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(10))
            .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
            .Resizable(58, 8, 110, 18)
            .Modal());
        }
        finally
        {
            if (_workspace.CredentialPrompts.Current is null)
            {
                _credentialWindowManager = null;
            }

            _application?.Invalidate();
        }
    }

    private void SynchronizeCredentialPromptWindow()
    {
        lock (_credentialPromptLock)
        {
            var request = _workspace.CredentialPrompts.Current;
            var windows = _credentialWindowManager;
            if (request is null)
            {
                if (_credentialPromptWindow is { } completedWindow &&
                    windows?.IsOpen(completedWindow) == true)
                {
                    windows.Close(completedWindow);
                }

                _credentialPromptWindow = null;
                _credentialPromptId = 0;
                return;
            }

            if (windows is null ||
                (_credentialPromptId == request.Id &&
                    _credentialPromptWindow is { } currentWindow &&
                    windows.IsOpen(currentWindow)))
            {
                return;
            }

            if (_credentialPromptWindow is { } previousWindow && windows.IsOpen(previousWindow))
            {
                windows.Close(previousWindow);
            }

            _credentialPromptWindow = OpenCredentialPromptWindow(windows, request);
            _credentialPromptId = request.Id;
        }
    }

    private WindowHandle OpenCredentialPromptWindow(
        WindowManager windows,
        CredentialPromptRequest request)
    {
        var visibleResponse = new TextBoxState();
        var secretResponse = new List<byte[]>();
        var secretCharacterCount = 0;
        var secretByteCount = 0;
        var submitted = false;
        var handle = windows.Window(window => window.VStack(builder =>
        {
            if (request.Kind == CredentialPromptKind.Confirmation)
            {
                return
                [
                    builder.Text($"Operation: {request.Operation}"),
                    builder.Text(request.Prompt).Wrap(),
                    builder.HStack(actions =>
                    [
                        actions.Button("No").OnClick(_ => SubmitConfirmation(accepted: false)),
                        actions.Text(" "),
                        actions.Button("Yes").OnClick(_ => SubmitConfirmation(accepted: true)),
                    ]),
                    builder.Text("No is focused first | Enter or mouse selects | Esc cancels"),
                ];
            }

            Hex1bWidget input;
            if (request.Kind == CredentialPromptKind.Text)
            {
                input = DismissOnEscape(
                    builder.TextBox()
                        .State(visibleResponse)
                        .OnSubmit(_ => SubmitText()),
                    window.Window)
                    .FillWidth();
            }
            else
            {
                var secretButton = builder.Button(
                        secretCharacterCount == 0
                            ? "Secret input: <empty>"
                            : $"Secret input: {new string('•', secretCharacterCount)}")
                    .OnClick(_ => SubmitSecret())
                    .InputBindings(bindings =>
                    {
                        bindings.AnyCharacter().Action(text =>
                        {
                            var bytes = s_strictUtf8.GetBytes(text);
                            if (secretCharacterCount + text.Length <= 16 * 1024 &&
                                secretByteCount + bytes.Length <= 64 * 1024)
                            {
                                secretResponse.Add(bytes);
                                secretCharacterCount += text.Length;
                                secretByteCount += bytes.Length;
                                _application?.Invalidate();
                            }
                            else
                            {
                                CryptographicOperations.ZeroMemory(bytes);
                            }
                        }, "Enter secret text without rendering it");
                        bindings.Key(Hex1bKey.Backspace).Action(() =>
                        {
                            if (secretResponse.Count > 0)
                            {
                                var removed = secretResponse[^1];
                                secretResponse.RemoveAt(secretResponse.Count - 1);
                                secretCharacterCount -= s_strictUtf8.GetCharCount(removed);
                                secretByteCount -= removed.Length;
                                CryptographicOperations.ZeroMemory(removed);
                                _application?.Invalidate();
                            }
                        }, "Remove the last secret character");
                    });
                input = builder.Pastable(secretButton)
                    .MaxSize(16 * 1024)
                    .Timeout(TimeSpan.FromSeconds(30))
                    .OnPaste(async eventArgs =>
                    {
                        var text = await eventArgs.Paste
                            .ReadToEndAsync(16 * 1024, _cancellationToken)
                            .ConfigureAwait(false);
                        var bytes = s_strictUtf8.GetBytes(text);
                        if (secretCharacterCount + text.Length <= 16 * 1024 &&
                            secretByteCount + bytes.Length <= 64 * 1024)
                        {
                            secretResponse.Add(bytes);
                            secretCharacterCount += text.Length;
                            secretByteCount += bytes.Length;
                            eventArgs.Paste.Invalidate();
                        }
                        else
                        {
                            CryptographicOperations.ZeroMemory(bytes);
                        }
                    });
            }

            return
            [
                builder.Text($"Operation: {request.Operation}"),
                builder.Text(request.Prompt).Wrap(),
                input,
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    actions.Button("Submit response").OnClick(_ =>
                    {
                        if (request.Kind == CredentialPromptKind.Text)
                        {
                            SubmitText();
                        }
                        else
                        {
                            SubmitSecret();
                        }
                    }),
                ]),
                builder.Text(request.Kind == CredentialPromptKind.Secret
                    ? "Characters are masked and never saved | Enter or mouse submits | Esc cancels"
                    : "Enter or mouse submits | Esc cancels"),
            ];

            void SubmitText()
            {
                submitted = _workspace.CredentialPrompts.Submit(request.Id, visibleResponse.Text);
                if (submitted)
                {
                    CloseWithResultIfOpen(windows, window.Window, "response");
                }
            }

            void SubmitSecret()
            {
                var response = new byte[secretByteCount];
                var offset = 0;
                foreach (var segment in secretResponse)
                {
                    segment.CopyTo(response, offset);
                    offset += segment.Length;
                }

                ClearSecretResponse();
                submitted = _workspace.CredentialPrompts.SubmitOwned(request.Id, response);

                if (submitted)
                {
                    CloseWithResultIfOpen(windows, window.Window, "response");
                }
            }

            void ClearSecretResponse()
            {
                foreach (var segment in secretResponse)
                {
                    CryptographicOperations.ZeroMemory(segment);
                }

                secretResponse.Clear();
                secretCharacterCount = 0;
                secretByteCount = 0;
            }

            void SubmitConfirmation(bool accepted)
            {
                submitted = _workspace.CredentialPrompts.Confirm(request.Id, accepted);
                if (submitted)
                {
                    CloseWithResultIfOpen(windows, window.Window, accepted ? "yes" : "no");
                }
            }
        }))
        .Title(request.Kind switch
        {
            CredentialPromptKind.Text => "Credential text required",
            CredentialPromptKind.Secret => "Credential secret required",
            CredentialPromptKind.Confirmation => "Transport confirmation required",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        })
        .Size(
            _popupViewport.FitWidth(78),
            _popupViewport.FitHeight(request.Kind == CredentialPromptKind.Confirmation ? 12 : 14))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 10, 118, 24)
        .Modal();
        OpenPopup(windows, handle, () =>
        {
            if (!submitted)
            {
                _workspace.CredentialPrompts.Cancel(request.Id);
            }

            foreach (var segment in secretResponse)
            {
                CryptographicOperations.ZeroMemory(segment);
            }

            secretResponse.Clear();
            secretCharacterCount = 0;
            secretByteCount = 0;
        });
        return handle;
    }

    private void SynchronizeExecutableCapabilityWindow()
    {
        lock (_executablePromptLock)
        {
            var prompt = _workspace.ExecutableCapabilities.Current;
            var windows = _executableWindowManager;
            if (prompt is null)
            {
                if (_executablePromptWindow is { } completedWindow &&
                    windows?.IsOpen(completedWindow) == true)
                {
                    windows.Close(completedWindow);
                }

                _executablePromptWindow = null;
                _executablePromptId = 0;
                return;
            }

            if (windows is null ||
                (_executablePromptId == prompt.Id &&
                    _executablePromptWindow is { } currentWindow &&
                    windows.IsOpen(currentWindow)))
            {
                return;
            }

            if (_executablePromptWindow is { } previousWindow && windows.IsOpen(previousWindow))
            {
                windows.Close(previousWindow);
            }

            _executablePromptWindow = OpenExecutableCapabilityWindow(windows, prompt);
            _executablePromptId = prompt.Id;
        }
    }

    private WindowHandle OpenExecutableCapabilityWindow(
        WindowManager windows,
        ExecutableCapabilityPrompt prompt)
    {
        var request = prompt.Request;
        var commandEditor = CreateReadOnlyEditor(TerminalOutputFormatter.Format(request.Command));
        var submitted = false;
        var handle = windows.Window(window => window.VStack(builder =>
        [
            builder.Text(
                "This repository configuration requests permission to run a command. " +
                "Review every field before allowing it.").Wrap(),
            builder.Text(
                $"Configuration: {TerminalTextSanitizer.Sanitize(request.ConfigurationKey)} | " +
                $"Scope: {FormatConfigurationScope(request.SourceScope)}").Wrap(),
            builder.Text(
                $"Origin: {GitPath.FromUnixBytes(request.SourceOrigin.GetBytes()).DisplayText}").Wrap(),
            builder.Text(
                $"Executable: {TerminalTextSanitizer.Sanitize(request.Executable.Path)} | " +
                (request.UsesShell ? "Shell involved: yes" : "Shell involved: no")).Wrap(),
            builder.Text(
                $"Working directory: {TerminalTextSanitizer.Sanitize(request.WorkingDirectory.ToString())}").Wrap(),
            builder.Border(DismissOnEscape(
                builder.Editor(commandEditor)
                    .LineNumbers()
                    .WordWrap(false),
                window.Window))
                .Title("Exact command")
                .Fill(),
            builder.Text(
                request.ExposedData.IsEmpty
                    ? "Data exposed: none"
                    : $"Data exposed: {string.Join(", ", request.ExposedData.Select(TerminalTextSanitizer.Sanitize))}")
                .Wrap(),
            builder.Text($"Command fingerprint: {request.CommandHash}").Wrap(),
            builder.HStack(actions =>
            [
                actions.Button("Deny").OnClick(
                    _ => Decide(ExecutableCapabilityDecision.Deny, window.Window)),
                actions.Text(" "),
                actions.Button("Allow once").OnClick(
                    _ => Decide(ExecutableCapabilityDecision.AllowOnce, window.Window)),
                actions.Text(" "),
                actions.Button("Allow for this repository").OnClick(
                    _ => Decide(ExecutableCapabilityDecision.AllowRepository, window.Window)),
            ]),
            builder.Text(
                "Deny is focused first | Repository approval is stored only in user-global Git configuration | " +
                "Esc or click outside denies").Wrap(),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Deny configured command")))
        .Title("Configured command security review")
        .Size(_popupViewport.FitWidth(104), _popupViewport.FitHeight(28))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(70, 20, 144, 56);
        OpenPopup(windows, handle, () =>
        {
            if (!submitted)
            {
                _workspace.ExecutableCapabilities.Cancel(prompt.Id);
            }
        });
        return handle;

        void Decide(ExecutableCapabilityDecision decision, WindowHandle window)
        {
            submitted = _workspace.ExecutableCapabilities.Decide(prompt.Id, decision);
            if (submitted)
            {
                CloseWithResultIfOpen(windows, window, decision);
            }
        }
    }

    private static void CloseWithResultIfOpen<T>(
        WindowManager windows,
        WindowHandle window,
        T result)
        => windows.Get(window)?.CloseWithResult(result);

    private bool IsResolutionOnlyMode
        => _mode is ApplicationMode.Merge or ApplicationMode.Rebase;

    private string GetResolutionExitDescription()
        => _mode == ApplicationMode.Rebase
            ? "Return to rebase recovery"
            : "Close conflict resolution";

    private bool CanRunPrimaryAction()
        => !IsResolutionOnlyMode &&
            (_options.Citool?.NoCommit == true
            ? _workspace.CanCompleteWithoutCommit
            : _workspace.CanCommit);

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
            _workspace.State.UnstagedTotalCount > 0 &&
            !HasUnmergedEntries();

    private bool CanUnstageAll()
        => !_workspace.IsBusy &&
            _workspace.State.StagedTotalCount > 0 &&
            !HasUnmergedEntries();

    private bool HasUnmergedEntries()
        => _workspace.State.Snapshot.Entries.Any(
            static entry => entry.Kind == RepositoryStatusEntryKind.Unmerged);

    private bool ContainsUnmergedPath(IReadOnlyList<GitPath> paths)
        => paths.Any(path => _workspace.State.Snapshot.Entries.Any(
            entry => entry.Kind == RepositoryStatusEntryKind.Unmerged && entry.Path.Equals(path)));

    private string GetPrimaryActionLabel()
        => _options.Citool?.NoCommit == true
            ? AppMessages.WorkspaceActionDone
            : AppMessages.WorkspaceActionCommit;

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

        if (IsResolutionOnlyMode)
        {
            return Task.CompletedTask;
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
                $"{(_workspace.ConflictResultIsExecutable
                    ? AppMessages.WorkspaceLabelExecutable
                    : AppMessages.WorkspaceLabelRegular)}]"
            : _workspace.Diff.Title;

    private string GetConflictModeLabel()
        => _workspace.ConflictResultIsExecutable
            ? $"{AppMessages.WorkspaceLabelMode}: {AppMessages.WorkspaceLabelExecutable} (100755)"
            : $"{AppMessages.WorkspaceLabelMode}: {AppMessages.WorkspaceLabelRegular} (100644)";

    private Hex1bWidget BuildSelectedLineAction<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
    {
        if (_workspace.CanStageSelectedLines)
        {
            return context.Button(compact ? "L" : AppMessages.WorkspaceActionStageLines)
                .OnClick(_ => _workspace.StageSelectedLinesAsync(_cancellationToken));
        }

        if (_workspace.CanUnstageSelectedLines)
        {
            return context.Button(compact ? "L" : AppMessages.WorkspaceActionUnstageLines)
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
            ? context.Button(compact ? "R" : AppMessages.WorkspaceActionRevert)
                .OnClick(eventArgs => ShowRevertConfirmation(eventArgs.Windows))
            : context.Text(string.Empty);

    private Hex1bWidget BuildPrepareUntrackedPatchAction<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
        => _workspace.CanPrepareUntrackedPatch
            ? context.Button(compact ? "P" : AppMessages.WorkspaceActionPrepareHunks)
                .OnClick(_ => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken))
            : context.Text(string.Empty);

    private Hex1bWidget BuildUndoRevertAction<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
        => _workspace.CanUndoRevert
            ? context.Button(compact ? "Undo" : AppMessages.WorkspaceActionUndoRevert)
                .OnClick(_ => _workspace.UndoRevertAsync(_cancellationToken))
            : context.Text(string.Empty);

    private Hex1bWidget BuildAbortMergeAction<TParent>(
        WidgetContext<TParent> context,
        bool compact)
        where TParent : Hex1bWidget
        => _workspace.CanAbortMerge
            ? context.Button(compact ? "Abort" : AppMessages.WorkspaceActionAbortMerge)
                .OnClick(eventArgs => ShowAbortMergeConfirmation(eventArgs.Windows))
            : context.Text(string.Empty);

    private bool CanRevert()
        => _workspace.CanRevertSelectedLines ||
            _workspace.CanRevertFocusedHunk ||
            _workspace.CanRevertFocusedFile;

    private void ShowApplicationMenu(WindowManager windows)
    {
        var categoryIndex = 0;
        string? focusedCommandId = null;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var category = s_workspaceMenuCategories[categoryIndex];
            var commands = BuildWorkspaceCommands();
            var categoryCommands = commands
                .Where(command => command.MenuCategories.Contains(category, StringComparer.Ordinal))
                .ToList();
            var commandIndex = focusedCommandId is null
                ? 0
                : Math.Max(0, categoryCommands.FindIndex(command => string.Equals(
                    command.Id,
                    focusedCommandId,
                    StringComparison.Ordinal)));
            if (commandIndex >= categoryCommands.Count)
            {
                commandIndex = 0;
            }

            var focusedCommand = categoryCommands.Count == 0
                ? null
                : categoryCommands[commandIndex];
            focusedCommandId = focusedCommand?.Id;
            return
            [
                builder.HSplitter(
                    builder.Border(DismissOnEscape(
                        builder.List(s_workspaceMenuCategories)
                            .ItemKey(static menuCategory => menuCategory)
                            .FocusedIndex(categoryIndex)
                            .OnFocusChanged(eventArgs =>
                            {
                                if (eventArgs.FocusedIndex >= 0 &&
                                    eventArgs.FocusedIndex < s_workspaceMenuCategories.Length)
                                {
                                    categoryIndex = eventArgs.FocusedIndex;
                                    focusedCommandId = null;
                                    _application?.Invalidate();
                                }
                            })
                            .Fill(),
                        window.Window))
                        .Title("Menus")
                        .Fill(),
                    builder.Border(DismissOnEscape(
                        builder.List(categoryCommands)
                            .ItemKey(static command => command.Id)
                            .FocusedIndex(commandIndex)
                            .OnItemActivated(eventArgs => ExecuteApplicationMenuCommandAsync(
                                eventArgs.ActivatedItem,
                                window.Window,
                                windows))
                            .OnFocusChanged(eventArgs =>
                            {
                                if (eventArgs.FocusedIndex >= 0 &&
                                    eventArgs.FocusedIndex < categoryCommands.Count)
                                {
                                    focusedCommandId = categoryCommands[eventArgs.FocusedIndex].Id;
                                    _application?.Invalidate();
                                }
                            })
                            .InputBindings(bindings => bindings.Key(Hex1bKey.Enter).Action(
                                _ => ExecuteApplicationMenuCommandAsync(
                                    focusedCommand,
                                    window.Window,
                                    windows),
                                "Run the focused available menu action"))
                            .Fill(),
                        window.Window))
                        .Title(category)
                        .Fill(),
                    16).Fill(),
                builder.Text(focusedCommand?.Description ?? "No action is available in this menu.").Wrap(),
                builder.Text(GetCommandAvailabilityText(focusedCommand)).Wrap(),
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    focusedCommand?.IsAvailable == true
                        ? actions.Button("Run selected").OnClick(
                            _ => ExecuteApplicationMenuCommandAsync(
                                focusedCommand,
                                window.Window,
                                windows))
                        : actions.Text("Run selected unavailable"),
                ]),
                builder.Text("Tab lists | Enter/mouse runs | Esc/click outside closes"),
            ];
        }).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close the application menu");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close the application menu");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }))
        .Title("GitSail menu")
        .Size(_popupViewport.FitWidth(58), _popupViewport.FitHeight(16))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 132, 48));
    }

    private void ShowCommandPalette(WindowManager windows)
    {
        var filterState = new TextBoxState();
        string? focusedId = null;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var filter = filterState.Text.Trim();
            var commands = BuildWorkspaceCommands();
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
                    DismissOnEscape(
                        search.TextBox()
                            .State(filterState)
                            .OnTextChanged(_ =>
                            {
                                focusedId = null;
                                _application?.Invalidate();
                            })
                            .OnSubmit(_ => ExecutePaletteCommandAsync(
                                ResolvePaletteCommand(filterState.Text, focusedId),
                                window.Window,
                                windows)),
                        window.Window)
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
                        _ => ExecutePaletteCommandAsync(focused, window.Window, windows),
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
                            _ => ExecutePaletteCommandAsync(focused, window.Window, windows))
                        : actions.Text("Run selected unavailable"),
                ]),
                builder.Text("Type filter | Up/Down | Enter/mouse runs | Esc closes"),
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
        .Size(_popupViewport.FitWidth(58), _popupViewport.FitHeight(16))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 130, 48));
    }

    private WorkspaceCommandItem? ResolvePaletteCommand(
        string filterText,
        string? focusedId)
    {
        var filter = filterText.Trim();
        var commands = BuildWorkspaceCommands();
        var visible = string.IsNullOrEmpty(filter)
            ? commands
            : [.. commands.Where(command => command.Matches(filter))];
        if (visible.Count == 0)
        {
            return null;
        }

        return focusedId is null
            ? visible[0]
            : visible.FirstOrDefault(command => string.Equals(
                command.Id,
                focusedId,
                StringComparison.Ordinal)) ?? visible[0];
    }

    private void ShowConfiguredToolDialog(
        WindowManager windows,
        ConfiguredToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(tool);
        if (!tool.IsAvailable || tool.Command is null)
        {
            return;
        }

        var argumentState = new TextBoxState();
        var revisionState = new TextBoxState(tool.RevisionPrompt is null ? string.Empty : "HEAD");
        var commandEditor = CreateReadOnlyEditor(TerminalOutputFormatter.Format(tool.Command));
        string? validationError = null;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var content = new List<Hex1bWidget>
            {
                builder.Text(TerminalOutputFormatter.Format(
                    tool.Prompt ?? $"Run configured tool {tool.Title}?"))
                    .Wrap(),
                builder.Text(
                    $"Source: {FormatConfigurationScope(tool.SourceScope)} | " +
                    $"{GitPath.FromUnixBytes(tool.SourceOrigin.GetBytes()).DisplayText}")
                    .Wrap(),
                builder.Border(DismissOnEscape(
                    builder.Editor(commandEditor)
                        .LineNumbers()
                        .WordWrap(false),
                    window.Window))
                    .Title("Exact configured command")
                    .Fill(),
            };
            if (tool.ArgumentPrompt is not null)
            {
                content.Add(builder.HStack(row =>
                [
                    row.Text($"{FormatToolPrompt(tool.ArgumentPrompt, "Arguments")}: "),
                    DismissOnEscape(row.TextBox().State(argumentState), window.Window).FillWidth(),
                ]).FillWidth());
            }

            if (tool.RevisionPrompt is not null)
            {
                content.Add(builder.HStack(row =>
                [
                    row.Text($"{FormatToolPrompt(tool.RevisionPrompt, "Revision")}: "),
                    DismissOnEscape(row.TextBox().State(revisionState), window.Window).FillWidth(),
                ]).FillWidth());
                if (tool.RevisionUnmerged)
                {
                    content.Add(builder.Text(
                        "This tool requests an unmerged revision; enter the exact revision to expose.").Wrap());
                }
            }

            if (validationError is not null)
            {
                content.Add(builder.Text(validationError).Wrap());
            }

            var selectedPaths = _workspace.State.GetSelectedOrFocusedPaths();
            var focusedPath = _workspace.State.FocusedItem?.Path;
            content.Add(builder.Text(
                focusedPath is null
                    ? $"Focused path: none | Selected paths: {selectedPaths.Length}"
                    : $"Focused path: {TerminalTextSanitizer.Sanitize(focusedPath.DisplayText)} | " +
                        $"Selected paths: {selectedPaths.Length}").Wrap());
            content.Add(builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Continue to security review").OnClick(async _ =>
                {
                    if (argumentState.Text.Length > 64 * 1024 ||
                        revisionState.Text.Length > 64 * 1024)
                    {
                        validationError = "Tool input cannot exceed 64 Ki characters.";
                        _application?.Invalidate();
                        return;
                    }

                    var input = new ConfiguredToolInvocation(
                        focusedPath,
                        selectedPaths,
                        _workspace.State.Snapshot.HeadName,
                        tool.ArgumentPrompt is null ? null : argumentState.Text,
                        tool.RevisionPrompt is null ? null : revisionState.Text);
                    window.Window.CloseWithResult("review");
                    await StartConfiguredToolOperation(windows, tool, input).ConfigureAwait(false);
                }),
            ]));
            content.Add(builder.Text(
                "The next screen shows the exact command, executable, directory, and exposed data. " +
                "Esc or click outside cancels.").Wrap());
            return [.. content];
        }).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel configured tool")))
        .Title($"Configured tool: {TerminalTextSanitizer.Sanitize(tool.Title)}")
        .Size(_popupViewport.FitWidth(92), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(62, 16, 132, 48));
    }

    private Task RunConfiguredToolCommand(
        WindowManager windows,
        ConfiguredToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.ArgumentPrompt is not null ||
            tool.RevisionPrompt is not null ||
            tool.Confirm)
        {
            ShowConfiguredToolDialog(windows, tool);
            return Task.CompletedTask;
        }

        return StartConfiguredToolOperation(
            windows,
            tool,
            CreateConfiguredToolInvocation(tool, arguments: null, revision: null));
    }

    private ConfiguredToolInvocation CreateConfiguredToolInvocation(
        ConfiguredToolDefinition tool,
        string? arguments,
        string? revision)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new ConfiguredToolInvocation(
            _workspace.State.FocusedItem?.Path,
            _workspace.State.GetSelectedOrFocusedPaths(),
            _workspace.State.Snapshot.HeadName,
            tool.ArgumentPrompt is null ? null : arguments,
            tool.RevisionPrompt is null ? null : revision);
    }

    private Task StartConfiguredToolOperation(
        WindowManager windows,
        ConfiguredToolDefinition tool,
        ConfiguredToolInvocation input)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(input);
        _executableWindowManager = windows;
        _workspace.Operations.Start(
            $"configured-tool-{CreateConfiguredToolCommandId(tool)}",
            context => RunConfiguredToolOperationAsync(
                windows,
                tool,
                input,
                context.CancellationToken),
            _cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunConfiguredToolOperationAsync(
        WindowManager windows,
        ConfiguredToolDefinition tool,
        ConfiguredToolInvocation input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _workspace.RunConfiguredToolAsync(
                tool,
                input,
                cancellationToken).ConfigureAwait(false);
            if (result is null || result.Outcome == ConfiguredToolOutcome.Denied)
            {
                return;
            }

            if (result.Outcome == ConfiguredToolOutcome.Succeeded && tool.NoConsole)
            {
                return;
            }

            ShowConfiguredToolResult(windows, tool, result);
        }
        finally
        {
            if (_workspace.ExecutableCapabilities.Current is null)
            {
                _executableWindowManager = null;
            }

            _application?.Invalidate();
        }
    }

    private void ShowConfiguredToolResult(
        WindowManager windows,
        ConfiguredToolDefinition tool,
        ConfiguredToolResult result)
    {
        var standardOutput = CreateReadOnlyEditor(
            FormatToolOutput(result.StandardOutput.Span));
        var standardError = CreateReadOnlyEditor(
            FormatToolOutput(result.StandardError.Span));
        var selectedTab = result.StandardOutput.IsEmpty && !result.StandardError.IsEmpty ? 1 : 0;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text(result.Outcome == ConfiguredToolOutcome.Succeeded
                ? $"Completed successfully in {result.Duration.TotalMilliseconds:F0} ms."
                : $"Exited with code {result.ExitCode} after {result.Duration.TotalMilliseconds:F0} ms."),
            builder.TabPanel(tabs =>
            [
                tabs.Tab("stdout", content =>
                [
                    DismissOnEscape(
                        content.Editor(standardOutput).LineNumbers().WordWrap(false),
                        window.Window).Fill(),
                ]).Selected(selectedTab == 0),
                tabs.Tab("stderr", content =>
                [
                    DismissOnEscape(
                        content.Editor(standardError).LineNumbers().WordWrap(false),
                        window.Window).Fill(),
                ]).Selected(selectedTab == 1),
            ]).OnSelectionChanged(eventArgs =>
            {
                selectedTab = eventArgs.SelectedIndex;
                _application?.Invalidate();
            }).Fill(),
            builder.Button("Close").OnClick(_ => window.Window.Cancel()),
            builder.Text("Output is bounded and terminal controls are shown as text | Esc closes"),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close configured tool output")))
        .Title($"Tool output: {TerminalTextSanitizer.Sanitize(tool.Title)}")
        .Size(_popupViewport.FitWidth(96), _popupViewport.FitHeight(26))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(62, 16, 140, 52));
    }

    private static string FormatToolOutput(ReadOnlySpan<byte> value)
    {
        var formatted = TerminalOutputFormatter.Format(value);
        return formatted.Length == 0 ? "<empty>" : formatted;
    }

    private static EditorState CreateReadOnlyEditor(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };

    private static string FormatToolPrompt(string prompt, string fallback)
        => prompt is "yes" or "true" or "1"
            ? fallback
            : TerminalTextSanitizer.Sanitize(prompt);

    private static string CreateConfiguredToolCommandId(ConfiguredToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tool.ConfigurationKey));
        return $"tool.configured.{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private async Task ShowConfiguredToolManagerAsync(WindowManager windows)
    {
        await _workspace.ReloadConfigurationAsync(_cancellationToken).ConfigureAwait(false);
        ShowConfiguredToolManager(windows);
    }

    private void ShowConfiguredToolManager(WindowManager windows)
    {
        var focusedIndex = 0;
        var selectedScope = GitConfigurationScope.Local;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var tools = _workspace.ConfiguredTools.Tools;
            if (focusedIndex < 0 || focusedIndex >= tools.Length)
            {
                focusedIndex = tools.IsEmpty ? -1 : Math.Clamp(focusedIndex, 0, tools.Length - 1);
            }

            var focused = focusedIndex >= 0 ? tools[focusedIndex] : null;
            var labels = tools.Select(tool =>
                $"{TerminalTextSanitizer.Sanitize(tool.Title)}  " +
                $"[{TerminalTextSanitizer.Sanitize(tool.Name)}]").ToImmutableArray();
            var details = focused is null
                ? "No user-defined tools are configured."
                : $"Command: {TerminalTextSanitizer.Sanitize(focused.Command ?? "<invalid>")}\n" +
                  $"Effective source: {FormatConfigurationScope(focused.SourceScope)} — " +
                  $"{GitPath.FromUnixBytes(focused.SourceOrigin.GetBytes()).DisplayText}\n" +
                  $"Requires file: {FormatBoolean(focused.NeedsFile)} | " +
                  $"Confirm: {FormatBoolean(focused.Confirm)} | " +
                  $"Show output: {FormatBoolean(!focused.NoConsole)} | " +
                  $"Refresh afterward: {FormatBoolean(!focused.NoRescan)}\n" +
                  (focused.UnavailableReason is null
                      ? "Ready to review before execution."
                      : $"Unavailable: {TerminalTextSanitizer.Sanitize(focused.UnavailableReason)}");
            var actions = new List<Hex1bWidget>
            {
                builder.Button("Close").OnClick(_ => window.Window.Cancel()),
                builder.Button($"Scope: {FormatConfigurationScope(selectedScope)}").OnClick(_ =>
                {
                    selectedScope = NextConfigurationScope(selectedScope);
                    _application?.Invalidate();
                }),
                builder.Button("Add...").OnClick(_ => ShowConfiguredToolEditor(
                    windows,
                    selectedScope,
                    existing: null)),
            };
            if (focused is not null)
            {
                actions.Add(builder.Button("Edit...").OnClick(_ => ShowConfiguredToolEditor(
                    windows,
                    selectedScope,
                    focused)));
                actions.Add(builder.Button("Remove...").OnClick(_ =>
                    ShowConfiguredToolRemoveConfirmation(windows, selectedScope, focused)));
            }

            actions.Add(builder.Button("Reload").OnClick(async _ =>
            {
                await _workspace.ReloadConfigurationAsync(_cancellationToken).ConfigureAwait(false);
                _application?.Invalidate();
            }));
            return
            [
                builder.Border(DismissOnEscape(
                    builder.List(labels)
                        .ItemKey(static label => label)
                        .FocusedIndex(Math.Max(0, focusedIndex))
                        .OnFocusChanged(eventArgs =>
                        {
                            if (eventArgs.FocusedIndex >= 0 && eventArgs.FocusedIndex < tools.Length)
                            {
                                focusedIndex = eventArgs.FocusedIndex;
                                _application?.Invalidate();
                            }
                        })
                        .Empty(empty => empty.Text("No configured tools. Choose Add to create one."))
                        .Fill(),
                    window.Window))
                    .Title($"Configured tools ({tools.Length})")
                    .Fill(),
                builder.Text(details).Wrap(),
                _workspace.ConfiguredTools.Warning is { } warning
                    ? builder.Text(TerminalTextSanitizer.Sanitize(warning)).Wrap()
                    : builder.Text(string.Empty),
                builder.WrapPanel(_ => [.. actions]),
                builder.Text(
                    "Scope controls where add, edit, or remove writes. " +
                    "Inherited values can become visible again after removal. Esc/click outside closes."),
            ];
        }).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close configured-tool management");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close configured-tool management");
        }))
        .Title("Manage configured tools")
        .Size(_popupViewport.FitWidth(88), _popupViewport.FitHeight(26))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(60, 18, 140, 52));
    }

    private void ShowConfiguredToolEditor(
        WindowManager windows,
        GitConfigurationScope scope,
        ConfiguredToolDefinition? existing)
    {
        var initial = existing is null
            ? new ConfiguredToolConfiguration(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                NoConsole: false,
                NeedsFile: false,
                Confirm: false,
                RevisionUnmerged: false,
                NoRescan: false)
            : ConfiguredToolConfiguration.FromDefinition(existing);
        var nameState = new TextBoxState { Text = initial.Name, CursorPosition = initial.Name.Length };
        var commandState = new TextBoxState
        {
            Text = initial.Command,
            CursorPosition = initial.Command.Length,
        };
        var titleState = new TextBoxState { Text = initial.Title, CursorPosition = initial.Title.Length };
        var promptState = new TextBoxState { Text = initial.Prompt, CursorPosition = initial.Prompt.Length };
        var argumentPromptState = new TextBoxState
        {
            Text = initial.ArgumentPrompt,
            CursorPosition = initial.ArgumentPrompt.Length,
        };
        var revisionPromptState = new TextBoxState
        {
            Text = initial.RevisionPrompt,
            CursorPosition = initial.RevisionPrompt.Length,
        };
        var noConsole = initial.NoConsole;
        var needsFile = initial.NeedsFile;
        var confirm = initial.Confirm;
        var revisionUnmerged = initial.RevisionUnmerged;
        var noRescan = initial.NoRescan;
        TextBoxWidget? editorFocusWidget = null;
        var editorWindow = windows.Window(window => window.VStack(builder =>
        {
            var draft = CreateDraft();
            var valid = ConfiguredToolConfigurationValidator.TryValidate(draft, out var error);
            if (existing is null && _workspace.ConfiguredTools.Tools.Any(tool => string.Equals(
                tool.Name,
                draft.Name,
                StringComparison.Ordinal)))
            {
                valid = false;
                error = "A configured tool with this exact name already exists.";
            }

            var fields = new List<Hex1bWidget>
            {
                builder.Text($"Scope: {FormatConfigurationScope(scope)} ({FormatConfigurationScopeSwitch(scope)})"),
                builder.Text(existing is null
                    ? "Name (used in guitool.<name>.*):"
                    : $"Name: {TerminalTextSanitizer.Sanitize(existing.Name)}"),
            };
            if (existing is null)
            {
                fields.Add(DismissOnEscape(
                    builder.TextBox().State(nameState).OnTextChanged(_ => _application?.Invalidate()),
                    window.Window).FillWidth());
            }

            fields.Add(builder.Text("Command (passed unchanged to the fixed platform shell):"));
            var commandWidget = builder.TextBox()
                .State(commandState)
                .OnTextChanged(_ => _application?.Invalidate());
            editorFocusWidget = commandWidget;
            fields.Add(DismissOnEscape(
                commandWidget,
                window.Window).FillWidth());
            fields.Add(builder.Text("Title (empty uses the name):"));
            fields.Add(DismissOnEscape(
                builder.TextBox().State(titleState).OnTextChanged(_ => _application?.Invalidate()),
                window.Window).FillWidth());
            fields.Add(builder.Text("Confirmation prompt (optional):"));
            fields.Add(DismissOnEscape(
                builder.TextBox().State(promptState).OnTextChanged(_ => _application?.Invalidate()),
                window.Window).FillWidth());
            fields.Add(builder.Text("Arguments prompt (optional):"));
            fields.Add(DismissOnEscape(
                builder.TextBox().State(argumentPromptState).OnTextChanged(_ => _application?.Invalidate()),
                window.Window).FillWidth());
            fields.Add(builder.Text("Revision prompt (optional):"));
            fields.Add(DismissOnEscape(
                builder.TextBox().State(revisionPromptState).OnTextChanged(_ => _application?.Invalidate()),
                window.Window).FillWidth());
            fields.Add(builder.WrapPanel(options =>
            [
                options.Button($"Needs file: {FormatBoolean(needsFile)}").OnClick(_ =>
                {
                    needsFile = !needsFile;
                    _application?.Invalidate();
                }),
                options.Button($"Confirm: {FormatBoolean(confirm)}").OnClick(_ =>
                {
                    confirm = !confirm;
                    _application?.Invalidate();
                }),
                options.Button($"Show output: {FormatBoolean(!noConsole)}").OnClick(_ =>
                {
                    noConsole = !noConsole;
                    _application?.Invalidate();
                }),
                options.Button($"Refresh afterward: {FormatBoolean(!noRescan)}").OnClick(_ =>
                {
                    noRescan = !noRescan;
                    _application?.Invalidate();
                }),
                options.Button($"Revision is unmerged: {FormatBoolean(revisionUnmerged)}").OnClick(_ =>
                {
                    revisionUnmerged = !revisionUnmerged;
                    _application?.Invalidate();
                }),
            ]));
            fields.Add(builder.Text(valid ? "Ready to review exact configuration changes." : error ?? "Invalid tool."));

            var actions = new List<Hex1bWidget>
            {
                builder.Button("Cancel").OnClick(_ => window.Window.Cancel()),
            };
            if (valid)
            {
                actions.Add(builder.Button(existing is null ? "Review add..." : "Review save...")
                    .OnClick(_ => ShowConfiguredToolSaveConfirmation(
                        windows,
                        scope,
                        draft,
                        existing is null,
                        RestoreEditorFocus)));
            }

            return
            [
                builder.VScrollPanel(_ => [.. fields], showScrollbar: true).Fill(),
                builder.WrapPanel(_ => [.. actions]),
                builder.Text("Saving a command does not grant execution permission. Esc cancels without changes."),
            ];

            ConfiguredToolConfiguration CreateDraft()
                => new(
                    existing?.Name ?? nameState.Text,
                    commandState.Text,
                    titleState.Text,
                    promptState.Text,
                    argumentPromptState.Text,
                    revisionPromptState.Text,
                    noConsole,
                    needsFile,
                    confirm,
                    revisionUnmerged,
                    noRescan);
        }).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel configured-tool editing")))
        .Title(existing is null ? "Add configured tool" : "Edit configured tool")
        .Size(_popupViewport.FitWidth(88), _popupViewport.FitHeight(30))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 2, 1))
        .Resizable(60, 18, 140, 52);
        OpenPopup(windows, editorWindow);
        RestoreEditorFocus();

        void RestoreEditorFocus()
        {
            _application?.RequestFocus(node =>
                node is TextBoxNode textBox &&
                ReferenceEquals(textBox.SourceWidget, editorFocusWidget));
            _application?.Invalidate();
        }
    }

    private void ShowConfiguredToolSaveConfirmation(
        WindowManager windows,
        GitConfigurationScope scope,
        ConfiguredToolConfiguration configuration,
        bool isNew,
        Action restoreEditorFocus)
    {
        ArgumentNullException.ThrowIfNull(restoreEditorFocus);
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Scope: {FormatConfigurationScope(scope)} ({FormatConfigurationScopeSwitch(scope)})"),
            builder.Text($"Name: {TerminalTextSanitizer.Sanitize(configuration.Name)}"),
            builder.Text($"Command: {TerminalTextSanitizer.Sanitize(configuration.Command)}").Wrap(),
            builder.Text($"Title: {FormatOptionalToolValue(configuration.Title)}"),
            builder.Text($"Confirmation prompt: {FormatOptionalToolValue(configuration.Prompt)}").Wrap(),
            builder.Text($"Arguments prompt: {FormatOptionalToolValue(configuration.ArgumentPrompt)}").Wrap(),
            builder.Text($"Revision prompt: {FormatOptionalToolValue(configuration.RevisionPrompt)}").Wrap(),
            builder.Text(
                $"Needs file: {FormatBoolean(configuration.NeedsFile)} | " +
                $"Confirm: {FormatBoolean(configuration.Confirm)} | " +
                $"Show output: {FormatBoolean(!configuration.NoConsole)} | " +
                $"Refresh: {FormatBoolean(!configuration.NoRescan)} | " +
                $"Unmerged revision: {FormatBoolean(configuration.RevisionUnmerged)}").Wrap(),
            builder.Text(
                "GitSail will reconcile only the displayed tool properties at this scope. " +
                "Execution still requires separate capability review."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button(isNew ? "Add exact tool" : "Save exact tool").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("save");
                    ClosePopupOnBackgroundClick(windows);
                    ClosePopupOnBackgroundClick(windows);
                    await _workspace.SaveConfiguredToolAsync(
                        scope,
                        configuration,
                        _cancellationToken).ConfigureAwait(false);
                    ShowConfiguredToolManager(windows);
                    _application?.Invalidate();
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel configured-tool save")))
        .Title(isNew ? "Add configured tool?" : "Save configured tool?")
        .Size(_popupViewport.FitWidth(92), _popupViewport.FitHeight(18))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 3, 2))
        .Resizable(60, 16, 140, 36)
        .Modal(), restoreEditorFocus);
    }

    private void ShowConfiguredToolRemoveConfirmation(
        WindowManager windows,
        GitConfigurationScope scope,
        ConfiguredToolDefinition tool)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Scope: {FormatConfigurationScope(scope)} ({FormatConfigurationScopeSwitch(scope)})"),
            builder.Text($"Tool: {TerminalTextSanitizer.Sanitize(tool.Title)} " +
                $"[{TerminalTextSanitizer.Sanitize(tool.Name)}]"),
            builder.Text(
                $"All explicit guitool.{TerminalTextSanitizer.Sanitize(tool.Name)}.* properties " +
                "supported by GitSail will be removed at this scope."),
            builder.Text(
                "Values from another scope remain unchanged and can become effective again. " +
                "The tool command is not executed."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Remove exact tool").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("remove");
                    ClosePopupOnBackgroundClick(windows);
                    await _workspace.RemoveConfiguredToolAsync(
                        scope,
                        tool.Name,
                        _cancellationToken).ConfigureAwait(false);
                    ShowConfiguredToolManager(windows);
                    _application?.Invalidate();
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel configured-tool removal")))
        .Title("Remove configured tool?")
        .Size(_popupViewport.FitWidth(84), _popupViewport.FitHeight(13))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 2, 1))
        .Resizable(60, 12, 130, 24)
        .Modal());
    }

    private static string FormatBoolean(bool value)
        => value ? "yes" : "no";

    private static string FormatOptionalToolValue(string value)
        => value.Length == 0 ? "<empty>" : TerminalTextSanitizer.Sanitize(value);

    private void ShowSshKeyCreation(WindowManager windows)
    {
        var algorithm = SshKeyAlgorithm.Ed25519;
        var initialPath = _workspace.DefaultSshKeyPath ?? string.Empty;
        var pathState = new TextBoxState
        {
            Text = initialPath,
            CursorPosition = initialPath.Length,
        };
        var commentState = new TextBoxState();
        TextBoxWidget? pathWidget = null;
        var handle = windows.Window(window => window.VStack(builder =>
        {
            var valid = SshKeyCreationService.TryValidateRequest(
                algorithm,
                pathState.Text,
                commentState.Text,
                replaceExisting: false,
                out var request,
                out var error);
            var existing = valid && SshKeyCreationService.RequiresReplacement(request.FilePath);
            pathWidget = builder.TextBox()
                .State(pathState)
                .OnTextChanged(_ => _application?.Invalidate());
            var fields = new List<Hex1bWidget>
            {
                builder.Text(
                    "Create a key for Git SSH authentication. Ed25519 is the recommended default; " +
                    "RSA 4096 and ECDSA 521 are deliberate compatibility alternatives.").Wrap(),
                builder.Button($"Algorithm: {SshKeyCreationService.GetDisplayName(algorithm)}").OnClick(_ =>
                {
                    var previousDefault = GetDefaultSshKeyPath(algorithm);
                    algorithm = SshKeyCreationService.GetNextAlgorithm(algorithm);
                    if (string.Equals(
                            pathState.Text,
                            previousDefault,
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))
                    {
                        pathState.Text = GetDefaultSshKeyPath(algorithm);
                        pathState.CursorPosition = pathState.Text.Length;
                    }

                    _application?.Invalidate();
                }),
                builder.Text("Private-key output path (fully qualified):"),
                DismissOnEscape(pathWidget, window.Window).FillWidth(),
                builder.Text("Public-key comment (optional, one line):"),
                DismissOnEscape(
                    builder.TextBox()
                        .State(commentState)
                        .OnTextChanged(_ => _application?.Invalidate()),
                    window.Window).FillWidth(),
                builder.Text(existing
                    ? "Existing private or public output detected. A separate replacement review is required."
                    : valid
                        ? "No existing output was detected. The path will be checked again immediately before launch."
                        : error ?? "The SSH key request is invalid.").Wrap(),
                builder.Text(
                    "After review, GitSail restores the terminal and runs the resolved ssh-keygen directly. " +
                    "ssh-keygen asks for the passphrase with terminal echo disabled; GitSail never receives it.").Wrap(),
            };
            var actions = new List<Hex1bWidget>
            {
                builder.Button("Cancel").OnClick(_ => window.Window.Cancel()),
            };
            if (valid)
            {
                actions.Add(builder.Button(existing ? "Review replacement..." : "Review creation...")
                    .OnClick(_ => ShowSshKeyCreationConfirmation(
                        windows,
                        request,
                        existing,
                        RestorePathFocus)));
            }

            fields.Add(builder.WrapPanel(_ => [.. actions]));
            fields.Add(builder.Text("Esc or click outside cancels without creating files."));
            return [builder.VScrollPanel(_ => [.. fields], showScrollbar: true).Fill()];
        }).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel SSH key creation")))
        .Title("Create SSH key")
        .Size(_popupViewport.FitWidth(88), _popupViewport.FitHeight(24))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(60, 18, 136, 44);
        OpenPopup(windows, handle);
        RestorePathFocus();

        string GetDefaultSshKeyPath(SshKeyAlgorithm selectedAlgorithm)
        {
            var directory = Path.GetDirectoryName(_workspace.DefaultSshKeyPath);
            return directory is null
                ? pathState.Text
                : Path.Combine(
                    directory,
                    SshKeyCreationService.GetDefaultFileName(selectedAlgorithm));
        }

        void RestorePathFocus()
        {
            _application?.RequestFocus(node =>
                node is TextBoxNode textBox && ReferenceEquals(textBox.SourceWidget, pathWidget));
            _application?.Invalidate();
        }
    }

    private void ShowSshKeyCreationConfirmation(
        WindowManager windows,
        SshKeyCreationRequest request,
        bool existing,
        Action restorePathFocus)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(restorePathFocus);
        var reviewed = request with { ReplaceExisting = existing };
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Algorithm: {SshKeyCreationService.GetDisplayName(reviewed.Algorithm)}"),
            builder.Text($"Private key: {TerminalTextSanitizer.Sanitize(reviewed.FilePath)}").Wrap(),
            builder.Text($"Public key: {TerminalTextSanitizer.Sanitize($"{reviewed.FilePath}.pub")}").Wrap(),
            builder.Text(reviewed.Comment.Length == 0
                ? "Comment: <OpenSSH default>"
                : $"Comment: {TerminalTextSanitizer.Sanitize(reviewed.Comment)}").Wrap(),
            existing
                ? builder.Text(
                    "Existing output is present. Continuing authorizes replacement review, but ssh-keygen " +
                    "will still ask at the terminal before overwriting the private key.").Wrap()
                : builder.Text(
                    "No output is replaced by this review. A newly appearing output file still requires " +
                    "ssh-keygen's terminal confirmation.").Wrap(),
            builder.Text(
                "The TUI will close before ssh-keygen starts. Enter the passphrase only at ssh-keygen's " +
                "terminal prompts; leave it empty only if that is your deliberate choice.").Wrap(),
            builder.WrapPanel(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Button(existing ? "Continue to overwrite prompt" : "Create with ssh-keygen")
                    .OnClick(eventArgs =>
                    {
                        _workspace.RequestSshKeyCreation(reviewed);
                        eventArgs.Context.RequestStop();
                    }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel SSH key creation review")))
        .Title(existing ? "Replace existing SSH key?" : "Create SSH key?")
        .Size(_popupViewport.FitWidth(94), _popupViewport.FitHeight(18))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 2, 1))
        .Resizable(64, 16, 140, 34)
        .Modal(), restorePathFocus);
    }

    private async Task ShowConfigurationOptionsAsync(WindowManager windows)
    {
        await _workspace.ReloadConfigurationAsync(_cancellationToken).ConfigureAwait(false);
        var filterState = new TextBoxState();
        var concreteKeyState = new TextBoxState();
        var valueState = new TextBoxState();
        var selectedScope = GitConfigurationScope.Local;
        string? focusedId = null;
        var focusedExplicitValueIndex = 0;
        var hasConfigurationValueDraft = false;
        var first = BuildConfigurationOptionItems(_workspace.Configuration).FirstOrDefault();
        if (first is not null)
        {
            FocusOption(first);
        }

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var options = BuildConfigurationOptionItems(_workspace.Configuration);
            var filter = filterState.Text;
            var visible = string.IsNullOrWhiteSpace(filter)
                ? options
                :
                [
                    .. options.Where(option => MatchesConfigurationFilter(option, filter)),
                ];
            var focusedIndex = focusedId is null
                ? 0
                : visible.FindIndex(option => string.Equals(
                    option.Id,
                    focusedId,
                    StringComparison.Ordinal));
            if (focusedIndex < 0 || focusedIndex >= visible.Count)
            {
                focusedIndex = 0;
            }

            var focused = visible.Count == 0 ? null : visible[focusedIndex];
            if (focused is not null && !string.Equals(focused.Id, focusedId, StringComparison.Ordinal))
            {
                FocusOption(focused);
            }

            var candidateKey = focused?.IsTemplate == true
                ? concreteKeyState.Text
                : focused?.Key ?? string.Empty;
            string? keyError = null;
            var keyValid = focused is not null && TryValidateConcreteConfigurationKey(
                focused,
                candidateKey,
                out keyError);
            var resolution = keyValid
                ? _workspace.Configuration.Resolve(candidateKey, selectedScope)
                : null;
            string? valueError = null;
            var valueValid = focused is not null && GitConfigurationValueValidator.TryParseText(
                focused.Definition,
                valueState.Text,
                out _,
                out valueError);
            var explicitText = resolution?.ExplicitEntry is null
                ? null
                : TryDecodeConfigurationValue(resolution.ExplicitEntry.Value, out var decoded)
                    ? decoded
                    : null;
            var alreadyExplicit = explicitText is not null && string.Equals(
                explicitText,
                valueState.Text,
                StringComparison.Ordinal);
            var definition = focused?.Definition;
            var explicitValueItems = keyValid && definition?.AllowsMultipleValues == true
                ? BuildConfigurationExplicitValueItems(
                    _workspace.Configuration,
                    candidateKey,
                    selectedScope,
                    definition)
                : [];
            if (focusedExplicitValueIndex < 0 || focusedExplicitValueIndex >= explicitValueItems.Count)
            {
                focusedExplicitValueIndex = 0;
            }

            var focusedExplicitValue = explicitValueItems.Count == 0
                ? null
                : explicitValueItems[focusedExplicitValueIndex];
            var canWrite = keyValid &&
                definition is { IsTerminalApplicable: true } &&
                definition.CanWrite(selectedScope);
            var canSave = canWrite && definition?.AllowsMultipleValues == false &&
                valueValid && !alreadyExplicit &&
                (definition.MayContainSecret == false || hasConfigurationValueDraft);
            var canAdd = canWrite && definition?.AllowsMultipleValues == true && valueValid &&
                (definition.MayContainSecret == false || hasConfigurationValueDraft);
            var canRemove = canWrite && definition?.AllowsMultipleValues == true &&
                focusedExplicitValue is not null;
            var canReset = canWrite &&
                resolution?.ExplicitEntry is not null;
            var status = GetConfigurationDraftStatus(
                focused,
                selectedScope,
                keyError,
                valueError,
                alreadyExplicit,
                explicitValueItems.Count,
                hasConfigurationValueDraft);
            var detailWidgets = new List<Hex1bWidget>();
            if (focused is null)
            {
                detailWidgets.Add(builder.Text("No setting matches the current filter."));
            }
            else
            {
                detailWidgets.Add(builder.Text(focused.Definition.Description));
                if (focused.IsTemplate)
                {
                    detailWidgets.Add(builder.HStack(row =>
                    [
                        row.Text("Concrete key: "),
                        DismissOnEscape(
                            row.TextBox()
                                .State(concreteKeyState)
                                .OnTextChanged(_ => _application?.Invalidate()),
                            window.Window)
                            .FillWidth(),
                    ]).FillWidth());
                }
                else
                {
                    detailWidgets.Add(builder.Text($"Key: {focused.Key}"));
                }

                detailWidgets.Add(builder.Text($"Type: {FormatConfigurationValueKind(focused.Definition)}"));
                detailWidgets.Add(builder.Text($"State: {FormatConfigurationState(resolution)}"));
                detailWidgets.Add(builder.Text($"Effective: {FormatEffectiveConfigurationValue(resolution)}"));
                detailWidgets.Add(builder.Text($"Source: {FormatConfigurationSource(resolution)}"));
                detailWidgets.Add(builder.Text(
                    $"Default: {FormatConfigurationDefault(focused.Definition.DefaultValue)}"));
                if (resolution?.ExplicitValidationError is { } explicitError)
                {
                    detailWidgets.Add(builder.Text(
                        $"Invalid selected value: {TerminalTextSanitizer.Sanitize(explicitError)}"));
                }

                if (resolution?.EffectiveValidationError is { } effectiveError)
                {
                    detailWidgets.Add(builder.Text(
                        $"Invalid effective value: {TerminalTextSanitizer.Sanitize(effectiveError)}"));
                }

                if (focused.Definition.AllowsMultipleValues)
                {
                    detailWidgets.Add(builder.Text(
                        $"Multivalue key: {explicitValueItems.Count} explicit " +
                        $"{FormatConfigurationScope(selectedScope)} value(s) in Git order."));
                    detailWidgets.Add(builder.Border(
                        DismissOnEscape(
                            builder.List(explicitValueItems)
                                .ItemKey(static item => item.Id)
                                .FocusedIndex(focusedExplicitValueIndex)
                                .OnFocusChanged(eventArgs =>
                                {
                                    if (eventArgs.FocusedIndex >= 0 &&
                                        eventArgs.FocusedIndex < explicitValueItems.Count)
                                    {
                                        focusedExplicitValueIndex = eventArgs.FocusedIndex;
                                        _application?.Invalidate();
                                    }
                                })
                                .Empty(empty => empty.Text("No explicit values at this scope."))
                                .Fill(),
                            window.Window))
                        .Title("Selected-scope values")
                        .FixedHeight(Math.Clamp(explicitValueItems.Count + 2, 3, 6)));
                    detailWidgets.Add(builder.HStack(row =>
                    [
                        row.Text("New value: "),
                        DismissOnEscape(
                            row.TextBox()
                                .State(valueState)
                                .OnTextChanged(_ =>
                                {
                                    hasConfigurationValueDraft = true;
                                    _application?.Invalidate();
                                }),
                            window.Window)
                            .FillWidth(),
                    ]).FillWidth());
                }

                if (focused.Definition.ExecutionKind != GitConfigurationExecutionKind.None)
                {
                    detailWidgets.Add(builder.Text(
                        $"Executable behavior: {FormatConfigurationExecution(focused.Definition.ExecutionKind)}. " +
                        "Saving does not grant permission to execute it."));
                }

                if (focused.Definition.MayContainSecret)
                {
                    detailWidgets.Add(builder.Text(
                        "Sensitive value: credentials remain redacted and are never copied into the value editor."));
                }

                if (!focused.Definition.IsTerminalApplicable)
                {
                    detailWidgets.Add(builder.Text(
                        "Desktop-only compatibility setting: visible here, never changed by GitSail."));
                }

                if (!focused.Definition.AllowsMultipleValues && focused.Definition.IsTerminalApplicable)
                {
                    detailWidgets.Add(builder.HStack(row =>
                    [
                        row.Text("Value: "),
                        DismissOnEscape(
                            row.TextBox()
                                .State(valueState)
                                .OnTextChanged(_ =>
                                {
                                    hasConfigurationValueDraft = true;
                                    _application?.Invalidate();
                                }),
                            window.Window)
                            .FillWidth(),
                    ]).FillWidth());
                }

                detailWidgets.Add(builder.Text(status));
            }

            var actions = new List<Hex1bWidget>
            {
                builder.Button("Close").OnClick(_ => window.Window.Cancel()),
                builder.Button("Reload").OnClick(async _ =>
                {
                    await _workspace.ReloadConfigurationAsync(_cancellationToken).ConfigureAwait(false);
                    var current = BuildConfigurationOptionItems(_workspace.Configuration)
                        .FirstOrDefault(option => string.Equals(option.Id, focusedId, StringComparison.Ordinal));
                    if (current is not null)
                    {
                        FocusOption(current);
                    }

                    _application?.Invalidate();
                }),
                builder.Button($"Scope: {FormatConfigurationScope(selectedScope)}").OnClick(_ =>
                {
                    selectedScope = NextConfigurationScope(selectedScope);
                    if (focused is not null)
                    {
                        FocusOption(focused);
                    }

                    _application?.Invalidate();
                }),
            };
            if (focused is not null &&
                focused.Definition.ValueKind is
                    GitConfigurationValueKind.Boolean or GitConfigurationValueKind.Enumeration)
            {
                actions.Add(builder.Button("Next value").OnClick(_ =>
                {
                    valueState.Text = CycleConfigurationValue(focused.Definition, valueState.Text);
                    valueState.CursorPosition = valueState.Text.Length;
                    hasConfigurationValueDraft = true;
                    _application?.Invalidate();
                }));
            }

            if (canReset)
            {
                var resetLabel = definition?.AllowsMultipleValues == true
                    ? "Review reset all..."
                    : "Review reset...";
                actions.Add(builder.Button(resetLabel).OnClick(_ =>
                {
                    var resetScope = selectedScope;
                    var resetKey = candidateKey;
                    var resetOption = focused!;
                    ShowConfigurationResetConfirmation(
                        windows,
                        resetScope,
                        resetKey,
                        async () =>
                        {
                            await _workspace.ResetConfigurationAsync(
                                resetScope,
                                resetKey,
                                _cancellationToken).ConfigureAwait(false);
                            FocusOption(resetOption);
                            _application?.Invalidate();
                        });
                }));
            }

            if (canRemove)
            {
                actions.Add(builder.Button("Review remove...").OnClick(_ =>
                {
                    var removeScope = selectedScope;
                    var removeKey = candidateKey;
                    var removeOption = focused!;
                    var currentItems = BuildConfigurationExplicitValueItems(
                        _workspace.Configuration,
                        removeKey,
                        removeScope,
                        removeOption.Definition);
                    if (currentItems.Count == 0)
                    {
                        return;
                    }

                    var currentIndex = Math.Clamp(
                        focusedExplicitValueIndex,
                        0,
                        currentItems.Count - 1);
                    var removeItem = currentItems[currentIndex];
                    var matchingValueCount = currentItems.Count(
                        item => item.Entry.Value.Equals(removeItem.Entry.Value));
                    ShowConfigurationRemoveValueConfirmation(
                        windows,
                        removeScope,
                        removeKey,
                        removeItem.Entry.Value,
                        removeOption.Definition,
                        matchingValueCount,
                        async () =>
                        {
                            await _workspace.RemoveConfigurationValueAsync(
                                removeScope,
                                removeKey,
                                removeItem.Entry.Value,
                                _cancellationToken).ConfigureAwait(false);
                            FocusOption(removeOption);
                            _application?.Invalidate();
                        });
                }));
            }

            if (canAdd)
            {
                actions.Add(builder.Button("Review add...").OnClick(_ =>
                {
                    var addScope = selectedScope;
                    var addKey = candidateKey;
                    var addValue = valueState.Text;
                    var addOption = focused!;
                    ShowConfigurationAddValueConfirmation(
                        windows,
                        addScope,
                        addKey,
                        addValue,
                        addOption.Definition,
                        async () =>
                        {
                            await _workspace.AddConfigurationValueAsync(
                                addScope,
                                addKey,
                                addValue,
                                _cancellationToken).ConfigureAwait(false);
                            FocusOption(addOption);
                            _application?.Invalidate();
                        });
                }));
            }

            if (canSave)
            {
                actions.Add(builder.Button("Review save...").OnClick(_ =>
                {
                    var saveScope = selectedScope;
                    var saveKey = candidateKey;
                    var saveValue = valueState.Text;
                    var saveOption = focused!;
                    ShowConfigurationSaveConfirmation(
                        windows,
                        saveScope,
                        saveKey,
                        saveValue,
                        saveOption.Definition,
                        async () =>
                        {
                            await _workspace.SetConfigurationAsync(
                                saveScope,
                                saveKey,
                                saveValue,
                                _cancellationToken).ConfigureAwait(false);
                            FocusOption(saveOption);
                            _application?.Invalidate();
                        });
                }));
            }

            return
            [
                builder.HStack(search =>
                [
                    search.Text("Find setting: "),
                    DismissOnEscape(
                        search.TextBox()
                            .State(filterState)
                            .OnTextChanged(_ =>
                            {
                                focusedId = null;
                                _application?.Invalidate();
                            }),
                        window.Window)
                        .FillWidth(),
                ]).FillWidth(),
                builder.Border(
                    DismissOnEscape(
                        builder.List(visible)
                            .ItemKey(static option => option.Id)
                            .FocusedIndex(focusedIndex)
                            .OnFocusChanged(eventArgs =>
                            {
                                if (eventArgs.FocusedIndex >= 0 && eventArgs.FocusedIndex < visible.Count)
                                {
                                    FocusOption(visible[eventArgs.FocusedIndex]);
                                    _application?.Invalidate();
                                }
                            })
                            .Empty(empty => empty.Text("No setting matches the current filter."))
                            .Fill(),
                        window.Window))
                    .Title($"Settings ({visible.Count}/{options.Count})")
                    .FixedHeight(8),
                builder.Border(
                    builder.VScrollPanel(_ => [.. detailWidgets], showScrollbar: true).Fill())
                    .Title(focused?.Key ?? "Setting details")
                    .Fill(),
                builder.WrapPanel(_ => [.. actions]),
                builder.Text("Tab moves focus | Mouse selects and activates | Esc or click outside closes"),
            ];
        }).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close options");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close options");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }))
        .Title("Options and Git configuration")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(28))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 20, 132, 52));

        void FocusOption(GitConfigurationOptionItem option)
        {
            focusedId = option.Id;
            focusedExplicitValueIndex = 0;
            hasConfigurationValueDraft = false;
            concreteKeyState.Text = option.Key;
            concreteKeyState.CursorPosition = concreteKeyState.Text.Length;
            if (option.Definition.MayContainSecret)
            {
                valueState.Text = string.Empty;
            }
            else if (option.IsTemplate || option.Definition.AllowsMultipleValues)
            {
                valueState.Text = option.Definition.DefaultValue ?? string.Empty;
            }
            else
            {
                var resolved = _workspace.Configuration.Resolve(option.Key, selectedScope);
                valueState.Text = GetEditableConfigurationValue(resolved);
            }

            valueState.CursorPosition = valueState.Text.Length;
        }
    }

    private void ShowConfigurationSaveConfirmation(
        WindowManager windows,
        GitConfigurationScope scope,
        string key,
        string value,
        GitConfigurationDefinition definition,
        Func<Task> saveAsync)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Scope: {FormatConfigurationScope(scope)} ({FormatConfigurationScopeSwitch(scope)})"),
            builder.Text($"Key: {key}"),
            builder.Text(
                $"{FormatConfigurationValueLabel(definition)}: " +
                FormatConfigurationDraftValue(definition, key, value)),
            builder.Text("GitSail will ask Git to replace this key only at the displayed scope."),
            definition.ExecutionKind == GitConfigurationExecutionKind.None
                ? builder.Text("No executable behavior is registered for this setting.")
                : builder.Text(
                    $"This selects {FormatConfigurationExecution(definition.ExecutionKind)} behavior; " +
                    "repository capability review remains separate."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Save exact change").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("save");
                    await saveAsync().ConfigureAwait(false);
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel configuration save")))
        .Title("Save configuration change?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(12))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 10, 120, 20)
        .Modal());
    }

    private void ShowConfigurationAddValueConfirmation(
        WindowManager windows,
        GitConfigurationScope scope,
        string key,
        string value,
        GitConfigurationDefinition definition,
        Func<Task> addAsync)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Scope: {FormatConfigurationScope(scope)} ({FormatConfigurationScopeSwitch(scope)})"),
            builder.Text($"Key: {key}"),
            builder.Text(
                $"{FormatConfigurationValueLabel(definition)}: " +
                FormatConfigurationDraftValue(definition, key, value)),
            builder.Text("GitSail will ask Git to append this exact value only at the displayed scope."),
            definition.ExecutionKind == GitConfigurationExecutionKind.None
                ? builder.Text("No executable behavior is registered for this setting.")
                : builder.Text(
                    $"This selects {FormatConfigurationExecution(definition.ExecutionKind)} behavior; " +
                    "repository capability review remains separate."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Add exact value").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("add");
                    await addAsync().ConfigureAwait(false);
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel configuration value addition")))
        .Title("Add configuration value?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(12))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 10, 120, 20)
        .Modal());
    }

    private void ShowConfigurationRemoveValueConfirmation(
        WindowManager windows,
        GitConfigurationScope scope,
        string key,
        GitConfigurationValue value,
        GitConfigurationDefinition definition,
        int matchingValueCount,
        Func<Task> removeAsync)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Scope: {FormatConfigurationScope(scope)} ({FormatConfigurationScopeSwitch(scope)})"),
            builder.Text($"Key: {key}"),
            builder.Text(
                $"{FormatConfigurationValueLabel(definition)}: " +
                FormatConfigurationValue(definition, key, value)),
            matchingValueCount == 1
                ? builder.Text("Only the exact selected-scope value displayed above will be removed.")
                : builder.Text(
                    $"Git stores {matchingValueCount} equal selected-scope values; " +
                    "all equal occurrences will be removed together."),
            builder.Text("Other values for this key and inherited values remain unchanged."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Remove exact value").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("remove");
                    await removeAsync().ConfigureAwait(false);
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel configuration value removal")))
        .Title("Remove configuration value?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(12))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 10, 120, 20)
        .Modal());
    }

    private void ShowConfigurationResetConfirmation(
        WindowManager windows,
        GitConfigurationScope scope,
        string key,
        Func<Task> resetAsync)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Scope: {FormatConfigurationScope(scope)} ({FormatConfigurationScopeSwitch(scope)})"),
            builder.Text($"Key: {key}"),
            builder.Text("Only explicit values for this key at the displayed scope will be removed."),
            builder.Text("The next value from normal Git precedence, or the application default, will become visible."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Reset exact value").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("reset");
                    await resetAsync().ConfigureAwait(false);
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel configuration reset")))
        .Title("Reset configuration value?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(11))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 9, 120, 18)
        .Modal());
    }

    private static List<GitConfigurationOptionItem> BuildConfigurationOptionItems(
        GitConfigurationSnapshot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new List<GitConfigurationOptionItem>();
        foreach (var definition in GitConfigurationRegistry.Definitions)
        {
            if (!definition.IsPattern)
            {
                options.Add(new GitConfigurationOptionItem(
                    definition.KeyPattern,
                    definition,
                    IsTemplate: false));
                continue;
            }

            var concreteKeys = configuration.Entries
                .Select(static entry => entry.Key.DisplayText)
                .Where(definition.Matches)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static key => key, StringComparer.Ordinal);
            options.AddRange(concreteKeys.Select(key => new GitConfigurationOptionItem(
                key,
                definition,
                IsTemplate: false)));
            options.Add(new GitConfigurationOptionItem(
                definition.KeyPattern,
                definition,
                IsTemplate: true));
        }

        return options;
    }

    private static List<GitConfigurationExplicitValueItem> BuildConfigurationExplicitValueItems(
        GitConfigurationSnapshot configuration,
        string key,
        GitConfigurationScope scope,
        GitConfigurationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(definition);
        var entries = configuration.GetExplicitValues(key, scope);
        var values = new List<GitConfigurationExplicitValueItem>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            values.Add(new GitConfigurationExplicitValueItem(
                index,
                entries[index],
                FormatConfigurationValue(definition, key, entries[index].Value)));
        }

        return values;
    }

    private static bool MatchesConfigurationFilter(
        GitConfigurationOptionItem option,
        string filter)
    {
        var query = filter.Trim();
        return option.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            option.Definition.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            option.Definition.ValueKind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
            option.Definition.ExecutionKind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateConcreteConfigurationKey(
        GitConfigurationOptionItem option,
        string key,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            error = "Enter a concrete configuration key.";
            return false;
        }

        if (key.Contains('*', StringComparison.Ordinal))
        {
            error = "Replace the * placeholder with one concrete name.";
            return false;
        }

        if (!option.Definition.Matches(key))
        {
            error = $"The key must match {option.Definition.KeyPattern}.";
            return false;
        }

        var registered = GitConfigurationRegistry.Find(key);
        if (registered is null || !string.Equals(
            registered.KeyPattern,
            option.Definition.KeyPattern,
            StringComparison.Ordinal))
        {
            error = "The key is not registered for this setting.";
            return false;
        }

        try
        {
            _ = GitConfigurationKey.FromBytes(s_strictUtf8.GetBytes(key));
        }
        catch (Exception exception) when (exception is ArgumentException or EncoderFallbackException)
        {
            error = exception.Message;
            return false;
        }

        error = null;
        return true;
    }

    private static string GetEditableConfigurationValue(ResolvedGitConfigurationValue resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var source = resolution.ExplicitEntry?.Value ?? resolution.EffectiveEntry?.Value;
        if (source is not null)
        {
            return TryDecodeConfigurationValue(source, out var text) ? text : string.Empty;
        }

        return resolution.EffectiveParsedValue?.Text ?? resolution.Definition.DefaultValue ?? string.Empty;
    }

    private static bool TryDecodeConfigurationValue(
        GitConfigurationValue value,
        out string text)
    {
        try
        {
            text = s_strictUtf8.GetString(value.GetBytes());
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string GetConfigurationDraftStatus(
        GitConfigurationOptionItem? option,
        GitConfigurationScope scope,
        string? keyError,
        string? valueError,
        bool alreadyExplicit,
        int explicitValueCount,
        bool hasValueDraft)
    {
        if (option is null)
        {
            return "Select a setting to inspect it.";
        }

        if (keyError is not null)
        {
            return $"Key: {TerminalTextSanitizer.Sanitize(keyError)}";
        }

        if (!option.Definition.IsTerminalApplicable)
        {
            return "This compatibility setting is read-only because it has no terminal equivalent.";
        }

        if (!option.Definition.CanWrite(scope))
        {
            return $"This setting cannot be written at {FormatConfigurationScope(scope)} scope.";
        }

        if (option.Definition.AllowsMultipleValues)
        {
            if (option.Definition.MayContainSecret && !hasValueDraft)
            {
                return explicitValueCount == 0
                    ? "Enter a new sensitive value to append; no current secret is loaded into the editor."
                    : "Select an explicit value to remove, or enter a new sensitive value to append.";
            }

            if (valueError is not null)
            {
                return $"New value: {TerminalTextSanitizer.Sanitize(valueError)}";
            }

            return explicitValueCount == 0
                ? $"Ready to review one exact {FormatConfigurationScope(scope)}-scope addition."
                : "Select an explicit value to remove, enter a new value to append, or reset the complete list.";
        }

        if (option.Definition.MayContainSecret && !hasValueDraft)
        {
            return "Enter a replacement sensitive value, or reset the existing value to reveal inheritance.";
        }

        if (valueError is not null)
        {
            return $"Value: {TerminalTextSanitizer.Sanitize(valueError)}";
        }

        return alreadyExplicit
            ? "The entered value already matches the selected scope's explicit value."
            : $"Ready to review one exact {FormatConfigurationScope(scope)}-scope change.";
    }

    private static string FormatConfigurationState(ResolvedGitConfigurationValue? resolution)
        => resolution?.State switch
        {
            GitConfigurationResolutionState.Absent => "not explicitly configured; application default applies",
            GitConfigurationResolutionState.Inherited => "inherited",
            GitConfigurationResolutionState.InheritedEmpty => "inherited explicit empty value",
            GitConfigurationResolutionState.InheritedInvalid => "inherited invalid value",
            GitConfigurationResolutionState.Explicit => "explicit at selected scope",
            GitConfigurationResolutionState.ExplicitEmpty => "explicit empty value at selected scope",
            GitConfigurationResolutionState.ExplicitInvalid => "explicit invalid value at selected scope",
            null => "enter a concrete key to resolve precedence",
            _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
        };

    private static string FormatEffectiveConfigurationValue(
        ResolvedGitConfigurationValue? resolution)
    {
        if (resolution is null)
        {
            return "not resolved";
        }

        if (resolution.EffectiveEntry is { } entry)
        {
            return FormatConfigurationValue(
                resolution.Definition,
                resolution.Key,
                entry.Value);
        }

        return FormatConfigurationDefault(resolution.Definition.DefaultValue);
    }

    private static string FormatConfigurationSource(ResolvedGitConfigurationValue? resolution)
    {
        if (resolution is null)
        {
            return "not resolved";
        }

        if (resolution.EffectiveEntry is not { } entry)
        {
            return "application default";
        }

        var origin = GitPath.FromUnixBytes(entry.Origin.GetBytes()).DisplayText;
        return $"{FormatConfigurationScope(entry.Scope)} — {TerminalTextSanitizer.Sanitize(origin)}";
    }

    private static string FormatConfigurationValue(
        GitConfigurationDefinition definition,
        string key,
        GitConfigurationValue value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsEmpty)
        {
            return "<explicit empty value>";
        }

        if (definition.MayContainSecret)
        {
            return FormatSensitiveConfigurationValue(key, value.GetBytes());
        }

        var display = TryDecodeConfigurationValue(value, out var text)
            ? text
            : GitPath.FromUnixBytes(value.GetBytes()).DisplayText;
        return FormatConfigurationDraftValue(display);
    }

    private static string FormatConfigurationDefault(string? value)
        => value switch
        {
            null => "<not set>",
            "" => "<empty>",
            _ => FormatConfigurationDraftValue(value),
        };

    private static string FormatConfigurationValueLabel(GitConfigurationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.MayContainSecret ? "Value (credential-redacted)" : "Value";
    }

    private static string FormatConfigurationDraftValue(
        GitConfigurationDefinition definition,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!definition.MayContainSecret || value.Length == 0)
        {
            return FormatConfigurationDraftValue(value);
        }

        try
        {
            return FormatSensitiveConfigurationValue(key, s_strictUtf8.GetBytes(value));
        }
        catch (EncoderFallbackException)
        {
            return "<redacted invalid Unicode value>";
        }
    }

    private static string FormatSensitiveConfigurationValue(
        string key,
        ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return "<empty>";
        }

        return IsRemoteUrlConfigurationKey(key)
            ? RemoteUrl.FromBytes(value).RedactedDisplayText
            : "<redacted>";
    }

    private static bool IsRemoteUrlConfigurationKey(string key)
        => key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) &&
            (key.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith(".pushurl", StringComparison.OrdinalIgnoreCase));

    private static string FormatConfigurationDraftValue(string value)
    {
        if (value.Length == 0)
        {
            return "<empty>";
        }

        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return TerminalTextSanitizer.Sanitize(escaped);
    }

    private static string FormatConfigurationValueKind(GitConfigurationDefinition definition)
    {
        var detail = definition.ValueKind switch
        {
            GitConfigurationValueKind.Boolean => "boolean",
            GitConfigurationValueKind.Integer =>
                $"integer {definition.Minimum?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-∞"}.." +
                $"{definition.Maximum?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "+∞"}",
            GitConfigurationValueKind.Enumeration =>
                $"one of {string.Join(", ", definition.AllowedValues)}",
            GitConfigurationValueKind.String => "text",
            GitConfigurationValueKind.NativePath => "native path",
            GitConfigurationValueKind.Color => "Git color expression",
            GitConfigurationValueKind.DiffOptions => "allowlisted diff options",
            GitConfigurationValueKind.ChordList => "comma-separated key chords",
            GitConfigurationValueKind.Layout => "versioned layout JSON",
            GitConfigurationValueKind.Capability => "versioned capability JSON",
            _ => throw new ArgumentOutOfRangeException(nameof(definition)),
        };
        return definition.AllowsMultipleValues ? $"{detail}, multiple values" : detail;
    }

    private static string CycleConfigurationValue(
        GitConfigurationDefinition definition,
        string current)
    {
        if (definition.ValueKind == GitConfigurationValueKind.Boolean)
        {
            return GitConfigurationValueValidator.TryParseText(
                    definition,
                    current,
                    out var parsed,
                    out _) && parsed?.BooleanValue == true
                ? bool.FalseString.ToLowerInvariant()
                : bool.TrueString.ToLowerInvariant();
        }

        if (definition.ValueKind != GitConfigurationValueKind.Enumeration ||
            definition.AllowedValues.IsEmpty)
        {
            return current;
        }

        var index = -1;
        for (var valueIndex = 0; valueIndex < definition.AllowedValues.Length; valueIndex++)
        {
            if (string.Equals(
                definition.AllowedValues[valueIndex],
                current,
                StringComparison.OrdinalIgnoreCase))
            {
                index = valueIndex;
                break;
            }
        }

        return definition.AllowedValues[(index + 1) % definition.AllowedValues.Length];
    }

    private static GitConfigurationScope NextConfigurationScope(GitConfigurationScope scope)
        => scope switch
        {
            GitConfigurationScope.Local => GitConfigurationScope.Worktree,
            GitConfigurationScope.Worktree => GitConfigurationScope.Global,
            GitConfigurationScope.Global => GitConfigurationScope.Local,
            _ => GitConfigurationScope.Local,
        };

    private static string FormatConfigurationScope(GitConfigurationScope scope)
        => scope switch
        {
            GitConfigurationScope.Local => "Repository",
            GitConfigurationScope.Worktree => "Worktree",
            GitConfigurationScope.Global => "Global",
            GitConfigurationScope.System => "System",
            GitConfigurationScope.Command => "Command override",
            GitConfigurationScope.Unknown => "Unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    private static string FormatConfigurationScopeSwitch(GitConfigurationScope scope)
        => scope switch
        {
            GitConfigurationScope.Local => "--local",
            GitConfigurationScope.Worktree => "--worktree",
            GitConfigurationScope.Global => "--global",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    private static string FormatConfigurationExecution(GitConfigurationExecutionKind execution)
        => execution switch
        {
            GitConfigurationExecutionKind.Hooks => "hooks",
            GitConfigurationExecutionKind.Diff => "external diff or text conversion",
            GitConfigurationExecutionKind.Filter => "content filtering",
            GitConfigurationExecutionKind.Tool => "configured tools",
            GitConfigurationExecutionKind.Editor => "an editor command",
            GitConfigurationExecutionKind.Browser => "a browser command",
            GitConfigurationExecutionKind.CredentialHelper => "a credential helper",
            GitConfigurationExecutionKind.Ssh => "an SSH command",
            GitConfigurationExecutionKind.Remote => "a remote transport command",
            GitConfigurationExecutionKind.Signing => "a signing program",
            GitConfigurationExecutionKind.None => "no executable",
            _ => throw new ArgumentOutOfRangeException(nameof(execution)),
        };

    private List<WorkspaceCommandItem> BuildWorkspaceCommands()
    {
        RememberFocusedWorkspaceEditor();
        var commands = new List<WorkspaceCommandItem>();
        var busy = _workspace.IsBusy ? "Another repository operation is running." : null;
        var editor = _commandEditor;
        var writableEditor = editor is not null && !editor.IsReadOnly;
        var hasEditorSelection = editor?.Cursors.Any(static cursor => cursor.HasSelection) == true;
        Add("edit.undo", "Edit", "Undo", "Undo the last edit in the active writable editor.", "Ctrl+Z",
            writableEditor && editor!.History.CanUndo ? null : "The active editor has no edit to undo.",
            () => Complete(() => MutateEditor(editor!, static state => state.Undo())));
        Add("edit.redo", "Edit", "Redo", "Redo the last undone edit in the active writable editor.", "Ctrl+Y",
            writableEditor && editor!.History.CanRedo ? null : "The active editor has no edit to redo.",
            () => Complete(() => MutateEditor(editor!, static state => state.Redo())));
        Add("edit.cut", "Edit", "Cut", "Copy and remove every selection in the active writable editor.", "Ctrl+X",
            writableEditor && hasEditorSelection ? null : "Select text in a writable editor before cutting.",
            () => Complete(() =>
            {
                CopyEditorSelection(editor!);
                MutateEditor(editor!, static state => state.DeleteForward());
            }));
        Add("edit.copy", "Edit", "Copy", "Copy every selection in the active editor through the configured terminal clipboard.", "Ctrl+C",
            hasEditorSelection ? null : "Select text in an editor before copying.",
            () => Complete(() => CopyEditorSelection(editor!)));
        Add("edit.delete", "Edit", "Delete", "Delete every selection or the next character in the active writable editor.", "Delete",
            writableEditor ? null : "Focus a writable editor before deleting text.",
            () => Complete(() => MutateEditor(editor!, static state => state.DeleteForward())));
        Add("edit.select-all", "Edit", "Select all", "Select the complete contents of the active editor.", "Ctrl+A",
            editor is null ? "Focus an editor before selecting text." : null,
            () => Complete(() => MutateEditor(editor!, static state => state.SelectAll())));
        AddWindow("help.context", "Help", "Context help", "Open the live keyboard, pointer, and workflow reference.", "F1", null,
            windows => Complete(() => ShowHelp(windows)));
        AddWindow("help.manual", "Help", "Offline manual", "Open the complete embedded GitSail manual inside the terminal.", string.Empty, null,
            windows => Complete(() => ShowOfflineManual(windows)));
        AddWindow("help.installation", "Help", "Installation and invocation", "Show global and local .NET tool commands and every supported GitSail invocation.", string.Empty, null,
            windows => Complete(() => ShowInstallationHelp(windows)),
            ["Help", "Repository"]);
        AddWindow("help.online-documentation", "Help", "Online documentation address", "Show and copy the official GitSail documentation address.", string.Empty, null,
            windows => Complete(() => ShowOnlineDocumentation(windows)));
        AddWindow("help.about", "Help", "About GitSail", "Show GitSail, package, runtime, Git, license, and command identity.", string.Empty, null,
            windows => Complete(() => ShowAbout(windows)));
        AddWindow("help.doctor", "Help", "Doctor and runtime", "Inspect the current build, runtime, Git, and repository capabilities.", string.Empty, null,
            windows => Complete(() => ShowDoctor(windows)),
            ["Help", "Tools"]);
        AddWindow("view.trace", "View", "Trace log", "Inspect the current sanitized structured trace without leaving the terminal.", "F2 Commands",
            ApplicationTrace.IsEnabled ? null : "Start GitSail with --trace to capture a trace.",
            windows => Complete(() => ShowTrace(windows)),
            ["View", "Help"]);
        Add("view.changed-path-filter", "View", "Find changed path", "Focus the shared unstaged and staged path filter.", "F7",
            null, () => Complete(FocusChangedPathFilter));
        Add("view.diff-text-search", "View", "Find in diff", "Focus case-insensitive text search for the current diff.", "Ctrl+F",
            null, () => Complete(FocusDiffSearch));
        AddWindow("view.branches", "Branch", "Branches and worktrees", "Open searchable local and remote-tracking branches with linked-worktree state.", "F8", busy,
            ShowBranchesAsync);
        AddWindow("view.worktrees", "Repository", "Linked worktrees", "Open searchable linked worktrees with create, open, lock, move, repair, remove, and prune actions.", string.Empty, busy,
            ShowWorktreesAsync);
        Add("repository.browse", "Repository", "Browse repository tree", "Open the revision tree browser and return to this workspace when it closes.", string.Empty,
            _mode == ApplicationMode.Gui ? null : "Repository browsing is available from the main workspace.",
            () => RequestDestinationAsync(RepositoryWorkspaceDestination.Browser));
        Add("history.graph", "History", "Repository history", "Open the searchable commit graph and return to this workspace when it closes.", string.Empty,
            _mode == ApplicationMode.Gui ? null : "Repository history is available from the main workspace.",
            () => RequestDestinationAsync(RepositoryWorkspaceDestination.History));
        AddWindow("view.remotes", "Remote", "Remotes and transport", "Open searchable remotes, fetch/prune controls, and separate transport output channels.", string.Empty, busy,
            ShowRemotesAsync);
        AddWindow("view.stashes", "Stash", "Stashes and exact patches", "Open searchable stash entries, exact patch previews, and lifecycle actions.", "F9", busy,
            ShowStashesAsync);
        Add("repository.refresh", "Repository", "Refresh", "Rescan repository status, exact diffs, warnings, and conflict state.", "F5 / Ctrl+R",
            busy, () => _workspace.RefreshAsync(_cancellationToken));
        AddWindow("repository.statistics", "Repository", "Repository statistics", "Inspect Git's exact object and pack storage counts without exposing alternate object-database paths.", string.Empty,
            busy, ShowRepositoryCareAsync, ["Repository", "Tools"]);
        AddWindow("repository.maintenance", "Repository", "Run configured maintenance", "Review and run the foreground maintenance tasks selected by Git configuration.", string.Empty,
            busy, windows => Complete(() => ShowConfiguredMaintenanceConfirmation(windows)), ["Repository", "Tools"]);
        AddWindow("repository.gc", "Repository", "Run garbage collection", "Review and run foreground Git garbage collection with configured expiry behavior.", string.Empty,
            busy, windows => Complete(() => ShowGarbageCollectionConfirmation(windows)), ["Repository", "Tools"]);
        AddWindow("repository.verify", "Repository", "Verify repository", "Review and run complete Git object and reference integrity verification without writing lost-found files.", string.Empty,
            busy, windows => Complete(() => ShowRepositoryVerificationConfirmation(windows)), ["Repository", "Tools"]);
        AddWindow("commit.spelling", "Commit", "Spelling", "Inspect checker status, retry checking, and review the next possible misspelling.", "Shift+F7",
            null, windows => Complete(() => ShowSpellingStatus(windows)), ["Commit", "Tools"]);
        var branch = _workspace.Branches.FocusedItem?.Branch;
        AddWindow("branch.create", "Branch", "Create branch...", "Create and switch to a local branch from the selected exact branch object.", string.Empty,
            branch is null || branch.SymbolicTarget is not null
                ? "Open Branches and select a nonsymbolic starting branch first."
                : busy,
            windows => branch is null
                ? Task.CompletedTask
                : Complete(() => ShowCreateBranchDialog(windows, branchWindow: null, branch)));
        Add("branch.checkout", "Branch", "Switch to selected branch", "Switch the current worktree to the selected unoccupied local branch.", string.Empty,
            branch is not { Kind: BranchKind.Local, IsCurrent: false, OccupiedWorktrees.IsEmpty: true }
                ? "Open Branches and select an unoccupied noncurrent local branch first."
                : busy,
            () => branch is null
                ? Task.CompletedTask
                : _workspace.SwitchBranchAsync(branch, _cancellationToken));
        Add("branch.detach", "Branch", "Detach at selected branch", "Detach HEAD at the exact object named by the selected branch.", string.Empty,
            branch is null || branch.SymbolicTarget is not null
                ? "Open Branches and select a nonsymbolic branch first."
                : busy,
            () => branch is null
                ? Task.CompletedTask
                : _workspace.DetachBranchAsync(branch, _cancellationToken));
        AddWindow("branch.rename", "Branch", "Rename selected branch...", "Rename the selected local branch while preserving its exact target object.", string.Empty,
            branch is not { Kind: BranchKind.Local } ||
                (!branch.IsCurrent && !branch.OccupiedWorktrees.IsEmpty)
                ? "Open Branches and select the current or an unoccupied local branch first."
                : busy,
            windows => branch is null
                ? Task.CompletedTask
                : Complete(() => ShowRenameBranchDialog(windows, branchWindow: null, branch)));
        AddWindow("branch.delete", "Branch", "Delete selected branch...", "Review safe and forced deletion choices for the selected unoccupied local branch.", string.Empty,
            branch is not { Kind: BranchKind.Local, IsCurrent: false, OccupiedWorktrees.IsEmpty: true }
                ? "Open Branches and select an unoccupied noncurrent local branch first."
                : busy,
            windows => branch is null
                ? Task.CompletedTask
                : Complete(() => ShowDeleteBranchDialog(windows, branchWindow: null, branch)));
        AddWindow("branch.reset", "Branch", "Reset current branch...", "Choose an exact revision and soft, mixed, or hard reset mode for the current branch.", string.Empty,
            branch is not { Kind: BranchKind.Local, IsCurrent: true }
                ? "Open Branches and select the current local branch first."
                : busy,
            windows => branch is null
                ? Task.CompletedTask
                : Complete(() => ShowResetBranchDialog(windows, branchWindow: null, branch)));
        AddWindow("branch.upstream", "Branch", "Change branch upstream...", "Set, change, or remove the selected local branch's exact remote-tracking upstream.", string.Empty,
            branch is not { Kind: BranchKind.Local }
                ? "Open Branches and select one local branch first."
                : busy,
            windows => branch is null
                ? Task.CompletedTask
                : Complete(() => ShowBranchUpstreamDialog(windows, branchWindow: null, branch)));
        if (!IsResolutionOnlyMode)
        {
            AddWindow("commit.primary", "Commit", GetPrimaryActionLabel(), GetPrimaryActionDescription(), "F4",
                CanRunPrimaryAction() ? null : GetPrimaryActionUnavailableLabel(),
                RunPrimaryActionAsync);
        }
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
        Add("diff.prepare-untracked", "Commit", "Prepare untracked hunks", "Add intent-to-add for the focused untracked path so hunk and line staging becomes available.", "P",
            _workspace.CanPrepareUntrackedPatch ? null : "Focus one eligible untracked file first.",
            () => _workspace.PrepareFocusedUntrackedPatchAsync(_cancellationToken));
        Add("diff.stage-hunk", "Commit", "Stage focused hunk", "Stage the exact focused worktree hunk.", "S in diff",
            _workspace.CanStageFocusedHunk ? null : "No stageable worktree hunk is focused.",
            () => _workspace.StageFocusedHunkAsync(_cancellationToken));
        Add("diff.unstage-hunk", "Commit", "Unstage focused hunk", "Unstage the exact focused index hunk.", "U in diff",
            _workspace.CanUnstageFocusedHunk ? null : "No unstageable index hunk is focused.",
            () => _workspace.UnstageFocusedHunkAsync(_cancellationToken));
        Add("diff.stage-lines", "Commit", "Stage selected lines", "Stage the exact selected added and context lines from the focused worktree patch.", "L",
            _workspace.CanStageSelectedLines ? null : "The current editor selection cannot be staged.",
            () => _workspace.StageSelectedLinesAsync(_cancellationToken));
        Add("diff.unstage-lines", "Commit", "Unstage selected lines", "Unstage the exact selected added and context lines from the focused index patch.", "L",
            _workspace.CanUnstageSelectedLines ? null : "The current editor selection cannot be unstaged.",
            () => _workspace.UnstageSelectedLinesAsync(_cancellationToken));
        AddWindow("diff.revert", "Commit", "Revert changes", "Review the available file, hunk, or selected-line destructive revert choices.", "R",
            CanRevert() ? null : "No revertible file, hunk, or line selection is available.",
            windows => Complete(() => ShowRevertConfirmation(windows)));
        Add("diff.undo-revert", "Commit", "Undo last revert", "Restore the most recent exact revert while its repository preconditions still match.", "Ctrl+Z",
            _workspace.CanUndoRevert ? null : "No current revert undo transaction is available.",
            () => _workspace.UndoRevertAsync(_cancellationToken));
        Add("diff.less-context", "View", "Decrease diff context", "Regenerate exact repository diffs with one fewer context line.", "[",
            _workspace.IsBusy || _workspace.DiffContextLines == 0 ? "Diff context cannot be decreased now." : null,
            () => _workspace.DecreaseDiffContextAsync(_cancellationToken));
        Add("diff.more-context", "View", "Increase diff context", "Regenerate exact repository diffs with one more context line.", "]",
            busy, () => _workspace.IncreaseDiffContextAsync(_cancellationToken));
        AddWindow("merge.abort", "Merge", "Abort merge", "Review the exact merge, worktree, index, and autostash state before asking Git to abort.", string.Empty,
            _workspace.CanAbortMerge ? null : "No verified active merge can be aborted.",
            windows => Complete(() => ShowAbortMergeConfirmation(windows)));
        var mergeSource = _workspace.Branches.FocusedItem?.Branch;
        AddWindow("merge.selected-branch", "Merge", "Merge selected branch", "Prepare an exact confirmation for the selected nonsymbolic branch and target object.", string.Empty,
            mergeSource is null || mergeSource.IsCurrent || mergeSource.SymbolicTarget is not null
                ? "Open Branches and select a noncurrent nonsymbolic branch first."
                : busy,
            windows => mergeSource is null
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
        var remote = _workspace.Remotes.FocusedItem?.Remote;
        AddWindow("remote.add", "Remote", "Add remote...", "Add a Git-validated remote name and literal URL.", string.Empty,
            busy, windows => Complete(() => ShowAddRemoteDialog(windows, remoteWindow: null)));
        AddWindow("remote.fetch-selected", "Remote", "Fetch selected remote", "Fetch the exact selected configured remote with Git-configured pruning and tags.", string.Empty,
            remote is null ? "Open Remotes and select one exact remote first." : busy,
            windows => remote is null
                ? Task.CompletedTask
                : StartCredentialOperation(
                    windows,
                    token => _workspace.FetchRemoteAsync(
                        remote,
                        FetchOptions.CreateDefault(),
                        token)));
        AddWindow("remote.push-selected", "Remote", "Push selected remote", "Resolve Git's complete default push into exact source, destination, OID, lease, and commit-count confirmation.", string.Empty,
            remote is null ? "Open Remotes and select one exact remote first." : busy,
            windows => remote is null
                ? Task.CompletedTask
                : ShowPushRemoteDialogAsync(windows, remoteWindow: null, remote));
        AddWindow("remote.push-tag-selected", "Remote", "Push tag to selected remote", "Select an exact local tag ref and review its per-destination OID lease plan.", string.Empty,
            remote is null ? "Open Remotes and select one exact remote first." : busy,
            windows => remote is null
                ? Task.CompletedTask
                : ShowTagPushSelectorAsync(windows, remoteWindow: null, remote));
        AddWindow("remote.delete-branch-selected", "Remote", "Delete branch from selected remote", "Select an exact advertised branch and review its per-destination deletion leases.", string.Empty,
            remote is null ? "Open Remotes and select one exact remote first." : busy,
            windows => remote is null
                ? Task.CompletedTask
                : ShowRemoteBranchDeletionSelectorAsync(windows, remoteWindow: null, remote));
        AddWindow("remote.initialize-selected", "Remote", "Initialize selected remote target", "Select one configured push URL, resolve its effective local or SSH target, and create a verified bare repository.", string.Empty,
            remote is null ? "Open Remotes and select one exact remote first." : busy,
            windows => remote is null
                ? Task.CompletedTask
                : ShowRemoteInitializationSelectorAsync(windows, remoteWindow: null, remote));
        AddWindow("remote.fetch-all", "Remote", "Fetch all remotes", "Fetch every remote from the exact displayed complete catalog.", string.Empty,
            _workspace.Remotes.Catalog is null || _workspace.Remotes.Catalog.Remotes.IsEmpty
                ? "Open Remotes and load at least one configured remote first."
                : busy,
            windows => StartCredentialOperation(
                windows,
                token => _workspace.FetchAllRemotesAsync(
                    FetchOptions.CreateDefault(),
                    token)));
        AddWindow("remote.prune-selected", "Remote", "Prune selected remote...", "Review Git's dry-run result before pruning stale remote-tracking references.", string.Empty,
            remote is null ? "Open Remotes and select one exact remote first." : busy,
            windows => remote is null
                ? Task.CompletedTask
                : ShowPruneRemoteDialogAsync(windows, remoteWindow: null, remote));
        AddWindow("remote.remove-selected", "Remote", "Remove selected remote...", "Review the selected remote configuration and tracking-reference removal before continuing.", string.Empty,
            remote is null ? "Open Remotes and select one exact remote first." : busy,
            windows => remote is null
                ? Task.CompletedTask
                : Complete(() => ShowRemoveRemoteDialog(windows, remoteWindow: null, remote)));
        var stash = _workspace.Stashes.FocusedItem?.Stash;
        AddWindow("stash.save", "Stash", "Save stash...", "Save selected worktree and index changes with tracked, untracked, ignored, keep-index, and staged-only choices.", "N in Stashes",
            busy, windows => Complete(() => ShowCreateStashDialog(windows, stashWindow: null)));
        AddWindow("stash.apply-selected", "Stash", "Apply selected stash...", "Apply the selected exact stash while retaining its reflog entry.", string.Empty,
            stash is null ? "Open Stashes and select one exact stash first." : busy,
            windows => stash is null
                ? Task.CompletedTask
                : Complete(() => ShowApplyStashDialog(windows, stashWindow: null, stash, pop: false)));
        AddWindow("stash.pop-selected", "Stash", "Pop selected stash...", "Apply the selected exact stash and drop it only after a clean application.", string.Empty,
            stash is null ? "Open Stashes and select one exact stash first." : busy,
            windows => stash is null
                ? Task.CompletedTask
                : Complete(() => ShowApplyStashDialog(windows, stashWindow: null, stash, pop: true)));
        AddWindow("stash.drop-selected", "Stash", "Drop selected stash...", "Review permanent removal of the selected exact stash reflog entry.", string.Empty,
            stash is null ? "Open Stashes and select one exact stash first." : busy,
            windows => stash is null
                ? Task.CompletedTask
                : Complete(() => ShowDropStashDialog(windows, stashWindow: null, stash)));
        AddWindow(
            "tool.manage",
            "Tools",
            "Manage configured tools...",
            "Add, edit, or remove user-defined Git GUI tools at repository, worktree, or global scope.",
            string.Empty,
            busy,
            ShowConfiguredToolManagerAsync);
        AddWindow(
            "tool.ssh-key.create",
            "Tools",
            "Create SSH key...",
            "Create a reviewed Ed25519, RSA 4096, or ECDSA 521 key through terminal-attached ssh-keygen.",
            string.Empty,
            _workspace.SshKeyCreationUnavailableReason ?? busy,
            windows => Complete(() => ShowSshKeyCreation(windows)));
        foreach (var tool in _workspace.ConfiguredTools.Tools)
        {
            var configuredTool = tool;
            var unavailableReason = configuredTool.UnavailableReason ?? busy;
            if (unavailableReason is null &&
                configuredTool.NeedsFile &&
                _workspace.State.FocusedItem is null)
            {
                unavailableReason = "Focus one changed path before running this configured tool.";
            }

            AddWindow(
                CreateConfiguredToolCommandId(configuredTool),
                "Tools",
                TerminalTextSanitizer.Sanitize(configuredTool.Title),
                $"Review and run configured Git GUI tool {TerminalTextSanitizer.Sanitize(configuredTool.Name)}.",
                string.Empty,
                unavailableReason,
                windows => RunConfiguredToolCommand(windows, configuredTool));
        }

        AddWindow("configuration.options", "Edit", "Options...", "Inspect and edit typed global, repository, worktree, and inherited Git settings.", string.Empty,
            busy, ShowConfigurationOptionsAsync);
        Add("commit.options", "Edit", "Commit options", "Show or hide author, amend, signoff, signing, cleanup, and hook-bypass controls.", string.Empty,
            busy, () => Complete(ToggleCommitOptions));
        Add("commit.toggle-amend", "Commit", "Toggle amend", "Toggle whether the next commit amends the exact current HEAD.", string.Empty,
            busy, () => ToggleAmendAsync());
        Add("commit.toggle-signoff", "Commit", "Toggle signoff", "Toggle Git's signoff trailer for the next commit.", string.Empty,
            busy, () => Complete(ToggleSignoff));
        Add("commit.toggle-signing", "Commit", "Toggle commit signing", "Toggle commit signing for the next commit transaction.", string.Empty,
            busy, () => Complete(ToggleSignCommit));
        Add("commit.cycle-cleanup", "Commit", "Cycle cleanup mode", "Cycle through Git-owned commit message cleanup modes.", string.Empty,
            busy, () => Complete(CycleCleanupMode));
        AddWindow("commit.without-hooks", "Commit", "Commit without bypassable hooks", "Review a warning before asking Git to bypass pre-commit and commit-msg.", string.Empty,
            _options.Citool?.NoCommit == true || !_workspace.CanCommit
                ? "No commit-without-hooks transaction is available."
                : null,
            windows => Complete(() => ShowCommitWithoutHooksConfirmation(windows)));
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
            Func<Task> executeAsync,
            IReadOnlyList<string>? menuCategories = null)
            => AddWindow(
                id,
                category,
                label,
                description,
                binding,
                unavailableReason,
                _ => executeAsync(),
                menuCategories);

        void AddWindow(
            string id,
            string category,
            string label,
            string description,
            string binding,
            string? unavailableReason,
            Func<WindowManager, Task> executeAsync,
            IReadOnlyList<string>? menuCategories = null)
            => commands.Add(new WorkspaceCommandItem(
                id,
                category,
                label,
                description,
                binding,
                unavailableReason,
                executeAsync,
                menuCategories));

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
        WindowHandle paletteWindow,
        WindowManager windows)
    {
        if (command?.IsAvailable != true)
        {
            return;
        }

        paletteWindow.CloseWithResult(command.Id);
        await command.ExecuteAsync(windows).ConfigureAwait(false);
    }

    private static async Task ExecuteApplicationMenuCommandAsync(
        WorkspaceCommandItem? command,
        WindowHandle menuWindow,
        WindowManager windows)
    {
        if (command?.IsAvailable != true)
        {
            return;
        }

        menuWindow.CloseWithResult(command.Id);
        await command.ExecuteAsync(windows).ConfigureAwait(false);
    }

    private static string GetCommandAvailabilityText(WorkspaceCommandItem? command)
        => command is null
            ? "Availability: no matching action"
            : command.IsAvailable
                ? $"Available now{(command.Binding.Length == 0 ? string.Empty : $" | Binding: {command.Binding}")}"
                : $"Unavailable: {command.UnavailableReason}";

    private void ShowHelp(WindowManager windows)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.WrapPanel(actions =>
            [
                actions.Button(AppMessages.CommonActionClose).OnClick(_ => window.Window.Cancel()),
                actions.Button(AppMessages.CommonActionDoctor).OnClick(_ => ShowDoctor(windows)),
                actions.Button("Manual").OnClick(_ => ShowOfflineManual(windows)),
                actions.Button("Install").OnClick(_ => ShowInstallationHelp(windows)),
                actions.Button("About").OnClick(_ => ShowAbout(windows)),
            ]),
            builder.VScrollPanel(help =>
            [
                help.Text(AppMessages.HelpMode(
                    version: BuildInformation.DisplayVersion,
                    mode: _mode.ToString().ToLowerInvariant())).Wrap(),
                help.Text(AppMessages.HelpKeysPrimary).Wrap(),
                help.Text(AppMessages.HelpKeysRegions).Wrap(),
                help.Text(AppMessages.HelpKeysSearch).Wrap(),
                help.Text(AppMessages.HelpKeysRepository).Wrap(),
                help.Text(AppMessages.HelpKeysMenu).Wrap(),
                help.Text(AppMessages.HelpKeysRefresh).Wrap(),
                help.Text(AppMessages.HelpKeysRemotes).Wrap(),
                help.Text(AppMessages.HelpKeysIndex).Wrap(),
                help.Text(AppMessages.HelpKeysDiff).Wrap(),
                help.Text(AppMessages.HelpKeysHunks).Wrap(),
                help.Text(AppMessages.HelpKeysConflictChoices).Wrap(),
                help.Text(AppMessages.HelpKeysConflictActions).Wrap(),
                help.Text(AppMessages.HelpMouse).Wrap(),
                help.Text(AppMessages.HelpNotePalette).Wrap(),
                help.Text(AppMessages.HelpNoteDestructive).Wrap(),
                help.Text(AppMessages.HelpNoteReadOnly).Wrap(),
                help.Text(AppMessages.HelpNoteDoctorJson).Wrap(),
            ], showScrollbar: true).Fill(),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                AppMessages.HelpBindingClose);
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                AppMessages.HelpBindingClose);
        }))
        .Title(AppMessages.HelpTitle)
        .Size(_popupViewport.FitWidth(58), _popupViewport.FitHeight(16))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 120, 42));
    }

    private void ShowOfflineManual(WindowManager windows)
    {
        var manual = RenderOfflineManual();
        var lines = manual.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.WrapPanel(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                actions.Button("Copy manual").OnClick(_ => _application?.CopyToClipboard(manual)),
                actions.Button("Installation").OnClick(_ => ShowInstallationHelp(windows)),
            ]),
            builder.VScrollPanel(
                content =>
                [
                    .. lines.Select(line => content.Text(line.Length == 0 ? " " : line).Wrap()),
                ],
                showScrollbar: true).Fill(),
            builder.Text("Wheel/Page Up/Page Down scroll | Esc/click outside closes").Wrap(),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close the offline manual");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close the offline manual");
        }))
        .Title("GitSail offline manual")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(20))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 132, 48));
    }

    private void ShowInstallationHelp(WindowManager windows)
    {
        const string commands =
            "Install globally:\n" +
            "  dotnet tool install --global GitSail\n\n" +
            "Update globally:\n" +
            "  dotnet tool update --global GitSail\n\n" +
            "Uninstall globally:\n" +
            "  dotnet tool uninstall --global GitSail\n\n" +
            "Repository-pinned local tool:\n" +
            "  dotnet new tool-manifest\n" +
            "  dotnet tool install GitSail\n" +
            "  dotnet tool run git-tui\n\n" +
            "Supported commands:\n" +
            "  git-tui\n" +
            "  git tui\n" +
            "  dotnet tool run git-tui\n\n" +
            "Check this installation:\n" +
            "  git-tui doctor\n" +
            "  git-tui doctor --json\n\n" +
            "Generate shell completions:\n" +
            "  git-tui completion bash|zsh|fish|powershell";
        var lines = commands.Split('\n');
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.WrapPanel(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                actions.Button("Copy commands").OnClick(_ => _application?.CopyToClipboard(commands)),
                actions.Button("Manual").OnClick(_ => ShowOfflineManual(windows)),
            ]),
            builder.VScrollPanel(
                content =>
                [
                    .. lines.Select(line => content.Text(line.Length == 0 ? " " : line).Wrap()),
                ],
                showScrollbar: true).Fill(),
            builder.Text("GitSail does not require an application bundle, desktop shortcut, or package signing.").Wrap(),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close installation help");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close installation help");
        }))
        .Title("Installation and invocation")
        .Size(_popupViewport.FitWidth(72), _popupViewport.FitHeight(20))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 120, 44));
    }

    private void ShowOnlineDocumentation(WindowManager windows)
    {
        const string documentationAddress = "https://github.com/willibrandon/gitsail";
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.WrapPanel(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                actions.Button("Copy address").OnClick(
                    _ => _application?.CopyToClipboard(documentationAddress)),
            ]),
            builder.Text("Official GitSail documentation:").Wrap(),
            builder.Text(documentationAddress).Wrap(),
            builder.Text("The address is displayed and copyable without starting an external program.").Wrap(),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close online documentation address");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close online documentation address");
        }))
        .Title("Online documentation")
        .Size(_popupViewport.FitWidth(64), _popupViewport.FitHeight(10))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 10, 100, 24));
    }

    private void ShowAbout(WindowManager windows)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.WrapPanel(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                actions.Button("Documentation").OnClick(_ => ShowOnlineDocumentation(windows)),
            ]),
            builder.VScrollPanel(details =>
            [
                details.Text(BuildInformation.DisplayVersion).Wrap(),
                details.Text("Package: GitSail | Command: git-tui | Git subcommand: git tui").Wrap(),
                details.Text("License: MIT").Wrap(),
                details.Text($"Runtime identifier: {RuntimeInformation.RuntimeIdentifier}").Wrap(),
                details.Text($"Native AOT: {!RuntimeFeature.IsDynamicCodeSupported}").Wrap(),
                details.Text($"Git: {_workspace.Installation.Version}").Wrap(),
                details.Text("A cross-platform Git GUI experience for the terminal with keyboard and mouse support.").Wrap(),
            ], showScrollbar: true).Fill(),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close About GitSail");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close About GitSail");
        }))
        .Title("About GitSail")
        .Size(_popupViewport.FitWidth(64), _popupViewport.FitHeight(13))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 13, 100, 30));
    }

    private static string RenderOfflineManual()
    {
        using var output = new StringWriter();
        OfflineManualRenderer.Write(output);
        return output.ToString().Trim();
    }

    private void ShowTrace(WindowManager windows)
    {
        if (!ApplicationTrace.IsEnabled)
        {
            return;
        }

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Refresh").OnClick(_ => _application?.Invalidate()),
            ]),
            builder.Text($"File: {TerminalTextSanitizer.Sanitize(ApplicationTrace.FilePath ?? string.Empty)}"),
            builder.Text($"Name: {TerminalTextSanitizer.Sanitize(Path.GetFileName(ApplicationTrace.FilePath) ?? string.Empty)}"),
            builder.VScrollPanel(
                entries =>
                [
                    .. ApplicationTrace.GetDisplayEntries().Select(
                        entry => entries.Text(entry.ToString())),
                ],
                showScrollbar: true).Fill(),
            builder.Text("Events omit command arguments, environment values, input, output, and exception messages."),
            builder.Text("F2 opens | Wheel scrolls | Esc/click outside closes"),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close the trace log");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close the trace log");
        }))
        .Title("Trace log")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(14))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 14, 132, 48));
    }

    private void ShowDoctor(WindowManager windows)
    {
        var repository = _workspace.State.Snapshot.Repository.WorkTree?.DisplayText ??
            _workspace.State.Snapshot.Repository.GitDirectory.DisplayText;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
            ]),
            builder.VScrollPanel(details =>
            [
                details.Text($"Product: {BuildInformation.DisplayVersion}").Wrap(),
                details.Text($"Runtime identifier: {RuntimeInformation.RuntimeIdentifier}").Wrap(),
                details.Text($"Operating system: {TerminalTextSanitizer.Sanitize(RuntimeInformation.OSDescription)}").Wrap(),
                details.Text($"Architecture: {RuntimeInformation.ProcessArchitecture}").Wrap(),
                details.Text($"Native AOT: {!RuntimeFeature.IsDynamicCodeSupported}").Wrap(),
                details.Text($"Git: {_workspace.Installation.Version}").Wrap(),
                details.Text($"Git executable: {TerminalTextSanitizer.Sanitize(_workspace.Installation.Executable.Path)}").Wrap(),
                details.Text($"Repository: {repository}").Wrap(),
                details.Text($"Mode: {_mode.ToString().ToLowerInvariant()}").Wrap(),
                details.Text("Stable JSON: git tui doctor --json").Wrap(),
            ], showScrollbar: true).Fill(),
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
        .Size(_popupViewport.FitWidth(58), _popupViewport.FitHeight(14))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 14, 120, 30));
    }

    private async Task ShowRepositoryCareAsync(WindowManager windows)
    {
        await _workspace.LoadRepositoryStatisticsAsync(_cancellationToken).ConfigureAwait(false);
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var statistics = _workspace.Maintenance.Statistics;
            var content = new List<Hex1bWidget>
            {
                builder.WrapPanel(actions =>
                [
                    actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                    actions.Button("Refresh statistics").OnClick(
                        _ => _workspace.LoadRepositoryStatisticsAsync(_cancellationToken)),
                    actions.Button("Run maintenance...").OnClick(
                        _ => ShowConfiguredMaintenanceConfirmation(windows)),
                    actions.Button("Garbage collect...").OnClick(
                        _ => ShowGarbageCollectionConfirmation(windows)),
                    actions.Button("Verify objects...").OnClick(
                        _ => ShowRepositoryVerificationConfirmation(windows)),
                ]),
            };
            if (statistics is null)
            {
                content.Add(builder.Text("Repository statistics are unavailable. Use Refresh statistics to try again."));
            }
            else
            {
                content.Add(builder.HStack(columns =>
                [
                    columns.VStack(left =>
                    [
                        left.Text($"Loose objects: {statistics.LooseObjectCount}"),
                        left.Text($"Loose size: {statistics.LooseObjectSizeKiB} KiB"),
                        left.Text($"Packed objects: {statistics.PackedObjectCount}"),
                        left.Text($"Pack files: {statistics.PackCount}"),
                        left.Text($"Pack size: {statistics.PackSizeKiB} KiB"),
                    ]).FillWidth(),
                    columns.VStack(right =>
                    [
                        right.Text($"Prune-packable objects: {statistics.PrunePackableObjectCount}"),
                        right.Text($"Unrecognized files: {statistics.GarbageFileCount}"),
                        right.Text($"Unrecognized size: {statistics.GarbageSizeKiB} KiB"),
                        right.Text($"Alternate databases: {statistics.AlternateObjectDatabaseCount}"),
                        right.Text(statistics.GarbageFileCount == 0
                            ? "Object database reports no unrecognized files."
                            : "Warning: Git reports unrecognized object-database files."),
                    ]).FillWidth(),
                ]).FillWidth());
            }

            content.Add(builder.Text($"{_workspace.Maintenance.OutputTitle}:"));
            content.Add(DismissOnEscape(
                builder.Editor(_workspace.Maintenance.Output)
                    .LineNumbers()
                    .WordWrap(false),
                window.Window)
                .Fill());
            content.Add(builder.Text(
                "Counts come from Git | Output channels stay separate | Esc or click outside closes"));
            return [.. content];
        }).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close repository statistics");
            bindings.Key(Hex1bKey.F5).Action(
                _ => _workspace.LoadRepositoryStatisticsAsync(_cancellationToken),
                "Refresh repository statistics");
            bindings.Ctrl().Key(Hex1bKey.W).Action(
                _ => window.Window.Cancel(),
                "Close repository statistics");
        }))
        .Title("Repository statistics and maintenance")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 132, 48));
    }

    private void ShowConfiguredMaintenanceConfirmation(WindowManager windows)
        => ShowRepositoryCareConfirmation(
            windows,
            "Run configured maintenance?",
            "git maintenance run",
            [
                "Git will run the foreground maintenance tasks selected by repository and global configuration.",
                "Configured tasks can rewrite object storage, update maintenance data, and contact remotes for prefetch.",
            ],
            "Run maintenance",
            () => _workspace.RunConfiguredMaintenanceAsync(_cancellationToken));

    private void ShowGarbageCollectionConfirmation(WindowManager windows)
        => ShowRepositoryCareConfirmation(
            windows,
            "Run garbage collection?",
            "git gc --no-detach",
            [
                "Git will optimize object storage in the foreground and apply configured reflog and prune expiry rules.",
                "Objects older than the configured expiry thresholds may become permanently unavailable.",
            ],
            "Garbage collect",
            () => _workspace.RunGarbageCollectionAsync(_cancellationToken));

    private void ShowRepositoryVerificationConfirmation(WindowManager windows)
        => ShowRepositoryCareConfirmation(
            windows,
            "Verify repository integrity?",
            "git fsck --full --no-progress",
            [
                "Git will verify complete object connectivity, validity, and reference integrity without modifying the repository.",
                "GitSail does not pass --lost-found, so verification does not write recovery files.",
            ],
            "Run verification",
            () => _workspace.VerifyRepositoryAsync(_cancellationToken));

    private void ShowRepositoryCareConfirmation(
        WindowManager windows,
        string title,
        string command,
        IReadOnlyList<string> explanations,
        string actionLabel,
        Func<Task> operation)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text(command).Wrap(),
            .. explanations.Select(explanation => builder.Text(explanation).Wrap()),
            builder.HStack(buttons =>
            [
                buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                buttons.Text(" "),
                buttons.Button(actionLabel).OnClick(async _ =>
                {
                    window.Window.CloseWithResult(true);
                    await operation().ConfigureAwait(false);
                }),
            ]),
            builder.Text("Esc cancels | Mouse buttons supported"),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Cancel repository care operation");
        }))
        .Title(title)
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(10 + explanations.Count))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 10 + explanations.Count, 120, 24)
        .Modal());
    }

    private static Task Complete(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }

    private Task ShowRemotesAsync(WindowManager windows)
        => ShowRemotesAsync(windows, reload: true);

    private async Task ShowPersistentPushActionAsync(WindowManager windows)
    {
        await _workspace.LoadRemotesAsync(_cancellationToken).ConfigureAwait(false);
        var catalog = _workspace.Remotes.Catalog;
        if (catalog is null)
        {
            return;
        }

        if (catalog.Remotes.Length == 1)
        {
            await ShowPushRemoteDialogAsync(
                windows,
                remoteWindow: null,
                catalog.Remotes[0]).ConfigureAwait(false);
            return;
        }

        await ShowRemotesAsync(windows, reload: false).ConfigureAwait(false);
    }

    private async Task ShowRemotesAsync(WindowManager windows, bool reload)
    {
        if (reload)
        {
            await _workspace.LoadRemotesAsync(_cancellationToken).ConfigureAwait(false);
        }

        if (_workspace.Remotes.Catalog is null)
        {
            return;
        }

        var outputTab = 0;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(filter =>
            [
                filter.Text("Filter: "),
                DismissOnEscape(
                    filter.TextBox()
                        .State(_workspace.Remotes.Filter)
                        .OnTextChanged(eventArgs =>
                        {
                            _workspace.Remotes.SetFilter(eventArgs.NewText);
                            _application?.Invalidate();
                        }),
                    window.Window)
                    .FillWidth(),
            ]).FillWidth(),
            builder.VSplitter(
                builder.VStack(top =>
                [
                    top.List(_workspace.Remotes.VisibleItems)
                        .ItemKey(static item => item.Key)
                        .FocusedIndex(_workspace.Remotes.FocusedIndex)
                        .OnFocusChanged(async eventArgs =>
                        {
                            if (eventArgs.FocusedIndex >= 0 &&
                                eventArgs.FocusedIndex < _workspace.Remotes.VisibleItems.Length)
                            {
                                await _workspace.FocusRemoteAsync(eventArgs.FocusedIndex).ConfigureAwait(false);
                            }
                        })
                        .Empty(empty => empty.Text(
                            _workspace.Remotes.Catalog.Remotes.IsEmpty
                                ? "No remotes are configured."
                                : "No remote matches the filter."))
                        .InputBindings(bindings =>
                        {
                            bindings.Key(Hex1bKey.Enter).Action(
                                _ => Complete(() => ShowFetchFocusedRemoteDialog(
                                    windows,
                                    window.Window)),
                                "Review fetch options for the focused exact remote");
                            bindings.Key(Hex1bKey.F5).Action(
                                _ => _workspace.LoadRemotesAsync(_cancellationToken),
                                "Refresh exact remote configuration");
                            bindings.Key(Hex1bKey.N).Action(
                                _ => Complete(() => ShowAddRemoteDialog(windows, window.Window)),
                                "Add a validated remote name and URL");
                        }).Fill(),
                    top.VStack(details => BuildRemoteDetails(details)),
                    top.WrapPanel(actions => BuildRemoteActions(actions, windows, window.Window)),
                ]).Fill(),
                builder.TabPanel(tabs =>
                [
                    tabs.Tab("stdout", content =>
                    [
                        DismissOnEscape(
                            content.Editor(_workspace.TransportOutput.StandardOutput)
                                .LineNumbers()
                                .WordWrap(false),
                            window.Window)
                            .Fill(),
                    ]).Selected(outputTab == 0),
                    tabs.Tab("stderr / progress", content =>
                    [
                        DismissOnEscape(
                            content.Editor(_workspace.TransportOutput.StandardError)
                                .LineNumbers()
                                .WordWrap(false),
                            window.Window)
                            .Fill(),
                    ]).Selected(outputTab == 1),
                ])
                .OnSelectionChanged(eventArgs =>
                {
                    outputTab = eventArgs.SelectedIndex;
                    _application?.Invalidate();
                })
                .Fill(),
                11).Fill(),
            builder.Text($"{_workspace.TransportOutput.Title} | Enter fetch | Exact push/tag/delete plans | N add | F5 refresh | Mouse supported"),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close the remote workspace");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }))
        .Title("Remotes and transport")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 130, 48));
    }

    private Hex1bWidget[] BuildRemoteDetails<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var remote = _workspace.Remotes.FocusedItem?.Remote;
        if (remote is null)
        {
            return [context.Text("Select a remote to inspect credential-redacted fetch and push destinations.")];
        }

        var details = new List<Hex1bWidget>
        {
            context.Text($"Remote: {remote.Name.DisplayText}"),
        };
        if (remote.FetchUrls.IsEmpty)
        {
            details.Add(context.Text("Fetch URL: <not configured>"));
        }
        else
        {
            for (var index = 0; index < remote.FetchUrls.Length; index++)
            {
                details.Add(context.Text(
                    $"Fetch URL {index + 1}: {remote.FetchUrls[index].RedactedDisplayText}"));
            }
        }

        for (var index = 0; index < remote.PushUrls.Length; index++)
        {
            details.Add(context.Text(
                $"Push URL {index + 1}: {remote.PushUrls[index].RedactedDisplayText}"));
        }

        return [.. details];
    }

    private Hex1bWidget[] BuildRemoteActions<TParent>(
        WidgetContext<TParent> context,
        WindowManager windows,
        WindowHandle remoteWindow)
        where TParent : Hex1bWidget
    {
        var actions = new List<Hex1bWidget>
        {
            context.Button("Close").OnClick(_ => remoteWindow.Cancel()),
            context.Button("Refresh").OnClick(_ => _workspace.LoadRemotesAsync(_cancellationToken)),
            context.Button("Add...").OnClick(_ => ShowAddRemoteDialog(windows, remoteWindow)),
        };
        var catalog = _workspace.Remotes.Catalog;
        if (catalog is not null && !catalog.Remotes.IsEmpty)
        {
            actions.Add(context.Button("Fetch all...").OnClick(
                _ => ShowFetchAllRemotesDialog(windows, remoteWindow)));
        }

        var remote = _workspace.Remotes.FocusedItem?.Remote;
        if (remote is not null)
        {
            actions.Add(context.Button("Fetch...").OnClick(
                _ => Complete(() => ShowFetchFocusedRemoteDialog(windows, remoteWindow))));
            actions.Add(context.Button("Push...").OnClick(
                _ => ShowPushFocusedRemoteDialogAsync(windows, remoteWindow)));
            actions.Add(context.Button("Push tag...").OnClick(
                _ => ShowTagPushFocusedRemoteDialogAsync(windows, remoteWindow)));
            actions.Add(context.Button("Delete branch...").OnClick(
                _ => ShowRemoteBranchDeletionFocusedDialogAsync(windows, remoteWindow)));
            actions.Add(context.Button("Initialize...").OnClick(
                _ => ShowRemoteInitializationFocusedDialogAsync(windows, remoteWindow)));
            actions.Add(context.Button("Prune...").OnClick(
                _ => ShowPruneFocusedRemoteDialogAsync(windows, remoteWindow)));
            actions.Add(context.Button("Remove...").OnClick(
                _ => Complete(() => ShowRemoveFocusedRemoteDialog(windows, remoteWindow))));
        }

        return [.. actions];
    }

    private void ShowFetchFocusedRemoteDialog(WindowManager windows, WindowHandle remoteWindow)
    {
        var remote = _workspace.Remotes.FocusedItem?.Remote;
        if (remote is not null)
        {
            ShowFetchRemoteDialog(windows, remoteWindow, remote);
        }
    }

    private Task ShowPruneFocusedRemoteDialogAsync(
        WindowManager windows,
        WindowHandle remoteWindow)
    {
        var remote = _workspace.Remotes.FocusedItem?.Remote;
        return remote is null
            ? Task.CompletedTask
            : ShowPruneRemoteDialogAsync(windows, remoteWindow, remote);
    }

    private void ShowRemoveFocusedRemoteDialog(
        WindowManager windows,
        WindowHandle remoteWindow)
    {
        var remote = _workspace.Remotes.FocusedItem?.Remote;
        if (remote is not null)
        {
            ShowRemoveRemoteDialog(windows, remoteWindow, remote);
        }
    }

    private Task ShowPushFocusedRemoteDialogAsync(
        WindowManager windows,
        WindowHandle remoteWindow)
    {
        var remote = _workspace.Remotes.FocusedItem?.Remote;
        return remote is null
            ? Task.CompletedTask
            : ShowPushRemoteDialogAsync(windows, remoteWindow, remote);
    }

    private Task ShowTagPushFocusedRemoteDialogAsync(
        WindowManager windows,
        WindowHandle remoteWindow)
    {
        var remote = _workspace.Remotes.FocusedItem?.Remote;
        return remote is null
            ? Task.CompletedTask
            : ShowTagPushSelectorAsync(windows, remoteWindow, remote);
    }

    private Task ShowRemoteBranchDeletionFocusedDialogAsync(
        WindowManager windows,
        WindowHandle remoteWindow)
    {
        var remote = _workspace.Remotes.FocusedItem?.Remote;
        return remote is null
            ? Task.CompletedTask
            : ShowRemoteBranchDeletionSelectorAsync(windows, remoteWindow, remote);
    }

    private Task ShowRemoteInitializationFocusedDialogAsync(
        WindowManager windows,
        WindowHandle remoteWindow)
    {
        var remote = _workspace.Remotes.FocusedItem?.Remote;
        return remote is null
            ? Task.CompletedTask
            : ShowRemoteInitializationSelectorAsync(windows, remoteWindow, remote);
    }

    private async Task ShowRemoteInitializationSelectorAsync(
        WindowManager windows,
        WindowHandle? remoteWindow,
        RemoteInfo remote)
    {
        if (remote.PushUrls.Length == 1)
        {
            await PrepareRemoteInitializationAsync(
                windows,
                remoteWindow,
                remote,
                configuredUrlIndex: 0).ConfigureAwait(false);
            return;
        }

        var items = remote.PushUrls
            .Select(static (url, index) => new RemoteInitializationUrlItem(index, url))
            .ToImmutableArray();
        var filterState = new TextBoxState();
        RemoteInitializationUrlItem? focusedItem = null;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var filter = filterState.Text.Trim();
            var visible = string.IsNullOrEmpty(filter)
                ? items
                : [.. items.Where(item => item.ToString().Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase))];
            var focusedIndex = focusedItem is null ? 0 : visible.IndexOf(focusedItem);
            if (focusedIndex < 0 || focusedIndex >= visible.Length)
            {
                focusedIndex = 0;
            }

            var focused = visible.IsEmpty ? null : visible[focusedIndex];
            focusedItem = focused;
            return
            [
                builder.HStack(search =>
                [
                    search.Text("Filter URLs: "),
                    DismissOnEscape(
                        search.TextBox()
                            .State(filterState)
                            .OnTextChanged(_ =>
                            {
                                focusedItem = null;
                                _application?.Invalidate();
                            })
                            .OnSubmit(_ => SubmitFocusedUrlAsync(focused, window.Window)),
                        window.Window)
                        .FillWidth(),
                ]).FillWidth(),
                builder.List(visible)
                    .ItemKey(static item => item.Index)
                    .FocusedIndex(focusedIndex)
                    .OnFocusChanged(eventArgs =>
                    {
                        if (eventArgs.FocusedIndex >= 0 && eventArgs.FocusedIndex < visible.Length)
                        {
                            focusedItem = visible[eventArgs.FocusedIndex];
                            _application?.Invalidate();
                        }
                    })
                    .Empty(empty => empty.Text("No configured push URL matches this filter."))
                    .InputBindings(bindings => bindings.Key(Hex1bKey.Enter).Action(
                        _ => SubmitFocusedUrlAsync(focused, window.Window),
                        "Review exact initialization target"))
                    .Fill(),
                builder.Text(focused is null
                    ? "Select one exact configured push URL."
                    : $"Configured URL {focused.Index + 1}: {focused.Url.RedactedDisplayText}"),
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    focused is null
                        ? actions.Text("Review exact target unavailable")
                        : actions.Button("Review exact target").OnClick(
                            _ => SubmitFocusedUrlAsync(focused, window.Window)),
                ]),
                builder.Text("Type to filter | Up/Down select | Enter or mouse button reviews | Esc cancels"),
            ];
        }).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel initialization URL selection")))
        .Title("Select a remote initialization URL")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 124, 40)
        .Modal());

        async Task SubmitFocusedUrlAsync(
            RemoteInitializationUrlItem? item,
            WindowHandle selectionWindow)
        {
            if (item is null)
            {
                return;
            }

            selectionWindow.CloseWithResult(item.Index.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            await PrepareRemoteInitializationAsync(
                windows,
                remoteWindow,
                remote,
                item.Index).ConfigureAwait(false);
        }
    }

    private Task PrepareRemoteInitializationAsync(
        WindowManager windows,
        WindowHandle? remoteWindow,
        RemoteInfo remote,
        int configuredUrlIndex)
        => StartCredentialOperation(windows, async token =>
        {
            var plan = await _workspace.PrepareRemoteInitializationAsync(
                remote,
                configuredUrlIndex,
                token).ConfigureAwait(false);
            if (plan is not null)
            {
                ShowRemoteInitializationPlanDialog(windows, remoteWindow, plan);
            }
        });

    private void ShowRemoteInitializationPlanDialog(
        WindowManager windows,
        WindowHandle? remoteWindow,
        RemoteInitializationPlan plan)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Initialize exact bare repository").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("initialize");
                    remoteWindow?.CloseWithResult("initialize");
                    await StartCredentialOperation(
                        windows,
                        token => _workspace.InitializeRemoteAsync(
                            plan,
                            token)).ConfigureAwait(false);
                }),
            ]),
            builder.VScrollPanel(content =>
            [
                content.Text(GetRemoteInitializationPlanText(plan)),
            ], showScrollbar: true).Fill(),
            builder.Text("Only a new target is accepted. Existing files, directories, and links are never reused."),
            builder.Text("Git creates and verifies the bare repository; SSH uses one fixed framed POSIX program."),
        ]))
        .Title("Initialize exact bare repository?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 130, 42)
        .Modal());
    }

    private static string GetRemoteInitializationPlanText(RemoteInitializationPlan plan)
    {
        var builder = new StringBuilder()
            .Append("Remote: ")
            .AppendLine(plan.Remote.Name.DisplayText)
            .Append("Configured push URL ")
            .Append(plan.ConfiguredUrlIndex + 1)
            .Append(": ")
            .AppendLine(plan.ConfiguredUrl.RedactedDisplayText)
            .Append("Effective URL after Git rewriting: ")
            .AppendLine(plan.Target.Url.RedactedDisplayText)
            .Append("Object format: ")
            .AppendLine(plan.ObjectFormat switch
            {
                RepositoryObjectFormat.Sha1 => "SHA-1",
                RepositoryObjectFormat.Sha256 => "SHA-256",
                _ => throw new ArgumentOutOfRangeException(nameof(plan)),
            });
        if (plan.Target.Kind == RemoteInitializationKind.Local)
        {
            var path = OperatingSystem.IsWindows()
                ? GitPath.FromWindowsPath(plan.Target.LocalPath!)
                : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(plan.Target.LocalPath!));
            return builder
                .AppendLine("Transport: isolated local Git operation")
                .Append("New bare repository path: ")
                .AppendLine(path.DisplayText)
                .ToString();
        }

        var remotePath = GitPath.FromUnixBytes(plan.Target.RemotePath!);
        return builder
            .AppendLine("Transport: SSH")
            .Append("SSH destination: ")
            .AppendLine(plan.Target.SshDestination!.ToString())
            .Append("SSH port: ")
            .AppendLine(plan.Target.SshPort?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "configured default")
            .Append("Remote repository path: ")
            .AppendLine(remotePath.DisplayText)
            .Append("Verified decoder: ")
            .AppendLine(plan.SshDecoder switch
            {
                SshBase64Decoder.Gnu => "GNU base64",
                SshBase64Decoder.Bsd => "BSD base64",
                SshBase64Decoder.ShortOption => "base64 -d",
                SshBase64Decoder.OpenSsl => "OpenSSL",
                _ => throw new ArgumentOutOfRangeException(nameof(plan)),
            })
            .ToString();
    }

    private async Task ShowTagPushSelectorAsync(
        WindowManager windows,
        WindowHandle? remoteWindow,
        RemoteInfo remote)
    {
        var tags = await _workspace.LoadLocalTagsAsync(_cancellationToken).ConfigureAwait(false);
        ShowReferenceSelector(
            windows,
            "Push an exact local tag",
            "Filter tags: ",
            "No local tags exist.",
            "Review exact tag push",
            tags,
            tag => StartCredentialOperation(windows, async token =>
            {
                var plan = await _workspace.PrepareTagPushAsync(
                    remote,
                    tag,
                    token).ConfigureAwait(false);
                if (plan is not null)
                {
                    ShowPushPlanDialog(
                        windows,
                        remoteWindow,
                        plan,
                        title: "Push exact tag plan?",
                        submitLabel: "Push exact tag",
                        allowUpstream: false);
                }
            }));
    }

    private Task ShowRemoteBranchDeletionSelectorAsync(
        WindowManager windows,
        WindowHandle? remoteWindow,
        RemoteInfo remote)
        => StartCredentialOperation(windows, async token =>
        {
            var branches = await _workspace.LoadRemoteBranchesAsync(
                remote,
                token).ConfigureAwait(false);
            ShowReferenceSelector(
                windows,
                "Delete an exact advertised remote branch",
                "Filter branches: ",
                "No branch is advertised by the configured push destinations.",
                "Review exact deletion",
                branches,
                branch => StartCredentialOperation(windows, async innerToken =>
                {
                    var plan = await _workspace.PrepareRemoteBranchDeletionAsync(
                        remote,
                        branch,
                        innerToken).ConfigureAwait(false);
                    if (plan is not null)
                    {
                        ShowPushPlanDialog(
                            windows,
                            remoteWindow,
                            plan,
                            title: "Delete exact remote branch?",
                            submitLabel: "Delete exact remote branch",
                            allowUpstream: false,
                            initialSafety: PushSafetyMode.ExplicitLease,
                            forceTitle: "Delete remote branch without an expected-OID lease?",
                            forceSubmitLabel: "Delete without lease");
                    }
                }));
        });

    private void ShowReferenceSelector(
        WindowManager windows,
        string title,
        string filterLabel,
        string emptyText,
        string submitLabel,
        ImmutableArray<RefName> references,
        Func<RefName, Task> submit)
    {
        var filterState = new TextBoxState();
        RefName? focusedReference = null;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var filter = filterState.Text.Trim();
            var visible = string.IsNullOrEmpty(filter)
                ? references
                : [.. references.Where(reference => reference.DisplayText.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase))];
            var focusedIndex = focusedReference is null ? 0 : visible.IndexOf(focusedReference);
            if (focusedIndex < 0 || focusedIndex >= visible.Length)
            {
                focusedIndex = 0;
            }

            var focused = visible.IsEmpty ? null : visible[focusedIndex];
            focusedReference = focused;
            return
            [
                builder.HStack(search =>
                [
                    search.Text(filterLabel),
                    DismissOnEscape(
                        search.TextBox()
                            .State(filterState)
                            .OnTextChanged(_ =>
                            {
                                focusedReference = null;
                                _application?.Invalidate();
                            })
                            .OnSubmit(_ => SubmitFocusedReferenceAsync(focused, window.Window)),
                        window.Window)
                        .FillWidth(),
                ]).FillWidth(),
                builder.List(visible)
                    .ItemKey(static reference => reference)
                    .FocusedIndex(focusedIndex)
                    .OnFocusChanged(eventArgs =>
                    {
                        if (eventArgs.FocusedIndex >= 0 && eventArgs.FocusedIndex < visible.Length)
                        {
                            focusedReference = visible[eventArgs.FocusedIndex];
                            _application?.Invalidate();
                        }
                    })
                    .Empty(empty => empty.Text(emptyText))
                    .InputBindings(bindings => bindings.Key(Hex1bKey.Enter).Action(
                        _ => SubmitFocusedReferenceAsync(focused, window.Window),
                        submitLabel))
                    .Fill(),
                builder.Text(focused is null
                    ? "Select one exact fully qualified ref."
                    : $"Exact ref: {focused.DisplayText}"),
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    focused is null
                        ? actions.Text($"{submitLabel} unavailable")
                        : actions.Button(submitLabel).OnClick(
                            _ => SubmitFocusedReferenceAsync(focused, window.Window)),
                ]),
                builder.Text("Type to filter | Up/Down select | Enter or mouse button reviews | Esc cancels"),
            ];
        }).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel exact reference selection")))
        .Title(title)
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 120, 40)
        .Modal());

        async Task SubmitFocusedReferenceAsync(RefName? reference, WindowHandle selectionWindow)
        {
            if (reference is null)
            {
                return;
            }

            selectionWindow.CloseWithResult(reference.DisplayText);
            await submit(reference).ConfigureAwait(false);
        }
    }

    private Task ShowPushRemoteDialogAsync(
        WindowManager windows,
        WindowHandle? remoteWindow,
        RemoteInfo remote)
        => StartCredentialOperation(windows, async token =>
        {
            var plan = await _workspace.PreparePushAsync(
                remote,
                GitOptionOverride.Configured,
                token).ConfigureAwait(false);
            if (plan is not null)
            {
                ShowPushPlanDialog(windows, remoteWindow, plan);
            }
        });

    private void ShowPushPlanDialog(
        WindowManager windows,
        WindowHandle? remoteWindow,
        PushPlan plan,
        string title = "Push exact Git default plan?",
        string submitLabel = "Push exact plan",
        bool allowUpstream = true,
        PushSafetyMode initialSafety = PushSafetyMode.Normal,
        string forceTitle = "Force push without an expected-OID lease?",
        string forceSubmitLabel = "Force without lease")
    {
        var safety = initialSafety;
        var setUpstream = allowUpstream && plan.WouldSetUpstream;
        var validationMessage = string.Empty;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button(safety == PushSafetyMode.Force
                    ? "Continue to force warning"
                    : submitLabel).OnClick(async _ =>
                {
                    if (safety == PushSafetyMode.Normal &&
                        (plan.RequiresForce || plan.IncludesDeletion))
                    {
                        validationMessage =
                            "Select explicit leases for non-fast-forward updates or deletions.";
                        _application?.Invalidate();
                        return;
                    }

                    window.Window.CloseWithResult("push");
                    if (safety == PushSafetyMode.Force)
                    {
                        ShowUnleasedForceConfirmation(
                            windows,
                            remoteWindow,
                            plan,
                            setUpstream,
                            allowUpstream,
                            forceTitle,
                            forceSubmitLabel);
                        return;
                    }

                    remoteWindow?.CloseWithResult("push");
                    await StartCredentialOperation(
                        windows,
                        token => _workspace.PushAsync(
                            plan,
                            new PushOptions(safety, setUpstream, plan.FollowTags),
                            token)).ConfigureAwait(false);
                }),
            ]),
            builder.WrapPanel(options =>
            [
                options.Button(GetPushSafetyLabel(safety)).OnClick(_ =>
                {
                    safety = CyclePushSafetyMode(safety);
                    validationMessage = string.Empty;
                    _application?.Invalidate();
                }),
                allowUpstream
                    ? CanSetUpstream(plan)
                        ? options.Button(setUpstream ? "Set upstream [x]" : "Set upstream [ ]").OnClick(_ =>
                        {
                            setUpstream = !setUpstream;
                            _application?.Invalidate();
                        })
                        : options.Text("Set upstream unavailable")
                    : options.Text(string.Empty),
                options.Text(GetOverrideLabel("Follow tags", plan.FollowTags)),
            ]),
            builder.Text(validationMessage),
            builder.VScrollPanel(content =>
            [
                content.Text(GetPushPlanText(plan, allowUpstream)),
            ], showScrollbar: true).Fill(),
            builder.Text(GetPushSafetyExplanation(safety, plan)),
        ]))
        .Title(title)
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 130, 46)
        .Modal());
    }

    private void ShowUnleasedForceConfirmation(
        WindowManager windows,
        WindowHandle? remoteWindow,
        PushPlan plan,
        bool setUpstream,
        bool showUpstream,
        string title = "Force push without an expected-OID lease?",
        string submitLabel = "Force without lease")
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button(submitLabel).OnClick(async _ =>
                {
                    window.Window.CloseWithResult("unleased force");
                    remoteWindow?.CloseWithResult("unleased force");
                    await StartCredentialOperation(
                        windows,
                        token => _workspace.PushAsync(
                            plan,
                            new PushOptions(
                                PushSafetyMode.Force,
                                setUpstream,
                                plan.FollowTags),
                            token)).ConfigureAwait(false);
                }),
            ]),
            builder.Text("This removes every expected-OID lease from the confirmed push."),
            builder.Text("A destination changed after the last check can be overwritten and its commits can be lost."),
            builder.Text("The frozen source and destination refspecs remain unchanged; only remote OID protection is removed."),
            builder.VScrollPanel(content =>
            [
                content.Text(GetPushPlanText(plan, showUpstream)),
            ], showScrollbar: true).Fill(),
        ]))
        .Title(title)
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 130, 44)
        .Modal());
    }

    private static string GetPushPlanText(PushPlan plan, bool showUpstream)
    {
        var builder = new StringBuilder();
        builder.Append("Remote: ")
            .AppendLine(plan.Remote.Name.DisplayText);
        if (showUpstream)
        {
            builder.Append("Current upstream: ")
                .AppendLine(plan.UpstreamName?.DisplayText ?? "<none>")
                .Append("Automatic upstream setup: ")
                .AppendLine(plan.WouldSetUpstream ? "yes" : "no");
        }

        builder.Append("Resolved updates: ")
            .AppendLine(plan.Updates.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var updateIndex = 0; updateIndex < plan.Updates.Length; updateIndex++)
        {
            var update = plan.Updates[updateIndex];
            builder.AppendLine()
                .Append("Update ")
                .Append(updateIndex + 1)
                .AppendLine(":")
                .Append("  Source ref: ")
                .AppendLine(update.RefSpec.Source?.DisplayText ?? "<delete>")
                .Append("  Source OID: ")
                .AppendLine(update.SourceObjectId?.ToString() ?? "<none>")
                .Append("  Destination ref: ")
                .AppendLine(update.RefSpec.Destination.DisplayText);
            for (var destinationIndex = 0; destinationIndex < update.Destinations.Length; destinationIndex++)
            {
                var destination = update.Destinations[destinationIndex];
                builder.Append("  Destination ")
                    .Append(destinationIndex + 1)
                    .Append(": ")
                    .AppendLine(destination.Url.RedactedDisplayText)
                    .Append("    Expected remote OID: ")
                    .AppendLine(destination.ExpectedObjectId?.ToString() ?? "<absent>")
                    .Append("    Relationship: ")
                    .AppendLine(GetPushRelationshipLabel(destination.Relationship))
                    .Append("    Introduced commits: ")
                    .AppendLine(destination.CommitCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static string GetPushRelationshipLabel(PushRelationship relationship)
        => relationship switch
        {
            PushRelationship.New => "new ref",
            PushRelationship.UpToDate => "up to date",
            PushRelationship.FastForward => "fast-forward",
            PushRelationship.NonFastForward => "non-fast-forward",
            PushRelationship.Delete => "delete",
            _ => throw new ArgumentOutOfRangeException(nameof(relationship)),
        };

    private static string GetPushSafetyLabel(PushSafetyMode mode)
        => mode switch
        {
            PushSafetyMode.Normal => "Safety: normal with exact leases",
            PushSafetyMode.ExplicitLease => "Safety: allow rewrite with exact leases",
            PushSafetyMode.Force => "Safety: force without leases",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static PushSafetyMode CyclePushSafetyMode(PushSafetyMode mode)
        => mode switch
        {
            PushSafetyMode.Normal => PushSafetyMode.ExplicitLease,
            PushSafetyMode.ExplicitLease => PushSafetyMode.Force,
            PushSafetyMode.Force => PushSafetyMode.Normal,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static string GetPushSafetyExplanation(PushSafetyMode mode, PushPlan plan)
        => mode switch
        {
            PushSafetyMode.Normal => plan.RequiresForce || plan.IncludesDeletion
                ? "Normal mode cannot execute this rewrite or deletion. Select explicit leases or cancel."
                : "Every destination must still equal the displayed expected OID; only proven fast-forwards and new refs proceed.",
            PushSafetyMode.ExplicitLease =>
                "Every destination must still equal the displayed expected OID; confirmed rewrites and deletions may proceed.",
            PushSafetyMode.Force =>
                "Submitting opens a second warning because changed remote OIDs will not protect against lost commits.",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static bool CanSetUpstream(PushPlan plan)
        => plan.Updates.Any(static update =>
            update.RefSpec.Source is { } source && source.GetBytes().StartsWith("refs/heads/"u8));

    private void ShowFetchRemoteDialog(
        WindowManager windows,
        WindowHandle remoteWindow,
        RemoteInfo remote)
        => ShowFetchDialog(
            windows,
            remoteWindow,
            $"Fetch {remote.Name.DisplayText}?",
            "Fetch exact remote",
            (options, token) => _workspace.FetchRemoteAsync(remote, options, token));

    private void ShowFetchAllRemotesDialog(WindowManager windows, WindowHandle remoteWindow)
        => ShowFetchDialog(
            windows,
            remoteWindow,
            "Fetch every configured remote?",
            "Fetch all exact remotes",
            (options, token) => _workspace.FetchAllRemotesAsync(options, token));

    private void ShowFetchDialog(
        WindowManager windows,
        WindowHandle remoteWindow,
        string title,
        string confirmLabel,
        Func<FetchOptions, CancellationToken, Task> executeAsync)
    {
        var prune = GitOptionOverride.Configured;
        var tags = FetchTagMode.Configured;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button(confirmLabel).OnClick(async _ =>
                {
                    var options = new FetchOptions(prune, tags);
                    window.Window.CloseWithResult("fetch");
                    remoteWindow.CloseWithResult("fetch");
                    await StartCredentialOperation(
                        windows,
                        token => executeAsync(options, token)).ConfigureAwait(false);
                }),
            ]),
            builder.Button(GetFetchPruneLabel(prune)).OnClick(_ =>
            {
                prune = CycleOverride(prune);
                _application?.Invalidate();
            }),
            builder.Button(GetFetchTagLabel(tags)).OnClick(_ =>
            {
                tags = CycleFetchTagMode(tags);
                _application?.Invalidate();
            }),
            builder.Text(GetFetchExplanation(prune, tags)),
            builder.Text("Git owns transport, ref updates, pruning rules, progress, and failure behavior."),
            builder.Text("Stored credential helpers and SSH agents remain available; unavailable credentials fail without hanging."),
        ]))
        .Title(title)
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(12))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 10, 110, 18)
        .Modal());
    }

    private void ShowAddRemoteDialog(WindowManager windows, WindowHandle? remoteWindow)
    {
        var name = new TextBoxState("origin");
        var url = new TextBoxState();
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Add exact remote").OnClick(async _ =>
                {
                    var enteredName = name.Text;
                    var enteredUrl = url.Text;
                    window.Window.CloseWithResult("add");
                    remoteWindow?.CloseWithResult("add");
                    await _workspace.AddRemoteAsync(
                        enteredName,
                        enteredUrl,
                        _cancellationToken).ConfigureAwait(false);
                }),
            ]),
            builder.HStack(row =>
            [
                row.Text("Name: "),
                DismissOnEscape(row.TextBox().State(name), window.Window).FillWidth(),
            ]).FillWidth(),
            builder.HStack(row =>
            [
                row.Text("URL:  "),
                DismissOnEscape(row.TextBox().State(url), window.Window).FillWidth(),
            ]).FillWidth(),
            builder.Text("Git validates the name; the URL is passed as one literal argument after --."),
        ]))
        .Title("Add remote")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(10))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 9, 110, 16)
        .Modal());
    }

    private Task ShowPruneRemoteDialogAsync(
        WindowManager windows,
        WindowHandle? remoteWindow,
        RemoteInfo remote)
        => StartCredentialOperation(windows, async token =>
        {
            var plan = await _workspace.PreparePruneRemoteAsync(
                remote,
                token).ConfigureAwait(false);
            if (plan is null)
            {
                return;
            }

            var previewOutput = TransportTextFormatter.Format(
                plan.Preview.StandardOutput.Span,
                plan.Catalog);
            var previewError = TransportTextFormatter.Format(
                plan.Preview.StandardError.Span,
                plan.Catalog);
            var preview = string.IsNullOrEmpty(previewOutput) && string.IsNullOrEmpty(previewError)
                ? "Git reports no stale refs for this remote."
                : $"stdout:\n{previewOutput}\nstderr:\n{previewError}";
            OpenPopup(windows, windows.Window(window => window.VStack(builder =>
            [
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    actions.Button("Prune exact remote").OnClick(async _ =>
                    {
                        window.Window.CloseWithResult("prune");
                        remoteWindow?.CloseWithResult("prune");
                        await StartCredentialOperation(
                            windows,
                            innerToken => _workspace.PruneRemoteAsync(
                                plan,
                                innerToken)).ConfigureAwait(false);
                    }),
                ]),
                builder.Text($"Remote: {plan.Remote.Name.DisplayText}"),
                builder.Text("Git dry-run output bound to this exact complete remote catalog:"),
                builder.VScrollPanel(content =>
                [
                    content.Text(preview),
                ], showScrollbar: true).Fill(),
                builder.Text("Pruning deletes stale local references selected by this remote's configured refspecs."),
            ]))
            .Title("Prune stale remote refs?")
            .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(18))
            .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
            .Resizable(58, 14, 120, 32)
            .Modal());
        });

    private void ShowRemoveRemoteDialog(
        WindowManager windows,
        WindowHandle? remoteWindow,
        RemoteInfo remote)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var content = new List<Hex1bWidget>
            {
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    actions.Button("Remove exact remote").OnClick(async _ =>
                    {
                        window.Window.CloseWithResult("remove");
                        remoteWindow?.CloseWithResult("remove");
                        await _workspace.RemoveRemoteAsync(remote, _cancellationToken).ConfigureAwait(false);
                    }),
                ]),
                builder.Text($"Remote: {remote.Name.DisplayText}"),
            };
            content.AddRange(remote.FetchUrls.Select((url, index) =>
                builder.Text($"Fetch URL {index + 1}: {url.RedactedDisplayText}")));
            content.Add(builder.Text(
                "Git removes this remote's configuration and associated remote-tracking refs. Local branches and commits remain."));
            return [.. content];
        }))
        .Title("Remove configured remote?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(14))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 11, 120, 26)
        .Modal());
    }

    private static string GetFetchPruneLabel(GitOptionOverride prune)
        => prune switch
        {
            GitOptionOverride.Configured => "Prune: Git config",
            GitOptionOverride.Enabled => "Prune: on",
            GitOptionOverride.Disabled => "Prune: off",
            _ => throw new ArgumentOutOfRangeException(nameof(prune)),
        };

    private static string GetFetchTagLabel(FetchTagMode tags)
        => tags switch
        {
            FetchTagMode.Configured => "Tags: Git config",
            FetchTagMode.All => "Tags: all",
            FetchTagMode.None => "Tags: none",
            _ => throw new ArgumentOutOfRangeException(nameof(tags)),
        };

    private static FetchTagMode CycleFetchTagMode(FetchTagMode tags)
        => tags switch
        {
            FetchTagMode.Configured => FetchTagMode.All,
            FetchTagMode.All => FetchTagMode.None,
            FetchTagMode.None => FetchTagMode.Configured,
            _ => throw new ArgumentOutOfRangeException(nameof(tags)),
        };

    private static string GetFetchExplanation(GitOptionOverride prune, FetchTagMode tags)
    {
        var pruneText = prune switch
        {
            GitOptionOverride.Configured => "Pruning follows effective Git configuration",
            GitOptionOverride.Enabled => "Stale configured destination refs will be pruned",
            GitOptionOverride.Disabled => "No stale refs will be pruned by this fetch",
            _ => throw new ArgumentOutOfRangeException(nameof(prune)),
        };
        var tagText = tags switch
        {
            FetchTagMode.Configured => "tag following uses remote configuration",
            FetchTagMode.All => "all remote tags are requested",
            FetchTagMode.None => "automatic tag following is disabled",
            _ => throw new ArgumentOutOfRangeException(nameof(tags)),
        };
        return $"{pruneText}; {tagText}.";
    }

    private async Task ShowStashesAsync(WindowManager windows)
    {
        await _workspace.LoadStashesAsync(_cancellationToken).ConfigureAwait(false);
        if (_workspace.Stashes.Catalog is null)
        {
            return;
        }

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.VSplitter(
                builder.VStack(top =>
                [
                    top.HStack(filter =>
                    [
                        filter.Text("Filter: "),
                        DismissOnEscape(
                            filter.TextBox()
                                .State(_workspace.Stashes.Filter)
                                .OnTextChanged(eventArgs => _workspace.FilterStashesAsync(
                                    eventArgs.NewText,
                                    _cancellationToken)),
                            window.Window)
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
                            bindings.Key(Hex1bKey.Escape).Action(
                                _ => window.Window.Cancel(),
                                "Close the stash window");
                        }).Fill(),
                    top.VStack(details => BuildStashDetails(details)),
                    top.WrapPanel(actions => BuildStashActions(actions, windows, window.Window)),
                ]).Fill(),
                builder.Border(
                    DismissOnEscape(
                        builder.Editor(_workspace.Stashes.Preview)
                            .LineNumbers()
                            .WordWrap(false)
                            .Decorations(_workspace.Stashes.PreviewDecorationProvider),
                        window.Window)
                        .Fill())
                    .Title(_workspace.Stashes.PreviewTitle)
                    .Fill(),
                9).Fill(),
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
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 130, 48));
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

    private void ShowCreateStashDialog(WindowManager windows, WindowHandle? stashWindow)
    {
        var messageState = new TextBoxState();
        var fileScope = StashFileScope.Tracked;
        var keepIndex = false;
        var stagedOnly = false;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text("Save current repository changes and restore the selected paths through Git."),
            builder.HStack(message =>
            [
                message.Text("Message: "),
                DismissOnEscape(message.TextBox().State(messageState), window.Window).FillWidth(),
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
                    stashWindow?.CloseWithResult("create");
                    await _workspace.CreateStashAsync(options, _cancellationToken).ConfigureAwait(false);
                    SelectWorkspaceRegion(_workspaceRegion);
                }),
            ]),
        ]))
        .Title("Save current changes to a stash")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(13))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Modal());
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

        ShowApplyStashDialog(windows, stashWindow, stash, pop);
    }

    private void ShowApplyStashDialog(
        WindowManager windows,
        WindowHandle? stashWindow,
        StashInfo stash,
        bool pop)
    {

        var restoreIndex = false;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
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
                    stashWindow?.CloseWithResult(pop ? "pop" : "apply");
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

                    SelectWorkspaceRegion(_workspaceRegion);
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
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(13))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Modal());
    }

    private void ShowDropFocusedStashDialog(WindowManager windows, WindowHandle stashWindow)
    {
        var stash = _workspace.Stashes.FocusedItem?.Stash;
        if (stash is null)
        {
            return;
        }

        ShowDropStashDialog(windows, stashWindow, stash);
    }

    private void ShowDropStashDialog(
        WindowManager windows,
        WindowHandle? stashWindow,
        StashInfo stash)
    {

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
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
                    stashWindow?.CloseWithResult("drop");
                    await _workspace.DropStashAsync(stash, _cancellationToken).ConfigureAwait(false);
                    SelectWorkspaceRegion(_workspaceRegion);
                }),
            ]),
        ]))
        .Title("Drop stash?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(12))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Modal());
    }

    private string GetCurrentChangeSummary()
    {
        var staged = _workspace.State.StagedTotalCount;
        var unstaged = _workspace.State.UnstagedTotalCount;
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

    private async Task ShowWorktreesAsync(WindowManager windows)
    {
        await _workspace.LoadWorktreesAsync(_cancellationToken).ConfigureAwait(false);
        if (_workspace.Worktrees.Catalog is null)
        {
            return;
        }

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(filter =>
            [
                filter.Text("Filter: "),
                DismissOnEscape(
                    filter.TextBox()
                        .State(_workspace.Worktrees.Filter)
                        .OnTextChanged(eventArgs =>
                        {
                            _workspace.Worktrees.SetFilter(eventArgs.NewText);
                            _application?.Invalidate();
                        }),
                    window.Window)
                    .FillWidth(),
            ]).FillWidth(),
            builder.List(_workspace.Worktrees.VisibleItems)
                .ItemKey(static item => item.Key)
                .FocusedIndex(_workspace.Worktrees.FocusedIndex)
                .OnFocusChanged(eventArgs =>
                {
                    if (eventArgs.FocusedIndex >= 0 &&
                        eventArgs.FocusedIndex < _workspace.Worktrees.VisibleItems.Length)
                    {
                        _workspace.Worktrees.Focus(eventArgs.FocusedIndex);
                        _application?.Invalidate();
                    }
                })
                .Empty(empty => empty.Text("No linked worktree matches the filter."))
                .InputBindings(bindings =>
                {
                    bindings.Key(Hex1bKey.Enter).Action(
                        _ => OpenFocusedWorktreeAsync(window.Window),
                        "Open the focused existing worktree");
                    bindings.Key(Hex1bKey.F5).Action(
                        _ => _workspace.LoadWorktreesAsync(_cancellationToken),
                        "Refresh linked worktrees");
                    bindings.Key(Hex1bKey.N).Action(
                        _ => Complete(() => ShowAddWorktreeDialog(windows, window.Window)),
                        "Create a linked worktree");
                }).Fill(),
            builder.VStack(details => BuildWorktreeDetails(details)),
            builder.WrapPanel(actions => BuildWorktreeActions(actions, windows, window.Window)),
            builder.Text("Enter open | N create | F5 refresh | Mouse select, scroll, resize, and activate buttons"),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Window.Cancel(),
                "Close the linked-worktree window");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }))
        .Title("Linked worktrees")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 130, 46));
    }

    private Hex1bWidget[] BuildWorktreeDetails<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var item = _workspace.Worktrees.FocusedItem;
        if (item is null)
        {
            return [context.Text("Select a worktree to inspect its exact path and Git-reported state.")];
        }

        var worktree = item.Worktree;
        var details = new List<Hex1bWidget>
        {
            context.Text($"Path: {worktree.Path.DisplayText}"),
            context.Text(worktree.IsBare
                ? "HEAD: bare repository"
                : worktree.BranchName is null
                    ? $"HEAD: detached {worktree.HeadObjectId}"
                    : $"Branch: {worktree.BranchName.DisplayText} | HEAD: {worktree.HeadObjectId}"),
            context.Text(item.IsMain
                ? IsCurrentWorktree(worktree) ? "Role: current main worktree" : "Role: main worktree"
                : IsCurrentWorktree(worktree) ? "Role: current linked worktree" : "Role: linked worktree"),
        };
        if (worktree.IsLocked)
        {
            details.Add(context.Text(worktree.LockReasonDisplay is null
                ? "Lock: locked without a reason"
                : $"Lock: {worktree.LockReasonDisplay}"));
        }

        if (worktree.IsPrunable)
        {
            details.Add(context.Text(worktree.PrunableReasonDisplay is null
                ? "Prune: Git reports this record as stale"
                : $"Prune: {worktree.PrunableReasonDisplay}"));
        }

        return [.. details];
    }

    private Hex1bWidget[] BuildWorktreeActions<TParent>(
        WidgetContext<TParent> context,
        WindowManager windows,
        WindowHandle worktreeWindow)
        where TParent : Hex1bWidget
    {
        var actions = new List<Hex1bWidget>
        {
            context.Button("Close").OnClick(_ => worktreeWindow.Cancel()),
            context.Button("Refresh").OnClick(_ => _workspace.LoadWorktreesAsync(_cancellationToken)),
            context.Button("Create...").OnClick(_ => ShowAddWorktreeDialog(windows, worktreeWindow)),
            context.Button("Repair...").OnClick(_ => ShowRepairWorktreeDialog(windows)),
            context.Button("Prune stale...").OnClick(_ => ShowPruneWorktreesDialogAsync(windows)),
        };
        var item = _workspace.Worktrees.FocusedItem;
        if (item is null)
        {
            return [.. actions];
        }

        var worktree = item.Worktree;
        if (!worktree.IsBare && !worktree.IsPrunable && !IsCurrentWorktree(worktree))
        {
            actions.Add(context.Button("Open").OnClick(_ => OpenFocusedWorktreeAsync(worktreeWindow)));
        }

        if (item.IsMain)
        {
            actions.Add(context.Text(IsCurrentWorktree(worktree) ? "Current" : "Main worktree"));
            return [.. actions];
        }

        if (worktree.IsLocked)
        {
            actions.Add(context.Button("Unlock").OnClick(async _ =>
            {
                await _workspace.UnlockWorktreeAsync(worktree, _cancellationToken).ConfigureAwait(false);
                await _workspace.LoadWorktreesAsync(_cancellationToken).ConfigureAwait(false);
            }));
        }
        else
        {
            actions.Add(context.Button("Lock...").OnClick(_ => ShowLockWorktreeDialog(windows, worktree)));
            if (!worktree.IsPrunable)
            {
                actions.Add(context.Button("Move...").OnClick(_ => ShowMoveWorktreeDialog(windows, worktree)));
                actions.Add(context.Button("Remove...").OnClick(
                    _ => ShowRemoveWorktreeDialogAsync(windows, worktree)));
            }
        }

        return [.. actions];
    }

    private async Task OpenFocusedWorktreeAsync(WindowHandle worktreeWindow)
    {
        var worktree = _workspace.Worktrees.FocusedItem?.Worktree;
        if (worktree is null || worktree.IsBare || worktree.IsPrunable || IsCurrentWorktree(worktree))
        {
            return;
        }

        await _workspace.OpenWorktreeAsync(worktree).ConfigureAwait(false);
        if (_workspace.RequestedOpenDirectory is not null)
        {
            worktreeWindow.CloseWithResult("open");
            _application?.RequestStop();
        }
    }

    private void ShowAddWorktreeDialog(WindowManager windows, WindowHandle worktreeWindow)
    {
        var catalog = _workspace.Worktrees.Catalog;
        if (catalog is null)
        {
            return;
        }

        var sources = new BranchWorkspaceState();
        sources.ApplyCatalog(catalog);
        var target = new TextBoxState(GetWorktreeTargetPrefill());
        var branchName = new TextBoxState();
        var lockReason = new TextBoxState();
        var mode = WorktreeAddMode.NewBranch;
        var trackSource = sources.FocusedItem?.Branch.Kind == BranchKind.RemoteTracking;
        var lockAfterCreation = false;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(filter =>
            [
                filter.Text("Find starting point: "),
                DismissOnEscape(
                    filter.TextBox()
                        .State(sources.Filter)
                        .OnTextChanged(eventArgs =>
                        {
                            sources.SetFilter(eventArgs.NewText);
                            _application?.Invalidate();
                        }),
                    window.Window)
                    .FillWidth(),
            ]).FillWidth(),
            builder.List(sources.VisibleItems)
                .ItemKey(static item => item.Key)
                .FocusedIndex(sources.FocusedIndex)
                .OnFocusChanged(eventArgs =>
                {
                    if (eventArgs.FocusedIndex >= 0 && eventArgs.FocusedIndex < sources.VisibleItems.Length)
                    {
                        sources.Focus(eventArgs.FocusedIndex);
                        var source = sources.FocusedItem?.Branch;
                        if (mode == WorktreeAddMode.ExistingBranch && !CanUseExistingBranch(source))
                        {
                            mode = WorktreeAddMode.Detached;
                        }

                        trackSource = mode == WorktreeAddMode.NewBranch &&
                            source?.Kind == BranchKind.RemoteTracking;
                        _application?.Invalidate();
                    }
                })
                .Empty(empty => empty.Text("No branch matches the starting-point filter."))
                .Fill(),
            builder.HStack(path =>
            [
                path.Text("Target: "),
                DismissOnEscape(path.TextBox().State(target), window.Window).FillWidth(),
            ]).FillWidth(),
            builder.WrapPanel(options =>
            [
                options.Button(GetWorktreeAddModeLabel(mode)).OnClick(_ =>
                {
                    mode = NextWorktreeAddMode(mode, sources.FocusedItem?.Branch);
                    trackSource = mode == WorktreeAddMode.NewBranch &&
                        sources.FocusedItem?.Branch.Kind == BranchKind.RemoteTracking;
                    _application?.Invalidate();
                }),
                options.Text(" "),
                options.Button(lockAfterCreation ? "[x] Lock after creation" : "[ ] Lock after creation")
                    .OnClick(_ =>
                    {
                        lockAfterCreation = !lockAfterCreation;
                        _application?.Invalidate();
                    }),
                options.Text(" "),
                mode == WorktreeAddMode.NewBranch &&
                    sources.FocusedItem?.Branch.Kind == BranchKind.RemoteTracking
                    ? options.Button(trackSource ? "[x] Track remote" : "[ ] Track remote")
                        .OnClick(_ =>
                        {
                            trackSource = !trackSource;
                            _application?.Invalidate();
                        })
                    : options.Text(string.Empty),
            ]).FillWidth(),
            mode == WorktreeAddMode.NewBranch
                ? builder.HStack(name =>
                [
                    name.Text("New branch: "),
                    DismissOnEscape(name.TextBox().State(branchName), window.Window).FillWidth(),
                ]).FillWidth()
                : builder.Text(mode == WorktreeAddMode.Detached
                    ? "The new worktree will have detached HEAD at the selected exact object."
                    : "The selected unoccupied local branch will be checked out directly."),
            lockAfterCreation
                ? builder.HStack(reason =>
                [
                    reason.Text("Lock reason: "),
                    DismissOnEscape(reason.TextBox().State(lockReason), window.Window).FillWidth(),
                ]).FillWidth()
                : builder.Text("Locking is useful for worktrees on removable or intermittently mounted storage."),
            builder.WrapPanel(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                sources.FocusedItem?.Branch.SymbolicTarget is not null
                    ? actions.Text("Select a nonsymbolic branch")
                    : actions.Button("Create").OnClick(
                        _ => CreateAsync(window.Window, openAfterCreation: false)),
                actions.Text(" "),
                sources.FocusedItem?.Branch.SymbolicTarget is not null
                    ? actions.Text("Create and open unavailable")
                    : actions.Button("Create and open").OnClick(
                        _ => CreateAsync(window.Window, openAfterCreation: true)),
            ]).FillWidth(),
            builder.Text(_workspace.Activity),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close linked-worktree creation")))
        .Title("Create linked worktree")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 120, 38)
        .Modal());

        async Task CreateAsync(WindowHandle addWindow, bool openAfterCreation)
        {
            var source = sources.FocusedItem?.Branch;
            if (source is null || source.SymbolicTarget is not null)
            {
                return;
            }

            addWindow.CloseWithResult(openAfterCreation ? "create-open" : "create");
            await _workspace.AddWorktreeAsync(
                source,
                target.Text,
                mode,
                mode == WorktreeAddMode.NewBranch ? branchName.Text : null,
                mode == WorktreeAddMode.NewBranch && trackSource,
                lockAfterCreation,
                lockAfterCreation ? lockReason.Text : null,
                openAfterCreation,
                _cancellationToken).ConfigureAwait(false);
            if (_workspace.RequestedOpenDirectory is not null)
            {
                worktreeWindow.CloseWithResult("create-open");
                _application?.RequestStop();
            }
            else
            {
                await _workspace.LoadWorktreesAsync(_cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ShowMoveWorktreeDialog(WindowManager windows, WorktreeInfo worktree)
    {
        var target = new TextBoxState(GetWorktreeTargetPrefill());
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Current path: {worktree.Path.DisplayText}"),
            builder.HStack(path =>
            [
                path.Text("New path: "),
                DismissOnEscape(path.TextBox().State(target), window.Window).FillWidth(),
            ]).FillWidth(),
            builder.Text("Git moves the linked worktree and updates its administrative connection."),
            builder.Text("Locked worktrees and worktrees containing submodules must be handled separately."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Move").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("move");
                    await _workspace.MoveWorktreeAsync(
                        worktree,
                        target.Text,
                        _cancellationToken).ConfigureAwait(false);
                    await _workspace.LoadWorktreesAsync(_cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Move linked worktree")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(13))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 12, 120, 24)
        .Modal());
    }

    private void ShowLockWorktreeDialog(WindowManager windows, WorktreeInfo worktree)
    {
        var reason = new TextBoxState();
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Worktree: {worktree.Path.DisplayText}"),
            builder.Text("Locking prevents automatic prune, move, and removal while storage is unavailable."),
            builder.HStack(input =>
            [
                input.Text("Reason (optional): "),
                DismissOnEscape(input.TextBox().State(reason), window.Window).FillWidth(),
            ]).FillWidth(),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Lock").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("lock");
                    await _workspace.LockWorktreeAsync(
                        worktree,
                        reason.Text,
                        _cancellationToken).ConfigureAwait(false);
                    await _workspace.LoadWorktreesAsync(_cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Lock linked worktree")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(12))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 11, 120, 22)
        .Modal());
    }

    private async Task ShowRemoveWorktreeDialogAsync(WindowManager windows, WorktreeInfo worktree)
    {
        var plan = await _workspace.PrepareWorktreeRemovalAsync(
            worktree,
            _cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return;
        }

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Remove exact linked worktree: {plan.Worktree.Path.DisplayText}"),
            builder.Text(plan.Worktree.BranchName is null
                ? $"Detached HEAD: {plan.Worktree.HeadObjectId}"
                : $"Branch: {plan.Worktree.BranchName.DisplayText} | HEAD: {plan.Worktree.HeadObjectId}"),
            builder.Text(plan.IsClean
                ? "Git reports no tracked, untracked, or ignored paths."
                : "Git reports tracked, untracked, or ignored paths that force removal will delete."),
            builder.Text(plan.HasSubmodules
                ? "Configured submodules are present and force removal is required."
                : "No configured submodule entry is present."),
            builder.Text(plan.RequiresForce
                ? "Force removal deletes the entire selected worktree directory. This cannot be undone."
                : "Git will refuse removal if the worktree becomes dirty after this review."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button(plan.RequiresForce ? "Force remove exact worktree" : "Remove clean worktree")
                    .OnClick(async _ =>
                    {
                        window.Window.CloseWithResult("remove");
                        await _workspace.RemoveWorktreeAsync(
                            plan,
                            plan.RequiresForce,
                            _cancellationToken).ConfigureAwait(false);
                        await _workspace.LoadWorktreesAsync(_cancellationToken).ConfigureAwait(false);
                    }),
            ]),
        ]))
        .Title(plan.RequiresForce ? "Force remove linked worktree?" : "Remove linked worktree?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(16))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 14, 120, 28)
        .Modal());
    }

    private async Task ShowPruneWorktreesDialogAsync(WindowManager windows)
    {
        var plan = await _workspace.PrepareWorktreePruneAsync(_cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return;
        }

        var empty = plan.StandardOutput.IsEmpty && plan.StandardError.IsEmpty;
        var preview = FormatWorktreePrunePreview(plan);
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text(empty
                ? "Git reports no stale linked-worktree records eligible under the configured expiry."
                : "Git's dry run reports these stale administrative records:"),
            builder.Border(builder.Text(preview).Wrap()).Title("Dry-run output").Fill(),
            builder.Text("Prune removes administrative records only; use repair instead when a worktree was moved."),
            builder.HStack(actions =>
            [
                actions.Button("Close").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                empty
                    ? actions.Text("Nothing to prune")
                    : actions.Button("Prune exact reviewed records").OnClick(async _ =>
                    {
                        window.Window.CloseWithResult("prune");
                        await _workspace.PruneWorktreesAsync(plan, _cancellationToken).ConfigureAwait(false);
                        await _workspace.LoadWorktreesAsync(_cancellationToken).ConfigureAwait(false);
                    }),
            ]),
        ]))
        .Title("Prune stale linked-worktree records?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 120, 38)
        .Modal());
    }

    private void ShowRepairWorktreeDialog(WindowManager windows)
    {
        var path = new TextBoxState(GetWorktreeTargetPrefill());
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text("Choose an existing worktree directory whose administrative connection needs repair."),
            builder.HStack(input =>
            [
                input.Text("Path: "),
                DismissOnEscape(input.TextBox().State(path), window.Window).FillWidth(),
            ]).FillWidth(),
            builder.Text("Use repair after moving a worktree outside Git or after moving the main repository."),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Repair through Git").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("repair");
                    await _workspace.RepairWorktreeAsync(path.Text, _cancellationToken).ConfigureAwait(false);
                    await _workspace.LoadWorktreesAsync(_cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Repair worktree connection")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(13))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 12, 120, 24)
        .Modal());
    }

    private bool IsCurrentWorktree(WorktreeInfo worktree)
        => _workspace.State.Snapshot.Repository.WorkTree?.Equals(worktree.Path) == true;

    private string GetWorktreeTargetPrefill()
    {
        var worktree = _workspace.State.Snapshot.Repository.WorkTree;
        if (worktree is null)
        {
            return string.Empty;
        }

        try
        {
            var path = worktree.Kind == NativePathKind.WindowsUtf16
                ? worktree.GetWindowsPath()
                : s_strictUtf8.GetString(worktree.GetUnixBytes());
            return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        }
        catch (DecoderFallbackException)
        {
            return string.Empty;
        }
    }

    private static bool CanUseExistingBranch(BranchInfo? branch)
        => branch is
        {
            Kind: BranchKind.Local,
            SymbolicTarget: null,
            OccupiedWorktrees.IsEmpty: true,
        };

    private static WorktreeAddMode NextWorktreeAddMode(WorktreeAddMode mode, BranchInfo? branch)
        => mode switch
        {
            WorktreeAddMode.NewBranch => WorktreeAddMode.Detached,
            WorktreeAddMode.Detached when CanUseExistingBranch(branch) => WorktreeAddMode.ExistingBranch,
            WorktreeAddMode.Detached => WorktreeAddMode.NewBranch,
            WorktreeAddMode.ExistingBranch => WorktreeAddMode.NewBranch,
            _ => WorktreeAddMode.NewBranch,
        };

    private static string GetWorktreeAddModeLabel(WorktreeAddMode mode)
        => mode switch
        {
            WorktreeAddMode.ExistingBranch => "HEAD: Existing branch",
            WorktreeAddMode.NewBranch => "HEAD: New branch",
            WorktreeAddMode.Detached => "HEAD: Detached commit",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static string FormatWorktreePrunePreview(WorktreePrunePlan plan)
    {
        const int maximumPreviewBytes = 16 * 1024;
        var output = plan.StandardOutput.AsSpan();
        var error = plan.StandardError.AsSpan();
        var combined = new byte[Math.Min(maximumPreviewBytes, output.Length + error.Length)];
        var outputLength = Math.Min(output.Length, combined.Length);
        output[..outputLength].CopyTo(combined);
        var errorLength = Math.Min(error.Length, combined.Length - outputLength);
        error[..errorLength].CopyTo(combined.AsSpan(outputLength));
        var text = TerminalTextSanitizer.Sanitize(Encoding.UTF8.GetString(combined));
        return output.Length + error.Length > maximumPreviewBytes
            ? text + " … <preview truncated>"
            : text.Length == 0 ? "<empty>" : text;
    }

    private async Task ShowBranchesAsync(WindowManager windows)
    {
        await _workspace.LoadBranchesAsync(_cancellationToken).ConfigureAwait(false);
        if (_workspace.Branches.Catalog is null)
        {
            return;
        }

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.HStack(filter =>
            [
                filter.Text("Filter: "),
                DismissOnEscape(
                    filter.TextBox()
                        .State(_workspace.Branches.Filter)
                        .OnTextChanged(eventArgs =>
                        {
                            _workspace.Branches.SetFilter(eventArgs.NewText);
                            _application?.Invalidate();
                        }),
                    window.Window)
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
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(20))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 120, 40));
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
            context.Button("Worktrees...").OnClick(async _ =>
            {
                branchWindow.CloseWithResult("worktrees");
                await ShowWorktreesAsync(windows).ConfigureAwait(false);
            }),
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

        if (branch.Kind == BranchKind.Local)
        {
            actions.Add(context.Button("Upstream...").OnClick(
                _ => ShowUpstreamFocusedBranchDialog(windows, branchWindow)));
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

    private void ShowUpstreamFocusedBranchDialog(WindowManager windows, WindowHandle branchWindow)
    {
        var branch = _workspace.Branches.FocusedItem?.Branch;
        if (branch is not null && branch.Kind == BranchKind.Local)
        {
            ShowBranchUpstreamDialog(windows, branchWindow, branch);
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
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
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
                autoStash)).Wrap(),
            builder.Text("Git runs hooks, strategy machinery, rerere, autostash, index updates, refs, and conflict setup.").Wrap(),
        ]))
        .Title("Merge exact selected branch?")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(19))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 120, 34)
        .Modal());
    }

    private void ShowCreateBranchDialog(
        WindowManager windows,
        WindowHandle? branchWindow,
        BranchInfo source)
    {
        var nameState = new TextBoxState(GetInitialBranchName(source));
        var trackSource = source.Kind == BranchKind.RemoteTracking;
        var validationMessage = string.Empty;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Source: {source.FullName.DisplayText}").Wrap(),
            builder.Text($"Exact commit: {source.TargetObjectId}").Wrap(),
            builder.HStack(name =>
            [
                name.Text("Local name: "),
                DismissOnEscape(
                    name.TextBox()
                        .State(nameState)
                        .OnTextChanged(_ => validationMessage = string.Empty),
                    window.Window),
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
                    branchWindow?.CloseWithResult("create");
                    await _workspace.CreateAndSwitchBranchAsync(
                        source,
                        nameState.Text,
                        trackSource,
                        _cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Create local branch")
        .Size(_popupViewport.FitWidth(58), _popupViewport.FitHeight(11))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 11, 120, 24)
        .Modal());
    }

    private void ShowRenameBranchDialog(
        WindowManager windows,
        WindowHandle? branchWindow,
        BranchInfo branch)
    {
        var nameState = new TextBoxState(TryGetEditableRefText(branch.ShortName, out var name)
            ? name
            : string.Empty);
        var validationMessage = string.Empty;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Rename: {branch.ShortName.DisplayText}").Wrap(),
            builder.Text($"Exact commit remains: {branch.TargetObjectId}").Wrap(),
            builder.HStack(nameRow =>
            [
                nameRow.Text("New name: "),
                DismissOnEscape(
                    nameRow.TextBox()
                        .State(nameState)
                        .OnTextChanged(_ => validationMessage = string.Empty),
                    window.Window),
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
                    branchWindow?.CloseWithResult("rename");
                    await _workspace.RenameBranchAsync(
                        branch,
                        nameState.Text,
                        _cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Rename local branch")
        .Size(_popupViewport.FitWidth(58), _popupViewport.FitHeight(10))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 10, 120, 22)
        .Modal());
    }

    private void ShowDeleteBranchDialog(
        WindowManager windows,
        WindowHandle? branchWindow,
        BranchInfo branch)
    {
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Delete local branch: {branch.ShortName.DisplayText}").Wrap(),
            builder.Text($"Current target: {branch.TargetObjectId}").Wrap(),
            builder.Text("Safe delete asks Git to verify mergedness.").Wrap(),
            builder.Text("Force delete removes the ref even when commits are unmerged.").Wrap(),
            builder.Text("The branch is not checked out in any linked worktree.").Wrap(),
            builder.HStack(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                actions.Text(" "),
                actions.Button("Safe delete").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("safe delete");
                    branchWindow?.CloseWithResult("delete");
                    await _workspace.DeleteBranchAsync(
                        branch,
                        BranchDeleteMode.Safe,
                        _cancellationToken).ConfigureAwait(false);
                }),
                actions.Text(" "),
                actions.Button("Force delete").OnClick(async _ =>
                {
                    window.Window.CloseWithResult("force delete");
                    branchWindow?.CloseWithResult("delete");
                    await _workspace.DeleteBranchAsync(
                        branch,
                        BranchDeleteMode.Force,
                        _cancellationToken).ConfigureAwait(false);
                }),
            ]),
        ]))
        .Title("Delete branch?")
        .Size(_popupViewport.FitWidth(58), _popupViewport.FitHeight(12))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 12, 120, 24)
        .Modal());
    }

    private void ShowResetBranchDialog(
        WindowManager windows,
        WindowHandle? branchWindow,
        BranchInfo branch)
    {
        var revisionState = new TextBoxState();
        var validationMessage = string.Empty;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Current branch: {branch.ShortName.DisplayText}"),
            builder.Text($"Current commit: {branch.TargetObjectId}"),
            builder.HStack(revision =>
            [
                revision.Text("Target revision: "),
                DismissOnEscape(
                    revision.TextBox()
                        .State(revisionState)
                        .OnTextChanged(_ => validationMessage = string.Empty),
                    window.Window),
            ]).FillWidth(),
            builder.Text("Soft keeps index and worktree; mixed resets index; hard also discards tracked worktree changes.").Wrap(),
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
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(13))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 13, 120, 24)
        .Modal());
    }

    private void ShowBranchUpstreamDialog(
        WindowManager windows,
        WindowHandle? branchWindow,
        BranchInfo branch)
    {
        var catalog = _workspace.Branches.Catalog;
        if (catalog is null || branch.Kind != BranchKind.Local)
        {
            return;
        }

        var remoteBranches = catalog.Branches
            .Where(static candidate =>
                candidate.Kind == BranchKind.RemoteTracking &&
                candidate.SymbolicTarget is null)
            .OrderBy(static candidate => candidate.FullName.DisplayText, StringComparer.Ordinal)
            .ToImmutableArray();
        var upstreams = new BranchWorkspaceState();
        upstreams.ApplyCatalog(new BranchCatalog(
            catalog.Precondition,
            remoteBranches,
            []));
        if (branch.UpstreamName is not null)
        {
            for (var index = 0; index < upstreams.VisibleItems.Length; index++)
            {
                if (upstreams.VisibleItems[index].Branch.FullName.Equals(branch.UpstreamName))
                {
                    upstreams.Focus(index);
                    break;
                }
            }
        }

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Local branch: {branch.FullName.DisplayText}"),
            builder.Text($"Exact local object: {branch.TargetObjectId}"),
            builder.Text(branch.UpstreamName is null
                ? "Current upstream: none"
                : $"Current upstream: {branch.UpstreamName.DisplayText}"),
            builder.HStack(filter =>
            [
                filter.Text("Filter: "),
                DismissOnEscape(
                    filter.TextBox()
                        .State(upstreams.Filter)
                        .OnTextChanged(eventArgs =>
                        {
                            upstreams.SetFilter(eventArgs.NewText);
                            _application?.Invalidate();
                        }),
                    window.Window)
                    .FillWidth(),
            ]).FillWidth(),
            builder.List(upstreams.VisibleItems)
                .ItemKey(static item => item.Key)
                .FocusedIndex(upstreams.FocusedIndex)
                .OnFocusChanged(eventArgs =>
                {
                    if (eventArgs.FocusedIndex >= 0 &&
                        eventArgs.FocusedIndex < upstreams.VisibleItems.Length)
                    {
                        upstreams.Focus(eventArgs.FocusedIndex);
                        _application?.Invalidate();
                    }
                })
                .Empty(empty => empty.Text("No nonsymbolic remote-tracking branch matches the filter."))
                .InputBindings(bindings => bindings.Key(Hex1bKey.Enter).Action(
                    _ => ConfigureAsync(window.Window, upstreams.FocusedItem?.Branch),
                    "Set the focused exact upstream"))
                .Fill(),
            builder.Text(upstreams.FocusedItem is null
                ? "Select one exact remote-tracking branch."
                : $"Selected object: {upstreams.FocusedItem.Branch.TargetObjectId}"),
            builder.WrapPanel(actions =>
            [
                actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                branch.UpstreamName is null
                    ? actions.Text(" No upstream to remove ")
                    : actions.Button("Remove upstream").OnClick(
                        _ => ConfigureAsync(window.Window, upstream: null)),
                upstreams.FocusedItem is null
                    ? actions.Text(" Set upstream ")
                    : actions.Button("Set exact upstream").OnClick(
                        _ => ConfigureAsync(window.Window, upstreams.FocusedItem?.Branch)),
            ]),
            builder.Text("Enter sets the focused upstream | Escape and outside click cancel"),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Cancel branch upstream changes")))
        .Title("Change branch upstream")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(17))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 15, 120, 32)
        .Modal());

        async Task ConfigureAsync(WindowHandle upstreamWindow, BranchInfo? upstream)
        {
            if (upstream is null && branch.UpstreamName is null)
            {
                return;
            }

            upstreamWindow.CloseWithResult(upstream is null ? "remove upstream" : "set upstream");
            branchWindow?.CloseWithResult("upstream");
            await _workspace.ConfigureBranchUpstreamAsync(
                branch,
                upstream,
                _cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunResetBranchAsync(
        WindowHandle resetWindow,
        WindowHandle? branchWindow,
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
        branchWindow?.CloseWithResult("reset");
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
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
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

            content.Add(builder.Text(
                "Git will run merge --abort and attempt to restore the pre-merge state.").Wrap());
            content.Add(builder.Text(
                "Uncommitted changes that Git cannot reconstruct may cause the abort to fail.").Wrap());
            return [.. content];
        }))
        .Title("Abort merge?")
        .Size(
            _popupViewport.FitWidth(78),
            _popupViewport.FitHeight(
                14 + Math.Min(warning.MergeHeads.Length, 4) +
                    (warning.MergeAutostash is null ? 0 : 1)))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 14, 120, 32)
        .Modal());
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

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text("Restore worktree content from the index."),
            builder.Text("The chosen scope discards current worktree bytes."),
            builder.Text("Undo remains available while preconditions match."),
            builder.Text(string.Empty),
            builder.WrapPanel(buttons => BuildRevertConfirmationButtons(buttons, window.Window)),
        ]))
        .Title("Revert worktree changes?")
        .Size(_popupViewport.FitWidth(62), _popupViewport.FitHeight(10))
        .Modal());
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
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var content = new List<Hex1bWidget>();
            if (detachedWarning is not null)
            {
                content.Add(builder.Text(
                    $"HEAD is detached at {detachedWarning.HeadObjectId.ToString()[..12]}."));
                content.Add(builder.Text("The new commit will not belong to a branch."));
                content.Add(builder.Text(
                    "Create or switch to a branch first unless this detached commit is intentional.").Wrap());
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
                content.Add(builder.Text(
                    "This is a local heuristic; remote servers may differ from these refs.").Wrap());
            }

            if (detachedWarning is not null)
            {
                content.Add(builder.Text(
                    "The new commit may become unreachable after HEAD moves away from it.").Wrap());
            }

            return [.. content];
        }))
        .Title(GetCommitWarningTitle(publishedWarning, detachedWarning))
        .Size(
            _popupViewport.FitWidth(78),
            _popupViewport.FitHeight(
                9 + (publishedWarning is null ? 0 : 6) +
                    (detachedWarning is null ? 0 : 4)))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 9, 120, 32)
        .Modal());
    }

    private void ShowCommitWithoutHooksConfirmation(WindowManager windows)
    {
        var publishedWarning = _workspace.CommitOptions.Amend
            ? _workspace.PublishedAmendWarning
            : null;
        var detachedWarning = _workspace.DetachedHeadWarning;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
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
                content.Add(builder.Text(
                    "HEAD is also contained by these local remote-tracking refs:").Wrap());
                var referenceLabels = GetRemoteTrackingReferenceLabels(publishedWarning);
                content.Add(builder.VScrollPanel(references =>
                    [.. referenceLabels.Select(label => references.Text(label))],
                    showScrollbar: false).Fill());
                content.Add(builder.Text(
                    "This is a local heuristic; remote servers may differ from these refs.").Wrap());
            }

            if (detachedWarning is not null)
            {
                content.Add(builder.Text(
                    $"HEAD is detached at {detachedWarning.HeadObjectId.ToString()[..12]}."));
                content.Add(builder.Text("The new commit will not belong to a branch."));
                content.Add(builder.Text(
                    "The new commit may become unreachable after HEAD moves away from it.").Wrap());
            }

            return [.. content];
        }))
        .Title("Commit without hooks?")
        .Size(
            _popupViewport.FitWidth(publishedWarning is null && detachedWarning is null ? 58 : 78),
            _popupViewport.FitHeight(
                9 + (publishedWarning is null ? 0 : 6) +
                    (detachedWarning is null ? 0 : 4)))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 9, 120, 32)
        .Modal());
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

        return enabled.Count == 0
            ? AppMessages.WorkspaceCommitDefaultTransaction
            : string.Join(", ", enabled);
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

    private static void ConfigureClampedListNavigation(
        InputBindingsBuilder bindings,
        int itemCount,
        Func<int> getFocusedIndex,
        Func<int, CancellationToken, Task> focusAsync,
        Action<int> extendSelection)
    {
        bindings.Remove(ListWidget<StatusWorkspaceItem>.MoveUp);
        bindings.Remove(ListWidget<StatusWorkspaceItem>.MoveDown);
        bindings.Remove(ListWidget<StatusWorkspaceItem>.ScrollUp);
        bindings.Remove(ListWidget<StatusWorkspaceItem>.ScrollDown);
        bindings.Remove(ListWidget<StatusWorkspaceItem>.ExtendSelectionUp);
        bindings.Remove(ListWidget<StatusWorkspaceItem>.ExtendSelectionDown);
        bindings.Key(Hex1bKey.UpArrow).Action(
            actionContext => MoveClampedAsync(actionContext, -1, extend: false),
            "Move toward the first change");
        bindings.Key(Hex1bKey.DownArrow).Action(
            actionContext => MoveClampedAsync(actionContext, 1, extend: false),
            "Move toward the last change");
        bindings.Shift().Key(Hex1bKey.UpArrow).Action(
            actionContext => MoveClampedAsync(actionContext, -1, extend: true),
            "Extend selection toward the first change");
        bindings.Shift().Key(Hex1bKey.DownArrow).Action(
            actionContext => MoveClampedAsync(actionContext, 1, extend: true),
            "Extend selection toward the last change");
        bindings.Mouse(MouseButton.ScrollUp).Action(
            actionContext => MoveClampedAsync(actionContext, -1, extend: false),
            "Scroll toward the first change");
        bindings.Mouse(MouseButton.ScrollDown).Action(
            actionContext => MoveClampedAsync(actionContext, 1, extend: false),
            "Scroll toward the last change");

        async Task MoveClampedAsync(
            InputBindingActionContext actionContext,
            int offset,
            bool extend)
        {
            if (itemCount == 0)
            {
                return;
            }

            var current = getFocusedIndex();
            var target = Math.Clamp(current + offset, 0, itemCount - 1);
            if (target == current)
            {
                return;
            }

            if (extend)
            {
                extendSelection(target);
            }

            await focusAsync(target, actionContext.CancellationToken).ConfigureAwait(false);
            actionContext.Invalidate();
        }
    }

}
