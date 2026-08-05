using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using Hex1b.Documents;
using System.Diagnostics;
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

    /// <summary>
    /// Verifies external worktree and index changes automatically publish complete refreshed status and diff state.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithExternalFileAndIndexChanges_RefreshesAutomatically()
    {
        var (repositoryPath, filePath) = await CreateModifiedRepositoryAsync("automatic-refresh");
        var opened = await RepositoryWorkspaceSession.OpenAsync(
            CanonicalDirectory.Create(repositoryPath),
            amend: false,
            _environment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var session = opened.Session;
        Assert.IsNotNull(session);
        try
        {
            Assert.HasCount(1, session.State.UnstagedItems);
            Assert.IsEmpty(session.State.StagedItems);
            var cursorPosition = new DocumentPosition(2, 3);
            session.Diff.Editor.SetCursorPosition(
                session.Diff.Editor.Document.PositionToOffset(cursorPosition));
            var unchangedEditor = session.Diff.Editor;

            await session.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.AreSame(unchangedEditor, session.Diff.Editor);
            Assert.AreEqual(
                cursorPosition,
                session.Diff.Editor.Document.OffsetToPosition(session.Diff.Editor.Cursor.Position));

            File.WriteAllText(filePath, "changed by another application\n");
            await WaitUntilAsync(
                () => session.Diff.Editor.Document.GetText().Contains(
                    "+changed by another application",
                    StringComparison.Ordinal),
                "the external file content to appear in the active diff");
            Assert.AreEqual(
                cursorPosition,
                session.Diff.Editor.Document.OffsetToPosition(session.Diff.Editor.Cursor.Position));

            await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
            await WaitUntilAsync(
                () => session.State.UnstagedItems.IsEmpty && session.State.StagedItems.Length == 1,
                "the externally staged file to move into the staged list");

            Assert.AreEqual("Repository refreshed automatically", session.Activity);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies typed diff-context and automatic-refresh settings control the live repository session.
    /// </summary>
    [TestMethod]
    public async Task Configuration_WithDiffContextAndAutoRescan_ControlsLiveSession()
    {
        var (repositoryPath, filePath) = await CreateModifiedRepositoryAsync("configuration-runtime");
        await RunGitAsync(repositoryPath, "config", "--local", "gui.diffcontext", "9");
        await RunGitAsync(repositoryPath, "config", "--local", "gui.tabsize", "6");
        await RunGitAsync(repositoryPath, "config", "--local", "gitsail.autorescan", "false");
        var opened = await RepositoryWorkspaceSession.OpenAsync(
            CanonicalDirectory.Create(repositoryPath),
            amend: false,
            _environment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var session = opened.Session;
        Assert.IsNotNull(session);
        try
        {
            Assert.AreEqual(9, session.DiffContextLines);
            Assert.AreEqual(6, session.Diff.Editor.TabSize);
            Assert.IsFalse(
                session.Configuration.Resolve("gitsail.autorescan", GitConfigurationScope.Local)
                    .EffectiveParsedValue?.BooleanValue ?? true);

            File.WriteAllText(filePath, "changed while automatic refresh is disabled\n");
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(
                "changed while automatic refresh is disabled",
                session.Diff.Editor.Document.GetText(),
                StringComparison.Ordinal);

            await session.SetConfigurationAsync(
                GitConfigurationScope.Local,
                "gitsail.autorescan",
                "true",
                TestContext.Current.CancellationToken);
            Assert.IsTrue(
                session.Configuration.Resolve("gitsail.autorescan", GitConfigurationScope.Local)
                    .EffectiveParsedValue?.BooleanValue ?? false);
            Assert.Contains(
                "changed while automatic refresh is disabled",
                session.Diff.Editor.Document.GetText(),
                StringComparison.Ordinal);

            File.WriteAllText(filePath, "changed while automatic refresh is enabled\n");
            await WaitUntilAsync(
                () => session.Diff.Editor.Document.GetText().Contains(
                    "+changed while automatic refresh is enabled",
                    StringComparison.Ordinal),
                "the enabled watcher to publish the external edit");

            await session.SetConfigurationAsync(
                GitConfigurationScope.Local,
                "gui.tabsize",
                "3",
                TestContext.Current.CancellationToken);
            Assert.AreEqual(3, session.Diff.Editor.TabSize);

            await session.SetConfigurationAsync(
                GitConfigurationScope.Local,
                "gui.diffopts",
                "--ignore-all-space --histogram --stat --numstat",
                TestContext.Current.CancellationToken);
            File.WriteAllText(filePath, "baseline \n");
            await session.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Contains(
                "Git emitted no patch content",
                session.Diff.Editor.Document.GetText(),
                StringComparison.Ordinal);

            await session.ResetConfigurationAsync(
                GitConfigurationScope.Local,
                "gui.diffopts",
                TestContext.Current.CancellationToken);
            Assert.Contains(
                "+baseline ",
                session.Diff.Editor.Document.GetText(),
                StringComparison.Ordinal);

            await session.SetConfigurationAsync(
                GitConfigurationScope.Local,
                "gui.diffcontext",
                "11",
                TestContext.Current.CancellationToken);
            Assert.AreEqual(11, session.DiffContextLines);
            Assert.Contains(
                "Saved gui.diffcontext at repository scope",
                session.Activity,
                StringComparison.Ordinal);

            await session.ResetConfigurationAsync(
                GitConfigurationScope.Local,
                "gui.diffcontext",
                TestContext.Current.CancellationToken);
            Assert.AreEqual(5, session.DiffContextLines);
            Assert.AreEqual(
                GitConfigurationResolutionState.Absent,
                session.Configuration.Resolve("gui.diffcontext", GitConfigurationScope.Local).State);
        }
        finally
        {
            await session.DisposeAsync();
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

    private static async Task WaitUntilAsync(Func<bool> predicate, string expectedResult)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current!.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for {expectedResult}.");
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
