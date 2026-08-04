using GitSail.Domain;
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

    /// <summary>
    /// Verifies that a legal non-UTF-8 Git reference argument reaches Git as exact Unix bytes.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task RunAsync_WithNativeUnixArgument_RoundTripsExactBytes()
    {
        byte[] referenceName = [.. "refs/heads/native-"u8, 0xff];
        var invocation = CreateInvocation(
            ProcessArgument.Literal("check-ref-format"),
            ProcessArgument.Literal("--normalize"),
            ProcessArgument.FromUnixBytes(referenceName));
        var runner = new ChildProcessRunner();

        var result = await runner.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode);
        byte[] expectedOutput = [.. referenceName, (byte)'\n'];
        CollectionAssert.AreEqual(expectedOutput, result.StandardOutput.ToArray());
        Assert.AreEqual(0, result.StandardError.Length);
    }

    /// <summary>
    /// Verifies that a non-UTF-8 environment value reaches Git without managed transcoding.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task RunAsync_WithNativeUnixEnvironmentValue_RoundTripsExactBytes()
    {
        byte[] configuredValue = [.. "value-"u8, 0xff];
        var environment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
            new KeyValuePair<string, string>("GIT_PAGER", "cat"),
            new KeyValuePair<string, string>("GIT_OPTIONAL_LOCKS", "0"),
            new KeyValuePair<string, string>("GIT_CONFIG_COUNT", "1"),
            new KeyValuePair<string, string>("GIT_CONFIG_KEY_0", "gitsail.raw"),
        ]).SetUnixValue("GIT_CONFIG_VALUE_0", configuredValue);
        var invocation = CreateInvocation(
            ProcessArgument.Literal("config"),
            ProcessArgument.Literal("--get"),
            ProcessArgument.Literal("gitsail.raw")) with
        {
            Environment = environment,
        };
        var runner = new ChildProcessRunner();

        var result = await runner.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode);
        byte[] expectedOutput = [.. configuredValue, (byte)'\n'];
        CollectionAssert.AreEqual(expectedOutput, result.StandardOutput.ToArray());
        Assert.AreEqual(0, result.StandardError.Length);
    }

    /// <summary>
    /// Verifies that Git executes inside a canonical working directory containing non-UTF-8 bytes.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task RunAsync_WithNativeUnixWorkingDirectory_RoundTripsExactBytes()
    {
        var parentPath = Path.Combine(Path.GetTempPath(), $"gitsail-native-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parentPath);
        var parentDirectory = CanonicalDirectory.Create(parentPath);
        var runner = new ChildProcessRunner();
        try
        {
            var parentInitialization = CreateInvocation("init", "--quiet") with
            {
                WorkingDirectory = parentDirectory,
            };
            var initializationResult = await runner.RunAsync(
                parentInitialization,
                TestContext.Current!.CancellationToken);
            Assert.AreEqual(0, initializationResult.ExitCode);

            byte[] childName = [.. "native-"u8, 0xff];
            var childInitialization = CreateInvocation(
                ProcessArgument.Literal("init"),
                ProcessArgument.Literal("--quiet"),
                ProcessArgument.Literal("--"),
                ProcessArgument.FromUnixBytes(childName)) with
            {
                WorkingDirectory = parentDirectory,
            };
            initializationResult = await runner.RunAsync(
                childInitialization,
                TestContext.Current.CancellationToken);
            Assert.AreEqual(
                0,
                initializationResult.ExitCode,
                Encoding.UTF8.GetString(initializationResult.StandardError.Span));

            var childPath = CombineUnixPath(parentDirectory.GetUnixBytes(), childName);
            var childDirectory = CanonicalDirectory.Create(GitPath.FromUnixBytes(childPath));
            var query = CreateInvocation("rev-parse", "--show-toplevel") with
            {
                WorkingDirectory = childDirectory,
            };

            var result = await runner.RunAsync(query, TestContext.Current.CancellationToken);

            Assert.AreEqual(0, result.ExitCode);
            byte[] expectedOutput = [.. childDirectory.GetUnixBytes(), (byte)'\n'];
            CollectionAssert.AreEqual(expectedOutput, result.StandardOutput.ToArray());
            Assert.AreEqual(0, result.StandardError.Length);
        }
        finally
        {
            var cleanup = CreateInvocation("clean", "--force", "--force", "-d", "-x") with
            {
                WorkingDirectory = parentDirectory,
            };
            _ = await runner.RunAsync(cleanup, CancellationToken.None);
            Directory.Delete(parentPath, recursive: true);
        }
    }

    /// <summary>
    /// Verifies Unix cancellation interrupts and reaps a long-running Git child.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task RunAsync_WithCancellation_InterruptsAndReapsUnixChild()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"gs-{Guid.NewGuid():N}"[..20]);
        Directory.CreateDirectory(temporaryDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                temporaryDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var socketPath = Path.Combine(temporaryDirectory, "socket");
        var invocation = CreateInvocation(
            "credential-cache--daemon",
            "--debug",
            socketPath);
        var runner = new ChildProcessRunner();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current!.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));

        try
        {
            _ = await Assert.ThrowsAsync<OperationCanceledException>(
                () => runner.RunAsync(invocation, cancellation.Token));
        }
        finally
        {
            File.Delete(socketPath);
            Directory.Delete(temporaryDirectory);
        }
    }

    private static ProcessInvocation CreateInvocation(params string[] arguments)
        => CreateInvocation([.. arguments.Select(ProcessArgument.Literal)]);

    private static ProcessInvocation CreateInvocation(params ProcessArgument[] arguments)
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
            [.. arguments],
            CanonicalDirectory.Create(Path.GetTempPath()),
            childEnvironment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));
    }

    private static byte[] CombineUnixPath(ReadOnlySpan<byte> parent, ReadOnlySpan<byte> child)
    {
        var separatorLength = parent[^1] == (byte)'/' ? 0 : 1;
        var result = new byte[parent.Length + separatorLength + child.Length];
        parent.CopyTo(result);
        if (separatorLength != 0)
        {
            result[parent.Length] = (byte)'/';
        }

        child.CopyTo(result.AsSpan(parent.Length + separatorLength));
        return result;
    }
}
