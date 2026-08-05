using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies canonical chooser initialization, cloning, mode selection, and identity-checked cleanup against real Git.
/// </summary>
[TestClass]
public sealed class RepositoryManagementServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private TestProcessEnvironment? _processEnvironment;

    /// <summary>
    /// Creates an isolated home, launch directory, and compatible Git installation for each repository-management test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-repository-management-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _processEnvironment = CreateProcessEnvironment();
        _installation = await new GitVersionService(
            new ExecutableResolver(_processEnvironment),
            _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Removes every isolated repository after each repository-management test.
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
    /// Verifies a relative target is resolved beneath the canonical launch directory without being created during planning.
    /// </summary>
    [TestMethod]
    public void PrepareTarget_WithRelativePath_CanonicalizesWithoutCreatingDirectory()
    {
        var service = CreateService(_runner!);

        var plan = service.PrepareTarget(Path.Combine("nested target"));

        var canonicalLaunchPath = GetManagedPath(CanonicalDirectory.Create(_temporaryDirectory!));
        var expected = Path.Combine(canonicalLaunchPath, "nested target");
        Assert.AreEqual(expected, plan.ManagedTargetPath);
        Assert.AreEqual(canonicalLaunchPath, GetManagedPath(plan.ParentDirectory));
        Assert.IsFalse(plan.ExistedBeforeOperation);
        Assert.IsFalse(Directory.Exists(expected));
    }

    /// <summary>
    /// Verifies Git initializes both normal and bare targets while retaining its configured default-branch behavior.
    /// </summary>
    /// <param name="bare">Whether the created repository has no worktree.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitializeAsync_WithNormalOrBareTarget_CreatesRequestedRepository(bool bare)
    {
        var targetPath = Path.Combine(_temporaryDirectory!, bare ? "bare repository.git" : "working repository");
        var service = CreateService(_runner!);

        var result = await service.InitializeAsync(
            targetPath,
            bare,
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(bare, result.IsBare);
        Assert.AreEqual(
            GetManagedPath(CanonicalDirectory.Create(targetPath)),
            GetManagedPath(result.Directory));
        Assert.AreEqual(
            bare ? "true" : "false",
            (await RunGitForOutputAsync(
                targetPath,
                bare ? $"--git-dir={targetPath}" : "--git-dir=.git",
                "rev-parse",
                "--is-bare-repository")).Trim());
    }

    /// <summary>
    /// Verifies standard, full-copy, and shared clone modes produce Git's expected alternates behavior.
    /// </summary>
    /// <param name="modeValue">The selected local-object behavior value.</param>
    [TestMethod]
    [DataRow((int)RepositoryCloneMode.Standard)]
    [DataRow((int)RepositoryCloneMode.FullCopy)]
    [DataRow((int)RepositoryCloneMode.Shared)]
    public async Task CloneAsync_WithLocalObjectMode_UsesExactGitSemantics(int modeValue)
    {
        var mode = (RepositoryCloneMode)modeValue;
        var sourcePath = await CreateRepositoryAsync("source repository");
        var targetPath = Path.Combine(_temporaryDirectory!, $"clone {mode}");
        var service = CreateService(_runner!);

        var result = await service.CloneAsync(
            new RepositoryCloneRequest(sourcePath, targetPath, mode, recurseSubmodules: false),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(
            GetManagedPath(CanonicalDirectory.Create(targetPath)),
            GetManagedPath(result.Directory));
        Assert.AreEqual("content\n", await File.ReadAllTextAsync(
            Path.Combine(targetPath, "tracked.txt"),
            TestContext.Current.CancellationToken));
        var alternatesPath = Path.Combine(targetPath, ".git", "objects", "info", "alternates");
        Assert.AreEqual(mode == RepositoryCloneMode.Shared, File.Exists(alternatesPath));
        if (mode == RepositoryCloneMode.Shared)
        {
            var actualAlternate = (await File.ReadAllTextAsync(
                alternatesPath,
                TestContext.Current.CancellationToken)).Trim();
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(sourcePath, ".git", "objects")),
                Path.GetFullPath(actualAlternate));
        }
    }

    /// <summary>
    /// Verifies recursive clone mode lets Git initialize and recursively clone an active local submodule.
    /// </summary>
    [TestMethod]
    public async Task CloneAsync_WithRecursiveSubmodules_PopulatesSubmoduleWorktree()
    {
        var submodulePath = await CreateRepositoryAsync("submodule source");
        var sourcePath = await CreateRepositoryAsync("superproject source");
        await RunGitAsync(
            sourcePath,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            "--quiet",
            "--",
            submodulePath,
            "modules/sample");
        await CommitAsync(sourcePath, "add submodule");
        var targetPath = Path.Combine(_temporaryDirectory!, "recursive clone");
        var service = CreateService(_runner!);

        _ = await service.CloneAsync(
            new RepositoryCloneRequest(
                sourcePath,
                targetPath,
                RepositoryCloneMode.Standard,
                recurseSubmodules: true),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual("content\n", await File.ReadAllTextAsync(
            Path.Combine(targetPath, "modules", "sample", "tracked.txt"),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies a failed clone offers and performs cleanup only for the exact newly created target identity.
    /// </summary>
    [TestMethod]
    public async Task CloneAsync_AfterTargetCreationFailure_OffersExactCleanup()
    {
        var sourcePath = await CreateRepositoryAsync("cleanup source");
        var targetPath = Path.Combine(_temporaryDirectory!, "failed clone");
        var service = CreateService(new SuccessfulCloneFailingProcessRunner(_runner!));

        var exception = await Assert.ThrowsExactlyAsync<RepositoryCreationException>(() => service.CloneAsync(
            new RepositoryCloneRequest(
                sourcePath,
                targetPath,
                RepositoryCloneMode.Standard,
                recurseSubmodules: false),
            TestContext.Current!.CancellationToken));

        var cleanup = exception.Cleanup;
        Assert.IsNotNull(cleanup);
        Assert.IsTrue(Directory.Exists(targetPath));
        await cleanup!.DeleteAsync(TestContext.Current!.CancellationToken);
        Assert.IsFalse(Directory.Exists(targetPath));
    }

    /// <summary>
    /// Verifies a cancelled clone retains an identity-checked cleanup offer for its newly created target.
    /// </summary>
    [TestMethod]
    public async Task CloneAsync_AfterTargetCreationCancellation_OffersExactCleanup()
    {
        var sourcePath = await CreateRepositoryAsync("cancel source");
        var targetPath = Path.Combine(_temporaryDirectory!, "cancelled clone");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current!.CancellationToken);
        var service = CreateService(new SuccessfulCloneCancellingProcessRunner(_runner!, cancellation));

        var exception = await Assert.ThrowsExactlyAsync<RepositoryCreationCancelledException>(() =>
            service.CloneAsync(
                new RepositoryCloneRequest(
                    sourcePath,
                    targetPath,
                    RepositoryCloneMode.Standard,
                    recurseSubmodules: false),
                cancellation.Token));

        var cleanup = exception.Cleanup;
        Assert.IsNotNull(cleanup);
        Assert.IsTrue(Directory.Exists(targetPath));
        await cleanup!.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.IsFalse(Directory.Exists(targetPath));
    }

    /// <summary>
    /// Verifies cleanup refuses a replacement directory and leaves its contents untouched.
    /// </summary>
    [TestMethod]
    public async Task CleanupAsync_WhenTargetIdentityChanges_RefusesReplacementDirectory()
    {
        var sourcePath = await CreateRepositoryAsync("replacement source");
        var targetPath = Path.Combine(_temporaryDirectory!, "replace failed clone");
        var movedPath = Path.Combine(_temporaryDirectory!, "original failed clone");
        var service = CreateService(new SuccessfulCloneFailingProcessRunner(_runner!));
        var exception = await Assert.ThrowsExactlyAsync<RepositoryCreationException>(() => service.CloneAsync(
            new RepositoryCloneRequest(
                sourcePath,
                targetPath,
                RepositoryCloneMode.Standard,
                recurseSubmodules: false),
            TestContext.Current!.CancellationToken));
        var cleanup = exception.Cleanup;
        Assert.IsNotNull(cleanup);
        Directory.Move(targetPath, movedPath);
        Directory.CreateDirectory(targetPath);
        var sentinelPath = Path.Combine(targetPath, "sentinel.txt");
        await File.WriteAllTextAsync(
            sentinelPath,
            "preserve replacement",
            TestContext.Current!.CancellationToken);

        _ = await Assert.ThrowsExactlyAsync<IOException>(() => cleanup!.DeleteAsync(
            TestContext.Current.CancellationToken));

        Assert.AreEqual("preserve replacement", await File.ReadAllTextAsync(
            sentinelPath,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies every clone mode remains a literal option contract followed by an operand separator.
    /// </summary>
    /// <param name="modeValue">The selected clone-mode value.</param>
    /// <param name="expectedModeOption">The expected Git option, or an empty string for standard behavior.</param>
    [TestMethod]
    [DataRow((int)RepositoryCloneMode.Standard, "")]
    [DataRow((int)RepositoryCloneMode.FullCopy, "--no-hardlinks")]
    [DataRow((int)RepositoryCloneMode.Shared, "--shared")]
    public void CreateCloneArguments_WithEveryMode_UsesLiteralOperandsAfterSeparator(
        int modeValue,
        string expectedModeOption)
    {
        var mode = (RepositoryCloneMode)modeValue;
        var targetPath = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(@"C:\target")
            : GitPath.FromUnixBytes("/target"u8);

        var arguments = RepositoryManagementService.CreateCloneArguments(
            new RepositoryCloneRequest("-hostile source", "ignored", mode, recurseSubmodules: true),
            targetPath).Select(static argument => argument.ToString()).ToArray();

        var expected = new List<string> { "--no-pager", "clone", "--progress" };
        if (expectedModeOption.Length > 0)
        {
            expected.Add(expectedModeOption);
        }

        expected.Add("--recurse-submodules");
        expected.Add("--");
        expected.Add("-hostile source");
        expected.Add(targetPath.DisplayText);
        CollectionAssert.AreEqual(expected, arguments);
    }

    /// <summary>
    /// Verifies global recent repositories are exact, newest-first, duplicate-free, and individually removable.
    /// </summary>
    [TestMethod]
    public async Task RecentRepositories_RecordAndRemove_RetainsExactNewestFirstPaths()
    {
        var firstPath = Path.Combine(_temporaryDirectory!, "first repository");
        var secondPath = Path.Combine(_temporaryDirectory!, "second repository");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        var first = CanonicalDirectory.Create(firstPath);
        var second = CanonicalDirectory.Create(secondPath);
        await RunGitAsync(
            _temporaryDirectory!,
            "config",
            "--global",
            "gui.maxrecentrepo",
            "2");
        var service = new RecentRepositoryService(
            _installation!,
            _runner!,
            new GitChildEnvironmentFactory(_processEnvironment!),
            CanonicalDirectory.Create(_temporaryDirectory!));

        await service.RecordAsync(first, TestContext.Current!.CancellationToken);
        await service.RecordAsync(second, TestContext.Current.CancellationToken);
        await service.RecordAsync(first, TestContext.Current.CancellationToken);
        var recorded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.HasCount(2, recorded);
        Assert.AreEqual(GetManagedPath(first), GetManagedPath(recorded[0]));
        Assert.AreEqual(GetManagedPath(second), GetManagedPath(recorded[1]));
        var persisted = (await RunGitForOutputAsync(
            _temporaryDirectory!,
            "config",
            "--global",
            "--get-all",
            "gui.recentrepo"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, persisted);
        Assert.AreEqual(GetManagedPath(second), persisted[0]);
        Assert.AreEqual(GetManagedPath(first), persisted[1]);
        var globalConfiguration = await File.ReadAllTextAsync(
            Path.Combine(_temporaryDirectory!, ".gitconfig"),
            TestContext.Current.CancellationToken);
        StringAssert.Contains(globalConfiguration, "recentrepo");
        Assert.IsFalse(globalConfiguration.Contains("recentRepositories", StringComparison.OrdinalIgnoreCase));
        await service.RemoveAsync(recorded[0], TestContext.Current.CancellationToken);
        var remaining = await service.LoadAsync(TestContext.Current.CancellationToken);
        Assert.HasCount(1, remaining);
        Assert.AreEqual(GetManagedPath(second), GetManagedPath(remaining[0]));
    }

    private RepositoryManagementService CreateService(IChildProcessRunner runner)
        => new(
            _installation!,
            runner,
            new GitChildEnvironmentFactory(_processEnvironment!),
            new CredentialPromptBroker(new TestCredentialPromptResponder()),
            CanonicalDirectory.Create(_temporaryDirectory!));

    private TestProcessEnvironment CreateProcessEnvironment()
    {
        var values = new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory,
            ["USERPROFILE"] = _temporaryDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory!, "xdg-config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "protocol.file.allow",
            ["GIT_CONFIG_VALUE_0"] = "always",
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        };
        return new TestProcessEnvironment(values);
    }

    private async Task<string> CreateRepositoryAsync(string name)
    {
        var path = Path.Combine(_temporaryDirectory!, name);
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            path);
        await File.WriteAllTextAsync(
            Path.Combine(path, "tracked.txt"),
            "content\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(path, "add", "--", "tracked.txt");
        await CommitAsync(path, "initial");
        return path;
    }

    private Task<ProcessResult> CommitAsync(string repositoryPath, string message)
        => RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "--no-gpg-sign",
            "--message",
            message);

    private async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitAsync(workingDirectory, arguments);
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private async Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            new GitChildEnvironmentFactory(_processEnvironment!).CreateCheckoutEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(16 * 1024 * 1024, 16 * 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return result;
    }

    private static string GetManagedPath(CanonicalDirectory directory)
        => directory.Kind == NativePathKind.WindowsUtf16
            ? directory.GetWindowsPath()
            : Encoding.UTF8.GetString(directory.GetUnixBytes());

    private static string GetManagedPath(GitPath path)
        => path.Kind == NativePathKind.WindowsUtf16
            ? path.GetWindowsPath()
            : Encoding.UTF8.GetString(path.GetUnixBytes());
}
