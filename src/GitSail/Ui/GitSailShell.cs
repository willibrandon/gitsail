using System.Runtime.ExceptionServices;
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
    /// <returns>The documented process exit code after terminal state has been restored.</returns>
    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (!TerminalSessionGuard.IsInteractive(
            Console.IsInputRedirected,
            Console.IsOutputRedirected))
        {
            await Console.Error.WriteLineAsync(
                "GitSail requires an interactive terminal on standard input and standard output. Use --help, version, or doctor for redirected output.")
                .ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var workingDirectoryPath = _options.WorkingDirectory is null
            ? Environment.CurrentDirectory
            : Path.GetFullPath(_options.WorkingDirectory, Environment.CurrentDirectory);
        var launchDirectory = CanonicalDirectory.Create(workingDirectoryPath);
        var processEnvironment = new RuntimeProcessEnvironment();
        if (_options.Mode == ApplicationMode.History)
        {
            return await RunHistoryAsync(
                launchDirectory,
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
        }

        if (_options.Mode == ApplicationMode.Browser)
        {
            return await RunBrowserAsync(
                launchDirectory,
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
        }

        if (_options.Mode == ApplicationMode.Blame)
        {
            return await RunBlameAsync(
                launchDirectory,
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
        }

        if (_options.Mode == ApplicationMode.Diff)
        {
            return await RunDiffAsync(
                launchDirectory,
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
        }

        if (_options.Mode == ApplicationMode.Rebase)
        {
            return await RunRebaseAsync(
                launchDirectory,
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
        }

        if (_options.Mode == ApplicationMode.Merge)
        {
            return await RunMergeAsync(
                launchDirectory,
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
        }

        var chooserMode = _options.Mode is ApplicationMode.Gui or ApplicationMode.Pick;
        CanonicalDirectory? selectedDirectory = _options.Mode == ApplicationMode.Pick
            ? null
            : launchDirectory;
        var chooserStatus = "Open, clone, or initialize a repository.";
        try
        {
            while (true)
            {
                if (selectedDirectory is null)
                {
                    using var chooser = await RepositoryChooserSession.CreateAsync(
                        launchDirectory,
                        processEnvironment,
                        chooserStatus,
                        cancellationToken).ConfigureAwait(false);
                    await RunChooserAsync(chooser, cancellationToken).ConfigureAwait(false);
                    selectedDirectory = chooser.SelectedDirectory;
                    if (selectedDirectory is null)
                    {
                        return ExitCodes.Success;
                    }
                }

                try
                {
                    var runResult = await OpenAndRunWorkspaceAsync(
                        selectedDirectory,
                        launchDirectory,
                        processEnvironment,
                        cancellationToken).ConfigureAwait(false);
                    if (runResult.IsBare)
                    {
                        return chooserMode ? ExitCodes.Success : ExitCodes.Failure;
                    }

                    if (runResult.RequestedDestination is { } destination)
                    {
                        var destinationExitCode = destination switch
                        {
                            RepositoryWorkspaceDestination.History => await RunHistoryAsync(
                                selectedDirectory,
                                processEnvironment,
                                cancellationToken).ConfigureAwait(false),
                            RepositoryWorkspaceDestination.Browser => await RunBrowserAsync(
                                selectedDirectory,
                                processEnvironment,
                                cancellationToken).ConfigureAwait(false),
                            _ => throw new InvalidOperationException(
                                $"Unsupported repository destination: {destination}"),
                        };
                        chooserStatus = destinationExitCode == ExitCodes.Success
                            ? "Returned to the repository workspace."
                            : $"{destination} closed after reporting an error.";
                        continue;
                    }

                    if (runResult.RequestedDirectory is not null)
                    {
                        selectedDirectory = runResult.RequestedDirectory;
                        chooserStatus = "Opened selected linked worktree.";
                        continue;
                    }

                    return _options.Mode == ApplicationMode.Citool && !runResult.CitoolCompleted
                        ? ExitCodes.Failure
                        : ExitCodes.Success;
                }
                catch (Exception exception) when (chooserMode && IsRepositoryOpenFailure(exception))
                {
                    chooserStatus = TerminalTextSanitizer.Sanitize(exception.Message);
                    selectedDirectory = null;
                }
            }
        }
        catch (Exception exception) when (IsRepositoryOpenFailure(exception))
        {
            await RunMessageShellAsync(
                "Repository unavailable",
                TerminalTextSanitizer.Sanitize(exception.Message),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    private async Task<int> RunHistoryAsync(
        CanonicalDirectory launchDirectory,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        try
        {
            using var session = await HistorySession.OpenAsync(
                launchDirectory,
                _options.History ?? new HistoryOptions(RevisionRange: null, Pathspecs: []),
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
            await session.LoadAsync(cancellationToken).ConfigureAwait(false);
            var view = new HistoryView(session, cancellationToken);
            await using var terminalSession = TerminalApplicationSession.CreateConsole(
                view.Build,
                CreateAppOptions());
            var application = terminalSession.Application;
            view.Attach(
                application,
                OperatingSystem.IsWindows() ? terminalSession.RequestCleanRepaint : null);
            try
            {
                await terminalSession.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                view.Detach();
            }

            return session.HasLoadFailure ? ExitCodes.Failure : ExitCodes.Success;
        }
        catch (Exception exception) when (IsRepositoryOpenFailure(exception))
        {
            await RunMessageShellAsync(
                "History unavailable",
                TerminalTextSanitizer.Sanitize(exception.Message),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    private async Task<int> RunBrowserAsync(
        CanonicalDirectory launchDirectory,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await TreeSession.OpenAsync(
                launchDirectory,
                _options.Browser ?? new BrowserOptions(Revision: null, Directories: []),
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
            await session.LoadRevisionAsync(cancellationToken).ConfigureAwait(false);
            var view = new TreeView(session, cancellationToken);
            await using var terminalSession = TerminalApplicationSession.CreateConsole(
                view.Build,
                CreateAppOptions());
            var application = terminalSession.Application;
            view.Attach(application);
            try
            {
                await terminalSession.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                view.Detach();
            }

            return session.HasLoadFailure ? ExitCodes.Failure : ExitCodes.Success;
        }
        catch (Exception exception) when (IsRepositoryOpenFailure(exception))
        {
            await RunMessageShellAsync(
                "Browser unavailable",
                TerminalTextSanitizer.Sanitize(exception.Message),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    private async Task<int> RunBlameAsync(
        CanonicalDirectory launchDirectory,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await BlameSession.OpenAsync(
                launchDirectory,
                _options.Blame ?? new BlameOptions(Revision: null, Paths: []),
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
            await session.LoadAsync(cancellationToken).ConfigureAwait(false);
            var view = new BlameView(session, cancellationToken);
            await using var terminalSession = TerminalApplicationSession.CreateConsole(
                view.Build,
                CreateAppOptions());
            var application = terminalSession.Application;
            view.Attach(application);
            try
            {
                await terminalSession.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                view.Detach();
            }

            return session.HasLoadFailure ? ExitCodes.Failure : ExitCodes.Success;
        }
        catch (Exception exception) when (IsRepositoryOpenFailure(exception))
        {
            await RunMessageShellAsync(
                "Blame unavailable",
                TerminalTextSanitizer.Sanitize(exception.Message),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    private async Task<int> RunDiffAsync(
        CanonicalDirectory launchDirectory,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        try
        {
            using var session = await DiffSession.OpenAsync(
                launchDirectory,
                _options.Diff ?? new DiffOptions(
                    Cached: false,
                    LeftRevision: null,
                    RightRevision: null,
                    Pathspecs: []),
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
            await session.LoadAsync(cancellationToken).ConfigureAwait(false);
            var view = new DiffView(session, cancellationToken);
            await using var terminalSession = TerminalApplicationSession.CreateConsole(
                view.Build,
                CreateAppOptions());
            var application = terminalSession.Application;
            view.Attach(application);
            try
            {
                await terminalSession.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                view.Detach();
            }

            return session.HasLoadFailure ? ExitCodes.Failure : ExitCodes.Success;
        }
        catch (Exception exception) when (IsRepositoryOpenFailure(exception))
        {
            await RunMessageShellAsync(
                "Comparison unavailable",
                TerminalTextSanitizer.Sanitize(exception.Message),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    private async Task<int> RunRebaseAsync(
        CanonicalDirectory launchDirectory,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        try
        {
            using var session = await RebaseSession.OpenAsync(
                launchDirectory,
                _options.Rebase ?? new RebaseOptions(Upstream: null, Onto: null),
                processEnvironment,
                cancellationToken).ConfigureAwait(false);
            await session.RefreshAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var view = new RebaseView(session, cancellationToken);
                await using (var terminalSession = TerminalApplicationSession.CreateConsole(
                    view.Build,
                    CreateAppOptions()))
                {
                    var application = terminalSession.Application;
                    view.Attach(application);
                    try
                    {
                        await terminalSession.RunAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        view.Detach();
                    }
                }

                if (session.RequestedAction is null)
                {
                    return session.HasFailure ? ExitCodes.Failure : ExitCodes.Success;
                }

                if (session.RequestedAction == Domain.RebaseRequestedAction.OpenWorkspace)
                {
                    session.ClearRequestedAction();
                    await RunRebaseWorkspaceAsync(
                        session.WorkingDirectory,
                        processEnvironment,
                        cancellationToken).ConfigureAwait(false);
                    await session.RefreshAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (await session.RunRequestedActionAsync(cancellationToken).ConfigureAwait(false))
                {
                    return ExitCodes.Success;
                }
            }
        }
        catch (Exception exception) when (IsRepositoryOpenFailure(exception))
        {
            await RunMessageShellAsync(
                "Rebase unavailable",
                TerminalTextSanitizer.Sanitize(exception.Message),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    private async Task<int> RunMergeAsync(
        CanonicalDirectory launchDirectory,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        try
        {
            var openResult = await RepositoryWorkspaceSession.OpenMergeAsync(
                launchDirectory,
                _options.Merge ?? new MergeCommandOptions(Paths: []),
                processEnvironment,
                TimeProvider.System,
                cancellationToken).ConfigureAwait(false);
            if (openResult.Session is null)
            {
                await RunMessageShellAsync(
                    "Conflict resolution unavailable",
                    $"{openResult.Repository.GitDirectory.DisplayText} | Git {openResult.Installation.Version} | A worktree is required.",
                    cancellationToken).ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            await using (openResult.Session)
            {
                await RunWorkspaceAsync(openResult.Session, cancellationToken).ConfigureAwait(false);
            }

            return ExitCodes.Success;
        }
        catch (Exception exception) when (IsRepositoryOpenFailure(exception))
        {
            await RunMessageShellAsync(
                "Conflict resolution unavailable",
                TerminalTextSanitizer.Sanitize(exception.Message),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    private async Task RunRebaseWorkspaceAsync(
        CanonicalDirectory workingDirectory,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        var openResult = await RepositoryWorkspaceSession.OpenAsync(
            workingDirectory,
            amend: false,
            processEnvironment,
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
        if (openResult.Session is null)
        {
            throw new InvalidOperationException("Rebase conflict resolution requires a worktree repository.");
        }

        await using (openResult.Session)
        {
            await RunWorkspaceAsync(
                openResult.Session,
                cancellationToken,
                _options with
                {
                    Mode = ApplicationMode.Rebase,
                    Citool = null,
                }).ConfigureAwait(false);
        }
    }

    private static async Task RunChooserAsync(
        RepositoryChooserSession chooser,
        CancellationToken cancellationToken)
    {
        var view = new RepositoryChooserView(chooser, cancellationToken);
        await using var terminalSession = TerminalApplicationSession.CreateConsole(
            view.Build,
            CreateAppOptions());
        var application = terminalSession.Application;
        view.Attach(application);
        try
        {
            await terminalSession.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            view.Detach();
        }
    }

    private async Task RunWorkspaceAsync(
        RepositoryWorkspaceSession workspace,
        CancellationToken cancellationToken,
        GitSailShellOptions? explicitOptions = null)
    {
        var workspaceOptions = explicitOptions ?? (_options.Mode == ApplicationMode.Pick
            ? _options with { Mode = ApplicationMode.Gui }
            : _options);
        var view = new RepositoryWorkspaceView(workspaceOptions, workspace, cancellationToken);
        await using var terminalSession = TerminalApplicationSession.CreateConsole(
            view.Build,
            CreateAppOptions());
        var application = terminalSession.Application;
        view.Attach(application);

        try
        {
            await terminalSession.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            view.Detach();
        }
    }

    private async Task<(
        RepositoryWorkspaceDestination? RequestedDestination,
        CanonicalDirectory? RequestedDirectory,
        bool CitoolCompleted,
        bool IsBare)> OpenAndRunWorkspaceAsync(
        CanonicalDirectory selectedDirectory,
        CanonicalDirectory launchDirectory,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        RepositoryWorkspaceSession? workspace = null;
        RepositoryWorkspaceView? workspaceView = null;
        Exception? openFailure = null;
        var repositoryIsBare = false;
        var statusTitle = "Opening repository";
        var statusDetail = "Loading repository status, configuration, and recovery state.";
        await using var terminalSession = TerminalApplicationSession.CreateConsole(
            context => workspaceView is null
                ? BuildOpeningWorkspace(
                    context,
                    statusTitle,
                    statusDetail,
                    _options.Mode.ToString().ToLowerInvariant(),
                    startupCancellation)
                : workspaceView.Build(context),
            CreateAppOptions());
        var application = terminalSession.Application;

        async Task OpenAsync()
        {
            try
            {
                var openResult = await RepositoryWorkspaceSession.OpenAsync(
                    selectedDirectory,
                    _options.Citool?.Amend ?? false,
                    processEnvironment,
                    TimeProvider.System,
                    startupCancellation.Token).ConfigureAwait(false);
                workspace = openResult.Session;
                if (workspace is null)
                {
                    repositoryIsBare = true;
                    statusTitle = "Bare repository";
                    statusDetail =
                        $"{openResult.Repository.GitDirectory.DisplayText} | " +
                        $"Git {openResult.Installation.Version} | Worktree actions are unavailable.";
                    application.Invalidate();
                    return;
                }

                var createdView = new RepositoryWorkspaceView(
                    _options.Mode == ApplicationMode.Pick
                        ? _options with { Mode = ApplicationMode.Gui }
                        : _options,
                    workspace,
                    cancellationToken);
                createdView.Attach(application);
                workspaceView = createdView;
                application.Invalidate();
                await TryRecordRecentRepositoryAsync(
                    launchDirectory,
                    openResult.Repository,
                    openResult.Installation,
                    processEnvironment,
                    startupCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                openFailure = exception;
                application.RequestStop();
            }
        }

        var openTask = OpenAsync();
        try
        {
            await terminalSession.RunAsync(cancellationToken).ConfigureAwait(false);
            startupCancellation.Cancel();
            await openTask.ConfigureAwait(false);
            if (openFailure is not null)
            {
                ExceptionDispatchInfo.Capture(openFailure).Throw();
            }

            return (
                workspace?.RequestedDestination,
                workspace?.RequestedOpenDirectory,
                workspace?.IsCitoolCompleted ?? false,
                IsBare: repositoryIsBare);
        }
        finally
        {
            startupCancellation.Cancel();
            await openTask.ConfigureAwait(false);
            workspaceView?.Detach();
            if (workspace is not null)
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static VStackWidget BuildOpeningWorkspace(
        RootContext context,
        string title,
        string detail,
        string mode,
        CancellationTokenSource startupCancellation)
        => context.VStack(builder =>
        [
            builder.InfoBar(info =>
            [
                info.Section(" GitSail "),
                info.Section(mode),
                info.Spacer(),
                info.Section(title),
            ]).Divider(" | "),
            builder.Border(builder.Text(detail).Wrap()).Title(title).Fill(),
            builder.HStack(actions =>
            [
                actions.Button("Quit").OnClick(eventArgs =>
                {
                    startupCancellation.Cancel();
                    eventArgs.Context.RequestStop();
                }),
            ]),
            builder.InfoBar(info =>
            [
                info.Section("Ctrl+Q Quit"),
                info.Spacer(),
                info.Section("Mouse enabled"),
            ]),
        ]).InputBindings(bindings =>
        {
            bindings.Ctrl().Key(Hex1bKey.Q).Action(actionContext =>
            {
                startupCancellation.Cancel();
                actionContext.RequestStop();
            }, "Quit GitSail");
        }).Fill();

    private async Task RunMessageShellAsync(
        string title,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var terminalSession = TerminalApplicationSession.CreateConsole(
            context => context.VStack(builder =>
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
        await terminalSession.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryRecordRecentRepositoryAsync(
        CanonicalDirectory launchDirectory,
        Domain.RepositoryLocation repository,
        GitInstallation installation,
        IProcessEnvironment processEnvironment,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = repository.WorkTree ?? repository.GitDirectory;
            var recent = new RecentRepositoryService(
                installation,
                new ChildProcessRunner(),
                new GitChildEnvironmentFactory(processEnvironment),
                launchDirectory);
            await recent.RecordAsync(
                CanonicalDirectory.Create(path),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRepositoryOpenFailure(exception))
        {
            // Opening the repository is the primary operation. A read-only or unavailable
            // global Git configuration must not send the user back to the chooser.
        }
    }

    private static bool IsRepositoryOpenFailure(Exception exception)
        => exception is ArgumentException or
            ExecutableResolutionException or
            GitCommandException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException;

    private static Hex1bAppOptions CreateAppOptions()
        => new()
        {
            EnableMouse = true,
            EnableDefaultCtrlCExit = true,
        };
}
