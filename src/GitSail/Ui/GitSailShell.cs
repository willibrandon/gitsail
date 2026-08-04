using GitSail.CommandLine;
using GitSail.Git.Execution;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Runs the interactive terminal shell for a selected application mode.
/// </summary>
/// <param name="options">The interactive shell inputs selected by the command line.</param>
internal sealed class GitSailShell(GitSailShellOptions options)
{
    private readonly GitSailShellOptions _options = options;

    /// <summary>
    /// Runs the terminal UI until the user exits or cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">Signals graceful terminal shutdown.</param>
    /// <returns>A task that completes after terminal state has been restored.</returns>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var workingDirectoryPath = _options.WorkingDirectory is null
            ? Environment.CurrentDirectory
            : Path.GetFullPath(_options.WorkingDirectory, Environment.CurrentDirectory);
        var workingDirectory = CanonicalDirectory.Create(workingDirectoryPath);
        if (_options.Mode == ApplicationMode.Pick)
        {
            await RunMessageShellAsync(
                "Repository chooser",
                "No repository is selected yet.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var openResult = await RepositoryWorkspaceSession
                .OpenAsync(workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (openResult.Session is null)
            {
                await RunMessageShellAsync(
                    "Bare repository",
                    $"{openResult.Repository.GitDirectory.DisplayText} | Git {openResult.Installation.Version} | Worktree actions are unavailable.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            using (openResult.Session)
            {
                await RunWorkspaceAsync(openResult.Session, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is ExecutableResolutionException or
            GitCommandException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await RunMessageShellAsync(
                "Repository unavailable",
                TerminalTextSanitizer.Sanitize(exception.Message),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunWorkspaceAsync(
        RepositoryWorkspaceSession workspace,
        CancellationToken cancellationToken)
    {
        Hex1bApp? application = null;
        application = new Hex1bApp(context =>
            context.VStack(builder =>
            [
                BuildHeader(builder, workspace),
                builder.Responsive(responsive =>
                [
                    responsive.When(
                        static (width, height) => width < 60 || height < 18,
                        compact => BuildResizeView(compact)),
                    responsive.WhenMinWidth(
                        100,
                        wide => wide.HSplitter(
                            BuildChangesPane(wide, workspace, application),
                            BuildDetailPane(wide, workspace),
                            44).Fill()),
                    responsive.Otherwise(medium => medium.VSplitter(
                        BuildChangesPane(medium, workspace, application),
                        BuildDetailPane(medium, workspace),
                        11).Fill()),
                ]).Fill(),
                BuildActionBar(builder, workspace, application, cancellationToken),
                BuildShortcutBar(builder, workspace),
            ]).InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.S).Action(
                    _ => workspace.StageAsync(cancellationToken),
                    "Stage checked or focused paths");
                bindings.Key(Hex1bKey.U).Action(
                    _ => workspace.UnstageAsync(cancellationToken),
                    "Unstage checked or focused paths");
                bindings.Key(Hex1bKey.F5).Action(
                    _ => workspace.RefreshAsync(cancellationToken),
                    "Refresh repository status");
                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            }).Fill(),
            CreateAppOptions());
        workspace.Changed += HandleWorkspaceChanged;

        try
        {
            using (application)
            {
                await application.RunAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            workspace.Changed -= HandleWorkspaceChanged;
        }

        void HandleWorkspaceChanged()
            => application?.Invalidate();
    }

    private async Task RunMessageShellAsync(
        string title,
        string detail,
        CancellationToken cancellationToken)
    {
        using var application = new Hex1bApp(context =>
            context.VStack(builder =>
            [
                builder.InfoBar(info =>
                [
                    info.Section(" GitSail "),
                    info.Section(_options.Mode.ToString().ToLowerInvariant()),
                    info.Spacer(),
                    info.Section(title),
                ]).Divider(" | "),
                builder.Border(builder.Text(detail).Wrap()).Title(title).Fill(),
                builder.HStack(actions =>
                [
                    actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
                ]),
                builder.InfoBar(info =>
                [
                    info.Section("Ctrl+Q Quit"),
                    info.Spacer(),
                    info.Section("Mouse enabled"),
                ]),
            ]).InputBindings(bindings =>
            {
                bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit GitSail");
            }).Fill(),
            CreateAppOptions());
        await application.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private InfoBarWidget BuildHeader<TParent>(
        WidgetContext<TParent> context,
        RepositoryWorkspaceSession workspace)
        where TParent : Hex1bWidget
    {
        var snapshot = workspace.State.Snapshot;
        var branch = snapshot.HeadName?.DisplayText ??
            (snapshot.HeadObjectId is null ? "unborn" : "detached");
        var repository = snapshot.Repository.WorkTree?.DisplayText ?? snapshot.Repository.GitDirectory.DisplayText;
        var tracking = snapshot.UpstreamName is null
            ? string.Empty
            : $" | {snapshot.UpstreamName.DisplayText} +{snapshot.AheadCount}/-{snapshot.BehindCount}";
        return context.InfoBar(info =>
        [
            info.Section(" GitSail "),
            info.Section(_options.Mode.ToString().ToLowerInvariant()),
            info.Section(branch + tracking),
            info.Spacer(),
            info.Section(repository),
            info.Section($"Git {workspace.Installation.Version}"),
        ]).Divider(" | ");
    }

    private static SplitterWidget BuildChangesPane<TParent>(
        WidgetContext<TParent> context,
        RepositoryWorkspaceSession workspace,
        Hex1bApp? application)
        where TParent : Hex1bWidget
        => context.VSplitter(
            BuildUnstagedPane(context, workspace, application),
            BuildStagedPane(context, workspace, application),
            9).Fill();

    private static BorderWidget BuildUnstagedPane<TParent>(
        WidgetContext<TParent> context,
        RepositoryWorkspaceSession workspace,
        Hex1bApp? application)
        where TParent : Hex1bWidget
    {
        var state = workspace.State;
        var list = context.List(state.UnstagedItems)
            .ItemKey(static item => item.Path)
            .MultiSelect()
            .FocusedIndex(state.UnstagedFocusedIndex)
            .SelectedIndices(state.UnstagedSelectedIndices)
            .OnFocusChanged(eventArgs =>
            {
                state.FocusUnstaged(eventArgs.FocusedIndex);
                application?.Invalidate();
            })
            .OnSelectionChanged(eventArgs =>
            {
                state.SetUnstagedSelection(eventArgs.SelectedIndices);
                application?.Invalidate();
            })
            .Empty(empty => empty.Text("Working tree clean."));
        return context.Border(list.Fill())
            .Title($"Unstaged ({state.UnstagedItems.Length})")
            .Fill();
    }

    private static BorderWidget BuildStagedPane<TParent>(
        WidgetContext<TParent> context,
        RepositoryWorkspaceSession workspace,
        Hex1bApp? application)
        where TParent : Hex1bWidget
    {
        var state = workspace.State;
        var list = context.List(state.StagedItems)
            .ItemKey(static item => item.Path)
            .MultiSelect()
            .FocusedIndex(state.StagedFocusedIndex)
            .SelectedIndices(state.StagedSelectedIndices)
            .OnFocusChanged(eventArgs =>
            {
                state.FocusStaged(eventArgs.FocusedIndex);
                application?.Invalidate();
            })
            .OnSelectionChanged(eventArgs =>
            {
                state.SetStagedSelection(eventArgs.SelectedIndices);
                application?.Invalidate();
            })
            .Empty(empty => empty.Text("No staged changes."));
        return context.Border(list.Fill())
            .Title($"Staged ({state.StagedItems.Length})")
            .Fill();
    }

    private static BorderWidget BuildDetailPane<TParent>(
        WidgetContext<TParent> context,
        RepositoryWorkspaceSession workspace)
        where TParent : Hex1bWidget
    {
        var item = workspace.State.FocusedItem;
        var content = item is null
            ? "Select a changed path to inspect it."
            : FormatDetail(item);
        return context.Border(context.Text(content).Wrap())
            .Title("Selected path")
            .Fill();
    }

    private static HStackWidget BuildActionBar<TParent>(
        WidgetContext<TParent> context,
        RepositoryWorkspaceSession workspace,
        Hex1bApp? application,
        CancellationToken cancellationToken)
        where TParent : Hex1bWidget
        => context.HStack(actions =>
        [
            workspace.IsBusy || workspace.State.UnstagedItems.Length == 0
                ? actions.Text("Stage unavailable")
                : actions.Button("Stage").OnClick(_ => workspace.StageAsync(cancellationToken)),
            actions.Text(" "),
            workspace.IsBusy || workspace.State.StagedItems.Length == 0
                ? actions.Text("Unstage unavailable")
                : actions.Button("Unstage").OnClick(_ => workspace.UnstageAsync(cancellationToken)),
            actions.Text(" "),
            workspace.IsBusy
                ? actions.Text("Refresh unavailable")
                : actions.Button("Refresh").OnClick(_ => workspace.RefreshAsync(cancellationToken)),
            actions.Text(" "),
            actions.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
        ]).FillWidth();

    private static InfoBarWidget BuildShortcutBar<TParent>(
        WidgetContext<TParent> context,
        RepositoryWorkspaceSession workspace)
        where TParent : Hex1bWidget
        => context.InfoBar(info =>
        [
            info.Section("S Stage"),
            info.Section("U Unstage"),
            info.Section("F5 Refresh"),
            info.Section("Space Check"),
            info.Section("Ctrl/Shift Click Multi-select"),
            info.Spacer(),
            info.Section(workspace.Activity),
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

    private static Hex1bAppOptions CreateAppOptions()
        => new()
        {
            EnableMouse = true,
            EnableDefaultCtrlCExit = true,
        };
}
