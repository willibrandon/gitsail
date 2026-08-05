using GitSail.Localization.Generated;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Composes the responsive keyboard-and-mouse immutable comparison workflow.
/// </summary>
internal sealed class DiffView
{
    private const int NoAuxiliaryInput = 0;
    private const int PathFilterInput = 1;
    private const int TextSearchInput = 2;
    private const int LineNavigationInput = 3;
    private readonly DiffSession _session;
    private readonly CancellationToken _cancellationToken;
    private Hex1bApp? _application;
    private int _visibleAuxiliaryInput;

    /// <summary>
    /// Initializes a comparison view over controlled session state.
    /// </summary>
    /// <param name="session">The comparison state and action source.</param>
    /// <param name="cancellationToken">Signals application shutdown.</param>
    internal DiffView(DiffSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Connects comparison invalidation notifications to the owning terminal application.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    internal void Attach(Hex1bApp application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The comparison view is already attached.");
        }

        _application = application;
        _session.Changed += HandleChanged;
    }

    /// <summary>
    /// Disconnects comparison invalidation notifications from the owning application.
    /// </summary>
    internal void Detach()
    {
        if (_application is null)
        {
            return;
        }

        _session.Changed -= HandleChanged;
        _application = null;
    }

    /// <summary>
    /// Builds the complete responsive comparison widget tree for one render generation.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The immutable comparison workspace.</returns>
    internal WindowPanelWidget Build(RootContext context)
        => context.WindowPanel()
            .Background(background => background.Responsive(responsive =>
            [
                responsive.When(
                    static (width, height) => width < 60 || height < 18,
                    compact => BuildResizeView(compact)),
                responsive.Otherwise(ready => BuildWorkspace(ready)),
            ]).InputBindings(ConfigureBindings).Fill())
            .Fill();

