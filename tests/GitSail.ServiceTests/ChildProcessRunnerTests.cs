using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies the shell-free typed child-process boundary against Git.
/// </summary>
[TestClass]
public sealed class ChildProcessRunnerTests
{
    /// <summary>
    /// Verifies that Git version output is captured as exact standard-output bytes.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithGitVersion_CapturesSuccessfulOutput()
    {
        var invocation = CreateInvocation("--version");
        var runner = new ChildProcessRunner();

        var result = await runner.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.StartsWith(Encoding.UTF8.GetString(result.StandardOutput.Span), "git version ");
        Assert.AreEqual(0, result.StandardError.Length);
    }

    /// <summary>
    /// Verifies that shell operators remain one literal Git argument and cannot create a file.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithShellOperatorInArgument_DoesNotInvokeShell()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"gitsail-shell-marker-{Guid.NewGuid():N}");
        var invocation = CreateInvocation($"--version;touch {marker}");
        var runner = new ChildProcessRunner();

        var result = await runner.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsFalse(File.Exists(marker));
    }

    /// <summary>
    /// Verifies that a spooling policy returns exact output through owned file-backed storage.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithSpoolingPolicy_ReturnsExactOwnedSpool()
    {
        var invocation = CreateInvocation("--version") with
        {
            OutputPolicy = OutputPolicy.CreateSpooling(
                memoryThresholdBytes: 1,
                maximumStandardOutputBytes: 1024 * 1024,
                maximumStandardErrorBytes: 1024 * 1024),
        };
        var runner = new ChildProcessRunner();

        var result = await runner.RunAsync(invocation, TestContext.Current!.CancellationToken);
        using var spool = result.StandardOutputSpool;

        Assert.IsNotNull(spool);
        Assert.IsTrue(spool.IsFileBacked);
        Assert.AreEqual(0, result.StandardOutput.Length);
        var output = await spool.ReadSliceAsync(
            0,
            checked((int)spool.Length),
            TestContext.Current.CancellationToken);
        StringAssert.StartsWith(Encoding.UTF8.GetString(output), "git version ");
    }

    private static ProcessInvocation CreateInvocation(params string[] arguments)
    {
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        var executable = resolver.Resolve(ProgramKind.Git);
        var childEnvironment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
            new KeyValuePair<string, string>("GIT_PAGER", "cat"),
            new KeyValuePair<string, string>("GIT_OPTIONAL_LOCKS", "0"),
        ]);
        return new ProcessInvocation(
            executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(Path.GetTempPath()),
            childEnvironment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));
    }
}
