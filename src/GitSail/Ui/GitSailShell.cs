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
        var repositoryDetail = await GetRepositoryDetailAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var detail = "F1 Help  F2 Commands  F10 Menu  Ctrl+Q Quit";
        Hex1bApp? application = null;
        application = new Hex1bApp(context =>
            context.VStack(builder =>
            [
                builder.Text("GitSail"),
                builder.Text($"Mode: {_options.Mode.ToString().ToLowerInvariant()}"),
                builder.Text(repositoryDetail).Wrap(),
                builder.Text("Keyboard-first Git workflows in your terminal."),
                builder.Text(string.Empty),
                builder.Text(detail).Wrap(),
                builder.Text(string.Empty),
                builder.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.F1).Action(() =>
                {
                    detail = "Help: use F2 to discover commands; Ctrl+Q exits safely.";
                    application?.Invalidate();
                }, "Open help");
                bindings.Key(Hex1bKey.F2).Action(() =>
                {
                    detail = "Commands: Repository Edit View Branch Commit Merge Remote Stash History Tools Help";
                    application?.Invalidate();
                }, "Open command palette");
                bindings.Key(Hex1bKey.F10).Action(() =>
                {
                    detail = "Menu: Repository | Edit | View | Branch | Commit | Merge | Remote | Stash | History | Tools | Help";
                    application?.Invalidate();
                }, "Open menu");
                bindings.Ctrl().Key(Hex1bKey.Q).Action(context => context.RequestStop(), "Quit GitSail");
            }),
            new Hex1bAppOptions
            {
                EnableMouse = true,
                EnableDefaultCtrlCExit = true,
            });

        using (application)
        {
            await application.RunAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetRepositoryDetailAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        if (_options.Mode == ApplicationMode.Pick)
        {
            return "Choose a repository to open.";
        }

        try
        {
            var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
            var runner = new ChildProcessRunner();
            var installation = await new GitVersionService(resolver, runner)
                .GetAsync(workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            var repository = await new RepositoryDiscoveryService(installation, runner)
                .DiscoverAsync(workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            var location = repository.WorkTree ?? repository.GitDirectory;
            return repository.IsBare
                ? $"Bare repository: {location.DisplayText}  |  Git {installation.Version}"
                : $"Repository: {location.DisplayText}  |  Git {installation.Version}";
        }
        catch (Exception exception) when (exception is ExecutableResolutionException or
            GitCommandException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"Repository unavailable: {TerminalTextSanitizer.Sanitize(exception.Message)}";
        }
    }
}
