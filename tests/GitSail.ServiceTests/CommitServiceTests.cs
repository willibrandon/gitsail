using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies Git-owned commit transactions, hooks, drafts, and HEAD preconditions.
/// </summary>
[TestClass]
public sealed class CommitServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private GitChildEnvironmentFactory? _environmentFactory;

    /// <summary>
    /// Creates an isolated Git environment for each commit transaction test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-commit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
        _environmentFactory = new GitChildEnvironmentFactory(new TestProcessEnvironment(
            new Dictionary<string, string?>
            {
                ["HOME"] = _temporaryDirectory,
                ["USERPROFILE"] = _temporaryDirectory,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
                ["TMPDIR"] = _temporaryDirectory,
                ["TEMP"] = _temporaryDirectory,
                ["TMP"] = _temporaryDirectory,
                ["GIT_AUTHOR_NAME"] = "GitSail Author",
                ["GIT_AUTHOR_EMAIL"] = "author@example.invalid",
                ["GIT_COMMITTER_NAME"] = "GitSail Committer",
                ["GIT_COMMITTER_EMAIL"] = "committer@example.invalid",
            }));
    }

    /// <summary>
    /// Removes the isolated commit repository and home after each test.
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
    /// Verifies Git runs hooks and applies author, signoff, cleanup, and draft cleanup atomically.
    /// </summary>
    [TestMethod]
    public async Task CommitAsync_WithControlledOptions_RunsGitTransactionAndRemovesDraft()
    {
        var repositoryPath = await InitializeStagedRepositoryAsync("successful");
        InstallHook(repositoryPath, "pre-commit", "#!/bin/sh\nprintf invoked > hook.marker\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var snapshot = await ScanAsync(workingDirectory, new OperationGeneration(1));
        using var coordinator = new RepositoryMutationCoordinator();
        var service = CreateService(coordinator);

        var result = await service.CommitAsync(
            snapshot,
            workingDirectory,
            new CommitRequest(
                "subject\n\nbody\n",
                signoff: true,
                author: "Different Author <different@example.invalid>",
                cleanupMode: CommitCleanupMode.Verbatim),
            TestContext.Current!.CancellationToken);
        var log = Encoding.UTF8.GetString(await RunGitAsync(
            repositoryPath,
            "log",
            "-1",
            "--format=%an%x00%ae%x00%B"));
        var draftPath = await new RepositoryStatePathService(
            _installation!,
            _runner!,
            _environmentFactory!).ResolveAsync(
            workingDirectory,
            RepositoryStateFile.EditMessage,
            TestContext.Current!.CancellationToken);

        Assert.IsNull(result.PreviousHead);
        Assert.IsNotNull(result.NewHead);
        Assert.IsNull(result.DraftCleanupWarning);
        Assert.IsTrue(File.Exists(Path.Combine(repositoryPath, "hook.marker")));
        StringAssert.StartsWith(log, "Different Author\0different@example.invalid\0subject\n\nbody\n");
        StringAssert.Contains(log, "Signed-off-by: GitSail Committer <committer@example.invalid>");
        Assert.IsNull(await RepositoryStateFileSystem.ReadIfExistsAsync(
            draftPath,
            maximumBytes: 1024,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies a rejecting commit-message hook leaves HEAD unborn and preserves the exact draft.
    /// </summary>
    [TestMethod]
    public async Task CommitAsync_WithRejectingHook_PreservesDraftAndReportsFailure()
    {
        var repositoryPath = await InitializeStagedRepositoryAsync("rejected");
        InstallHook(repositoryPath, "commit-msg", "#!/bin/sh\nprintf rejected > commit-msg.marker\nexit 1\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var snapshot = await ScanAsync(workingDirectory, new OperationGeneration(1));
        using var coordinator = new RepositoryMutationCoordinator();
        var service = CreateService(coordinator);
        const string message = "keep this exact draft\n";

        _ = await Assert.ThrowsExactlyAsync<GitCommandException>(() => service.CommitAsync(
            snapshot,
            workingDirectory,
            new CommitRequest(message, cleanupMode: CommitCleanupMode.Verbatim),
            TestContext.Current!.CancellationToken));
        var afterFailure = await ScanAsync(workingDirectory, new OperationGeneration(2));
        var draftPath = await new RepositoryStatePathService(
            _installation!,
            _runner!,
            _environmentFactory!).ResolveAsync(
            workingDirectory,
            RepositoryStateFile.EditMessage,
            TestContext.Current!.CancellationToken);

        Assert.IsNull(afterFailure.HeadObjectId);
        Assert.IsTrue(File.Exists(Path.Combine(repositoryPath, "commit-msg.marker")));
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(message),
            await RepositoryStateFileSystem.ReadIfExistsAsync(
                draftPath,
                maximumBytes: 1024,
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies an explicit bypass skips only pre-commit and commit-msg while other hooks still run.
    /// </summary>
    [TestMethod]
    public async Task CommitAsync_WithExplicitHookBypass_SkipsOnlyBypassableHooks()
    {
        var repositoryPath = await InitializeStagedRepositoryAsync("bypassed");
        InstallHook(repositoryPath, "pre-commit", "#!/bin/sh\nprintf invoked > pre-commit.marker\nexit 1\n");
        InstallHook(
            repositoryPath,
            "prepare-commit-msg",
            "#!/bin/sh\nprintf invoked > prepare-commit-msg.marker\n");
        InstallHook(repositoryPath, "commit-msg", "#!/bin/sh\nprintf invoked > commit-msg.marker\nexit 1\n");
        InstallHook(repositoryPath, "post-commit", "#!/bin/sh\nprintf invoked > post-commit.marker\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var snapshot = await ScanAsync(workingDirectory, new OperationGeneration(1));
        using var coordinator = new RepositoryMutationCoordinator();
        var service = CreateService(coordinator);

        var result = await service.CommitAsync(
            snapshot,
            workingDirectory,
            new CommitRequest(
                "explicitly bypassed hooks\n",
                cleanupMode: CommitCleanupMode.Verbatim,
                skipHooks: true),
            TestContext.Current!.CancellationToken);

        Assert.IsNotNull(result.NewHead);
        Assert.IsFalse(File.Exists(Path.Combine(repositoryPath, "pre-commit.marker")));
        Assert.IsFalse(File.Exists(Path.Combine(repositoryPath, "commit-msg.marker")));
        Assert.IsTrue(File.Exists(Path.Combine(repositoryPath, "prepare-commit-msg.marker")));
        Assert.IsTrue(File.Exists(Path.Combine(repositoryPath, "post-commit.marker")));
    }

    /// <summary>
    /// Verifies an externally changed HEAD blocks the transaction before any draft is written.
    /// </summary>
    [TestMethod]
    public async Task CommitAsync_AfterHeadChanged_ThrowsPreconditionBeforeDraftWrite()
    {
        var repositoryPath = await InitializeStagedRepositoryAsync("precondition");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var snapshot = await ScanAsync(workingDirectory, new OperationGeneration(1));
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=External Committer",
            "-c",
            "user.email=external@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "external");
        using var coordinator = new RepositoryMutationCoordinator();
        var service = CreateService(coordinator);

        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.CommitAsync(
            snapshot,
            workingDirectory,
            new CommitRequest("stale draft\n"),
            TestContext.Current!.CancellationToken));
        var draftPath = await new RepositoryStatePathService(
            _installation!,
            _runner!,
            _environmentFactory!).ResolveAsync(
            workingDirectory,
            RepositoryStateFile.EditMessage,
            TestContext.Current!.CancellationToken);

        Assert.IsNull(await RepositoryStateFileSystem.ReadIfExistsAsync(
            draftPath,
            maximumBytes: 1024,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies externally changed staged content is never committed without a fresh user-visible status generation.
    /// </summary>
    [TestMethod]
    public async Task CommitAsync_AfterIndexChanged_ThrowsPreconditionBeforeDraftWrite()
    {
        var repositoryPath = await InitializeStagedRepositoryAsync("index-precondition");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var snapshot = await ScanAsync(workingDirectory, new OperationGeneration(1));
        File.WriteAllText(Path.Combine(repositoryPath, "unexpected.txt"), "not reviewed\n");
        await RunGitAsync(repositoryPath, "add", "--", "unexpected.txt");
        using var coordinator = new RepositoryMutationCoordinator();
        var service = CreateService(coordinator);

        var exception = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.CommitAsync(
            snapshot,
            workingDirectory,
            new CommitRequest("stale staged content\n"),
            TestContext.Current!.CancellationToken));
        var draftPath = await new RepositoryStatePathService(
            _installation!,
            _runner!,
            _environmentFactory!).ResolveAsync(
            workingDirectory,
            RepositoryStateFile.EditMessage,
            TestContext.Current!.CancellationToken);

        StringAssert.Contains(exception.Message, "index changed", StringComparison.Ordinal);
        Assert.IsNull(await RepositoryStateFileSystem.ReadIfExistsAsync(
            draftPath,
            maximumBytes: 1024,
            TestContext.Current.CancellationToken));
        Assert.IsNull((await ScanAsync(workingDirectory, new OperationGeneration(2))).HeadObjectId);
    }

    private CommitService CreateService(RepositoryMutationCoordinator coordinator)
    {
        var statePathService = new RepositoryStatePathService(
            _installation!,
            _runner!,
            _environmentFactory!);
        return new CommitService(
            _installation!,
            _runner!,
            _environmentFactory!,
            coordinator,
            statePathService);
    }

    private async Task<string> InitializeStagedRepositoryAsync(string directoryName)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, directoryName);
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "content\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        return repositoryPath;
    }

    private async Task<RepositoryStatusSnapshot> ScanAsync(
        CanonicalDirectory workingDirectory,
        OperationGeneration generation)
    {
        var repository = await new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            _environmentFactory!).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        return await new RepositoryStatusService(
            _installation!,
            _runner!,
            _environmentFactory!,
            new PorcelainV2StatusParser()).ScanAsync(
            repository,
            workingDirectory,
            generation,
            TestContext.Current.CancellationToken);
    }

    private static void InstallHook(string repositoryPath, string hookName, string script)
    {
        var hookPath = Path.Combine(repositoryPath, ".git", "hooks", hookName);
        File.WriteAllText(hookPath, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private async Task<byte[]> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var environment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("HOME", _temporaryDirectory!),
            new KeyValuePair<string, string>("USERPROFILE", _temporaryDirectory!),
            new KeyValuePair<string, string>("GIT_CONFIG_NOSYSTEM", "1"),
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
        return result.StandardOutput.ToArray();
    }
}
