using GitSail.Domain;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Composes the responsive keyboard-and-mouse line-history workflow.
/// </summary>
internal sealed class BlameView
{
    private readonly BlameSession _session;
    private readonly CancellationToken _cancellationToken;
    private Hex1bApp? _application;

    /// <summary>
    /// Initializes a line-history view over controlled session state.
    /// </summary>
    /// <param name="session">The exact blame state and action source.</param>
    /// <param name="cancellationToken">Signals application shutdown.</param>
    internal BlameView(BlameSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Connects blame invalidation notifications to the owning terminal application.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    internal void Attach(Hex1bApp application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The blame view is already attached.");
        }

        _application = application;
        _session.Changed += HandleChanged;
    }

    /// <summary>
    /// Disconnects blame invalidation notifications from the owning application.
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
    /// Builds the complete responsive blame widget tree for one render generation.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The exact-content line-history workspace.</returns>
    internal WindowPanelWidget Build(RootContext context)
        => context.WindowPanel()
            .Background(background => background.VStack(builder =>
            [
                BuildHeader(builder),
                BuildInputs(builder),
                builder.Responsive(responsive =>
                [
                    responsive.When(
                        static (width, height) => width < 60 || height < 18,
                        compact => compact.Border(compact.Text(
                            "Resize the terminal to at least 60 columns by 18 rows. Ctrl+Q remains available.").Wrap())
                            .Title("More room needed")
                            .Fill()),
                    responsive.WhenMinWidth(
                        110,
                        wide => BuildBlameContent(wide, 66)),
                    responsive.Otherwise(medium => BuildBlameContent(medium, 48)),
                ]).Fill(),
                BuildActions(builder),
                BuildShortcuts(builder),
            ]).InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.F5).Action(
                    _ => _session.LoadAsync(_cancellationToken),
                    "Refresh exact line history");
                bindings.Ctrl().Key(Hex1bKey.R).Action(
                    _ => _session.LoadAsync(_cancellationToken),
                    "Refresh exact line history");
                bindings.Key(Hex1bKey.F6).Action(
                    _ => _session.NavigateParentAsync(_cancellationToken),
                    "Open the focused line's previous origin");
                bindings.Key(Hex1bKey.F7).Action(
                    _ => FocusFilter(),
                    "Focus line-history search");
                bindings.Key(Hex1bKey.F8).Action(
                    _ => _session.NavigateBackAsync(_cancellationToken),
                    "Return to the prior blame location");
                bindings.Alt().Key(Hex1bKey.G).Action(
                    _ => FocusGoToLine(),
                    "Focus one-based line navigation");
                bindings.Alt().Key(Hex1bKey.C).Action(
                    _ => CopyPath(),
                    "Copy the exact displayed path");
                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            }).Fill())
            .Fill();

    private HStackWidget BuildHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var repository = RepositoryLabel.Create(_session.Repository);
        return context.HStack(header =>
        [
            header.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("blame"),
                info.Section(_session.RevisionDisplay),
                info.Section(_session.PathDisplay),
                info.Spacer(),
                info.Section($" | {repository}"),
            ]).Divider(" | ").FillWidth(),
        ]).FillWidth();
    }

    private HStackWidget BuildInputs<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(inputs =>
        [
            inputs.Text("Find: "),
            inputs.TextBox()
                .State(_session.State.Filter)
                .OnTextChanged(eventArgs => _session.FilterAsync(
                    eventArgs.NewText,
                    _cancellationToken))
                .FillWidth(),
            inputs.Text("  Line: "),
            inputs.TextBox().State(_session.State.GoToLine).FixedWidth(8),
            inputs.Text(" "),
            inputs.Button("Go").OnClick(_ => GoToLineAsync()),
        ]).FillWidth();

    private SplitterWidget BuildBlameContent<TParent>(WidgetContext<TParent> context, int split)
        where TParent : Hex1bWidget
        => context.HSplitter(
            context.VStack(left =>
            [
                left.List(_session.State.VisibleItems)
                    .ItemKey(static item => item.Attribution.ResultLineNumber)
                    .FocusedIndex(_session.State.FocusedIndex)
                    .OnFocusChanged(eventArgs => FocusLineAsync(eventArgs.FocusedIndex))
                    .Empty(empty => empty.Text(
                        _session.State.Catalog?.Attributions.IsEmpty == true
                            ? "The selected range contains no attributable lines."
                            : "No line matches the current search."))
                    .Fill(),
                left.VStack(details => BuildDetails(details)),
            ]).Fill(),
            context.Border(
                context.Editor(_session.State.Preview)
                    .LineNumbers()
                    .WordWrap(false)
                    .Decorations(_session.State.PreviewDecorationProvider)
                    .Fill())
                .Title(_session.State.PreviewTitle)
                .Fill(),
            split).Fill();

    private Hex1bWidget[] BuildDetails<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var attribution = _session.State.FocusedItem?.Attribution;
        if (attribution is null)
        {
            return [context.Text("Select a line to inspect its author, origin path, prior location, and commit context.")];
        }

        var commit = attribution.Commit;
        var identity = commit.IsUncommitted ? "uncommitted worktree content" : commit.ObjectId.ToString();
        var previous = attribution.Previous is null
            ? "no earlier origin reported"
            : $"{attribution.Previous.ObjectId.ToString()[..12]} {attribution.Previous.Path.DisplayText}:{attribution.SourceLineNumber}";
        return
        [
            context.Text($"Author: {Decode(commit.AuthorName.Span, "(unknown)")} {Decode(commit.AuthorEmail.Span, string.Empty)} | {commit.AuthoredAt.ToLocalTime():F}"),
            context.Text($"Origin: {attribution.SourcePath.DisplayText}:{attribution.SourceLineNumber} | Previous: {previous}"),
            context.Text($"Commit: {identity} | {Decode(commit.Summary.Span, "(no summary)")} | Encoding: {_session.State.EncodingLabel}"),
        ];
    }

    private ResponsiveWidget BuildActions<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(120, wide => wide.HStack(actions =>
            [
                BuildBackAction(actions),
                actions.Text(" "),
                BuildParentAction(actions),
                actions.Text(" "),
                BuildMovesAction(actions),
                actions.Text(" "),
                BuildCopiesAction(actions),
                actions.Text(" "),
                actions.Button("Copy path").OnClick(_ => CopyPath()),
                actions.Text(" "),
                BuildRefreshAction(actions),
                actions.Text(string.Empty).FillWidth(),
                actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth()),
            responsive.Otherwise(compact => compact.HStack(actions =>
            [
                BuildBackAction(actions),
                actions.Text(" "),
                BuildParentAction(actions),
                actions.Text(" "),
                BuildMovesAction(actions),
                actions.Text(" "),
                BuildCopiesAction(actions),
                actions.Text(" "),
                actions.Button("Copy path").OnClick(_ => CopyPath()),
                actions.Text(string.Empty).FillWidth(),
                BuildRefreshAction(actions),
                actions.Text(" "),
                actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).FillWidth()),
        ]);

    private ResponsiveWidget BuildShortcuts<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.Responsive(responsive =>
        [
            responsive.WhenMinWidth(120, wide => wide.VStack(rows =>
            [
                rows.InfoBar(info =>
                [
                    info.Section("F5 Refresh"),
                    info.Section("F6 Parent"),
                    info.Section("F8 Back"),
                    info.Section("F7 Find"),
                    info.Section("Alt+G Go to line"),
                    info.Section("Alt+C Copy path"),
                    info.Spacer(),
                    info.Section("Ctrl+Q Quit"),
                ]).Divider(" | "),
                rows.InfoBar(info =>
                [
                    info.Section("Mouse Select/Scroll/Resize"),
                    info.Section(_session.Activity),
                ]).Divider(" | "),
            ])),
            responsive.Otherwise(compact => compact.InfoBar(info =>
            [
                info.Section("F5 Refresh"),
                info.Section("F6 Parent"),
                info.Section("F8 Back"),
                info.Section("F7 Find"),
                info.Section("Alt+C Copy path"),
                info.Spacer(),
                info.Section("Ctrl+Q Quit"),
            ]).Divider(" | ")),
        ]);

    private Hex1bWidget BuildBackAction<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.CanNavigateBack && !_session.IsBusy
            ? context.Button("Back").OnClick(_ => NavigateBackAsync())
            : context.Text(" Back ");

    private Hex1bWidget BuildParentAction<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.CanNavigateParent && !_session.IsBusy
            ? context.Button("Parent").OnClick(_ => NavigateParentAsync())
            : context.Text(" Parent ");

    private Hex1bWidget BuildMovesAction<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.IsBusy
            ? context.Text($" Moves {FormatToggle(_session.DetectMoves)} ")
            : context.Button($"Moves {FormatToggle(_session.DetectMoves)}")
                .OnClick(_ => ToggleMovesAsync());

    private Hex1bWidget BuildCopiesAction<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.IsBusy
            ? context.Text($" Copies {FormatToggle(_session.DetectCopies)} ")
            : context.Button($"Copies {FormatToggle(_session.DetectCopies)}")
                .OnClick(_ => ToggleCopiesAsync());

    private Hex1bWidget BuildRefreshAction<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.IsBusy
            ? context.Text(" Refresh ")
            : context.Button("Refresh").OnClick(_ => RefreshAsync());

    private void FocusFilter()
    {
        _application?.RequestFocus(static node => node is TextBoxNode);
        _application?.Invalidate();
    }

    private void FocusGoToLine()
    {
        var textBoxIndex = 0;
        _application?.RequestFocus(node =>
        {
            if (node is not TextBoxNode)
            {
                return false;
            }

            textBoxIndex++;
            return textBoxIndex == 2;
        });
        _application?.Invalidate();
    }

    private async Task FocusLineAsync(int index)
    {
        await _session.FocusAsync(index, _cancellationToken).ConfigureAwait(false);
        FocusLineList();
    }

    private async Task GoToLineAsync()
    {
        await _session.GoToLineAsync(_cancellationToken).ConfigureAwait(false);
        FocusLineList();
    }

    private async Task NavigateParentAsync()
    {
        await _session.NavigateParentAsync(_cancellationToken).ConfigureAwait(false);
        FocusLineList();
    }

    private async Task NavigateBackAsync()
    {
        await _session.NavigateBackAsync(_cancellationToken).ConfigureAwait(false);
        FocusLineList();
    }

    private async Task ToggleMovesAsync()
    {
        await _session.ToggleMoveDetectionAsync(_cancellationToken).ConfigureAwait(false);
        FocusLineList();
    }

    private async Task ToggleCopiesAsync()
    {
        await _session.ToggleCopyDetectionAsync(_cancellationToken).ConfigureAwait(false);
        FocusLineList();
    }

    private async Task RefreshAsync()
    {
        await _session.LoadAsync(_cancellationToken).ConfigureAwait(false);
        FocusLineList();
    }

    private void CopyPath()
    {
        _application?.CopyToClipboard(_session.PathDisplay);
        _application?.Invalidate();
    }

    private void FocusLineList()
    {
        _application?.RequestFocus(static node => node is ListNode<BlameWorkspaceItem>);
        _application?.Invalidate();
    }

    private void HandleChanged()
        => _application?.Invalidate();

    private static string Decode(ReadOnlySpan<byte> bytes, string emptyValue)
        => bytes.IsEmpty ? emptyValue : GitPath.FromUnixBytes(bytes).DisplayText;

    private static string FormatToggle(bool enabled)
        => enabled ? "on" : "off";
}
