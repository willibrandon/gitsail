using GitSail.CommandLine;
using GitSail.Diagnostics;
using GitSail.Git.Execution;
using System.Text;
using System.Text.Json;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies bounded private trace storage and secret-free child-process diagnostics.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TraceSessionTests
{
    private static readonly string[] s_expectedEvents =
    [
        "trace.started",
        "application.started",
        "child.started",
        "child.completed",
        "application.completed",
    ];

    /// <summary>
    /// Verifies trace JSON has the stable schema while child secrets and stream content remain absent.
    /// </summary>
    [TestMethod]
    public async Task Trace_WithChildInvocation_WritesStableSecretFreeEvents()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"gitsail-trace-{Guid.NewGuid():N}.jsonl");
        const string argumentSecret = "argument-secret-77a7";
        const string environmentSecret = "environment-secret-10b2";
        var invocation = CreateInvocation(argumentSecret, environmentSecret);
        try
        {
            using (var trace = TraceSession.Create(
                new TraceOptions(tracePath),
                new RuntimeProcessEnvironment(),
                TimeProvider.System))
            using (ApplicationTrace.Begin(trace))
            {
                trace.WriteApplicationStarted(ApplicationMode.Gui);
                var result = await new ChildProcessRunner().RunAsync(
                    invocation,
                    TestContext.Current!.CancellationToken);
                Assert.AreEqual(0, result.ExitCode);
                trace.WriteApplicationCompleted(ExitCodes.Success);
            }

            var contents = await File.ReadAllTextAsync(tracePath);
            Assert.IsFalse(contents.Contains(argumentSecret, StringComparison.Ordinal));
            Assert.IsFalse(contents.Contains(environmentSecret, StringComparison.Ordinal));
            Assert.IsFalse(contents.Contains("git version", StringComparison.Ordinal));
            var lines = contents.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.HasCount(5, lines);
            var events = new List<string>();
            long expectedSequence = 1;
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.AreEqual(expectedSequence++, root.GetProperty("sequence").GetInt64());
                Assert.IsLessThanOrEqualTo(
                    DateTimeOffset.UtcNow,
                    root.GetProperty("timestampUtc").GetDateTimeOffset());
                events.Add(root.GetProperty("event").GetString()!);
            }

            CollectionAssert.AreEqual(s_expectedEvents, events);
            if (!OperatingSystem.IsWindows())
            {
                Assert.AreEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(tracePath));
            }
        }
        finally
        {
            File.Delete(tracePath);
        }
    }

    /// <summary>
    /// Verifies an explicit trace never overwrites an existing file.
    /// </summary>
    [TestMethod]
    public void Create_WithExistingExplicitPath_RejectsOverwrite()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"gitsail-trace-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(tracePath, "keep", Encoding.UTF8);
        try
        {
            _ = Assert.Throws<IOException>(() => TraceSession.Create(
                new TraceOptions(tracePath),
                new RuntimeProcessEnvironment(),
                TimeProvider.System));

            Assert.AreEqual("keep", File.ReadAllText(tracePath, Encoding.UTF8));
        }
        finally
        {
            File.Delete(tracePath);
        }
    }

    /// <summary>
    /// Verifies an explicit Unix trace refuses a dangling final symbolic link instead of creating its target.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void Create_WithDanglingSymbolicLink_RejectsLinkWithoutCreatingTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"gitsail-trace-link-{Guid.NewGuid():N}");
        var targetPath = Path.Combine(root, "target.jsonl");
        var linkPath = Path.Combine(root, "trace.jsonl");
        Directory.CreateDirectory(root);
        _ = File.CreateSymbolicLink(linkPath, targetPath);
        try
        {
            _ = Assert.Throws<IOException>(() => TraceSession.Create(
                new TraceOptions(linkPath),
                new RuntimeProcessEnvironment(),
                TimeProvider.System));

            Assert.IsFalse(File.Exists(targetPath));
        }
        finally
        {
            File.Delete(linkPath);
            File.Delete(targetPath);
            Directory.Delete(root);
        }
    }

    private static ProcessInvocation CreateInvocation(
        string argumentSecret,
        string environmentSecret)
    {
        var executable = new ExecutableResolver(new RuntimeProcessEnvironment())
            .Resolve(ProgramKind.Git);
        var environment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
            new KeyValuePair<string, string>("GIT_PAGER", "cat"),
            new KeyValuePair<string, string>("GIT_OPTIONAL_LOCKS", "0"),
            new KeyValuePair<string, string>("GITSAIL_TEST_SECRET", environmentSecret),
        ]);
        return new ProcessInvocation(
            executable,
            [
                ProcessArgument.Literal("-c"),
                ProcessArgument.Literal($"gitsail.test-secret={argumentSecret}"),
                ProcessArgument.Literal("--version"),
            ],
            CanonicalDirectory.Create(Path.GetTempPath()),
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));
    }
}
