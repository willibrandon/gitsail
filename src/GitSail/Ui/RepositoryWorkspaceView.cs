using GitSail.CommandLine;
using Hex1b;
using Hex1b.Input;
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
            bindings.Key(Hex1bKey.U).Action(
                _ => _workspace.UnstageAsync(_cancellationToken),
                "Unstage checked or focused paths");
            bindings.Key(Hex1bKey.F5).Action(
                _ => _workspace.RefreshAsync(_cancellationToken),
                "Refresh repository status");
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
            .OnFocusChanged(eventArgs =>
            {
                state.FocusUnstaged(eventArgs.FocusedIndex);
                _application?.Invalidate();
            })
            .OnSelectionChanged(eventArgs =>
            {
                state.SetUnstagedSelection(eventArgs.SelectedIndices, eventArgs.ToggledIndex);
                _application?.Invalidate();
            })
            .Empty(empty => empty.Text("Working tree clean."))
            .InputBindings(bindings =>
            {
                bindings.Mouse(MouseButton.Left).Ctrl().Action(actionContext =>
                {
                    var index = GetPointerItemIndex(actionContext);
                    if (index >= 0)
                    {
                        state.ToggleUnstagedSelection(index);
                        actionContext.Invalidate();
                    }
                }, "Toggle worktree row selection");
                bindings.Mouse(MouseButton.Left).Shift().Action(actionContext =>
                {
                    var index = GetPointerItemIndex(actionContext);
                    if (index >= 0)
                    {
                        state.ExtendUnstagedSelection(index);
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
            .OnFocusChanged(eventArgs =>
            {
                state.FocusStaged(eventArgs.FocusedIndex);
                _application?.Invalidate();
            })
            .OnSelectionChanged(eventArgs =>
            {
                state.SetStagedSelection(eventArgs.SelectedIndices, eventArgs.ToggledIndex);
                _application?.Invalidate();
            })
            .Empty(empty => empty.Text("No staged changes."))
            .InputBindings(bindings =>
            {
                bindings.Mouse(MouseButton.Left).Ctrl().Action(actionContext =>
                {
                    var index = GetPointerItemIndex(actionContext);
                    if (index >= 0)
                    {
                        state.ToggleStagedSelection(index);
                        actionContext.Invalidate();
                    }
                }, "Toggle index row selection");
                bindings.Mouse(MouseButton.Left).Shift().Action(actionContext =>
                {
                    var index = GetPointerItemIndex(actionContext);
                    if (index >= 0)
                    {
                        state.ExtendStagedSelection(index);
                        actionContext.Invalidate();
                    }
                }, "Extend index row selection");
            });
        return context.Border(list.Fill())
            .Title($"Staged ({state.StagedItems.Length})")
            .Fill();
    }

    private BorderWidget BuildDetailPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var item = _workspace.State.FocusedItem;
        var content = item is null
            ? "Select a changed path to inspect it."
            : FormatDetail(item);
        return context.Border(context.Text(content).Wrap())
            .Title("Selected path")
            .Fill();
    }

    private HStackWidget BuildActionBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            _workspace.IsBusy || _workspace.State.UnstagedItems.Length == 0
                ? actions.Text("Stage unavailable")
                : actions.Button("Stage").OnClick(_ => _workspace.StageAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy || _workspace.State.StagedItems.Length == 0
                ? actions.Text("Unstage unavailable")
                : actions.Button("Unstage").OnClick(_ => _workspace.UnstageAsync(_cancellationToken)),
            actions.Text(" "),
            _workspace.IsBusy
                ? actions.Text("Refresh unavailable")
                : actions.Button("Refresh").OnClick(_ => _workspace.RefreshAsync(_cancellationToken)),
            actions.Text(" "),
            actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private InfoBarWidget BuildShortcutBar<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section("S Stage"),
            info.Section("U Unstage"),
            info.Section("F5 Refresh"),
            info.Section("Space Check"),
            info.Section("Ctrl/Shift Click Multi-select"),
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

    private static string FormatDetail(StatusWorkspaceItem item)
    {
        var entry = item.Entry;
        var original = entry.OriginalPath is null
            ? string.Empty
            : $"\nOriginal: {entry.OriginalPath.DisplayText}";
        var similarity = entry.SimilarityPercentage is null
            ? string.Empty
            : $"\nSimilarity: {entry.SimilarityPercentage}%";
        var submodule = entry.IsSubmodule ? "yes" : "no";
        return $"Path: {entry.Path.DisplayText}{original}\n" +
            $"Record: {entry.Kind}\n" +
            $"Index: {entry.IndexStatus}\n" +
            $"Worktree: {entry.WorkTreeStatus}\n" +
            $"Submodule: {submodule}{similarity}";
    }
}
