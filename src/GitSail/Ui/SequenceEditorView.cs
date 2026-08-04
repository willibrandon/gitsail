using GitSail.Domain;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Composes the responsive keyboard-and-mouse editor for Git's interactive-rebase todo.
/// </summary>
internal sealed class SequenceEditorView
{
    private readonly SequenceEditorSession _session;
    private readonly string _repositoryLabel;
    private readonly string _gitVersion;
    private readonly List<WindowHandle> _popupWindows = [];
    private Hex1bApp? _application;
    private WindowManager? _popupWindowManager;

    /// <summary>
    /// Initializes a sequence-editor view over controlled todo state.
    /// </summary>
    /// <param name="session">The typed todo editing session.</param>
    /// <param name="repositoryLabel">The control-safe repository label.</param>
    /// <param name="gitVersion">The complete resolved Git version.</param>
    internal SequenceEditorView(
        SequenceEditorSession session,
        string repositoryLabel,
        string gitVersion)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitVersion);
        _session = session;
        _repositoryLabel = repositoryLabel;
        _gitVersion = gitVersion;
    }

    /// <summary>
    /// Connects editor invalidation notifications to the owning terminal application.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    internal void Attach(Hex1bApp application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The sequence editor is already attached.");
        }

        _application = application;
        _session.Changed += HandleChanged;
    }

    /// <summary>
    /// Disconnects editor invalidation notifications from the owning application.
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
    /// Builds the complete responsive sequence-editor widget tree.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The sequence-editor window panel.</returns>
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
                        "Resize the terminal to at least 60 columns by 18 rows. Esc still cancels the rebase plan.").Wrap())
                        .Title("More room needed")
                        .Fill()),
                responsive.WhenMinWidth(100, wide => BuildEditorContent(wide, showDetails: true)),
                responsive.Otherwise(compact => BuildEditorContent(compact, showDetails: false)),
            ]).Fill(),
            BuildActions(builder),
            builder.Text(_session.Status).Wrap(),
            BuildShortcuts(builder),
        ]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.J).Action(_ => _session.MoveFocus(1), "Focus the next todo line");
            bindings.Key(Hex1bKey.K).Action(_ => _session.MoveFocus(-1), "Focus the previous todo line");
            bindings.Key(Hex1bKey.U).Action(_ => _session.MoveCommand(-1), "Move the selected command up");
            bindings.Key(Hex1bKey.N).Action(_ => _session.MoveCommand(1), "Move the selected command down");
            bindings.Key(Hex1bKey.P).Action(_ => _session.ChangeAction(RebaseTodoAction.Pick), "Set pick");
            bindings.Key(Hex1bKey.W).Action(_ => _session.ChangeAction(RebaseTodoAction.Reword), "Set reword");
            bindings.Key(Hex1bKey.E).Action(_ => _session.ChangeAction(RebaseTodoAction.Edit), "Set edit");
            bindings.Key(Hex1bKey.S).Action(_ => _session.ChangeAction(RebaseTodoAction.Squash), "Set squash");
            bindings.Key(Hex1bKey.F).Action(_ => _session.ChangeAction(RebaseTodoAction.Fixup), "Set fixup");
            bindings.Key(Hex1bKey.D).Action(_ => _session.ChangeAction(RebaseTodoAction.Drop), "Set drop");
            bindings.Key(Hex1bKey.A).Action(
                actionContext => ShowAddExecDialog(actionContext.Windows),
                "Add an explicitly trusted exec command");
            bindings.Ctrl().Key(Hex1bKey.S).Action(
                actionContext => ShowSaveConfirmation(actionContext.Windows),
                "Review and save the rebase plan");
            bindings.Key(Hex1bKey.Escape).Action(actionContext => actionContext.RequestStop(), "Cancel the rebase plan");
            bindings.Ctrl().Key(Hex1bKey.Q).Action(actionContext => actionContext.RequestStop(), "Cancel the rebase plan");
        }).Fill();

    private ResponsiveWidget BuildHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(100, wide => wide.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("rebase todo"),
                info.Spacer(),
                info.Section(_repositoryLabel),
                info.Section($"Git {_gitVersion}"),
            ]).Divider(" | ")),
            responsive.Otherwise(compact => compact.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("rebase todo"),
                info.Spacer(),
                info.Section($"Git {_gitVersion}"),
            ]).Divider(" | ")),
        ]);

    private Hex1bWidget BuildEditorContent<TParent>(
        WidgetContext<TParent> context,
        bool showDetails)
        where TParent : Hex1bWidget
    {
        var list = context.List(_session.Document.Entries)
            .ItemKey(static entry => entry)
            .FocusedIndex(_session.FocusedIndex)
            .OnFocusChanged(eventArgs => _session.Focus(eventArgs.FocusedIndex))
            .Empty(empty => empty.Text("Git supplied an empty rebase plan."))
            .Fill();
        if (!showDetails)
        {
            return context.Border(list).Title("Rebase plan").Fill();
        }

        return context.HSplitter(
            context.Border(list).Title("Rebase plan").Fill(),
            context.Border(context.VStack(details => BuildFocusedDetails(details)))
                .Title("Selected command")
                .Fill(),
            68).Fill();
    }

    private Hex1bWidget[] BuildFocusedDetails<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var entry = _session.FocusedEntry;
        if (entry is null)
        {
            return [context.Text("No todo line is selected.")];
        }

        if (entry.Action is not { } action)
        {
            return
            [
                context.Text(entry.Kind == RebaseTodoLineKind.Comment ? "Git ignores this comment." : "Blank separator line."),
                context.Text(entry.DisplayText).Wrap(),
            ];
        }

        return
        [
            context.Text($"Action: {RebaseTodoParser.GetCommandName(action)}"),
            context.Text(GetActionDescription(action)).Wrap(),
            context.Text(entry.DisplayText).Wrap(),
        ];
    }

    private WrapPanelWidget BuildActions<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.WrapPanel(actions =>
        {
            var widgets = new List<Hex1bWidget>
            {
                actions.Button("Pick").OnClick(_ => _session.ChangeAction(RebaseTodoAction.Pick)),
                actions.Button("Reword").OnClick(_ => _session.ChangeAction(RebaseTodoAction.Reword)),
                actions.Button("Edit").OnClick(_ => _session.ChangeAction(RebaseTodoAction.Edit)),
                actions.Button("Squash").OnClick(_ => _session.ChangeAction(RebaseTodoAction.Squash)),
                actions.Button("Fixup").OnClick(_ => _session.ChangeAction(RebaseTodoAction.Fixup)),
                actions.Button("Drop").OnClick(_ => _session.ChangeAction(RebaseTodoAction.Drop)),
                actions.Button("Move up").OnClick(_ => _session.MoveCommand(-1)),
                actions.Button("Move down").OnClick(_ => _session.MoveCommand(1)),
                actions.Button("Add exec...").OnClick(eventArgs => ShowAddExecDialog(eventArgs.Context.Windows)),
            };
            if (_session.FocusedEntry?.Action == RebaseTodoAction.Exec)
            {
                widgets.Add(actions.Button("Remove exec").OnClick(_ => _session.RemoveExec()));
            }

            widgets.Add(actions.Button("Cancel").OnClick(eventArgs => eventArgs.Context.RequestStop()));
            widgets.Add(actions.Button("Save plan...").OnClick(
                eventArgs => ShowSaveConfirmation(eventArgs.Context.Windows)));
            return [.. widgets];
        });

    private static ResponsiveWidget BuildShortcuts<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(100, wide => wide.InfoBar(info =>
            [
                info.Section("J/K Select"),
                info.Section("U/N Move"),
                info.Section("P/W/E/S/F/D Action"),
                info.Section("A Add exec"),
                info.Spacer(),
                info.Section("Ctrl+S Save"),
                info.Section("Esc Cancel"),
                info.Section("Mouse Select/Scroll/Resize"),
            ]).Divider(" | ")),
            responsive.Otherwise(compact => compact.InfoBar(info =>
            [
                info.Section("J/K Select"),
                info.Section("U/N Move"),
                info.Spacer(),
                info.Section("Ctrl+S Save"),
                info.Section("Esc Cancel"),
            ]).Divider(" | ")),
        ]);

    private void ShowAddExecDialog(WindowManager windows)
    {
        var command = new TextBoxState();
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text("Git will run this command through a shell at this exact point in the rebase."),
            builder.Text("Only add a command you have reviewed and trust."),
            builder.HStack(input =>
            [
                input.Text("Command: "),
                input.TextBox().State(command).FillWidth(),
            ]).FillWidth(),
            builder.HStack(buttons =>
            [
                buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                buttons.Text(" "),
                buttons.Button("Add and trust").OnClick(_ =>
                {
                    _session.InsertTrustedExec(command.Text);
                    if (_session.FocusedEntry?.Action == RebaseTodoAction.Exec)
                    {
                        window.Window.CloseWithResult(true);
                    }
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close the exec command dialog")))
        .Title("Add shell command?")
        .Size(76, 10)
        .Resizable(58, 10, 110, 18));
    }

    private void ShowSaveConfirmation(WindowManager windows)
    {
        var commands = _session.Document.Entries.Count(static entry => entry.Action is not null);
        var execCommands = _session.Document.Entries.Count(static entry => entry.Action == RebaseTodoAction.Exec);
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"Return {commands} {(commands == 1 ? "command" : "commands")} to Git."),
            builder.Text("Git will start rewriting commits as soon as this editor closes successfully."),
            execCommands == 0
                ? builder.Text("No shell commands are present in this plan.")
                : builder.Text($"Warning: Git will run {execCommands} explicitly trusted shell {(execCommands == 1 ? "command" : "commands")}.").Wrap(),
            builder.HStack(buttons =>
            [
                buttons.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                buttons.Text(" "),
                buttons.Button(execCommands > 0 && !_session.ExecCommandsTrusted
                    ? "Trust exec and save"
                    : "Save plan").OnClick(eventArgs =>
                {
                    if (execCommands > 0 && !_session.ExecCommandsTrusted)
                    {
                        _session.TrustExecCommands();
                    }

                    if (_session.TrySave())
                    {
                        window.Window.CloseWithResult(true);
                        eventArgs.Context.RequestStop();
                    }
                }),
            ]),
        ]).InputBindings(bindings => bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Window.Cancel(),
            "Close the save-plan confirmation")))
        .Title("Start interactive rebase?")
        .Size(76, 10)
        .Resizable(58, 10, 110, 18));
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

    private static string GetActionDescription(RebaseTodoAction action)
        => action switch
        {
            RebaseTodoAction.Pick => "Apply this commit without editing its message.",
            RebaseTodoAction.Reword => "Apply this commit and edit its commit message.",
            RebaseTodoAction.Edit => "Apply this commit and stop so its contents can be amended.",
            RebaseTodoAction.Squash => "Combine this commit with the preceding commit and edit the combined message.",
            RebaseTodoAction.Fixup => "Combine this commit with the preceding commit and discard this message.",
            RebaseTodoAction.Drop => "Omit this commit from the rewritten history.",
            RebaseTodoAction.Exec => "Run this explicitly trusted command through a shell.",
            RebaseTodoAction.Break => "Stop before the next todo command.",
            RebaseTodoAction.Label => "Label the current sequencer head.",
            RebaseTodoAction.Reset => "Reset the sequencer head to a label.",
            RebaseTodoAction.Merge => "Recreate a merge while preserving topology.",
            RebaseTodoAction.UpdateRef => "Update this ref after the rebase completes.",
            RebaseTodoAction.Noop => "Perform no sequencer operation.",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
}
