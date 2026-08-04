using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies repository statistics, maintenance, collection, and integrity checks against real Git.
/// </summary>
[TestClass]
public sealed class RepositoryMaintenanceServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private RepositoryMutationCoordinator? _coordinator;
    private GitChildEnvironmentFactory? _environmentFactory;
    private CredentialPromptCoordinator? _credentialPrompts;

    /// <summary>
    /// Creates an isolated Git home and repository-operation boundary for each test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-maintenance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _coordinator = new RepositoryMutationCoordinator();
        _environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory);
        _credentialPrompts = new CredentialPromptCoordinator();
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        _installation = await new GitVersionService(resolver, _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Releases prompt and mutation state and removes every isolated repository after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        _credentialPrompts?.Dispose();
        _coordinator?.Dispose();
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies every stable count field and repeated alternate record parses without retaining paths.
    /// </summary>
    [TestMethod]
    public void ParseStatistics_WithCompleteCrLfOutput_ReturnsEveryCountAndAlternateTotal()
    {
        var output = "count: 12\r\nsize: 48\r\nin-pack: 345\r\npacks: 2\r\n" +
            "size-pack: 678\r\nprune-packable: 3\r\ngarbage: 1\r\nsize-garbage: 4\r\n" +
            "alternate: /private/first\r\nalternate: C:\\private\\second\r\n" +
            "future-field: forward-compatible value\r\n";

        var statistics = RepositoryMaintenanceService.ParseStatistics(Encoding.UTF8.GetBytes(output));

        Assert.AreEqual(12, statistics.LooseObjectCount);
        Assert.AreEqual(48, statistics.LooseObjectSizeKiB);
        Assert.AreEqual(345, statistics.PackedObjectCount);
        Assert.AreEqual(2, statistics.PackCount);
        Assert.AreEqual(678, statistics.PackSizeKiB);
        Assert.AreEqual(3, statistics.PrunePackableObjectCount);
        Assert.AreEqual(1, statistics.GarbageFileCount);
        Assert.AreEqual(4, statistics.GarbageSizeKiB);
        Assert.AreEqual(2, statistics.AlternateObjectDatabaseCount);
    }

    /// <summary>
    /// Verifies malformed, duplicate, negative, and incomplete statistics cannot reach presentation state.
    /// </summary>
    [TestMethod]
    public void ParseStatistics_WithInvalidOutput_RejectsEveryInvalidShape()
    {
        string[] invalidOutputs =
        [
            "not a record\n",
            CompleteStatistics("count: -1"),
            CompleteStatistics("count: 1\ncount: 2"),
            "count: 1\n",
            CompleteStatistics("count: nope"),
        ];

        foreach (var output in invalidOutputs)
        {
            _ = Assert.ThrowsExactly<InvalidDataException>(
                () => RepositoryMaintenanceService.ParseStatistics(Encoding.UTF8.GetBytes(output)));
        }
    }

    /// <summary>
    /// Verifies statistics, configured maintenance, and foreground collection complete through real Git.
    /// </summary>
    [TestMethod]
    public async Task RepositoryCareAsync_WithHealthyRepository_CompletesAndRefreshesObjectCounts()
    {
        var repositoryPath = await CreateRepositoryAsync("healthy");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var service = CreateService();

        var before = await service.CaptureStatisticsAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        _ = await service.RunConfiguredMaintenanceAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);
        _ = await service.RunGarbageCollectionAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);
        var after = await service.CaptureStatisticsAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);
        _ = await service.VerifyAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);

        Assert.IsGreaterThanOrEqualTo(1, before.LooseObjectCount + before.PackedObjectCount);
        Assert.IsGreaterThanOrEqualTo(1, after.PackedObjectCount);
        Assert.AreEqual(0, after.GarbageFileCount);
    }

    /// <summary>
    /// Verifies full integrity checking reports exact failure output without changing the corrupt object.
    /// </summary>
    [TestMethod]
    public async Task VerifyAsync_WithCorruptLooseObject_FailsWithoutWritingRecoveryFiles()
    {
        var repositoryPath = await CreateRepositoryAsync("corrupt");
        var objectId = (await RunGitForOutputAsync(repositoryPath, "rev-parse", "HEAD")).Trim();
        var objectPath = Path.Combine(
            repositoryPath,
            ".git",
            "objects",
            objectId[..2],
            objectId[2..]);
        var corruptBytes = File.ReadAllBytes(objectPath);
        corruptBytes[^1] ^= 0x5a;
        File.SetAttributes(objectPath, File.GetAttributes(objectPath) & ~FileAttributes.ReadOnly);
        File.WriteAllBytes(objectPath, corruptBytes);

        var exception = await Assert.ThrowsExactlyAsync<RepositoryMaintenanceException>(
            () => CreateService().VerifyAsync(
                CanonicalDirectory.Create(repositoryPath),
                TestContext.Current!.CancellationToken));

        Assert.AreNotEqual(0, exception.ExitCode);
        Assert.IsGreaterThan(0, exception.StandardOutput.Length + exception.StandardError.Length);
        CollectionAssert.AreEqual(corruptBytes, File.ReadAllBytes(objectPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(repositoryPath, ".git", "lost-found")));
    }

    private RepositoryMaintenanceService CreateService()
        => new(
            _installation!,
            _runner!,
            _environmentFactory!,
            _coordinator!,
            new CredentialPromptBroker(_credentialPrompts!));

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
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "baseline\n");
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
        return repositoryPath;
    }

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
        => _ = await RunGitForOutputAsync(workingDirectory, arguments);

    private async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            _environmentFactory!.CreateRepositoryMutationEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(64 * 1024 * 1024, 4 * 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private static string CompleteStatistics(string countLine)
        => $"{countLine}\nsize: 1\nin-pack: 1\npacks: 1\nsize-pack: 1\n" +
            "prune-packable: 1\ngarbage: 0\nsize-garbage: 0\n";
}
