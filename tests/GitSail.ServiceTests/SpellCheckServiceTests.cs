using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies bounded trusted GNU Aspell integration and strict pipe-protocol parsing.
/// </summary>
[TestClass]
public sealed class SpellCheckServiceTests
{
    private static readonly string[] s_expectedSuggestions = ["the", "tech"];
    private static readonly string[] s_expectedArguments =
        ["--encoding=utf-8", "--mode=none", "--master=en_US", "pipe"];

    /// <summary>
    /// Verifies Unicode offsets, suggestions, dictionary selection, and signed-off-by exclusion.
    /// </summary>
    [TestMethod]
    public async Task CheckAsync_WithValidPipeResponse_ReturnsExactDocumentIssues()
    {
        const string message =
            "Fix teh parser\n" +
            "Signed-off-by: Example <person@example.invalid>\n" +
            "emoji 😀 wierd";
        const string output =
            "@(#) International Ispell Version 3.1.20 (but really Aspell 0.60.8.2)\n" +
            "*\n" +
            "& teh 2 5: the, tech\n" +
            "*\n" +
            "\n" +
            "*\n" +
            "& wierd 2 9: weird, wired\n" +
            "\n";
        var runner = CreateRunner(output);
        var service = CreateService(runner);

        var result = await service.CheckAsync(
            CanonicalDirectory.Create(Path.GetTempPath()),
            message,
            documentVersion: 42,
            dictionary: "en_US",
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(42, result.DocumentVersion);
        Assert.AreEqual("en_US", result.Dictionary);
        StringAssert.Contains(result.CheckerVersion, "Aspell 0.60.8.2", StringComparison.Ordinal);
        Assert.HasCount(2, result.Issues);
        Assert.AreEqual(message.IndexOf("teh", StringComparison.Ordinal), result.Issues[0].Offset);
        Assert.AreEqual("teh", result.Issues[0].Word);
        CollectionAssert.AreEqual(s_expectedSuggestions, result.Issues[0].Suggestions);
        Assert.AreEqual(message.IndexOf("wierd", StringComparison.Ordinal), result.Issues[1].Offset);
        Assert.AreEqual("wierd", result.Issues[1].Word);
        Assert.IsNotNull(runner.Invocation);
        CollectionAssert.AreEqual(
            s_expectedArguments,
            runner.Invocation.Arguments.Select(static argument => argument.ToString()).ToArray());
        var input = Encoding.UTF8.GetString(runner.Invocation.StandardInput.GetBytes().Span);
        Assert.AreEqual("^Fix teh parser\n^emoji 😀 wierd\n", input);
        Assert.AreEqual(4 * 1024 * 1024, runner.Invocation.OutputPolicy.MaximumStandardOutputBytes);
        Assert.AreEqual(64 * 1024, runner.Invocation.OutputPolicy.MaximumStandardErrorBytes);
    }

    /// <summary>
    /// Verifies an incompatible banner becomes a visible optional-feature failure.
    /// </summary>
    [TestMethod]
    public async Task CheckAsync_WithUnsupportedVersion_ThrowsActionableFailure()
    {
        var runner = CreateRunner("@(#) Aspell 0.50.0\n*\n\n");
        var service = CreateService(runner);

        var exception = await Assert.ThrowsExactlyAsync<SpellCheckException>(() => service.CheckAsync(
            CanonicalDirectory.Create(Path.GetTempPath()),
            "message",
            documentVersion: 1,
            dictionary: string.Empty,
            TestContext.Current!.CancellationToken));

        StringAssert.Contains(exception.Message, "version banner", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies cancellation is forwarded to the child boundary without translation into a spell failure.
    /// </summary>
    [TestMethod]
    public async Task CheckAsync_WithCancellation_PropagatesCancellation()
    {
        var runner = new StubChildProcessRunner
        {
            Handler = static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            },
        };
        var service = CreateService(runner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.CheckAsync(
            CanonicalDirectory.Create(Path.GetTempPath()),
            "message",
            documentVersion: 1,
            dictionary: string.Empty,
            cancellation.Token));
    }

    private static StubChildProcessRunner CreateRunner(string standardOutput)
        => new()
        {
            Handler = (_, _) => Task.FromResult(new ProcessResult(
                ExitCode: 0,
                Encoding.UTF8.GetBytes(standardOutput),
                ReadOnlyMemory<byte>.Empty,
                TimeSpan.FromMilliseconds(1))),
        };

    private static SpellCheckService CreateService(IChildProcessRunner runner)
        => new(
            new ResolvedExecutable(
                ProgramKind.Aspell,
                OperatingSystem.IsWindows() ? "C:\\tools\\aspell.exe" : "/usr/bin/aspell",
                new ExecutableFingerprint(1, 1)),
            runner,
            ChildEnvironment.Create([]));
}
