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
    private readonly DiffSession _session;
    private readonly CancellationToken _cancellationToken;
    private Hex1bApp? _application;

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
            .Background(background => background.VStack(builder =>
            [
                BuildHeader(builder),
                builder.Responsive(responsive =>
                [
                    responsive.When(
                        static (width, height) => width < 60 || height < 18,
                        compact => compact.Border(compact.Text(
                            "Resize the terminal to at least 60 columns by 18 rows. Ctrl+Q remains available.").Wrap())
                            .Title("More room needed")
                            .Fill()),
                    responsive.WhenMinWidth(
                        100,
                        wide => wide.HSplitter(
                            BuildFilePane(wide),
                            BuildComparisonPane(wide),
                            34).Fill()),
                    responsive.Otherwise(medium => medium.VSplitter(
                        BuildFilePane(medium),
                        BuildComparisonPane(medium),
                        7).Fill()),
                ]).Fill(),
                BuildActions(builder),
                BuildShortcuts(builder),
            ]).InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.F5).Action(
                    _ => _session.LoadAsync(_cancellationToken),
                    "Reload the exact comparison");
                bindings.Ctrl().Key(Hex1bKey.R).Action(
                    _ => _session.LoadAsync(_cancellationToken),
                    "Reload the exact comparison");
                bindings.Key(Hex1bKey.F7).Action(
                    _ => FocusFilter(),
                    "Focus changed-file search");
                bindings.Key(Hex1bKey.J).Action(
                    _ => _session.MoveHunk(1),
                    "Focus the next comparison hunk");
                bindings.Key(Hex1bKey.K).Action(
                    _ => _session.MoveHunk(-1),
                    "Focus the previous comparison hunk");
                bindings.Key(Hex1bKey.N).Action(
                    _ => _session.MoveFileAsync(1, _cancellationToken),
                    "Focus the next changed file");
                bindings.Shift().Key(Hex1bKey.N).Action(
                    _ => _session.MoveFileAsync(-1, _cancellationToken),
                    "Focus the previous changed file");
                bindings.Key(Hex1bKey.V).Action(
                    _ => _session.ToggleLayout(),
                    "Toggle unified and side-by-side layouts");
                bindings.Key(Hex1bKey.Oem4).Action(
                    _ => _session.ChangeContextAsync(-1, _cancellationToken),
                    "Show one fewer unchanged line around each hunk");
                bindings.Key(Hex1bKey.Oem6).Action(
                    _ => _session.ChangeContextAsync(1, _cancellationToken),
                    "Show one more unchanged line around each hunk");
                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            }).Fill())
            .Fill();

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
            responsive.Otherwise(compact => compact.VStack(header =>
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
                    info.Section(Shorten(_session.ComparisonLabel, 28)),
                    info.Spacer(),
                    info.Section(RepositoryLabel.Create(_session.Repository)),
                ]).Divider(" | ").FillWidth(),
            ]).FillWidth()),
        ]);

    private BorderWidget BuildFilePane<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(context.VStack(files =>
        [
            files.HStack(filter =>
            [
                filter.Text("Find: "),
                filter.TextBox()
                    .State(_session.State.Filter)
                    .OnTextChanged(eventArgs => _session.FilterAsync(
                        eventArgs.NewText,
                        _cancellationToken))
                    .FillWidth(),
            ]).FillWidth(),
            files.List(_session.State.VisibleItems)
                .ItemKey(static item => item.File.NewPath)
                .FocusedIndex(_session.State.FocusedIndex)
                .OnFocusChanged(eventArgs => _session.FocusAsync(
                    eventArgs.FocusedIndex,
                    _cancellationToken))
                .Empty(empty => empty.Text(
                    _session.State.Filter.Text.Length == 0
                        ? "No changed files."
                        : "No changed path matches the filter."))
                .Fill(),
        ]).Fill())
        .Title($"Changed files ({_session.State.VisibleItems.Length})")
        .Fill();

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
                context.Editor(_session.State.LeftEditor)
                    .LineNumbers()
                    .WordWrap(false)
                    .Decorations(_session.State.LeftDecorationProvider)
                    .Fill())
                .Title(_session.State.LeftTitle)
                .Fill(),
            context.Border(
                context.Editor(_session.State.RightEditor)
                    .LineNumbers()
                    .WordWrap(false)
                    .Decorations(_session.State.RightDecorationProvider)
                    .Fill())
                .Title(_session.State.RightTitle)
                .Fill(),
            leftWidth).Fill();

    private BorderWidget BuildUnified<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Border(
            context.Editor(_session.State.UnifiedEditor)
                .LineNumbers()
                .WordWrap(false)
                .Decorations(_session.State.UnifiedDecorationProvider)
                .Fill())
            .Title(_session.State.UnifiedTitle)
            .Fill();

    private ResponsiveWidget BuildActions<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(100, wide => wide.HStack(actions =>
            [
                BuildRefreshAction(actions, "Refresh"),
                actions.Text(" "),
                actions.Button(_session.State.IsSideBySide ? "Unified" : "Side by side")
                    .OnClick(_ => _session.ToggleLayout()),
                actions.Text(" "),
                actions.Button("Previous hunk").OnClick(_ => _session.MoveHunk(-1)),
                actions.Text(" "),
                actions.Button("Next hunk").OnClick(_ => _session.MoveHunk(1)),
                actions.Text(" "),
                actions.Button("Copy patch").OnClick(_ => CopyPatch()),
                actions.Text(" "),
                BuildContextAction(actions, "[", -1),
                actions.Text(" "),
                BuildContextAction(actions, "]", 1),
                actions.Text(string.Empty).FillWidth(),
                actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth()),
            responsive.Otherwise(compact => compact.HStack(actions =>
            [
                BuildRefreshAction(actions, "Reload"),
                actions.Text(" "),
                actions.Button("View").OnClick(_ => _session.ToggleLayout()),
                actions.Text(" "),
                actions.Button("Prev").OnClick(_ => _session.MoveHunk(-1)),
                actions.Text(" "),
                actions.Button("Next").OnClick(_ => _session.MoveHunk(1)),
                actions.Text(" "),
                actions.Button("Copy").OnClick(_ => CopyPatch()),
                actions.Text(" "),
                BuildContextAction(actions, "[", -1),
                actions.Text(" "),
                BuildContextAction(actions, "]", 1),
                actions.Text(string.Empty).FillWidth(),
                actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth()),
        ]);

    private ResponsiveWidget BuildShortcuts<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(160, roomy => roomy.InfoBar(info =>
            [
                info.Section("F5 Refresh"),
                info.Section("F7 Find"),
                info.Section("N/Shift+N Files"),
                info.Section("J/K Hunks"),
                info.Section("V Change view"),
                info.Section($"[/] Context ({_session.ContextLines})"),
                info.Section("Mouse Select/Scroll/Resize"),
                info.Spacer(),
                info.Section(_session.Activity),
                info.Section("Ctrl+Q Quit"),
            ]).Divider(" | ")),
            responsive.WhenMinWidth(120, wide => wide.InfoBar(info =>
            [
                info.Section("F5 Refresh"),
                info.Section("F7 Find"),
                info.Section("N/Shift+N Files"),
                info.Section("J/K Hunks"),
                info.Section("V View"),
                info.Section($"[/] Context ({_session.ContextLines})"),
                info.Section("Mouse"),
                info.Spacer(),
                info.Section("Ctrl+Q Quit"),
            ]).Divider(" | ")),
            responsive.Otherwise(compact => compact.InfoBar(info =>
            [
                info.Section("F5 Reload"),
                info.Section("F7 Find"),
                info.Section("N Files"),
                info.Section("J/K Hunks"),
                info.Section("V View"),
                info.Section($"[/] Ctx {_session.ContextLines}"),
                info.Section("Ctrl+Q Quit"),
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

    private void FocusFilter()
    {
        _application?.RequestFocus(node =>
            node is TextBoxNode);
        _application?.Invalidate();
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
