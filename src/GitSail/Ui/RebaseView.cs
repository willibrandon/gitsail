using GitSail.Domain;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Composes the responsive keyboard-and-mouse interactive-rebase planning and recovery UI.
/// </summary>
internal sealed class RebaseView
{
    private readonly RebaseSession _session;
    private readonly CancellationToken _cancellationToken;
    private readonly List<WindowHandle> _popupWindows = [];
    private Hex1bApp? _application;
    private WindowManager? _popupWindowManager;

    /// <summary>
    /// Initializes an interactive-rebase view over controlled session state.
    /// </summary>
    /// <param name="session">The rebase plan and recovery state source.</param>
    /// <param name="cancellationToken">Signals application shutdown.</param>
    internal RebaseView(RebaseSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Connects rebase invalidation notifications to the owning terminal application.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    internal void Attach(Hex1bApp application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The rebase view is already attached.");
        }

        _application = application;
        _session.Changed += HandleChanged;
    }

    /// <summary>
    /// Disconnects rebase invalidation notifications from the owning application.
    /// </summary>
    internal void Detach()
    {
        if (_application is null)
        {
            return;
        }

        _session.Changed -= HandleChanged;
        _application = null;
        _popupWindowManager = null;
        _popupWindows.Clear();
    }

    /// <summary>
    /// Builds the complete responsive rebase widget tree for one render generation.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The interactive-rebase workspace.</returns>
    internal WindowPanelWidget Build(RootContext context)
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
                    static (width, height) => width < 60 || height < 9,
                    compact => compact.Border(compact.Text(
                        "Resize the terminal to at least 60 columns by 18 rows. Ctrl+Q remains available.").Wrap())
                        .Title("More room needed")
                        .Fill()),
                responsive.Otherwise(content => _session.State is null
                    ? BuildPlanningContent(content)
                    : BuildRecoveryContent(content)),
            ]).Fill(),
            BuildActions(builder),
            builder.Text(_session.Activity).Wrap(),
            BuildShortcuts(builder),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.F5).Action(
                _ => _session.RefreshAsync(_cancellationToken),
                "Refresh the rebase plan or recovery state");
            bindings.Ctrl().Key(Hex1bKey.R).Action(
                _ => _session.RefreshAsync(_cancellationToken),
                "Refresh the rebase plan or recovery state");
            if (_session.State is null)
            {
                bindings.Key(Hex1bKey.Enter).Action(
                    actionContext => ShowStartConfirmation(actionContext.Windows),
                    "Review and start the prepared rebase");
            }
            else
            {
                bindings.Key(Hex1bKey.F4).Action(
                    actionContext => RequestControl(actionContext, RebaseRequestedAction.OpenWorkspace),
                    "Open conflict resolution and staging tools");
                bindings.Key(Hex1bKey.C).Action(
                    actionContext => RequestControl(actionContext, RebaseRequestedAction.Continue),
                    "Continue the stopped rebase");
                bindings.Key(Hex1bKey.S).Action(
                    actionContext => ShowControlConfirmation(
                        actionContext.Windows,
                        actionContext,
                        RebaseRequestedAction.Skip),
                    "Review and skip the current commit");
                bindings.Key(Hex1bKey.E).Action(
                    actionContext => RequestControl(actionContext, RebaseRequestedAction.EditTodo),
                    "Edit the remaining rebase todo");
                bindings.Key(Hex1bKey.A).Action(
                    actionContext => ShowControlConfirmation(
                        actionContext.Windows,
                        actionContext,
                        RebaseRequestedAction.Abort),
                    "Review and abort the rebase");
            }

            bindings.Ctrl().Key(Hex1bKey.Q).Action(
                actionContext => actionContext.RequestStop(),
                "Quit GitSail");
        }).Fill();

    private ResponsiveWidget BuildHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var repository = RepositoryLabel.Create(_session.Repository);
        var version = _session.Installation.Version.ToString();
        return context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(100, wide => wide.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("rebase"),
                info.Spacer(),
                info.Section(repository),
                info.Section($"Git {version}"),
            ]).Divider(" | ")),
            responsive.Otherwise(compact => compact.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("rebase"),
                info.Spacer(),
                info.Section($"Git {version}"),
            ]).Divider(" | ")),
        ]);
    }

    private BorderWidget BuildPlanningContent<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(builder =>
        {
            var content = new List<Hex1bWidget>
            {
                builder.HStack(upstream =>
                [
                    upstream.Text("Upstream: "),
                    upstream.TextBox().State(_session.Upstream).FillWidth(),
                ]).FillWidth(),
                builder.HStack(onto =>
                [
                    onto.Text("New base: "),
                    onto.TextBox().State(_session.Onto).FillWidth(),
                ]).FillWidth(),
                builder.Text("Leave Upstream empty to use the current branch's configured upstream. Leave New base empty to use Upstream."),
                builder.Button("Resolve plan").OnClick(_ => _session.RefreshAsync(_cancellationToken)),
            };
            if (_session.Plan is { } plan)
            {
                content.Add(builder.Text($"Commits to rewrite: {plan.CommitCount}"));
                content.Add(builder.Text($"Current HEAD: {plan.Head}"));
                content.Add(builder.Text($"Upstream:     {plan.Upstream}"));
                content.Add(builder.Text($"New base:     {plan.Onto}"));
                content.Add(builder.Text(FormatTarget(plan.Precondition.HeadName)));
                content.Add(builder.Text(
                    "Saving the todo starts the rewrite. Git remains responsible for commits, hooks, conflicts, refs, and rollback."));
            }

            return [.. content];
        }).Fill()).Title("Interactive rebase plan").Fill();

    private BorderWidget BuildRecoveryContent<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var state = _session.State!;
        return context.Border(context.VStack(builder =>
        [
            builder.Text("Git has an active rebase transaction."),
            builder.Text(state.CurrentCommit is null
                ? "Current commit: Git does not expose one at this stop."
                : $"Current commit: {state.CurrentCommit}"),
            builder.Text("Resolve conflicts and stage the intended result before Continue."),
            builder.Text(state.CanEditTodo
                ? "The remaining interactive todo can be reviewed and edited."
                : "This rebase state does not expose an editable interactive todo."),
            builder.Text("Skip discards the current commit's partial application. Abort restores the pre-rebase state."),
        ]).Fill()).Title("Rebase stopped").Fill();
    }

    private WrapPanelWidget BuildActions<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.WrapPanel(actions =>
        {
            var widgets = new List<Hex1bWidget>
            {
                _session.IsBusy
                    ? actions.Text("Refresh unavailable")
                    : actions.Button("Refresh").OnClick(_ => _session.RefreshAsync(_cancellationToken)),
            };
            if (_session.State is null)
            {
                widgets.Add(_session.Plan is null || _session.IsBusy
                    ? actions.Text("Start unavailable")
                    : actions.Button("Start rebase...").OnClick(
                        eventArgs => ShowStartConfirmation(eventArgs.Context.Windows)));
            }
            else
            {
                widgets.Add(actions.Button("Resolve files").OnClick(
                    eventArgs => RequestControl(eventArgs.Context, RebaseRequestedAction.OpenWorkspace)));
                widgets.Add(actions.Button("Continue").OnClick(
                    eventArgs => RequestControl(eventArgs.Context, RebaseRequestedAction.Continue)));
                if (_session.State.CanEditTodo)
                {
                    widgets.Add(actions.Button("Edit todo").OnClick(
                        eventArgs => RequestControl(eventArgs.Context, RebaseRequestedAction.EditTodo)));
                }

                widgets.Add(actions.Button("Skip...").OnClick(eventArgs => ShowControlConfirmation(
                    eventArgs.Context.Windows,
                    eventArgs.Context,
                    RebaseRequestedAction.Skip)));
                widgets.Add(actions.Button("Abort...").OnClick(eventArgs => ShowControlConfirmation(
                    eventArgs.Context.Windows,
                    eventArgs.Context,
                    RebaseRequestedAction.Abort)));
            }

            widgets.Add(actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()));
            return [.. widgets];
        });

    private ResponsiveWidget BuildShortcuts<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(90, wide => wide.InfoBar(info =>
            {
                var sections = new List<IInfoBarChild>
                {
                    info.Section(_session.State is null ? "Enter Start" : "C Continue"),
                    info.Section(_session.State is null ? "F5 Resolve" : "S Skip"),
                };
                if (_session.State?.CanEditTodo == true)
                {
                    sections.Add(info.Section("E Edit todo"));
                }

                if (_session.State is not null)
                {
                    sections.Add(info.Section("F4 Resolve files"));
                    sections.Add(info.Section("A Abort"));
                }

                sections.Add(info.Spacer());
                sections.Add(info.Section("Ctrl+Q Quit"));
                sections.Add(info.Section("Mouse enabled"));
                return [.. sections];
            }).Divider(" | ")),
            responsive.Otherwise(compact => compact.InfoBar(info =>
            [
                info.Section(_session.State is null ? "F5 Resolve" : "C Continue"),
                _session.State is null ? info.Section(string.Empty) : info.Section("F4 Files"),
                info.Spacer(),
                info.Section("Ctrl+Q Quit"),
            ]).Divider(" | ")),
        ]);

    private void ShowStartConfirmation(WindowManager windows)
    {
        var plan = _session.Plan;
        if (plan is null || _session.IsBusy)
        {
            return;
        }

        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Rewrite {plan.CommitCount} {(plan.CommitCount == 1 ? "commit" : "commits")} from {plan.Head.ToString()[..12]}."),
            builder.Text($"Exclude commits through upstream: {plan.Upstream}"),
            builder.Text($"Replay the selected commits onto: {plan.Onto}"),
            builder.Text(FormatTarget(plan.Precondition.HeadName)),
            builder.Text("Git will open a typed todo editor before changing any commit."),
            builder.HStack(buttons =>
            [
                buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                buttons.Text(" "),
                buttons.Button("Open rebase plan").OnClick(eventArgs =>
                {
                    _session.RequestStart();
                    window.Window.CloseWithResult(true);
                    eventArgs.Context.RequestStop();
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close the rebase confirmation")))
        .Title("Start interactive rebase?")
        .Size(80, 12)
        .Resizable(58, 11, 110, 20));
    }

    private void ShowControlConfirmation(
        WindowManager windows,
        InputBindingActionContext actionContext,
        RebaseRequestedAction action)
    {
        var state = _session.State;
        if (state is null || action is not (RebaseRequestedAction.Skip or RebaseRequestedAction.Abort))
        {
            return;
        }

        var abort = action == RebaseRequestedAction.Abort;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text(state.CurrentCommit is null
                ? "Git does not expose a current commit at this stop."
                : $"Current commit: {state.CurrentCommit}"),
            builder.Text(abort
                ? "Git will abort the entire rebase and restore its recorded pre-rebase state."
                : "Git will discard the current commit's partial application and advance to the next todo command."),
            builder.HStack(buttons =>
            [
                buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                buttons.Text(" "),
                buttons.Button(abort ? "Abort rebase" : "Skip commit").OnClick(_ =>
                {
                    _session.RequestControl(action);
                    window.Window.CloseWithResult(true);
                    actionContext.RequestStop();
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close the rebase recovery confirmation")))
        .Title(abort ? "Abort this rebase?" : "Skip this commit?")
        .Size(78, 10)
        .Resizable(56, 10, 108, 18));
    }

    private void RequestControl(
        InputBindingActionContext actionContext,
        RebaseRequestedAction action)
    {
        _session.RequestControl(action);
        if (_session.RequestedAction is not null)
        {
            actionContext.RequestStop();
        }
    }

    private void OpenPopup(WindowManager windows, WindowHandle popup)
    {
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
        if (_popupWindowManager is not { } windows || windows.ActiveWindow is not { } activeWindow)
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

    private void HandleChanged()
        => _application?.Invalidate();

    private static string FormatTarget(RefName? headName)
    {
        const string localPrefix = "refs/heads/";
        if (headName is null)
        {
            return "Target: detached HEAD";
        }

        var display = headName.DisplayText;
        return display.StartsWith(localPrefix, StringComparison.Ordinal)
            ? $"Target branch: {display[localPrefix.Length..]}"
            : $"Target ref: {display}";
    }
}
