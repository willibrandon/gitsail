using GitSail.CommandLine;
using GitSail.Git.Execution;
using Hex1b;
using Hex1b.Input;

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
        var workingDirectoryPath = _options.WorkingDirectory is null
            ? Environment.CurrentDirectory
            : Path.GetFullPath(_options.WorkingDirectory, Environment.CurrentDirectory);
        var launchDirectory = CanonicalDirectory.Create(workingDirectoryPath);
        var processEnvironment = new RuntimeProcessEnvironment();
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
                    var openResult = await RepositoryWorkspaceSession
                        .OpenAsync(
                            selectedDirectory,
                            _options.Citool?.Amend ?? false,
                            processEnvironment,
                            TimeProvider.System,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await TryRecordRecentRepositoryAsync(
                        launchDirectory,
                        openResult.Repository,
                        openResult.Installation,
                        processEnvironment,
                        cancellationToken).ConfigureAwait(false);
                    if (openResult.Session is null)
                    {
                        await RunMessageShellAsync(
                            "Bare repository",
                            $"{openResult.Repository.GitDirectory.DisplayText} | Git {openResult.Installation.Version} | Worktree actions are unavailable.",
                            cancellationToken).ConfigureAwait(false);
                        return chooserMode ? ExitCodes.Success : ExitCodes.Failure;
                    }

                    await using (openResult.Session)
                    {
                        await RunWorkspaceAsync(openResult.Session, cancellationToken).ConfigureAwait(false);
                        return _options.Mode == ApplicationMode.Citool && !openResult.Session.IsCitoolCompleted
                            ? ExitCodes.Failure
                            : ExitCodes.Success;
                    }
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

    private static async Task RunChooserAsync(
        RepositoryChooserSession chooser,
        CancellationToken cancellationToken)
    {
        var view = new RepositoryChooserView(chooser, cancellationToken);
        using var application = new Hex1bApp(view.Build, CreateAppOptions());
        view.Attach(application);
        try
        {
            await application.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            view.Detach();
        }
    }

    private async Task RunWorkspaceAsync(
        RepositoryWorkspaceSession workspace,
        CancellationToken cancellationToken)
    {
        var workspaceOptions = _options.Mode == ApplicationMode.Pick
            ? _options with { Mode = ApplicationMode.Gui }
            : _options;
        var view = new RepositoryWorkspaceView(workspaceOptions, workspace, cancellationToken);
        using var application = new Hex1bApp(view.Build, CreateAppOptions());
        view.Attach(application);

        try
        {
            await application.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            view.Detach();
        }
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
