using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using Hex1b.Documents;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies commit-message initialization precedence against isolated real Git repository state.
/// </summary>
[TestClass]
public sealed class CommitMessageInitializationServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private TestProcessEnvironment? _processEnvironment;
    private GitChildEnvironmentFactory? _environmentFactory;

    /// <summary>
    /// Creates an isolated Git, user-directory, and repository environment for each message test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-message-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
        _processEnvironment = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = Path.Combine(_temporaryDirectory, "home"),
            ["USERPROFILE"] = Path.Combine(_temporaryDirectory, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory, "xdg-config"),
            ["XDG_CACHE_HOME"] = Path.Combine(_temporaryDirectory, "xdg-cache"),
            ["APPDATA"] = Path.Combine(_temporaryDirectory, "roaming"),
            ["LOCALAPPDATA"] = Path.Combine(_temporaryDirectory, "local"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["TMPDIR"] = _temporaryDirectory,
            ["TEMP"] = _temporaryDirectory,
            ["TMP"] = _temporaryDirectory,
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        });
        _environmentFactory = new GitChildEnvironmentFactory(_processEnvironment);
    }

    /// <summary>
    /// Removes the isolated repositories and user directories after each message test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies exact recovery files override amend text and retain their documented precedence including empty drafts.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_WithRecoveryAndAmend_PrefersExactRecoveryOrder()
    {
        const string amendMessage = "amend subject\n\namend body";
        var repositoryPath = await CreateCommittedRepositoryAsync("recovery", amendMessage);
        var templatePath = Path.Combine(repositoryPath, "commit-template.txt");
        File.WriteAllText(templatePath, "configured template\n", new UTF8Encoding(false));
        await RunGitAsync(repositoryPath, "config", "--local", "commit.template", templatePath);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var paths = await ResolvePathsAsync(workingDirectory);
        var service = CreateService();
        var head = await ResolveHeadAsync(repositoryPath);

        var loadedAmend = await service.LoadAsync(
            workingDirectory,
            paths.Recovery,
            paths.MergeMessage,
            paths.SquashMessage,
            hasMergeHead: false,
            head,
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(amendMessage, loadedAmend.Message);
        Assert.AreEqual(CommitMessageInitializationKind.Amend, loadedAmend.Kind);
        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            paths.Backup,
            "backup\n"u8.ToArray(),
            TestContext.Current.CancellationToken);
        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            paths.Message,
            "primary\n"u8.ToArray(),
            TestContext.Current.CancellationToken);
        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            paths.EditMessage,
            ReadOnlyMemory<byte>.Empty,
            TestContext.Current.CancellationToken);

        var explicitlyEmpty = await service.LoadAsync(
            workingDirectory,
            paths.Recovery,
            paths.MergeMessage,
            paths.SquashMessage,
            hasMergeHead: false,
            head,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(string.Empty, explicitlyEmpty.Message);
        Assert.AreEqual(CommitMessageInitializationKind.Recovery, explicitlyEmpty.Kind);
        _ = await RepositoryStateFileSystem.DeleteIfExistsAsync(
            paths.EditMessage,
            TestContext.Current.CancellationToken);
        var primary = await service.LoadAsync(
            workingDirectory,
            paths.Recovery,
            paths.MergeMessage,
            paths.SquashMessage,
            hasMergeHead: false,
            head,
            TestContext.Current.CancellationToken);
        Assert.AreEqual("primary\n", primary.Message);
        Assert.AreEqual(CommitMessageInitializationKind.Recovery, primary.Kind);
        _ = await RepositoryStateFileSystem.DeleteIfExistsAsync(
            paths.Message,
            TestContext.Current.CancellationToken);
        var backup = await service.LoadAsync(
            workingDirectory,
            paths.Recovery,
            paths.MergeMessage,
            paths.SquashMessage,
            hasMergeHead: false,
            head,
            TestContext.Current.CancellationToken);
        Assert.AreEqual("backup\n", backup.Message);
        Assert.AreEqual(CommitMessageInitializationKind.Recovery, backup.Kind);
    }

    /// <summary>
    /// Verifies an ordinary commit loads the exact effective relative template through normal linked-file semantics.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_WithRelativeConfiguredTemplate_LoadsExactTemplate()
    {
        const string expectedTemplate = "Subject: \n\nWhy:\n\n# guidance\n";
        var repositoryPath = await CreateCommittedRepositoryAsync("template", "base\n");
        var templateDirectory = Path.Combine(repositoryPath, "templates");
        Directory.CreateDirectory(templateDirectory);
        var sharedTemplatePath = Path.Combine(_temporaryDirectory!, "shared-template.txt");
        File.WriteAllText(sharedTemplatePath, expectedTemplate, new UTF8Encoding(false));
        var configuredTemplatePath = Path.Combine(templateDirectory, "commit.txt");
        if (OperatingSystem.IsWindows())
        {
            File.Copy(sharedTemplatePath, configuredTemplatePath);
        }
        else
        {
            File.CreateSymbolicLink(configuredTemplatePath, sharedTemplatePath);
        }

        await RunGitAsync(
            repositoryPath,
            "config",
            "--local",
            "commit.template",
            "templates/commit.txt");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var paths = await ResolvePathsAsync(workingDirectory);

        var loaded = await CreateService().LoadAsync(
            workingDirectory,
            paths.Recovery,
            paths.MergeMessage,
            paths.SquashMessage,
            hasMergeHead: false,
            amendHead: null,
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(expectedTemplate, loaded.Message);
        Assert.AreEqual(CommitMessageInitializationKind.Template, loaded.Kind);
    }

    /// <summary>
    /// Verifies a configured missing template is reported instead of silently becoming an empty message.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_WithMissingConfiguredTemplate_ThrowsActionableFailure()
    {
        var repositoryPath = await CreateCommittedRepositoryAsync("missing-template", "base\n");
        await RunGitAsync(
            repositoryPath,
            "config",
            "--local",
            "commit.template",
            "missing-template.txt");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var paths = await ResolvePathsAsync(workingDirectory);

        var exception = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            CreateService().LoadAsync(
                workingDirectory,
                paths.Recovery,
                paths.MergeMessage,
                paths.SquashMessage,
                hasMergeHead: false,
                amendHead: null,
                TestContext.Current!.CancellationToken));

        StringAssert.Contains(exception.Message, "configured commit template");
        StringAssert.Contains(exception.Message, "missing-template.txt");
    }

    /// <summary>
    /// Verifies pending no-commit merge and squash operations load the exact messages authored by Git.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_WithMergeAndSquashState_UsesMatchingGitMessage()
    {
        var repositoryPath = await CreateCommittedRepositoryAsync("integration", "base\n");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "-c", "feature");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "feature\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "feature");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        await RunGitAsync(repositoryPath, "merge", "--quiet", "--no-ff", "--no-commit", "feature");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var paths = await ResolvePathsAsync(workingDirectory);
        var service = CreateService();
        var expectedMerge = await ReadRequiredMessageAsync(paths.MergeMessage);

        var mergeMessage = await service.LoadAsync(
            workingDirectory,
            paths.Recovery,
            paths.MergeMessage,
            paths.SquashMessage,
            hasMergeHead: true,
            amendHead: null,
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(expectedMerge, mergeMessage.Message);
        Assert.AreEqual(CommitMessageInitializationKind.Merge, mergeMessage.Kind);
        await RunGitAsync(repositoryPath, "merge", "--abort");
        await RunGitAsync(repositoryPath, "merge", "--quiet", "--squash", "feature");
        var expectedSquash = await ReadRequiredMessageAsync(paths.SquashMessage);

        var squashMessage = await service.LoadAsync(
            workingDirectory,
            paths.Recovery,
            paths.MergeMessage,
            paths.SquashMessage,
            hasMergeHead: false,
            amendHead: null,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(expectedSquash, squashMessage.Message);
        Assert.AreEqual(CommitMessageInitializationKind.Squash, squashMessage.Kind);
    }

    /// <summary>
    /// Verifies an amend session presents the selected HEAD message through the persistent editor state.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithAmend_PrefillsCommitEditorFromExactHead()
    {
        const string expectedMessage = "session subject\n\nsession body\n";
        var repositoryPath = await CreateCommittedRepositoryAsync("session", expectedMessage);

        var opened = await RepositoryWorkspaceSession.OpenAsync(
            CanonicalDirectory.Create(repositoryPath),
            amend: true,
            _processEnvironment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var session = opened.Session;
        Assert.IsNotNull(session);
        try
        {
            Assert.IsTrue(session.CommitOptions.Amend);
            Assert.AreEqual(expectedMessage, session.CommitMessage.Message);
            Assert.AreEqual("Loaded HEAD message for amend", session.Activity);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a saved commit message is described without implying that Git recovered a lost commit.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithSavedCommitMessage_UsesClearActivityText()
    {
        const string expectedMessage = "saved subject\n\nsaved body\n";
        var repositoryPath = await CreateCommittedRepositoryAsync("saved-message", "base\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var paths = await ResolvePathsAsync(workingDirectory);
        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            paths.EditMessage,
            Encoding.UTF8.GetBytes(expectedMessage),
            TestContext.Current!.CancellationToken);

        var opened = await RepositoryWorkspaceSession.OpenAsync(
            workingDirectory,
            amend: false,
            _processEnvironment!,
            TimeProvider.System,
            TestContext.Current.CancellationToken);
        var session = opened.Session;
        Assert.IsNotNull(session);
        try
        {
            Assert.AreEqual(expectedMessage, session.CommitMessage.Message);
            Assert.AreEqual("Loaded saved commit message text", session.Activity);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a real staged session blocks an untouched template and enables commit after an editor change.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithUnchangedTemplate_RequiresEditBeforeCommit()
    {
        const string template = "Subject\n\nDescribe the change\n";
        var repositoryPath = await CreateCommittedRepositoryAsync("template-session", "base\n");
        var templatePath = Path.Combine(repositoryPath, "commit-template.txt");
        File.WriteAllText(templatePath, template, new UTF8Encoding(false));
        await RunGitAsync(
            repositoryPath,
            "config",
            "--local",
            "commit.template",
            "commit-template.txt");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "staged\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");

        var opened = await RepositoryWorkspaceSession.OpenAsync(
            CanonicalDirectory.Create(repositoryPath),
            amend: false,
            _processEnvironment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var session = opened.Session;
        Assert.IsNotNull(session);
        try
        {
            Assert.AreEqual(template, session.CommitMessage.Message);
            Assert.AreEqual(
                "Loaded configured commit template; edit it before committing",
                session.Activity);
            Assert.IsTrue(session.NeedsCommitTemplateEdit);
            Assert.IsFalse(session.CanCommit);

            await session.CommitAsync(TestContext.Current.CancellationToken);
            Assert.AreEqual("Edit the configured commit template before committing", session.Activity);
            _ = session.CommitMessage.Editor.Document.Apply(
                new InsertOperation(DocumentOffset.Zero, "Implemented: "),
                "test");

            Assert.IsFalse(session.NeedsCommitTemplateEdit);
            Assert.IsTrue(session.CanCommit);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private CommitMessageInitializationService CreateService()
        => new(_installation!, _runner!, _environmentFactory!);

    private async Task<(
        GitPath EditMessage,
        GitPath Message,
        GitPath Backup,
        GitPath MergeMessage,
        GitPath SquashMessage,
        IReadOnlyList<GitPath> Recovery)> ResolvePathsAsync(CanonicalDirectory workingDirectory)
    {
        var resolver = new RepositoryStatePathService(
            _installation!,
            _runner!,
            _environmentFactory!);
        var editMessage = await resolver.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.EditMessage,
            TestContext.Current!.CancellationToken);
        var message = await resolver.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.Message,
            TestContext.Current.CancellationToken);
        var backup = await resolver.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.MessageBackup,
            TestContext.Current.CancellationToken);
        var mergeMessage = await resolver.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.MergeMessage,
            TestContext.Current.CancellationToken);
        var squashMessage = await resolver.ResolveAsync(
            workingDirectory,
            RepositoryStateFile.SquashMessage,
            TestContext.Current.CancellationToken);
        return (editMessage, message, backup, mergeMessage, squashMessage, [editMessage, message, backup]);
    }

    private async Task<string> CreateCommittedRepositoryAsync(string name, string message)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, name);
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "base\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        var messagePath = Path.Combine(_temporaryDirectory!, $"{name}-message.txt");
        File.WriteAllText(messagePath, message, new UTF8Encoding(false));
        await RunGitAsync(
            repositoryPath,
            "commit",
            "--quiet",
            "--cleanup=verbatim",
            $"--file={messagePath}");
        return repositoryPath;
    }

    private async Task CommitAsync(string repositoryPath, string message)
        => await RunGitAsync(repositoryPath, "commit", "--quiet", "--message", message);

    private async Task<ObjectId?> ResolveHeadAsync(string repositoryPath)
    {
        var result = await RunGitAsync(repositoryPath, "rev-parse", "--verify", "HEAD");
        var output = result.StandardOutput.Span;
        if (!output.IsEmpty && output[^1] == (byte)'\n')
        {
            output = output[..^1];
        }

        return ObjectId.TryParseHex(output, out var objectId)
            ? objectId
            : throw new InvalidDataException("Git returned an invalid test HEAD object identifier.");
    }

    private static async Task<string> ReadRequiredMessageAsync(GitPath path)
    {
        var bytes = await RepositoryStateFileSystem.ReadIfExistsAsync(
            path,
            16 * 1024 * 1024,
            TestContext.Current!.CancellationToken)
            ?? throw new InvalidDataException("Git did not create the expected test message file.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private async Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var environment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("HOME", Path.Combine(_temporaryDirectory!, "home")),
            new KeyValuePair<string, string>("USERPROFILE", Path.Combine(_temporaryDirectory!, "home")),
            new KeyValuePair<string, string>("GIT_CONFIG_NOSYSTEM", "1"),
            new KeyValuePair<string, string>("GIT_AUTHOR_NAME", "GitSail Author"),
            new KeyValuePair<string, string>("GIT_AUTHOR_EMAIL", "author@example.invalid"),
            new KeyValuePair<string, string>("GIT_COMMITTER_NAME", "GitSail Committer"),
            new KeyValuePair<string, string>("GIT_COMMITTER_EMAIL", "committer@example.invalid"),
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
        ]);
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return result;
    }
}
