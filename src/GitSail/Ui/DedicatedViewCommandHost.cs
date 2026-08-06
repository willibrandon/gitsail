using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Presents live help, command-palette, and application-menu windows for dedicated views.
/// </summary>
internal sealed class DedicatedViewCommandHost
{
    private readonly string _contextName;
    private readonly Func<IReadOnlyList<WorkspaceCommandItem>> _commandProvider;
    private readonly List<WindowHandle> _popupWindows = [];
    private readonly PopupViewport _popupViewport = new();
    private Hex1bApp? _application;
    private WindowManager? _popupWindowManager;

    /// <summary>
    /// Initializes discovery windows for one dedicated application context.
    /// </summary>
    /// <param name="contextName">The concise user-facing name of the active context.</param>
    /// <param name="commandProvider">Builds the current action list and availability state.</param>
    internal DedicatedViewCommandHost(
        string contextName,
        Func<IReadOnlyList<WorkspaceCommandItem>> commandProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextName);
        ArgumentNullException.ThrowIfNull(commandProvider);
        _contextName = contextName;
        _commandProvider = commandProvider;
    }

    /// <summary>
    /// Connects command-window invalidation and quit behavior to the terminal application.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    internal void Attach(Hex1bApp application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The dedicated command host is already attached.");
        }

        _application = application;
    }

    /// <summary>
    /// Disconnects the command host and forgets every closed application window.
    /// </summary>
    internal void Detach()
    {
        _application = null;
        _popupWindowManager = null;
        _popupWindows.Clear();
    }

    /// <summary>
    /// Records the current root viewport for bounded command-window sizing.
    /// </summary>
    /// <param name="width">The available terminal width.</param>
    /// <param name="height">The available terminal height.</param>
    /// <returns><see langword="true"/> so the responsive branch remains selected.</returns>
    internal bool CaptureViewport(int width, int height)
        => _popupViewport.Capture(width, height);

    /// <summary>
    /// Adds universal help, command-palette, menu, and close-window bindings.
    /// </summary>
    /// <param name="bindings">The active dedicated-view binding collection.</param>
    internal void ConfigureBindings(InputBindingsBuilder bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        bindings.Key(Hex1bKey.F1).Triggers(
            WorkspaceActionIds.Help,
            actionContext => ShowHelp(actionContext.Windows),
            "Open context help and the live keyboard reference");
        bindings.Key(Hex1bKey.F2).Triggers(
            WorkspaceActionIds.CommandPalette,
            actionContext => ShowCommandPalette(actionContext.Windows),
            "Open the searchable command palette");
        bindings.Key(Hex1bKey.F10).Triggers(
            WorkspaceActionIds.ApplicationMenu,
            actionContext => ShowApplicationMenu(actionContext.Windows),
            "Open the complete application menu");
        bindings.Ctrl().Key(Hex1bKey.W).Triggers(
            WorkspaceActionIds.CloseWindow,
            actionContext => CloseWindowOrView(actionContext.Windows),
            "Close the active window or dedicated view");
    }

    /// <summary>
    /// Builds the click-away and one-Escape dismissal layer for an active command window.
    /// </summary>
    /// <typeparam name="TParent">The parent widget type.</typeparam>
    /// <param name="context">The widget context receiving the backdrop.</param>
    /// <returns>A transparent dismissal layer, or <see langword="null"/> when no command window is open.</returns>
    internal Hex1bWidget? BuildBackdrop<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _popupWindows.Count == 0
            ? null
            : context.Backdrop()
                .Transparent()
                .OnClickAway(CloseActivePopup)
                .InputBindings(bindings => bindings.Key(Hex1bKey.Escape)
                    .Global()
                    .OverridesCapture()
                    .Action(
                        _ => CloseActivePopup(),
                        "Close the active command window"));

    private List<WorkspaceCommandItem> GetCommands()
    {
        var commands = _commandProvider().ToList();
        AddApplicationCommand(
            commands,
            WorkspaceActionIds.Help.Value,
            "Context help",
            "Open the live keyboard and action reference for this view.",
            "F1",
            windows => ShowHelp(windows));
        AddApplicationCommand(
            commands,
            WorkspaceActionIds.CommandPalette.Value,
            "Command palette",
            "Search every action, binding, description, and availability reason in this view.",
            "F2",
            windows => ShowCommandPalette(windows));
        AddApplicationCommand(
            commands,
            WorkspaceActionIds.ApplicationMenu.Value,
            "Application menu",
            "Browse every action in this view by category.",
            "F10",
            windows => ShowApplicationMenu(windows));
        if (!commands.Any(command => string.Equals(
            command.Id,
            WorkspaceActionIds.CloseWindow.Value,
            StringComparison.Ordinal)))
        {
            commands.Add(new WorkspaceCommandItem(
                WorkspaceActionIds.CloseWindow.Value,
                "Application",
                "Close window or view",
                "Close the active command window, or close this dedicated view when no window is open.",
                "Ctrl+W",
                null,
                windows =>
                {
                    CloseWindowOrView(windows);
                    return Task.CompletedTask;
                }));
        }

        if (!commands.Any(command => string.Equals(
            command.Id,
            WorkspaceActionIds.Quit.Value,
            StringComparison.Ordinal)))
        {
            commands.Add(new WorkspaceCommandItem(
                WorkspaceActionIds.Quit.Value,
                "Application",
                "Quit",
                "Close the current GitSail terminal session.",
                "Ctrl+Q",
                null,
                _ =>
                {
                    _application?.RequestStop();
                    return Task.CompletedTask;
                }));
        }

        return commands;
    }

    private static void AddApplicationCommand(
        List<WorkspaceCommandItem> commands,
        string id,
        string label,
        string description,
        string binding,
        Action<WindowManager> execute)
    {
        if (commands.Any(command => string.Equals(command.Id, id, StringComparison.Ordinal)))
        {
            return;
        }

        commands.Add(new WorkspaceCommandItem(
            id,
            "Help",
            label,
            description,
            binding,
            null,
            windows =>
            {
                execute(windows);
                return Task.CompletedTask;
            }));
    }

    private void ShowHelp(WindowManager windows)
    {
        var commands = GetCommands();
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        [
            builder.Text($"{_contextName} help and keyboard reference").Wrap(),
            builder.VScrollPanel(content =>
            [
                .. commands.Select(command => content.Text(
                    $"{command} — {command.Description} " +
                    (command.IsAvailable
                        ? "Available now."
                        : $"Unavailable: {command.UnavailableReason}"))
                    .Wrap()),
            ], showScrollbar: true).Fill(),
            builder.Button("Close").OnClick(_ => window.Window.Cancel()),
            builder.Text("Up/Down or mouse wheel scrolls | Esc/click outside closes"),
        ]).InputBindings(bindings => ConfigureWindowBindings(bindings, window.Window)))
        .Title("Help and keyboard reference")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(22))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 14, 132, 48));
    }

    private void ShowCommandPalette(WindowManager windows)
    {
        var filterState = new TextBoxState();
        string? focusedId = null;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var filter = filterState.Text.Trim();
            var commands = GetCommands();
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
                            .OnSubmit(_ => ExecuteCommandAsync(
                                ResolveCommand(filterState.Text, focusedId),
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
                    .OnItemActivated(eventArgs => ExecuteCommandAsync(
                        eventArgs.ActivatedItem,
                        window.Window,
                        windows))
                    .Empty(empty => empty.Text("No command matches the current filter."))
                    .InputBindings(bindings => bindings.Key(Hex1bKey.Enter).Action(
                        _ => ExecuteCommandAsync(focused, window.Window, windows),
                        "Run the focused available action"))
                    .Fill(),
                builder.Text(focused?.Description ?? "Type to search every action in this view.").Wrap(),
                builder.Text(GetAvailabilityText(focused)).Wrap(),
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    focused?.IsAvailable == true
                        ? actions.Button("Run selected").OnClick(
                            _ => ExecuteCommandAsync(focused, window.Window, windows))
                        : actions.Text("Run selected unavailable"),
                ]),
                builder.Text("Type filter | Up/Down | Enter/mouse runs | Esc/click outside closes"),
            ];
        }).InputBindings(bindings => ConfigureWindowBindings(bindings, window.Window)))
        .Title("Command palette")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(18))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 132, 48));
    }

    private void ShowApplicationMenu(WindowManager windows)
    {
        var categoryIndex = 0;
        string? focusedId = null;
        OpenPopup(windows, windows.Window(window => window.VStack(builder =>
        {
            var commands = GetCommands();
            var categories = commands
                .SelectMany(static command => command.MenuCategories)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (categoryIndex >= categories.Count)
            {
                categoryIndex = 0;
            }

            var category = categories.Count == 0 ? "Application" : categories[categoryIndex];
            var categoryCommands = commands
                .Where(command => command.MenuCategories.Contains(category, StringComparer.Ordinal))
                .ToList();
            var commandIndex = focusedId is null
                ? 0
                : Math.Max(0, categoryCommands.FindIndex(command => string.Equals(
                    command.Id,
                    focusedId,
                    StringComparison.Ordinal)));
            if (commandIndex >= categoryCommands.Count)
            {
                commandIndex = 0;
            }

            var focused = categoryCommands.Count == 0 ? null : categoryCommands[commandIndex];
            focusedId = focused?.Id;
            return
            [
                builder.HSplitter(
                    builder.Border(DismissOnEscape(
                        builder.List(categories)
                            .ItemKey(static item => item)
                            .FocusedIndex(categoryIndex)
                            .OnFocusChanged(eventArgs =>
                            {
                                if (eventArgs.FocusedIndex >= 0 && eventArgs.FocusedIndex < categories.Count)
                                {
                                    categoryIndex = eventArgs.FocusedIndex;
                                    focusedId = null;
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
                            .OnFocusChanged(eventArgs =>
                            {
                                if (eventArgs.FocusedIndex >= 0 && eventArgs.FocusedIndex < categoryCommands.Count)
                                {
                                    focusedId = categoryCommands[eventArgs.FocusedIndex].Id;
                                    _application?.Invalidate();
                                }
                            })
                            .OnItemActivated(eventArgs => ExecuteCommandAsync(
                                eventArgs.ActivatedItem,
                                window.Window,
                                windows))
                            .InputBindings(bindings => bindings.Key(Hex1bKey.Enter).Action(
                                _ => ExecuteCommandAsync(focused, window.Window, windows),
                                "Run the focused available menu action"))
                            .Fill(),
                        window.Window))
                        .Title(category)
                        .Fill(),
                    18).Fill(),
                builder.Text(focused?.Description ?? "No action is available in this menu.").Wrap(),
                builder.Text(GetAvailabilityText(focused)).Wrap(),
                builder.HStack(actions =>
                [
                    actions.Button("Cancel").OnClick(_ => window.Window.Cancel()),
                    actions.Text(" "),
                    focused?.IsAvailable == true
                        ? actions.Button("Run selected").OnClick(
                            _ => ExecuteCommandAsync(focused, window.Window, windows))
                        : actions.Text("Run selected unavailable"),
                ]),
                builder.Text("Tab lists | Enter/mouse runs | Esc/click outside closes"),
            ];
        }).InputBindings(bindings => ConfigureWindowBindings(bindings, window.Window)))
        .Title("GitSail menu")
        .Size(_popupViewport.FitWidth(78), _popupViewport.FitHeight(18))
        .Position(new WindowPositionSpec(WindowPosition.TopLeft, 1, 1))
        .Resizable(58, 16, 132, 48));
    }

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

            _application?.Invalidate();
        });
        popup.Open(windows);
    }

    private void CloseActivePopup()
    {
        if (_popupWindowManager is not { } windows || windows.ActiveWindow is not { } active)
        {
            return;
        }

        for (var index = _popupWindows.Count - 1; index >= 0; index--)
        {
            var popup = _popupWindows[index];
            if (ReferenceEquals(windows.Get(popup), active))
            {
                windows.Close(popup);
                return;
            }
        }
    }

    private void CloseWindowOrView(WindowManager windows)
    {
        if (windows.ActiveWindow is { } active)
        {
            active.Close();
            return;
        }

        _application?.RequestStop();
    }

    private WorkspaceCommandItem? ResolveCommand(string filterText, string? focusedId)
    {
        var filter = filterText.Trim();
        var visible = string.IsNullOrEmpty(filter)
            ? GetCommands()
            : [.. GetCommands().Where(command => command.Matches(filter))];
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

    private static async Task ExecuteCommandAsync(
        WorkspaceCommandItem? command,
        WindowHandle window,
        WindowManager windows)
    {
        if (command?.IsAvailable != true)
        {
            return;
        }

        window.Cancel();
        await command.ExecuteAsync(windows).ConfigureAwait(false);
    }

    private static string GetAvailabilityText(WorkspaceCommandItem? command)
        => command is null
            ? "No matching action."
            : command.IsAvailable
                ? "Available now."
                : $"Unavailable: {command.UnavailableReason}";

    private static void ConfigureWindowBindings(
        InputBindingsBuilder bindings,
        WindowHandle window)
    {
        bindings.Key(Hex1bKey.Escape).Action(
            _ => window.Cancel(),
            "Close the active command window");
        bindings.Ctrl().Key(Hex1bKey.W).Action(
            _ => window.Cancel(),
            "Close the active command window");
        bindings.Ctrl().Key(Hex1bKey.Q).Action(
            actionContext => actionContext.RequestStop(),
            "Quit GitSail");
    }

    private static TWidget DismissOnEscape<TWidget>(TWidget widget, WindowHandle window)
        where TWidget : Hex1bWidget
        => widget.InputBindings(bindings =>
        {
            bindings.Remove(Hex1bKey.Escape);
            bindings.Key(Hex1bKey.Escape).Action(
                _ => window.Cancel(),
                "Close the active command window");
        });
}
