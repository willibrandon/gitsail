using GitSail.Domain;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Composes the responsive keyboard-and-mouse immutable repository tree browser.
/// </summary>
internal sealed class TreeView
{
    private readonly TreeSession _session;
    private readonly CancellationToken _cancellationToken;
    private Hex1bApp? _application;

    /// <summary>
    /// Initializes a tree-browser view over controlled session state.
    /// </summary>
    /// <param name="session">The tree-browser state and action source.</param>
    /// <param name="cancellationToken">Signals application shutdown.</param>
    internal TreeView(TreeSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Connects tree-browser invalidation notifications to the owning terminal application.
    /// </summary>
    /// <param name="application">The owning terminal application.</param>
    internal void Attach(Hex1bApp application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("The tree-browser view is already attached.");
        }

        _application = application;
        _session.Changed += HandleChanged;
    }

    /// <summary>
    /// Disconnects tree-browser invalidation notifications from the owning application.
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
    /// Builds the complete responsive tree-browser widget tree for one render generation.
    /// </summary>
    /// <param name="context">The root widget context.</param>
    /// <returns>The immutable repository tree workspace.</returns>
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
                        100,
                        wide => BuildTreeContent(wide, 48)),
                    responsive.Otherwise(medium => BuildTreeContent(medium, 34)),
                ]).Fill(),
                BuildActions(builder),
                BuildShortcuts(builder),
            ]).InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.Enter).Action(
                    _ => OpenFocusedAsync(),
                    "Open the focused directory");
                bindings.Key(Hex1bKey.Backspace).Action(
                    _ => NavigateUpAsync(),
                    "Open the parent directory");
                bindings.Key(Hex1bKey.F5).Action(
                    _ => _session.RefreshAsync(_cancellationToken),
                    "Refresh the current revision and directory");
                bindings.Ctrl().Key(Hex1bKey.R).Action(
                    _ => _session.RefreshAsync(_cancellationToken),
                    "Refresh the current revision and directory");
                bindings.Key(Hex1bKey.F7).Action(
                    _ => FocusFilter(),
                    "Focus tree search");
                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            }).Fill())
            .Fill();

    private HStackWidget BuildHeader<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var repository = RepositoryLabel.Create(_session.Repository);
        var directory = _session.State.Catalog?.Directory?.DisplayText ?? "repository root";
        return context.HStack(header =>
        [
            header.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section("browser"),
                info.Section(directory),
                info.Spacer(),
                info.Section($" | {repository}"),
            ]).Divider(" | ").FillWidth(),
        ]).FillWidth();
    }

    private HStackWidget BuildInputs<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(inputs =>
        [
            inputs.Text("Revision: "),
            inputs.TextBox().State(_session.State.Revision).FillWidth(),
            inputs.Text(" "),
            _session.IsBusy
                ? inputs.Text(" Load ")
                : inputs.Button("Load").OnClick(_ => _session.LoadRevisionAsync(_cancellationToken)),
            inputs.Text("  Find: "),
            inputs.TextBox()
                .State(_session.State.Filter)
                .OnTextChanged(eventArgs => _session.FilterAsync(
                    eventArgs.NewText,
                    _cancellationToken))
                .FillWidth(),
        ]).FillWidth();

    private SplitterWidget BuildTreeContent<TParent>(WidgetContext<TParent> context, int split)
        where TParent : Hex1bWidget
        => context.HSplitter(
            context.VStack(left =>
            [
                left.List(_session.State.VisibleItems)
                    .ItemKey(static item => item.Entry)
                    .FocusedIndex(_session.State.FocusedIndex)
                    .OnFocusChanged(eventArgs => FocusTreeEntryAsync(eventArgs.FocusedIndex))
                    .Empty(empty => empty.Text(
                        _session.State.Catalog?.Entries.IsEmpty == true
                            ? "This directory is empty."
                            : "No tree entry matches the current search."))
                    .Fill(),
                left.VStack(details => BuildDetails(details)),
            ]).Fill(),
            context.Border(
                context.Editor(_session.State.Preview)
                    .LineNumbers()
                    .WordWrap(false)
                    .Fill())
                .Title(_session.State.PreviewTitle)
                .Fill(),
            split).Fill();

    private Hex1bWidget[] BuildDetails<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
    {
        var entry = _session.State.FocusedItem?.Entry;
        if (entry is null)
        {
            return [context.Text("Select a tree entry to inspect its exact type, mode, object, size, and content.")];
        }

        var size = entry.Size is null ? "not applicable" : $"{entry.Size.Value:N0} bytes";
        return
        [
            context.Text($"{FormatKind(entry.Kind)} | Mode {entry.Mode} | Size {size}"),
            context.Text($"Object: {entry.ObjectId}"),
        ];
    }

    private HStackWidget BuildActions<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            _session.CanNavigateUp && !_session.IsBusy
                ? actions.Button("Up").OnClick(_ => NavigateUpAsync())
                : actions.Text(" Up "),
            actions.Text(" "),
            _session.State.FocusedItem?.Entry.Kind == TreeEntryKind.Tree && !_session.IsBusy
                ? actions.Button("Open").OnClick(_ => OpenFocusedAsync())
                : actions.Text(" Open "),
            actions.Text(" "),
            _session.IsBusy
                ? actions.Text(" Refresh ")
                : actions.Button("Refresh").OnClick(_ => _session.RefreshAsync(_cancellationToken)),
            actions.Text(string.Empty).FillWidth(),
            actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private InfoBarWidget BuildShortcuts<TParent>(WidgetContext<TParent> context)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section("Enter/Open Directory"),
            info.Section("Backspace Up"),
            info.Section("F5 Refresh"),
            info.Section("F7 Find"),
            info.Section("Mouse Select/Open/Scroll/Resize"),
            info.Spacer(),
            info.Section(_session.Activity),
            info.Section("Ctrl+Q Quit"),
        ]).Divider(" | ");

    private void FocusFilter()
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

    private async Task FocusTreeEntryAsync(int index)
    {
        await _session.FocusAsync(index, _cancellationToken).ConfigureAwait(false);
        FocusTreeList();
    }

    private async Task OpenFocusedAsync()
    {
        await _session.ActivateFocusedAsync(_cancellationToken).ConfigureAwait(false);
        FocusTreeList();
    }

    private async Task NavigateUpAsync()
    {
        await _session.NavigateUpAsync(_cancellationToken).ConfigureAwait(false);
        FocusTreeList();
    }

    private void FocusTreeList()
    {
        _application?.RequestFocus(static node => node is ListNode<TreeWorkspaceItem>);
        _application?.Invalidate();
    }

    private void HandleChanged()
        => _application?.Invalidate();

    private static string FormatKind(TreeEntryKind kind)
        => kind switch
        {
            TreeEntryKind.Tree => "Directory",
            TreeEntryKind.RegularFile => "File",
            TreeEntryKind.ExecutableFile => "Executable file",
            TreeEntryKind.SymbolicLink => "Symbolic link",
            TreeEntryKind.GitLink => "Submodule",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
