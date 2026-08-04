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

            await using (openResult.Session)
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
        var view = new RepositoryWorkspaceView(_options.Mode, workspace, cancellationToken);
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

    private static Hex1bAppOptions CreateAppOptions()
        => new()
        {
            EnableMouse = true,
            EnableDefaultCtrlCExit = true,
        };
}
