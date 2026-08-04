using GitSail.Domain;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Composes the responsive keyboard-and-mouse structured history workflow.
/// </summary>
internal sealed class HistoryView
{
    private readonly HistorySession _session;
    private readonly CancellationToken _cancellationToken;
    private Hex1bApp? _application;

    /// <summary>
    /// Initializes a structured history view over controlled session state.
    /// </summary>
    /// <param name="session">The structured history state and action source.</param>
    /// <param name="cancellationToken">Signals application shutdown.</param>
    internal HistoryView(HistorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Connects history invalidation notifications to the owning terminal application.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    internal void Attach(Hex1bApp application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The history view is already attached.");
        }

        _application = application;
        _session.Changed += HandleChanged;
    }

    /// <summary>
    /// Disconnects history invalidation notifications from the owning application.
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
    /// Builds the complete responsive history widget tree for one render generation.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The structured history workspace.</returns>
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
                        wide => BuildHistoryContent(wide, 52)),
                    responsive.Otherwise(medium => BuildHistoryContent(medium, 38)),
                ]).Fill(),
                BuildActions(builder),
                BuildShortcuts(builder),
            ]).InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.F5).Action(
                    _ => _session.LoadAsync(_cancellationToken),
                    "Refresh structured commit history");
                bindings.Ctrl().Key(Hex1bKey.R).Action(
                    _ => _session.LoadAsync(_cancellationToken),
                    "Refresh structured commit history");
                bindings.Key(Hex1bKey.F7).Action(
                    _ => FocusFilter(),
                    "Focus history search");
                bindings.Key(Hex1bKey.J).Action(
                    _ => _session.MoveFocusAsync(1, _cancellationToken),
                    "Focus the next commit");
                bindings.Key(Hex1bKey.K).Action(
                    _ => _session.MoveFocusAsync(-1, _cancellationToken),
                    "Focus the previous commit");
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
                info.Section("history"),
                info.Spacer(),
                info.Section($" | {repository}"),
                info.Section($"Git {_session.Installation.Version}"),
            ]).Divider(" | ").FillWidth(),
        ]).FillWidth();
    }

    private SplitterWidget BuildHistoryContent<TParent>(WidgetContext<TParent> context, int split)
        where TParent : Hex1bWidget
        => context.HSplitter(
            context.VStack(left =>
            [
                left.HStack(filter =>
                [
                    filter.Text("Find: "),
                    filter.TextBox()
                        .State(_session.State.Filter)
                        .OnTextChanged(eventArgs => _session.FilterAsync(
                            eventArgs.NewText,
                            _cancellationToken))
                        .FillWidth(),
                ]).FillWidth(),
                left.List(_session.State.VisibleItems)
                    .ItemKey(static item => item.Commit.ObjectId)
                    .FocusedIndex(_session.State.FocusedIndex)
                    .OnFocusChanged(eventArgs => _session.FocusAsync(
                        eventArgs.FocusedIndex,
                        _cancellationToken))
                    .Empty(empty => empty.Text(
                        _session.State.Catalog?.Commits.IsEmpty == true
                            ? "No commits match this history request."
                            : "No commit matches the current search."))
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
        var commit = _session.State.FocusedItem?.Commit;
        if (commit is null)
        {
            return [context.Text("Select a commit to inspect its identity, parents, author, signature, and patch.")];
        }

        var author = Decode(commit.AuthorName.Span, "(unknown author)");
        var email = Decode(commit.AuthorEmail.Span, "(no email)");
        var parents = commit.Parents.IsEmpty
            ? "none (root commit)"
            : string.Join(' ', commit.Parents.Select(static parent => parent.ToString()[..12]));
        return
        [
            context.Text($"Author: {author} <{email}> | {commit.AuthoredAt.ToLocalTime():F}"),
            context.Text($"Parents: {parents} | Signature: {FormatSignature(commit.SignatureStatus)}"),
        ];
    }

    private HStackWidget BuildActions<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            BuildRefreshAction(actions),
            actions.Text(" "),
            actions.Button("Find").OnClick(_ => FocusFilter()),
            actions.Text(string.Empty).FillWidth(),
            actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private InfoBarWidget BuildShortcuts<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section("F5 Refresh"),
            info.Section("F7 Find"),
            info.Section("J/K Commits"),
            info.Section("Arrows Navigate"),
            info.Section("Mouse Select/Scroll/Resize"),
            info.Spacer(),
            info.Section(_session.Activity),
            info.Section("Ctrl+Q Quit"),
        ]).Divider(" | ");

    private void FocusFilter()
    {
        _application?.RequestFocus(node =>
            node is TextBoxNode);
        _application?.Invalidate();
    }

    private Hex1bWidget BuildRefreshAction<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => _session.IsBusy
            ? context.Text(" Refresh ")
            : context.Button("Refresh").OnClick(_ => _session.LoadAsync(_cancellationToken));

    private void HandleChanged()
        => _application?.Invalidate();

    private static string Decode(ReadOnlySpan<byte> bytes, string emptyValue)
        => bytes.IsEmpty
            ? emptyValue
            : TerminalTextSanitizer.Sanitize(GitPath.FromUnixBytes(bytes).DisplayText);

    private static string FormatSignature(CommitSignatureStatus status)
        => status switch
        {
            CommitSignatureStatus.None => "unsigned",
            CommitSignatureStatus.Good => "valid",
            CommitSignatureStatus.Bad => "bad",
            CommitSignatureStatus.UnknownValidity => "valid, trust unknown",
            CommitSignatureStatus.ExpiredSignature => "expired signature",
            CommitSignatureStatus.ExpiredKey => "expired key",
            CommitSignatureStatus.RevokedKey => "revoked key",
            CommitSignatureStatus.CannotCheck => "cannot verify",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
}
