using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies configuration scope and source loading against an isolated real Git installation.
/// </summary>
[TestClass]
public sealed class GitConfigurationServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;

    /// <summary>
    /// Creates an isolated configuration home and resolves Git for each test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
                CanonicalDirectory.Create(_temporaryDirectory),
                TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Removes the isolated repository and configuration home after each test.
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
    /// Verifies global, local, and command values retain exact precedence, origins, and bytes.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_WithIsolatedScopes_ReturnsOrderedSources()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        var globalConfigPath = Path.Combine(_temporaryDirectory!, "global.gitconfig");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        await RunGitAsync(_temporaryDirectory!, "config", "--file", globalConfigPath, "user.name", "Global User");
        await RunGitAsync(repositoryPath, "config", "--local", "gitsail.theme", string.Empty);
        var processEnvironment = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory,
            ["USERPROFILE"] = _temporaryDirectory,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = globalConfigPath,
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "gitsail.commandvalue",
            ["GIT_CONFIG_VALUE_0"] = "first\nsecond",
        });
        var service = new GitConfigurationService(
            _installation!,
            _runner!,
            new GitChildEnvironmentFactory(processEnvironment),
            new GitConfigurationParser());

        var entries = await service.LoadAsync(
            CanonicalDirectory.Create(repositoryPath),
            TestContext.Current!.CancellationToken);

        var global = entries.Single(static entry => entry.Key.DisplayText == "user.name");
        Assert.AreEqual(GitConfigurationScope.Global, global.Scope);
        StringAssert.StartsWith(Encoding.UTF8.GetString(global.Origin.GetBytes()), "file:");
        Assert.AreEqual("Global User", Encoding.UTF8.GetString(global.Value.GetBytes()));
        var local = entries.Single(static entry => entry.Key.DisplayText == "gitsail.theme");
        Assert.AreEqual(GitConfigurationScope.Local, local.Scope);
        Assert.IsTrue(local.Value.IsEmpty);
        var command = entries.Single(static entry => entry.Key.DisplayText == "gitsail.commandvalue");
        Assert.AreEqual(GitConfigurationScope.Command, command.Scope);
        Assert.AreEqual("command line:", Encoding.UTF8.GetString(command.Origin.GetBytes()));
        Assert.AreEqual("first\nsecond", Encoding.UTF8.GetString(command.Value.GetBytes()));
    }

    /// <summary>
    /// Verifies typed global and local writes, multivalue additions, and reset-to-inheritance semantics.
    /// </summary>
    [TestMethod]
    public async Task Mutations_WithRegisteredScopes_RoundTripAndRevealInheritance()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        var globalConfigPath = Path.Combine(_temporaryDirectory!, "global.gitconfig");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        var processEnvironment = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory,
            ["USERPROFILE"] = _temporaryDirectory,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = globalConfigPath,
        });
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new GitConfigurationService(
            _installation!,
            _runner!,
            new GitChildEnvironmentFactory(processEnvironment),
            new GitConfigurationParser(),
            coordinator);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var theme = Key("gitsail.theme");

        await service.SetAsync(
            workingDirectory,
            GitConfigurationScope.Global,
            theme,
            Value("dark"),
            TestContext.Current!.CancellationToken);
        await service.SetAsync(
            workingDirectory,
            GitConfigurationScope.Local,
            theme,
            Value("light"),
            TestContext.Current.CancellationToken);
        await service.AddAsync(
            workingDirectory,
            GitConfigurationScope.Global,
            Key("gui.recentrepo"),
            Value("/first repository"),
            TestContext.Current.CancellationToken);
        await service.AddAsync(
            workingDirectory,
            GitConfigurationScope.Global,
            Key("gui.recentrepo"),
            Value("/second repository"),
            TestContext.Current.CancellationToken);
        await service.AddAsync(
            workingDirectory,
            GitConfigurationScope.Global,
            Key("gui.recentrepo"),
            Value("/second repository"),
            TestContext.Current.CancellationToken);

        var explicitLocal = (await service.LoadSnapshotAsync(
            workingDirectory,
            TestContext.Current.CancellationToken)).Resolve(
                "gitsail.theme",
                GitConfigurationScope.Local);
        Assert.AreEqual(GitConfigurationResolutionState.Explicit, explicitLocal.State);
        Assert.AreEqual("light", explicitLocal.ExplicitParsedValue!.Text);
        await service.UnsetAsync(
            workingDirectory,
            GitConfigurationScope.Local,
            theme,
            TestContext.Current.CancellationToken);

        var reset = await service.LoadSnapshotAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);
        var inherited = reset.Resolve("gitsail.theme", GitConfigurationScope.Local);
        Assert.AreEqual(GitConfigurationResolutionState.Inherited, inherited.State);
        Assert.AreEqual("dark", inherited.EffectiveParsedValue!.Text);
        Assert.HasCount(
            3,
            reset.GetExplicitValues("gui.recentrepo", GitConfigurationScope.Global));

        await service.RemoveValueAsync(
            workingDirectory,
            GitConfigurationScope.Global,
            Key("gui.recentrepo"),
            Value("/first repository"),
            TestContext.Current.CancellationToken);
        var afterFirstRemoval = await service.LoadSnapshotAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);
        var remaining = afterFirstRemoval.GetExplicitValues(
            "gui.recentrepo",
            GitConfigurationScope.Global);
        Assert.HasCount(2, remaining);
        Assert.IsTrue(remaining.All(entry => entry.Value.Equals(Value("/second repository"))));

        await service.RemoveValueAsync(
            workingDirectory,
            GitConfigurationScope.Global,
            Key("gui.recentrepo"),
            Value("/second repository"),
            TestContext.Current.CancellationToken);
        var afterDuplicateRemoval = await service.LoadSnapshotAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);
        Assert.IsEmpty(afterDuplicateRemoval.GetExplicitValues(
            "gui.recentrepo",
            GitConfigurationScope.Global));
    }

    /// <summary>
    /// Verifies invalid values, single-value additions, and terminal-inapplicable writes fail before Git execution.
    /// </summary>
    [TestMethod]
    public async Task Mutations_WithInvalidContracts_RejectBeforeWriting()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new GitConfigurationService(
            _installation!,
            _runner!,
            new GitChildEnvironmentFactory(new TestProcessEnvironment(new Dictionary<string, string?>
            {
                ["HOME"] = _temporaryDirectory,
                ["USERPROFILE"] = _temporaryDirectory,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            })),
            new GitConfigurationParser(),
            coordinator);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var cancellationToken = TestContext.Current!.CancellationToken;

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.SetAsync(
            workingDirectory,
            GitConfigurationScope.Local,
            Key("gitsail.theme"),
            Value("fluorescent"),
            cancellationToken));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.AddAsync(
            workingDirectory,
            GitConfigurationScope.Local,
            Key("gitsail.theme"),
            Value("dark"),
            cancellationToken));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RemoveValueAsync(
            workingDirectory,
            GitConfigurationScope.Local,
            Key("gitsail.theme"),
            Value("dark"),
            cancellationToken));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.SetAsync(
            workingDirectory,
            GitConfigurationScope.Local,
            Key("gui.geometry"),
            Value("120x40"),
            cancellationToken));
    }

    /// <summary>
    /// Verifies worktree writes cannot silently alias local scope and use true worktree scope once enabled.
    /// </summary>
    [TestMethod]
    public async Task SetAsync_WithWorktreeScope_RequiresRepositoryExtension()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new GitConfigurationService(
            _installation!,
            _runner!,
            new GitChildEnvironmentFactory(new TestProcessEnvironment(new Dictionary<string, string?>
            {
                ["HOME"] = _temporaryDirectory,
                ["USERPROFILE"] = _temporaryDirectory,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            })),
            new GitConfigurationParser(),
            coordinator);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var cancellationToken = TestContext.Current!.CancellationToken;

        await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.SetAsync(
            workingDirectory,
            GitConfigurationScope.Worktree,
            Key("gitsail.theme"),
            Value("dark"),
            cancellationToken));
        Assert.AreEqual(
            GitConfigurationResolutionState.Absent,
            (await service.LoadSnapshotAsync(workingDirectory, cancellationToken))
                .Resolve("gitsail.theme", GitConfigurationScope.Local)
                .State);

        await RunGitAsync(repositoryPath, "config", "--local", "extensions.worktreeConfig", "true");
        await service.SetAsync(
            workingDirectory,
            GitConfigurationScope.Worktree,
            Key("gitsail.theme"),
            Value("dark"),
            cancellationToken);

        var resolved = (await service.LoadSnapshotAsync(workingDirectory, cancellationToken))
            .Resolve("gitsail.theme", GitConfigurationScope.Worktree);
        Assert.AreEqual(GitConfigurationResolutionState.Explicit, resolved.State);
        Assert.AreEqual(GitConfigurationScope.Worktree, resolved.ExplicitEntry!.Scope);
        Assert.AreEqual("dark", resolved.ExplicitParsedValue!.Text);
    }

    private static GitConfigurationKey Key(string text)
        => GitConfigurationKey.FromBytes(Encoding.UTF8.GetBytes(text));

    private static GitConfigurationValue Value(string text)
        => GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(text));

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
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
    }
}
