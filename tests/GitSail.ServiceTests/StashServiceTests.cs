using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies exact stash capture and revalidated lifecycle transactions against real Git.
/// </summary>
[TestClass]
public sealed class StashServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private RepositoryMutationCoordinator? _coordinator;
    private GitChildEnvironmentFactory? _environmentFactory;

    /// <summary>
    /// Creates an isolated home and resolves Git for each stash-service test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-stashes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _coordinator = new RepositoryMutationCoordinator();
        _environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory);
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        _installation = await new GitVersionService(resolver, _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Removes isolated repositories and the mutation coordinator after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        _coordinator?.Dispose();
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies ignored and untracked creation, exact show, indexed apply, and drop round-trip through Git.
    /// </summary>
    [TestMethod]
    public async Task CreateShowApplyAndDropAsync_WithAllFileClasses_RoundTripsExactState()
    {
        var repositoryPath = await CreateRepositoryAsync("all-files");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "worktree\n");
        File.WriteAllText(Path.Combine(repositoryPath, "untracked.txt"), "untracked\n");
        File.WriteAllText(Path.Combine(repositoryPath, "ignored.tmp"), "ignored\n");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var precondition = await CapturePreconditionAsync(workingDirectory);

        _ = await service.CreateAsync(
            workingDirectory,
            precondition,
            new StashCreateOptions("--all classes", StashFileScope.IncludeIgnored, keepIndex: false, stagedOnly: false),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual("baseline\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(repositoryPath, "untracked.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(repositoryPath, "ignored.tmp")));
        var catalog = await service.CaptureAsync(workingDirectory, TestContext.Current.CancellationToken);
        var entry = AssertSingle(catalog);
        StringAssert.Contains(entry.DisplayMessage, "--all classes");
        using (var patch = await service.ShowAsync(
            workingDirectory,
            catalog,
            entry,
            TestContext.Current.CancellationToken))
        {
            var bytes = await patch.ReadSliceAsync(
                0,
                checked((int)patch.Length),
                TestContext.Current.CancellationToken);
            var text = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains(text, "tracked.txt");
            StringAssert.Contains(text, "untracked.txt");
            StringAssert.Contains(text, "ignored.tmp");
        }

        _ = await service.ApplyAsync(
            workingDirectory,
            catalog,
            entry,
            restoreIndex: true,
            TestContext.Current.CancellationToken);

        Assert.AreEqual("baseline\nworktree\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
        Assert.AreEqual("untracked\n", File.ReadAllText(Path.Combine(repositoryPath, "untracked.txt")));
        Assert.AreEqual("ignored\n", File.ReadAllText(Path.Combine(repositoryPath, "ignored.tmp")));
        var appliedCatalog = await service.CaptureAsync(workingDirectory, TestContext.Current.CancellationToken);
        Assert.HasCount(1, appliedCatalog.Entries);
        _ = await service.DropAsync(
            workingDirectory,
            appliedCatalog,
            appliedCatalog.Entries[0],
            TestContext.Current.CancellationToken);
        var emptyCatalog = await service.CaptureAsync(workingDirectory, TestContext.Current.CancellationToken);
        Assert.IsEmpty(emptyCatalog.Entries);
    }

    /// <summary>
    /// Verifies pop removes the selected reflog entry only after a successful application.
    /// </summary>
    [TestMethod]
    public async Task PopAsync_WithCleanWorktree_AppliesAndRemovesExactEntry()
    {
        var repositoryPath = await CreateRepositoryAsync("pop-success");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "stashed\n");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        await CreateTrackedStashAsync(service, workingDirectory, "pop target");
        var catalog = await service.CaptureAsync(workingDirectory, TestContext.Current!.CancellationToken);

        _ = await service.PopAsync(
            workingDirectory,
            catalog,
            AssertSingle(catalog),
            restoreIndex: false,
            TestContext.Current.CancellationToken);

        Assert.AreEqual("baseline\nstashed\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
        var refreshed = await service.CaptureAsync(workingDirectory, TestContext.Current.CancellationToken);
        Assert.IsEmpty(refreshed.Entries);
    }

    /// <summary>
    /// Verifies a changed worktree invalidates apply before Git can merge the selected stash.
    /// </summary>
    [TestMethod]
    public async Task ApplyAsync_AfterWorktreeChanged_RejectsStaleConfirmation()
    {
        var repositoryPath = await CreateRepositoryAsync("stale-worktree");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "stashed\n");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        await CreateTrackedStashAsync(service, workingDirectory, "stale target");
        var catalog = await service.CaptureAsync(workingDirectory, TestContext.Current!.CancellationToken);
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "concurrent\n");

        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.ApplyAsync(
            workingDirectory,
            catalog,
            AssertSingle(catalog),
            restoreIndex: false,
            TestContext.Current.CancellationToken));

        Assert.AreEqual("baseline\nconcurrent\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
        Assert.HasCount(1, (await service.CaptureAsync(
            workingDirectory,
            TestContext.Current.CancellationToken)).Entries);
    }

    /// <summary>
    /// Verifies an externally shifted reflog selector is rejected before drop can remove another entry.
    /// </summary>
    [TestMethod]
    public async Task DropAsync_AfterSelectorShift_RejectsWrongEntryDeletion()
    {
        var repositoryPath = await CreateRepositoryAsync("stale-selector");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "first\n");
        await CreateTrackedStashAsync(service, workingDirectory, "first");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "second\n");
        await CreateTrackedStashAsync(service, workingDirectory, "second");
        var catalog = await service.CaptureAsync(workingDirectory, TestContext.Current!.CancellationToken);
        Assert.HasCount(2, catalog.Entries);
        var originallyOlder = catalog.Entries[1];
        await RunGitAsync(repositoryPath, "stash", "drop", "stash@{0}");

        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.DropAsync(
            workingDirectory,
            catalog,
            originallyOlder,
            TestContext.Current.CancellationToken));

        var remaining = await service.CaptureAsync(workingDirectory, TestContext.Current.CancellationToken);
        Assert.HasCount(1, remaining.Entries);
        Assert.AreEqual(originallyOlder.ObjectId, remaining.Entries[0].ObjectId);
    }

    /// <summary>
    /// Verifies Git preserves a stash reflog entry when pop stops with content conflicts.
    /// </summary>
    [TestMethod]
    public async Task PopAsync_WithConflict_LeavesStashEntryAvailable()
    {
        var repositoryPath = await CreateRepositoryAsync("pop-conflict");
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "stash version\n");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        await CreateTrackedStashAsync(service, workingDirectory, "conflicting");
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "head version\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "conflicting head");
        var catalog = await service.CaptureAsync(workingDirectory, TestContext.Current!.CancellationToken);

        _ = await Assert.ThrowsExactlyAsync<GitCommandException>(() => service.PopAsync(
            workingDirectory,
            catalog,
            AssertSingle(catalog),
            restoreIndex: false,
            TestContext.Current.CancellationToken));

        var stashCount = (await RunGitForOutputAsync(repositoryPath, "stash", "list", "--format=%H"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Length;
        Assert.AreEqual(1, stashCount);
    }

    private StashService CreateService()
        => new(_installation!, _runner!, _environmentFactory!, _coordinator!);

    private Task<RepositoryPrecondition> CapturePreconditionAsync(CanonicalDirectory workingDirectory)
        => new RepositoryPreconditionService(_installation!, _runner!, _environmentFactory!).CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);

    private async Task CreateTrackedStashAsync(
        StashService service,
        CanonicalDirectory workingDirectory,
        string message)
    {
        var precondition = await CapturePreconditionAsync(workingDirectory);
        _ = await service.CreateAsync(
            workingDirectory,
            precondition,
            new StashCreateOptions(message, StashFileScope.Tracked, keepIndex: false, stagedOnly: false),
            TestContext.Current!.CancellationToken);
    }

    private async Task<string> CreateRepositoryAsync(string directoryName)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, directoryName);
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, ".gitignore"), "*.tmp\n");
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "baseline\n");
        await RunGitAsync(repositoryPath, "add", "--", ".gitignore", "tracked.txt");
        await CommitAsync(repositoryPath, "baseline");
        return repositoryPath;
    }

    private async Task CommitAsync(string repositoryPath, string message)
        => await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            message);

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
        => _ = await RunGitForOutputAsync(workingDirectory, arguments);

    private async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
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
            OutputPolicy.Create(64 * 1024 * 1024, 4 * 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private static StashInfo AssertSingle(StashCatalog catalog)
    {
        Assert.HasCount(1, catalog.Entries);
        return catalog.Entries[0];
    }
}