    private VStackWidget BuildWorkspace<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            BuildHeader(builder),
            builder.Responsive(responsive =>
            [
                responsive.WhenMinWidth(
                    100,
                    wide => wide.HSplitter(
                        BuildFilePane(wide),
                        BuildComparisonPane(wide),
                        34).Fill()),
                responsive.WhenMinWidth(80, medium => medium.VSplitter(
                    BuildFilePane(medium),
                    BuildComparisonPane(medium),
                    7).Fill()),
                responsive.Otherwise(narrow => narrow.VSplitter(
                    BuildFilePane(narrow),
                    BuildComparisonPane(narrow),
                    5).Fill()),
            ]).Fill(),
            BuildActions(builder),
            BuildShortcuts(builder),
        ]).Fill();

    private static VStackWidget BuildResizeView<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.VStack(builder =>
        [
            builder.Border(builder.Text(
                AppMessages.WorkspaceResizeRequirement).Wrap())
                .Title(AppMessages.WorkspaceResizeTitle)
                .Fill(),
            builder.HStack(actions =>
            [
                actions.Text(string.Empty).FillWidth(),
                actions.Button(AppMessages.WorkspaceActionQuit)
                    .OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth(),
            builder.InfoBar(info =>
            [
                info.Section(AppMessages.DiffActionResizeTerminal),
                info.Spacer(),
                info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
            ]).Divider(" | ").FillWidth(),
        ]).Fill();

    private void ConfigureBindings(InputBindingsBuilder bindings)
    {
        bindings.Key(Hex1bKey.F5).Action(
            _ => _session.LoadAsync(_cancellationToken),
            AppMessages.DiffBindingReload);
        bindings.Ctrl().Key(Hex1bKey.R).Action(
            _ => _session.LoadAsync(_cancellationToken),
            AppMessages.DiffBindingReload);
        bindings.Key(Hex1bKey.F7).Action(
            _ => ShowAuxiliaryInput(PathFilterInput),
            AppMessages.DiffBindingFocusPathSearch);
        bindings.Ctrl().Key(Hex1bKey.F).Action(
            _ => ShowAuxiliaryInput(TextSearchInput),
            AppMessages.DiffBindingFocusTextSearch);
        bindings.Key(Hex1bKey.F3).Action(
            actionContext => FindTextAsync(actionContext, reverse: false),
            AppMessages.DiffBindingNextTextMatch);
        bindings.Shift().Key(Hex1bKey.F3).Action(
            actionContext => FindTextAsync(actionContext, reverse: true),
            AppMessages.DiffBindingPreviousTextMatch);
        bindings.Alt().Key(Hex1bKey.G).Action(
            _ => ShowAuxiliaryInput(LineNavigationInput),
            AppMessages.DiffBindingFocusLineNavigation);
        bindings.Key(Hex1bKey.J).Action(
            actionContext => MoveHunkAsync(actionContext, 1),
            AppMessages.DiffBindingNextHunk);
        bindings.Key(Hex1bKey.K).Action(
            actionContext => MoveHunkAsync(actionContext, -1),
            AppMessages.DiffBindingPreviousHunk);
        bindings.Key(Hex1bKey.N).Action(
            _ => _session.MoveFileAsync(1, _cancellationToken),
            AppMessages.DiffBindingNextFile);
        bindings.Shift().Key(Hex1bKey.N).Action(
            _ => _session.MoveFileAsync(-1, _cancellationToken),
            AppMessages.DiffBindingPreviousFile);
        bindings.Key(Hex1bKey.V).Action(
            actionContext => ToggleLayoutAsync(actionContext),
            AppMessages.DiffBindingToggleLayout);
        bindings.Key(Hex1bKey.Oem4).Action(
            _ => _session.ChangeContextAsync(-1, _cancellationToken),
            AppMessages.DiffBindingLessContext);
        bindings.Key(Hex1bKey.Oem6).Action(
            _ => _session.ChangeContextAsync(1, _cancellationToken),
            AppMessages.DiffBindingMoreContext);
        bindings.Ctrl().Key(Hex1bKey.Q).Action(
            actionContext => actionContext.RequestStop(),
            AppMessages.DiffBindingQuit);
    }

    private ResponsiveWidget BuildHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(130, wide => wide.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("diff"),
                info.Section(Shorten(_session.ComparisonLabel, 48)),
                info.Spacer(),
                info.Section(RepositoryLabel.Create(_session.Repository)),
                info.Section($"Git {_session.Installation.Version}"),
            ]).Divider(" | ").FillWidth()),
            responsive.WhenMinWidth(
                100,
                compact => BuildCompactHeader(compact, 46, 48)),
            responsive.WhenMinWidth(
                80,
                compact => BuildCompactHeader(compact, 34, 40)),
            responsive.Otherwise(
                compact => BuildCompactHeader(compact, 26, 28)),
        ]);

    private VStackWidget BuildCompactHeader<TParent>(
        WidgetContext<TParent> context,
        int comparisonLength,
        int repositoryLength)
        where TParent : Hex1bWidget
        => context.VStack(header =>
        [
            header.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("diff"),
                info.Spacer(),
                info.Section($"Git {_session.Installation.Version}"),
            ]).Divider(" | ").FillWidth(),
            header.InfoBar(info =>
            [
                info.Section(Shorten(_session.ComparisonLabel, comparisonLength)),
                info.Spacer(),
                info.Section(Shorten(
                    RepositoryLabel.Create(_session.Repository),
                    repositoryLength)),
            ]).Divider(" | ").FillWidth(),
        ]).FillWidth();

    private BorderWidget BuildFilePane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(files =>
        [
            files.HStack(controls =>
            [
                controls.Button(AppMessages.WorkspaceActionPaths).OnClick(
                    _ => ToggleAuxiliaryInput(PathFilterInput)),
                controls.Text(" "),
                controls.Button(AppMessages.DiffActionText).OnClick(
                    _ => ToggleAuxiliaryInput(TextSearchInput)),
                controls.Text(" "),
                controls.Button(AppMessages.DiffActionLine).OnClick(
                    _ => ToggleAuxiliaryInput(LineNavigationInput)),
                controls.Text(string.Empty).FillWidth(),
            ]).FillWidth(),
            .. BuildAuxiliaryInputs(files),
            files.List(_session.State.VisibleItems)
                .ItemKey(static item => item.File.NewPath)
                .FocusedIndex(_session.State.FocusedIndex)
                .OnFocusChanged(eventArgs => _session.FocusAsync(
                    eventArgs.FocusedIndex,
                    _cancellationToken))
                .Empty(empty => empty.Text(
                    _session.State.Filter.Text.Length == 0
                        ? AppMessages.DiffStatusNoChangedFiles
                        : AppMessages.DiffStatusNoFilterMatch))
                .Fill(),
        ]).Fill())
        .Title(AppMessages.DiffTitleChangedFiles(_session.State.VisibleItems.Length))
        .Fill();

    private HStackWidget[] BuildAuxiliaryInputs<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _visibleAuxiliaryInput == NoAuxiliaryInput
            ? []
            : [BuildAuxiliaryInput(context)];

    private HStackWidget BuildAuxiliaryInput<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _visibleAuxiliaryInput switch
        {
            PathFilterInput => context.HStack(filter =>
            [
                filter.Text($"{AppMessages.WorkspaceActionPaths}: "),
                filter.TextBox()
                    .State(_session.State.Filter)
                    .InputBindings(ConfigureAuxiliaryInputBindings)
                    .OnTextChanged(eventArgs => _session.FilterAsync(
                        eventArgs.NewText,
                        _cancellationToken))
                    .FillWidth(),
                filter.Text(" "),
                filter.Button(AppMessages.DiffActionHide).OnClick(_ => HideAuxiliaryInput()),
            ]).FillWidth(),
            TextSearchInput => context.HStack(search =>
            [
                search.Text($"{AppMessages.DiffActionText}: "),
                search.TextBox()
                    .State(_session.State.Search)
                    .InputBindings(ConfigureAuxiliaryInputBindings)
                    .OnSubmit(eventArgs => FindTextAsync(
                        eventArgs.Context,
                        reverse: false))
                    .FillWidth(),
                search.Text(" "),
                search.Button(AppMessages.DiffActionPreviousShort).OnClick(
                    eventArgs => FindTextAsync(eventArgs.Context, reverse: true)),
                search.Text(" "),
                search.Button(AppMessages.DiffActionNext).OnClick(
                    eventArgs => FindTextAsync(eventArgs.Context, reverse: false)),
                search.Text(" "),
                search.Button(AppMessages.DiffActionHide).OnClick(_ => HideAuxiliaryInput()),
            ]).FillWidth(),
            LineNavigationInput => context.HStack(line =>
            [
                line.Text($"{AppMessages.DiffActionLine}: "),
                line.TextBox()
                    .State(_session.State.GoToLine)
                    .InputBindings(ConfigureAuxiliaryInputBindings)
                    .OnSubmit(eventArgs => GoToPresentationLineAsync(eventArgs.Context))
                    .FixedWidth(8),
                line.Text(" "),
                line.Button(AppMessages.DiffActionGo).OnClick(
                    eventArgs => GoToPresentationLineAsync(eventArgs.Context)),
                line.Text(" "),
                line.Button(AppMessages.DiffActionHide).OnClick(_ => HideAuxiliaryInput()),
                line.Text(string.Empty).FillWidth(),
            ]).FillWidth(),
            _ => throw new InvalidOperationException("The comparison input is not supported."),
        };

    private void ConfigureAuxiliaryInputBindings(InputBindingsBuilder bindings)
    {
        bindings.Remove(Hex1bKey.Escape);
        bindings.Key(Hex1bKey.Escape).Action(
            _ => HideAuxiliaryInput(),
            AppMessages.DiffBindingHideInput);
    }

    private Hex1bWidget BuildComparisonPane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.State.IsSideBySide
            ? context.Responsive(responsive =>
            [
                responsive.WhenMinWidth(150, roomy => BuildSideBySide(roomy, 74)),
                responsive.WhenMinWidth(120, wide => BuildSideBySide(wide, 59)),
                responsive.WhenMinWidth(90, medium => BuildSideBySide(medium, 44)),
                responsive.Otherwise(compact => BuildSideBySide(compact, 29)),
            ]).Fill()
            : BuildUnified(context);

    private SplitterWidget BuildSideBySide<TParent>(
        WidgetContext<TParent> context,
        int leftWidth)
        where TParent : Hex1bWidget
        => context.HSplitter(
            context.Border(
                ConfigureComparisonEditor(context.Editor(_session.State.LeftEditor)
                    .Gutter(_session.State.LeftGutterProvider)
                    .WordWrap(false)
                    .Decorations(_session.State.LeftDecorationProvider)
                    .Fill()))
                .Title(_session.State.LeftTitle)
                .Fill(),
            context.Border(
                ConfigureComparisonEditor(context.Editor(_session.State.RightEditor)
                    .Gutter(_session.State.RightGutterProvider)
                    .WordWrap(false)
                    .Decorations(_session.State.RightDecorationProvider)
                    .Fill()))
                .Title(_session.State.RightTitle)
                .Fill(),
            leftWidth).Fill();

    private BorderWidget BuildUnified<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(
            ConfigureComparisonEditor(context.Editor(_session.State.UnifiedEditor)
                .Gutter(_session.State.UnifiedGutterProvider)
                .WordWrap(false)
                .Decorations(_session.State.UnifiedDecorationProvider)
                .Fill()))
            .Title(_session.State.UnifiedTitle)
            .Fill();

    private ResponsiveWidget BuildActions<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(130, wide => wide.HStack(actions =>
            [
                BuildRefreshAction(actions, AppMessages.WorkspaceActionRefresh),
                actions.Text(" "),
                actions.Button(_session.State.IsSideBySide
                    ? AppMessages.DiffActionUnified
                    : AppMessages.DiffActionSideBySide)
                    .OnClick(eventArgs => ToggleLayoutAsync(eventArgs.Context)),
                actions.Text(" "),
                actions.Button(AppMessages.DiffActionPreviousHunk).OnClick(
                    eventArgs => MoveHunkAsync(eventArgs.Context, -1)),
                actions.Text(" "),
                actions.Button(AppMessages.DiffActionNextHunk).OnClick(
                    eventArgs => MoveHunkAsync(eventArgs.Context, 1)),
                actions.Text(" "),
                actions.Button(AppMessages.DiffActionCopy).OnClick(_ => CopyPatch()),
                actions.Text(" "),
                BuildContextAction(actions, "[", -1),
                actions.Text(" "),
                BuildContextAction(actions, "]", 1),
                actions.Text(string.Empty).FillWidth(),
                actions.Button(AppMessages.WorkspaceActionQuit)
                    .OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth()),
            responsive.WhenMinWidth(80, compact => compact.HStack(actions =>
            [
                BuildRefreshAction(actions, AppMessages.WorkspaceActionRefresh),
                actions.Text(" "),
                actions.Button(AppMessages.DiffActionView)
                    .OnClick(eventArgs => ToggleLayoutAsync(eventArgs.Context)),
                actions.Text(" "),
                actions.Button(AppMessages.DiffActionPreviousShort).OnClick(
                    eventArgs => MoveHunkAsync(eventArgs.Context, -1)),
                actions.Text(" "),
                actions.Button(AppMessages.DiffActionNext).OnClick(
                    eventArgs => MoveHunkAsync(eventArgs.Context, 1)),
                actions.Text(" "),
                actions.Button(AppMessages.DiffActionCopy).OnClick(_ => CopyPatch()),
                actions.Text(" "),
                BuildContextAction(actions, "[", -1),
                actions.Text(" "),
                BuildContextAction(actions, "]", 1),
                actions.Text(string.Empty).FillWidth(),
                actions.Button(AppMessages.WorkspaceActionQuit)
                    .OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth()),
            responsive.Otherwise(narrow => narrow.HStack(actions =>
            [
                BuildRefreshAction(actions, "F5"),
                actions.Text(" "),
                actions.Button("V").OnClick(eventArgs => ToggleLayoutAsync(eventArgs.Context)),
                actions.Text(" "),
                actions.Button("K").OnClick(
                    eventArgs => MoveHunkAsync(eventArgs.Context, -1)),
                actions.Text(" "),
                actions.Button("J").OnClick(
                    eventArgs => MoveHunkAsync(eventArgs.Context, 1)),
                actions.Text(" "),
                actions.Button("C").OnClick(_ => CopyPatch()),
                actions.Text(" "),
                BuildContextAction(actions, "[", -1),
                actions.Text(" "),
                BuildContextAction(actions, "]", 1),
                actions.Text(string.Empty).FillWidth(),
                actions.Button("Q").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth()),
        ]);

    private ResponsiveWidget BuildShortcuts<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(160, roomy => roomy.VStack(rows =>
            [
                rows.InfoBar(info =>
                [
                    info.Section($"F5 {AppMessages.WorkspaceActionRefresh}"),
                    info.Section($"F7 {AppMessages.WorkspaceActionPaths}"),
                    info.Section($"Ctrl+F {AppMessages.DiffActionText}"),
                    info.Section($"F3/Shift+F3 {AppMessages.DiffActionMatches}"),
                    info.Section($"Alt+G {AppMessages.DiffActionLine}"),
                ]).Divider(" | "),
                rows.InfoBar(info =>
                [
                    info.Section($"N/Shift+N {AppMessages.DiffActionFiles}"),
                    info.Section($"J/K {AppMessages.DiffActionHunks}"),
                    info.Section($"V {AppMessages.DiffActionView}"),
                    info.Section($"[/] {AppMessages.WorkspaceActionContext} ({_session.ContextLines})"),
                    info.Spacer(),
                    info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
                ]).Divider(" | "),
                rows.InfoBar(info =>
                [
                    info.Section($"{AppMessages.WorkspaceActionMouse} {AppMessages.DiffActionSelectScrollResize}"),
                    info.Spacer(),
                    info.Section(_session.Activity),
                ]).Divider(" | "),
            ])),
            responsive.WhenMinWidth(120, wide => wide.VStack(rows =>
            [
                rows.InfoBar(info =>
                [
                    info.Section($"F5 {AppMessages.WorkspaceActionRefresh}"),
                    info.Section($"F7 {AppMessages.WorkspaceActionPaths}"),
                    info.Section($"Ctrl+F {AppMessages.DiffActionText}"),
                    info.Section($"F3 {AppMessages.DiffActionMatch}"),
                ]).Divider(" | "),
                rows.InfoBar(info =>
                [
                    info.Section($"N/Shift+N {AppMessages.DiffActionFiles}"),
                    info.Section($"J/K {AppMessages.DiffActionHunks}"),
                    info.Section($"V {AppMessages.DiffActionView}"),
                    info.Section($"[/] {_session.ContextLines}"),
                    info.Spacer(),
                    info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
                ]).Divider(" | "),
            ])),
            responsive.WhenMinWidth(80, compact => compact.InfoBar(info =>
            [
                info.Section($"F5 {AppMessages.WorkspaceActionRefresh}"),
                info.Section($"F7 {AppMessages.DiffActionFind}"),
                info.Section($"V {AppMessages.DiffActionView}"),
                info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
            ]).Divider(" | ")),
            responsive.Otherwise(narrow => narrow.InfoBar(info =>
            [
                info.Section($"F7 {AppMessages.WorkspaceActionPaths}"),
                info.Section($"Ctrl+F {AppMessages.DiffActionText}"),
                info.Section($"Alt+G {AppMessages.DiffActionLine}"),
                info.Section($"Ctrl+Q {AppMessages.WorkspaceActionQuit}"),
            ]).Divider(" | ")),
        ]);

    private Hex1bWidget BuildRefreshAction<TParent>(
        WidgetContext<TParent> context,
        string label)
        where TParent : Hex1bWidget
        => _session.IsBusy
            ? context.Text($" {label} ")
            : context.Button(label).OnClick(_ => _session.LoadAsync(_cancellationToken));

    private Hex1bWidget BuildContextAction<TParent>(
        WidgetContext<TParent> context,
        string label,
        int offset)
        where TParent : Hex1bWidget
        => _session.IsBusy || (offset < 0 && _session.ContextLines == 0)
            ? context.Text($" {label} ")
            : context.Button(label).OnClick(
                _ => _session.ChangeContextAsync(offset, _cancellationToken));

    private EditorWidget ConfigureComparisonEditor(EditorWidget editor)
        => editor.InputBindings(bindings =>
        {
            RemoveEditorMutationBindings(bindings);
            bindings.Remove(EditorWidget.ScrollUp);
            bindings.Remove(EditorWidget.ScrollDown);
            bindings.Remove(EditorWidget.PageUp);
            bindings.Remove(EditorWidget.PageDown);
            bindings.Mouse(MouseButton.ScrollUp).Action(
                actionContext => ExecuteVisibleEditorActionAsync(
                    actionContext,
                    EditorWidget.ScrollUp),
                AppMessages.DiffBindingScrollPanesUp);
            bindings.Mouse(MouseButton.ScrollDown).Action(
                actionContext => ExecuteVisibleEditorActionAsync(
                    actionContext,
                    EditorWidget.ScrollDown),
                AppMessages.DiffBindingScrollPanesDown);
            bindings.Key(Hex1bKey.PageUp).Action(
                actionContext => ExecuteVisibleEditorActionAsync(
                    actionContext,
                    EditorWidget.PageUp),
                AppMessages.DiffBindingPagePanesUp);
            bindings.Key(Hex1bKey.PageDown).Action(
                actionContext => ExecuteVisibleEditorActionAsync(
                    actionContext,
                    EditorWidget.PageDown),
                AppMessages.DiffBindingPagePanesDown);
        });

    private static void RemoveEditorMutationBindings(InputBindingsBuilder bindings)
    {
        var retainedKeys = bindings.Bindings
            .Where(static binding => !IsEditorMutationBinding(binding))
            .ToArray();
        var retainedMouse = bindings.MouseBindings.ToArray();
        var retainedDrag = bindings.DragBindings.ToArray();
        bindings.RemoveAll();
        foreach (var binding in retainedKeys)
        {
            bindings.Add(binding);
        }

        foreach (var binding in retainedMouse)
        {
            bindings.Add(binding);
        }

        foreach (var binding in retainedDrag)
        {
            bindings.Add(binding);
        }
    }

    private static bool IsEditorMutationBinding(InputBinding binding)
    {
        var actionId = binding.ActionId;
        if (actionId == EditorWidget.Undo ||
            actionId == EditorWidget.Redo ||
            actionId == EditorWidget.DeleteBackward ||
            actionId == EditorWidget.DeleteForward ||
            actionId == EditorWidget.DeleteWordBackward ||
            actionId == EditorWidget.DeleteWordForward ||
            actionId == EditorWidget.DeleteLine ||
            actionId == EditorWidget.InsertNewline ||
            actionId == EditorWidget.InsertTab)
        {
            return true;
        }

        var first = binding.FirstStep;
        return first is
        { Key: Hex1bKey.Spacebar, Modifiers: Hex1bModifiers.Control } or
        { Key: Hex1bKey.K, Modifiers: Hex1bModifiers.Control } or
        { Key: Hex1bKey.F12, Modifiers: Hex1bModifiers.None } or
        { Key: Hex1bKey.F12, Modifiers: Hex1bModifiers.Shift } or
        { Key: Hex1bKey.F4, Modifiers: Hex1bModifiers.None };
    }

    private async Task MoveHunkAsync(InputBindingActionContext actionContext, int offset)
    {
        _session.MoveHunk(offset);
        await BringVisibleEditorCursorsIntoViewAsync(actionContext).ConfigureAwait(false);
    }

    private Task ToggleLayoutAsync(InputBindingActionContext actionContext)
    {
        _session.ToggleLayout();
        var targetState = _session.State.IsSideBySide
            ? _session.State.RightEditor
            : _session.State.UnifiedEditor;
        _application?.RequestFocus(node =>
            node is EditorNode editor && ReferenceEquals(editor.State, targetState));
        actionContext.Invalidate();
        return Task.CompletedTask;
    }

    private async Task ExecuteVisibleEditorActionAsync(
        InputBindingActionContext actionContext,
        ActionId actionId)
    {
        foreach (var editor in actionContext.Focusables
            .OfType<EditorNode>()
            .Where(IsVisibleComparisonEditor))
        {
            await ExecuteEditorActionAsync(editor, actionContext, actionId).ConfigureAwait(false);
        }

        actionContext.Invalidate();
    }

    private async Task BringVisibleEditorCursorsIntoViewAsync(
        InputBindingActionContext actionContext)
    {
        foreach (var editor in actionContext.Focusables
            .OfType<EditorNode>()
            .Where(IsVisibleComparisonEditor))
        {
            var cursors = editor.State.Cursors.Snapshot();
            await ExecuteEditorActionAsync(
                editor,
                actionContext,
                EditorWidget.MoveToLineStart).ConfigureAwait(false);
            editor.State.Cursors.Restore(cursors);
        }

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

    private bool IsVisibleComparisonEditor(EditorNode editor)
        => ReferenceEquals(editor.State, _session.State.UnifiedEditor) ||
            ReferenceEquals(editor.State, _session.State.LeftEditor) ||
            ReferenceEquals(editor.State, _session.State.RightEditor);

    private void ToggleAuxiliaryInput(int input)
    {
        if (_visibleAuxiliaryInput == input)
        {
            HideAuxiliaryInput();
            return;
        }

        ShowAuxiliaryInput(input);
    }

    private void ShowAuxiliaryInput(int input)
    {
        _visibleAuxiliaryInput = input;
        _application?.RequestFocus(static node => node is TextBoxNode);
        _application?.Invalidate();
    }

    private void HideAuxiliaryInput()
    {
        var hiddenInput = _visibleAuxiliaryInput;
        _visibleAuxiliaryInput = NoAuxiliaryInput;
        _application?.RequestFocus(node => hiddenInput == PathFilterInput
            ? node is ListNode<DiffWorkspaceItem>
            : node is EditorNode editor && IsVisibleComparisonEditor(editor));
        _application?.Invalidate();
    }

    private async Task FindTextAsync(
        InputBindingActionContext actionContext,
        bool reverse)
    {
        _session.FindText(reverse);
        await BringVisibleEditorCursorsIntoViewAsync(actionContext).ConfigureAwait(false);
    }

    private async Task GoToPresentationLineAsync(InputBindingActionContext actionContext)
    {
        _session.GoToPresentationLine();
        await BringVisibleEditorCursorsIntoViewAsync(actionContext).ConfigureAwait(false);
    }

    private void CopyPatch()
        => _application?.CopyToClipboard(_session.GetUnifiedPresentation());

    private void HandleChanged()
        => _application?.Invalidate();

    private static string Shorten(string value, int maximumRunes)
    {
        var runes = value.EnumerateRunes().ToArray();
        if (runes.Length <= maximumRunes)
        {
            return value;
        }

        var builder = new StringBuilder(maximumRunes + 1);
        for (var index = 0; index < maximumRunes - 1; index++)
        {
            builder.Append(runes[index]);
        }

        return builder.Append('…').ToString();
    }
}
