using GitSail.Domain;
using GitSail.Git.Execution;
using Hex1b;

namespace GitSail.Ui;

/// <summary>
/// Authenticates, validates, displays, and saves one Git-owned interactive-rebase todo.
/// </summary>
internal static class SequenceEditorShell
{
    private const int MaximumTodoBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Runs the minimal sequence-editor TUI for the exact path supplied by Git.
    /// </summary>
    /// <param name="todoPath">The exact native todo path appended by Git to the configured editor command.</param>
    /// <param name="cancellationToken">Signals editor cancellation and terminal restoration.</param>
    /// <returns>Success only when a validated plan was atomically returned to Git.</returns>
    internal static async Task<int> RunAsync(GitPath todoPath, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(todoPath);
            var processEnvironment = new RuntimeProcessEnvironment();
            var suppliedPath = NormalizeSuppliedPath(todoPath);
            await AuthenticateIfRequestedAsync(
                processEnvironment,
                suppliedPath,
                cancellationToken).ConfigureAwait(false);

            var launchDirectory = CanonicalDirectory.Create(Environment.CurrentDirectory);
            var runner = new ChildProcessRunner();
            var environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
            var installation = await new GitVersionService(
                    new ExecutableResolver(processEnvironment),
                    runner)
                .GetAsync(launchDirectory, cancellationToken)
                .ConfigureAwait(false);
            var repository = await new RepositoryDiscoveryService(
                    installation,
                    runner,
                    environmentFactory)
                .DiscoverAsync(launchDirectory, cancellationToken)
                .ConfigureAwait(false);
            var workingDirectory = CanonicalDirectory.Create(repository.WorkTree ?? repository.GitDirectory);
            var expectedPath = await new RepositoryStatePathService(
                    installation,
                    runner,
                    environmentFactory)
                .ResolveAsync(
                    workingDirectory,
                    RepositoryStateFile.RebaseTodo,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!expectedPath.Equals(suppliedPath))
            {
                throw new InvalidDataException(
                    "The sequence editor was not given Git's exact interactive-rebase todo path.");
            }

            var contents = await RepositoryStateFileSystem.ReadIfExistsAsync(
                expectedPath,
                MaximumTodoBytes,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Git's interactive-rebase todo file is missing.");
            var session = new SequenceEditorSession(RebaseTodoParser.Parse(contents));
            var view = new SequenceEditorView(
                session,
                RepositoryLabel.Create(repository),
                installation.Version.ToString());
            await using var terminalSession = TerminalApplicationSession.CreateConsole(
                view.Build,
                new Hex1bAppOptions
                {
                    EnableMouse = true,
                    EnableDefaultCtrlCExit = true,
                    UseSoftWrapEmission = OperatingSystem.IsWindows(),
                });
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

            if (!session.IsSaved)
            {
                return ExitCodes.Cancelled;
            }

            await RepositoryStateFileSystem.WriteAtomicallyAsync(
                expectedPath,
                session.Document.Render(),
                cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }
        catch (Exception exception) when (exception is ArgumentException or
            ExecutableResolutionException or
            GitCommandException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(
                TerminalTextSanitizer.Sanitize(exception.Message)).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    private static async Task AuthenticateIfRequestedAsync(
        RuntimeProcessEnvironment processEnvironment,
        GitPath suppliedPath,
        CancellationToken cancellationToken)
    {
        var requestPath = processEnvironment.GetVariable(
            RebaseSequenceEditorRequest.RequestPathVariable);
        var requestSecret = processEnvironment.GetVariable(
            RebaseSequenceEditorRequest.RequestSecretVariable);
        if (requestPath is null && requestSecret is null)
        {
            return;
        }

        if (requestPath is null || requestSecret is null)
        {
            throw new InvalidDataException("The sequence-editor request environment is incomplete.");
        }

        _ = await RebaseSequenceEditorRequest.ConsumeAsync(
            requestPath,
            requestSecret,
            suppliedPath,
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
    }

    private static GitPath NormalizeSuppliedPath(GitPath path)
    {
        if (path.Kind == NativePathKind.WindowsUtf16)
        {
            return GitPath.FromWindowsPath(Path.GetFullPath(
                path.GetWindowsPath(),
                Environment.CurrentDirectory));
        }

        if (path.GetUnixBytes()[0] != (byte)'/')
        {
            throw new InvalidDataException(
                "Git did not provide an absolute interactive-rebase todo path.");
        }

        return path;
    }
}
