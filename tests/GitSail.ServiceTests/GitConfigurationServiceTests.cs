using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies configuration provenance loading against an isolated real Git installation.
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
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies global, local, and command values retain exact precedence, origins, and bytes.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_WithIsolatedScopes_ReturnsOrderedProvenance()
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
