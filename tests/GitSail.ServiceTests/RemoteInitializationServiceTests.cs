using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies isolated local and fixed-script SSH bare-repository initialization against real processes.
/// </summary>
[TestClass]
public sealed class RemoteInitializationServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private RepositoryMutationCoordinator? _coordinator;

    /// <summary>
    /// Creates an isolated home and resolves Git for each remote-initialization test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-remote-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _coordinator = new RepositoryMutationCoordinator();
        var processEnvironment = CreateProcessEnvironment(GetSystemPath());
        _installation = await new GitVersionService(
            new ExecutableResolver(processEnvironment),
            _runner).GetAsync(
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
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies a local absolute target is initialized and verified through an isolated canonical git-dir.
    /// </summary>
    [TestMethod]
    public async Task PrepareAndInitializeAsync_WithLocalPath_CreatesExactBareRepository()
    {
        var repositoryPath = await CreateRepositoryAsync("local-working");
        var targetPath = Path.Combine(_temporaryDirectory!, "local remote with spaces.git");
        await RunGitAsync(repositoryPath, "remote", "add", "origin", targetPath);
        var processEnvironment = CreateProcessEnvironment(GetSystemPath());
        var service = CreateService(processEnvironment);
        var remoteService = CreateRemoteService(processEnvironment);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await remoteService.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);

        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            configuredUrlIndex: 0,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(RemoteInitializationKind.Local, plan.Target.Kind);
        var targetParent = CanonicalDirectory.Create(Path.GetDirectoryName(targetPath)!);
        var canonicalParent = OperatingSystem.IsWindows()
            ? targetParent.GetWindowsPath()
            : Encoding.UTF8.GetString(targetParent.GetUnixBytes());
        Assert.AreEqual(
            Path.Combine(canonicalParent, Path.GetFileName(targetPath)),
            plan.Target.LocalPath);
        _ = await service.InitializeAsync(
            workingDirectory,
            plan,
            TestContext.Current.CancellationToken);
        Assert.AreEqual(
            "true",
            (await RunGitForOutputAsync(
                repositoryPath,
                $"--git-dir={targetPath}",
                "rev-parse",
                "--is-bare-repository")).Trim());
        Assert.AreEqual(
            "sha1",
            (await RunGitForOutputAsync(
                repositoryPath,
                $"--git-dir={targetPath}",
                "rev-parse",
                "--show-object-format=storage")).Trim());
    }

    /// <summary>
    /// Verifies an exact target created after confirmation is refused without changing its contents.
    /// </summary>
    [TestMethod]
    public async Task InitializeAsync_WhenLocalTargetAppearsAfterConfirmation_LeavesTargetUntouched()
    {
        var repositoryPath = await CreateRepositoryAsync("local-race-working");
        var targetPath = Path.Combine(_temporaryDirectory!, "appeared-after-confirmation.git");
        await RunGitAsync(repositoryPath, "remote", "add", "origin", targetPath);
        var processEnvironment = CreateProcessEnvironment(GetSystemPath());
        var service = CreateService(processEnvironment);
        var remoteService = CreateRemoteService(processEnvironment);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await remoteService.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            configuredUrlIndex: 0,
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(targetPath);
        var sentinelPath = Path.Combine(targetPath, "sentinel.txt");
        await File.WriteAllTextAsync(
            sentinelPath,
            "retain exactly",
            TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsExactlyAsync<RemoteInitializationException>(
            () => service.InitializeAsync(
                workingDirectory,
                plan,
                TestContext.Current.CancellationToken));

        Assert.AreEqual("retain exactly", await File.ReadAllTextAsync(
            sentinelPath,
            TestContext.Current.CancellationToken));
        Assert.IsFalse(File.Exists(Path.Combine(targetPath, "HEAD")));
    }

    /// <summary>
    /// Verifies the fixed SSH script decodes a separately framed hostile path without shell interpolation.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task PrepareAndInitializeAsync_WithSshTarget_UsesFramedBase64UrlPath()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var executableDirectory = Path.Combine(_temporaryDirectory!, "executables");
        Directory.CreateDirectory(executableDirectory);
        var sshPath = Path.Combine(executableDirectory, "ssh");
        await File.WriteAllTextAsync(
            sshPath,
            "#!/bin/sh\nexec /bin/sh -s\n",
            TestContext.Current!.CancellationToken);
        File.SetUnixFileMode(
            sshPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = string.Join(
            Path.PathSeparator,
            executableDirectory,
            Path.GetDirectoryName(_installation!.Executable.Path),
            "/usr/bin",
            "/bin");
        var processEnvironment = CreateProcessEnvironment(path);
        var repositoryPath = await CreateRepositoryAsync("ssh-working");
        var targetPath = Path.Combine(_temporaryDirectory!, "remote;touch injected.git");
        var uri = new UriBuilder("ssh", "example.invalid")
        {
            Path = targetPath,
        }.Uri.AbsoluteUri;
        await RunGitAsync(repositoryPath, "remote", "add", "origin", uri);
        var service = CreateService(processEnvironment);
        var remoteService = CreateRemoteService(processEnvironment);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await remoteService.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);

        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            configuredUrlIndex: 0,
            TestContext.Current.CancellationToken);
        var result = await service.InitializeAsync(
            workingDirectory,
            plan,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(RemoteInitializationKind.Ssh, plan.Target.Kind);
        Assert.IsTrue(Encoding.UTF8.GetString(result.StandardOutput.Span).EndsWith(
            "GITSAIL_INIT_OK_V1\n",
            StringComparison.Ordinal));
        Assert.IsTrue(Directory.Exists(targetPath));
        Assert.IsFalse(File.Exists(Path.Combine(repositoryPath, "injected.git")));
        Assert.AreEqual(
            "true",
            (await RunGitForOutputAsync(
                repositoryPath,
                $"--git-dir={targetPath}",
                "rev-parse",
                "--is-bare-repository")).Trim());
    }

    private RemoteInitializationService CreateService(IProcessEnvironment processEnvironment)
    {
        var environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
        var credentialPromptBroker = new CredentialPromptBroker(new TestCredentialPromptResponder());
        var remoteService = new RemoteService(
            _installation!,
            _runner!,
            environmentFactory,
            _coordinator!,
            credentialPromptBroker);
        return new RemoteInitializationService(
            _installation!,
            _runner!,
            environmentFactory,
            _coordinator!,
            remoteService,
            new ExecutableResolver(processEnvironment),
            RepositoryObjectFormat.Sha1,
            credentialPromptBroker);
    }

    private RemoteService CreateRemoteService(IProcessEnvironment processEnvironment)
    {
        var credentialPromptBroker = new CredentialPromptBroker(new TestCredentialPromptResponder());
        return new RemoteService(
            _installation!,
            _runner!,
            new GitChildEnvironmentFactory(processEnvironment),
            _coordinator!,
            credentialPromptBroker);
    }

    private TestProcessEnvironment CreateProcessEnvironment(string path)
        => new(new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory!,
            ["USERPROFILE"] = _temporaryDirectory!,
            ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory!, "xdg-config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["PATH"] = path,
        });

    private async Task<string> CreateRepositoryAsync(string name)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, name);
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "--allow-empty",
            "--no-gpg-sign",
            "--message",
            "baseline");
        return repositoryPath;
    }

    private async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitAsync(workingDirectory, arguments);
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] arguments)
        => RunGitAsync(workingDirectory, arguments, expectSuccess: true);

    private async Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        string[] arguments,
        bool expectSuccess)
    {
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!).CreateCheckoutEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(16 * 1024 * 1024, 16 * 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
        if (expectSuccess)
        {
            Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        }

        return result;
    }

    private static string GetSystemPath()
        => Environment.GetEnvironmentVariable("PATH")
            ?? throw new InvalidOperationException("The service-test PATH is unavailable.");
}
