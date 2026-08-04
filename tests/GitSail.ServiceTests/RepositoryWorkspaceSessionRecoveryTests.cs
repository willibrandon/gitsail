using GitSail.Git.Execution;
using GitSail.Ui;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies repository-session revert recovery across restart, undo, and graceful close boundaries.
/// </summary>
[TestClass]
public sealed class RepositoryWorkspaceSessionRecoveryTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private TestProcessEnvironment? _environment;

    /// <summary>
    /// Creates an isolated Git repository and platform user-directory environment for each test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-session-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
        _environment = new TestProcessEnvironment(new Dictionary<string, string?>
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
    }

    /// <summary>
    /// Removes the isolated repository and user-directory tree after each recovery test.
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
    /// Verifies a persisted revert is recovered by a new session and deleted after exact undo.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_AfterPersistedRevert_RecoversAndDeletesUndoAfterUse()
    {
        var (repositoryPath, filePath) = await CreateModifiedRepositoryAsync("restart");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var firstOpen = await RepositoryWorkspaceSession.OpenAsync(
            workingDirectory,
            amend: false,
            _environment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var first = firstOpen.Session;
        Assert.IsNotNull(first);
        RepositoryWorkspaceSession? second = null;
        try
        {
            Assert.IsTrue(first.CanRevertFocusedFile);
            await first.RevertFocusedFileAsync(TestContext.Current.CancellationToken);
            Assert.AreEqual("baseline\n", File.ReadAllText(filePath));
            Assert.HasCount(1, GetRecoveryFiles());

            var secondOpen = await RepositoryWorkspaceSession.OpenAsync(
                workingDirectory,
                amend: false,
                _environment!,
                TimeProvider.System,
                TestContext.Current.CancellationToken);
            second = secondOpen.Session;
            Assert.IsNotNull(second);
            Assert.IsTrue(second.CanUndoRevert);
            StringAssert.Contains(second.Activity, "Recovered revert undo");

            await second.UndoRevertAsync(TestContext.Current.CancellationToken);

            Assert.AreEqual("changed\n", File.ReadAllText(filePath));
            Assert.IsFalse(second.CanUndoRevert);
            Assert.HasCount(0, GetRecoveryFiles());
        }
        finally
        {
            if (second is not null)
            {
                await second.DisposeAsync();
            }

            await first.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a normal repository close removes unused recovery while retaining the private identity key.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_WithUnusedRevertUndo_RemovesRecoveryFile()
    {
        var (repositoryPath, _) = await CreateModifiedRepositoryAsync("close");
        var opened = await RepositoryWorkspaceSession.OpenAsync(
            CanonicalDirectory.Create(repositoryPath),
            amend: false,
            _environment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var session = opened.Session;
        Assert.IsNotNull(session);
        await session.RevertFocusedFileAsync(TestContext.Current.CancellationToken);
        Assert.HasCount(1, GetRecoveryFiles());

        await session.DisposeAsync();

        Assert.HasCount(0, GetRecoveryFiles());
        var configurationDirectory = new UserDirectoryPathService(_environment!).GetConfigurationDirectory();
        Assert.IsTrue(File.Exists(Path.Combine(configurationDirectory, "repository-id.key")));
    }

    /// <summary>
    /// Verifies startup deletes cached undo when newer worktree content makes its patch inapplicable.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithStaleRecoveredPatch_DiscardsUndoBeforePresentingIt()
    {
        var (repositoryPath, filePath) = await CreateModifiedRepositoryAsync("stale");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var firstOpen = await RepositoryWorkspaceSession.OpenAsync(
            workingDirectory,
            amend: false,
            _environment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var first = firstOpen.Session;
        Assert.IsNotNull(first);
        RepositoryWorkspaceSession? second = null;
        try
        {
            await first.RevertFocusedFileAsync(TestContext.Current.CancellationToken);
            Assert.HasCount(1, GetRecoveryFiles());
            File.WriteAllText(filePath, "newer content\n");

            var secondOpen = await RepositoryWorkspaceSession.OpenAsync(
                workingDirectory,
                amend: false,
                _environment!,
                TimeProvider.System,
                TestContext.Current.CancellationToken);
            second = secondOpen.Session;
            Assert.IsNotNull(second);

            Assert.IsFalse(second.CanUndoRevert);
            Assert.HasCount(0, GetRecoveryFiles());
            Assert.AreEqual("newer content\n", File.ReadAllText(filePath));
        }
        finally
        {
            if (second is not null)
            {
                await second.DisposeAsync();
            }

            await first.DisposeAsync();
        }
    }

    private async Task<(string RepositoryPath, string FilePath)> CreateModifiedRepositoryAsync(string name)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, $"repository-{name}");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        var filePath = Path.Combine(repositoryPath, "tracked.txt");
        File.WriteAllText(filePath, "baseline\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        File.WriteAllText(filePath, "changed\n");
        return (repositoryPath, filePath);
    }

    private string[] GetRecoveryFiles()
    {
        var cacheDirectory = new UserDirectoryPathService(_environment!).GetCacheDirectory();
        var undoDirectory = Path.Combine(cacheDirectory, "undo");
        return Directory.Exists(undoDirectory)
            ? Directory.GetFiles(undoDirectory, "revert-*.bin", SearchOption.TopDirectoryOnly)
            : [];
    }

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var childEnvironment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("HOME", Path.Combine(_temporaryDirectory!, "home")),
            new KeyValuePair<string, string>("USERPROFILE", Path.Combine(_temporaryDirectory!, "home")),
            new KeyValuePair<string, string>("GIT_CONFIG_NOSYSTEM", "1"),
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
        ]);
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            childEnvironment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));

        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
    }
}
