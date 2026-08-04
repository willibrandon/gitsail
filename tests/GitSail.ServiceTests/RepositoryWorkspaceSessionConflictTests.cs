using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Testing;
using GitSail.Ui;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies editable conflict loading and rollback-capable staging through a complete repository session.
/// </summary>
[TestClass]
public sealed class RepositoryWorkspaceSessionConflictTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private TestProcessEnvironment? _environment;

    /// <summary>
    /// Creates an isolated Git and platform user-directory environment for each session test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-session-conflict-{Guid.NewGuid():N}");
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
    /// Removes the isolated repository and user-directory tree after each session test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies a real content conflict supports editor undo and exact staged resolution end to end.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithContentConflict_LoadsEditableResultAndStagesExactChoice()
    {
        var repositoryPath = await CreateConflictedRepositoryAsync();
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
            Assert.IsTrue(session.IsConflictResolutionActive);
            Assert.AreEqual(1, session.ConflictChunkCount);
            Assert.IsTrue(session.CanChooseFocusedConflictChunk);
            Assert.IsFalse(session.Diff.Editor.IsReadOnly);
            Assert.IsFalse(session.CanCommit);

            await session.StageAsync(TestContext.Current.CancellationToken);
            await session.StageAllAsync(TestContext.Current.CancellationToken);
            await session.UnstageAsync(TestContext.Current.CancellationToken);
            await session.UnstageAllAsync(TestContext.Current.CancellationToken);
            Assert.IsTrue(session.IsConflictResolutionActive);
            Assert.IsTrue(session.CanChooseFocusedConflictChunk);

            await session.ChooseFocusedConflictChunkAsync(ConflictResolutionChoice.Ours);

            Assert.IsTrue(session.CanStageConflictResolution);
            Assert.AreEqual("line one\nours\nline three\n", session.Diff.Editor.Document.GetText());
            session.Diff.Editor.Undo();
            Assert.IsFalse(session.CanStageConflictResolution);
            session.Diff.Editor.Redo();
            Assert.IsTrue(session.CanStageConflictResolution);

            await session.StageConflictResolutionAsync(TestContext.Current.CancellationToken);

            Assert.IsFalse(session.IsConflictResolutionActive);
            Assert.IsTrue(session.CanCommit);
            Assert.AreEqual("line one\nours\nline three\n", File.ReadAllText(Path.Combine(repositoryPath, "conflict.txt")));
            var unmerged = await RunGitAsync(repositoryPath, "ls-files", "--unmerged");
            Assert.AreEqual(0, unmerged.StandardOutput.Length);
            var staged = await RunGitAsync(repositoryPath, "show", ":conflict.txt");
            Assert.AreEqual(
                "line one\nours\nline three\n",
                Encoding.UTF8.GetString(staged.StandardOutput.Span));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies merge abort rejects stale displayed state and delegates exact recovery to Git porcelain.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithContentConflict_AbortsOnlyExactConfirmedMergeState()
    {
        var repositoryPath = await CreateConflictedRepositoryAsync(withAutostash: true);
        var expectedHead = Encoding.UTF8.GetString(
            (await RunGitAsync(repositoryPath, "rev-parse", "HEAD")).StandardOutput.Span).Trim();
        var expectedMergeHead = Encoding.UTF8.GetString(
            (await RunGitAsync(repositoryPath, "rev-parse", "incoming")).StandardOutput.Span).Trim();
        var expectedAutostash = Encoding.UTF8.GetString(
            (await RunGitAsync(
                repositoryPath,
                "rev-parse",
                "--verify",
                "--quiet",
                "--end-of-options",
                "MERGE_AUTOSTASH")).StandardOutput.Span).Trim();
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
            var displayedWarning = session.MergeAbortWarning;
            Assert.IsNotNull(displayedWarning);
            Assert.IsTrue(session.CanAbortMerge);
            Assert.AreEqual(expectedHead, displayedWarning.Precondition.HeadObjectId?.ToString());
            Assert.AreEqual("refs/heads/main", displayedWarning.Precondition.HeadName?.DisplayText);
            Assert.AreEqual(expectedMergeHead, TestSeq.Single(displayedWarning.MergeHeads).ToString());
            Assert.AreEqual(expectedAutostash, displayedWarning.MergeAutostash?.ToString());

            File.WriteAllText(Path.Combine(repositoryPath, "conflict.txt"), "externally resolved\n");
            _ = await RunGitAsync(repositoryPath, "add", "--", "conflict.txt");
            await session.AbortMergeAsync(
                displayedWarning,
                TestContext.Current.CancellationToken);

            StringAssert.Contains(session.Activity, "Failed:");
            Assert.IsTrue(session.CanAbortMerge);
            var refreshedWarning = session.MergeAbortWarning;
            Assert.IsNotNull(refreshedWarning);
            Assert.IsFalse(displayedWarning.Matches(refreshedWarning));
            Assert.AreEqual(expectedMergeHead, TestSeq.Single(refreshedWarning.MergeHeads).ToString());
            Assert.AreEqual(expectedAutostash, refreshedWarning.MergeAutostash?.ToString());

            File.WriteAllText(Path.Combine(repositoryPath, "conflict.txt"), "changed after confirmation\n");
            await session.AbortMergeAsync(
                refreshedWarning,
                TestContext.Current.CancellationToken);

            StringAssert.Contains(session.Activity, "Failed:");
            Assert.IsTrue(session.CanAbortMerge);
            var workTreeRefreshedWarning = session.MergeAbortWarning;
            Assert.IsNotNull(workTreeRefreshedWarning);
            Assert.IsTrue(refreshedWarning.Precondition.Matches(workTreeRefreshedWarning.Precondition));
            Assert.IsFalse(
                refreshedWarning.WorkTreeFingerprint.Span.SequenceEqual(
                    workTreeRefreshedWarning.WorkTreeFingerprint.Span));
            Assert.IsFalse(refreshedWarning.Matches(workTreeRefreshedWarning));

            File.WriteAllText(Path.Combine(repositoryPath, "conflict.txt"), "externally resolved\n");
            await session.RefreshAsync(TestContext.Current.CancellationToken);
            var abortableWarning = session.MergeAbortWarning;
            Assert.IsNotNull(abortableWarning);
            await session.AbortMergeAsync(
                abortableWarning,
                TestContext.Current.CancellationToken);

            Assert.IsFalse(session.CanAbortMerge, session.Activity);
            Assert.IsNull(session.MergeAbortWarning);
            Assert.AreEqual("Merge aborted", session.Activity);
            Assert.AreEqual(
                expectedHead,
                Encoding.UTF8.GetString(
                    (await RunGitAsync(repositoryPath, "rev-parse", "HEAD")).StandardOutput.Span).Trim());
            Assert.AreEqual(
                "main",
                Encoding.UTF8.GetString(
                    (await RunGitAsync(repositoryPath, "branch", "--show-current")).StandardOutput.Span).Trim());
            Assert.AreEqual(
                "line one\nours\nline three\n",
                File.ReadAllText(Path.Combine(repositoryPath, "conflict.txt")));
            Assert.AreEqual(
                "local work\n",
                File.ReadAllText(Path.Combine(repositoryPath, "local.txt")));
            Assert.AreEqual(
                " M local.txt\n",
                Encoding.UTF8.GetString(
                    (await RunGitAsync(repositoryPath, "status", "--porcelain=v1")).StandardOutput.Span));
            var mergeHead = await RunGitAsync(
                repositoryPath,
                ["rev-parse", "--verify", "--quiet", "MERGE_HEAD"],
                expectSuccess: false);
            Assert.AreEqual(1, mergeHead.ExitCode);
            var mergeAutostash = await RunGitAsync(
                repositoryPath,
                ["rev-parse", "--verify", "--quiet", "--end-of-options", "MERGE_AUTOSTASH"],
                expectSuccess: false);
            Assert.AreEqual(1, mergeAutostash.ExitCode);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private async Task<string> CreateConflictedRepositoryAsync(bool withAutostash = false)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        _ = await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        var path = Path.Combine(repositoryPath, "conflict.txt");
        var localPath = Path.Combine(repositoryPath, "local.txt");
        File.WriteAllText(path, "line one\nbase\nline three\n");
        File.WriteAllText(localPath, "base\n");
        _ = await RunGitAsync(repositoryPath, "add", "--", "conflict.txt", "local.txt");
        _ = await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "base");
        _ = await RunGitAsync(repositoryPath, "switch", "--quiet", "-c", "incoming");
        File.WriteAllText(path, "line one\ntheirs\nline three\n");
        _ = await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-am",
            "theirs");
        _ = await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        File.WriteAllText(path, "line one\nours\nline three\n");
        _ = await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-am",
            "ours");
        if (withAutostash)
        {
            File.WriteAllText(localPath, "local work\n");
            Assert.AreEqual(
                " M local.txt\n",
                Encoding.UTF8.GetString(
                    (await RunGitAsync(repositoryPath, "status", "--porcelain=v1")).StandardOutput.Span));
        }

        var merge = await RunGitAsync(
            repositoryPath,
            withAutostash
                ? ["merge", "--no-edit", "--autostash", "incoming"]
                : ["merge", "--no-edit", "incoming"],
            expectSuccess: false);
        Assert.AreEqual(1, merge.ExitCode, Encoding.UTF8.GetString(merge.StandardError.Span));
        if (withAutostash)
        {
            var mergeAutostash = await RunGitAsync(
                repositoryPath,
                "rev-parse",
                "--verify",
                "--quiet",
                "--end-of-options",
                "MERGE_AUTOSTASH");
            Assert.IsGreaterThan(0, mergeAutostash.StandardOutput.Length);
        }

        return repositoryPath;
    }

    private Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] arguments)
        => RunGitAsync(workingDirectory, arguments, expectSuccess: true);

    private async Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        string[] arguments,
        bool expectSuccess)
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
        if (expectSuccess)
        {
            Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        }

        return result;
    }
}
