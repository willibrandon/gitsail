using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies reviewed configured tools use the fixed shell and exact native path environment.
/// </summary>
[TestClass]
public sealed class ConfiguredToolServiceTests
{
    private const string RepositoryId =
        "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    /// <summary>
    /// Verifies approval launches the fixed shell and preserves the focused native path value.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithAllowOnce_PreservesFocusedPathAndCompletes()
    {
        var environment = CreateEnvironment();
        var resolver = new ExecutableResolver(environment);
        var shell = resolver.Resolve(ProgramKind.Shell);
        var configuration = new GitConfigurationSnapshot([]);
        var store = new ExecutableCapabilityGrantStore(
            RepositoryId,
            configuration,
            static (_, _, _) => throw new InvalidOperationException(
                "A one-time decision must not persist configuration."));
        using var prompts = new ExecutableCapabilityCoordinator();
        var service = new ConfiguredToolService(
            new ChildProcessRunner(),
            new GitChildEnvironmentFactory(environment),
            new RepositoryMutationCoordinator(),
            new ExecutableConfigurationBroker(store, prompts),
            shell);
        var path = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("focused path.txt")
            : GitPath.FromUnixBytes([.. "focused-"u8, 0x80]);
        var tool = CreateTool(OperatingSystem.IsWindows()
            ? "echo(%FILENAME%"
            : "test \"$0\" = /bin/sh && printf '%s' \"$FILENAME\"");
        var run = service.RunAsync(
            CanonicalDirectory.Create(Path.GetTempPath()),
            tool,
            new ConfiguredToolInvocation(
                path,
                [path],
                RefName.FromBytes("main"u8),
                arguments: "review",
                revision: "HEAD"),
            TestContext.Current!.CancellationToken);
        var prompt = await WaitForPromptAsync(prompts, TestContext.Current.CancellationToken);

        Assert.IsTrue(prompts.Decide(
            prompt.Id,
            ExecutableCapabilityDecision.AllowOnce));
        var result = await run;

        Assert.AreEqual(ConfiguredToolOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(0, result.ExitCode);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                "focused path.txt",
                Encoding.UTF8.GetString(result.StandardOutput.Span).TrimEnd('\r', '\n'));
        }
        else
        {
            CollectionAssert.AreEqual(path.GetUnixBytes().ToArray(), result.StandardOutput.ToArray());
        }
    }

    /// <summary>
    /// Verifies denial returns without launching the configured shell command.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithDeny_DoesNotLaunchConfiguredCommand()
    {
        var environment = CreateEnvironment();
        var resolver = new ExecutableResolver(environment);
        var shell = resolver.Resolve(ProgramKind.Shell);
        var store = new ExecutableCapabilityGrantStore(
            RepositoryId,
            new GitConfigurationSnapshot([]),
            static (_, _, _) => throw new InvalidOperationException(
                "A denied decision must not persist configuration."));
        using var prompts = new ExecutableCapabilityCoordinator();
        var service = new ConfiguredToolService(
            new ChildProcessRunner(),
            new GitChildEnvironmentFactory(environment),
            new RepositoryMutationCoordinator(),
            new ExecutableConfigurationBroker(store, prompts),
            shell);
        var run = service.RunAsync(
            CanonicalDirectory.Create(Path.GetTempPath()),
            CreateTool("exit 19"),
            new ConfiguredToolInvocation(null, [], null, null, null),
            TestContext.Current!.CancellationToken);
        var prompt = await WaitForPromptAsync(prompts, TestContext.Current.CancellationToken);

        Assert.IsTrue(prompts.Cancel(prompt.Id));
        var result = await run;

        Assert.AreEqual(ConfiguredToolOutcome.Denied, result.Outcome);
        Assert.IsNull(result.ExitCode);
        Assert.IsTrue(result.StandardOutput.IsEmpty);
        Assert.IsTrue(result.StandardError.IsEmpty);
    }

    private static ConfiguredToolDefinition CreateTool(string command)
        => new(
            "review",
            "Review",
            "guitool.review.cmd",
            command,
            GitConfigurationScope.Local,
            GitConfigurationOrigin.FromBytes("file:.git/config"u8),
            Prompt: null,
            ArgumentPrompt: null,
            RevisionPrompt: null,
            NoConsole: false,
            NeedsFile: false,
            Confirm: false,
            RevisionUnmerged: false,
            NoRescan: false,
            UnavailableReason: null);

    private static TestProcessEnvironment CreateEnvironment()
        => new(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["HOME"] = Path.GetTempPath(),
            ["USERPROFILE"] = Path.GetTempPath(),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
            ["COMSPEC"] = Environment.GetEnvironmentVariable("COMSPEC"),
        });

    private static async Task<ExecutableCapabilityPrompt> WaitForPromptAsync(
        ExecutableCapabilityCoordinator prompts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (prompts.Current is { } prompt)
            {
                return prompt;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        Assert.Fail("The executable capability prompt did not become current.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }
}
