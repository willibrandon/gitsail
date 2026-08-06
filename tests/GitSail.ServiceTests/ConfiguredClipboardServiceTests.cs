using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies configured clipboard policy at terminal and child-process boundaries.
/// </summary>
[TestClass]
public sealed class ConfiguredClipboardServiceTests
{
    private string? _temporaryDirectory;

    /// <summary>
    /// Creates an isolated executable and working directory for each test.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"gitsail-clipboard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>
    /// Removes the isolated directory after each test.
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
    /// Verifies disabled policy emits neither terminal output nor a child invocation.
    /// </summary>
    [TestMethod]
    public async Task CopyAsync_WithOffPolicy_RejectsWithoutOutput()
    {
        var runner = RejectingRunner();
        var service = CreateService("off", runner, path: _temporaryDirectory!);
        string? terminalText = null;

        var result = await service.CopyAsync(
            "patch",
            ClipboardContentClassification.RepositoryData,
            text => terminalText = text,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Confirmed);
        Assert.IsNull(terminalText);
        Assert.IsNull(runner.Invocation);
        StringAssert.Contains(result.Message, "disabled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies OSC 52 reports that terminal acceptance cannot be confirmed.
    /// </summary>
    [TestMethod]
    public async Task CopyAsync_WithOsc52Policy_EmitsExactTextAndReportsUnconfirmed()
    {
        var service = CreateService("osc52", RejectingRunner(), path: _temporaryDirectory!);
        string? terminalText = null;

        var result = await service.CopyAsync(
            "line one\nline two",
            ClipboardContentClassification.RepositoryData,
            text => terminalText = text,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Confirmed);
        Assert.AreEqual("line one\nline two", terminalText);
        StringAssert.Contains(result.Message, "did not confirm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies helper mode writes exact platform-encoded input without a shell.
    /// </summary>
    [TestMethod]
    public async Task CopyAsync_WithHelperPolicy_UsesResolvedPlatformHelperAndConfirmsSuccess()
    {
        _ = CreateClipboardExecutable();
        var runner = new StubChildProcessRunner
        {
            Handler = (_, _) => Task.FromResult(new ProcessResult(
                ExitCode: 0,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty,
                TimeSpan.FromMilliseconds(1))),
        };
        var service = CreateService("helper", runner, path: _temporaryDirectory!);
        var terminalCalled = false;

        var result = await service.CopyAsync(
            "snowman ☃",
            ClipboardContentClassification.Public,
            _ => terminalCalled = true,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.Confirmed);
        Assert.IsFalse(terminalCalled);
        var invocation = runner.Invocation ?? throw new AssertFailedException(
            "The clipboard helper invocation was not captured.");
        Assert.AreEqual(ProgramKind.Clipboard, invocation.Executable.Kind);
        byte[] expected = OperatingSystem.IsWindows()
            ? [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("snowman ☃")]
            : Encoding.UTF8.GetBytes("snowman ☃");
        CollectionAssert.AreEqual(
            expected,
            invocation.StandardInput.GetBytes().ToArray());
        StringAssert.Contains(result.Message, "Copied", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies automatic policy falls back to an honest terminal request when no helper exists.
    /// </summary>
    [TestMethod]
    public async Task CopyAsync_WithAutoPolicyAndNoHelper_FallsBackToOsc52()
    {
        var service = CreateService("auto", RejectingRunner(), path: _temporaryDirectory!);
        string? terminalText = null;

        var result = await service.CopyAsync(
            "fallback",
            ClipboardContentClassification.Public,
            text => terminalText = text,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Confirmed);
        Assert.AreEqual("fallback", terminalText);
    }

    /// <summary>
    /// Verifies automatic policy falls back when a resolved helper reports failure.
    /// </summary>
    [TestMethod]
    public async Task CopyAsync_WithAutoPolicyAndFailingHelper_FallsBackToOsc52()
    {
        _ = CreateClipboardExecutable();
        var runner = new StubChildProcessRunner
        {
            Handler = (_, _) => Task.FromResult(new ProcessResult(
                ExitCode: 1,
                ReadOnlyMemory<byte>.Empty,
                "helper error"u8.ToArray(),
                TimeSpan.FromMilliseconds(1))),
        };
        var service = CreateService("auto", runner, path: _temporaryDirectory!);
        string? terminalText = null;

        var result = await service.CopyAsync(
            "fallback after failure",
            ClipboardContentClassification.Public,
            text => terminalText = text,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Confirmed);
        Assert.AreEqual("fallback after failure", terminalText);
        StringAssert.Contains(result.Message, "helper error", StringComparison.Ordinal);
        StringAssert.Contains(result.Message, "did not confirm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies secret-classified text is rejected before configuration or output is consulted.
    /// </summary>
    [TestMethod]
    public async Task CopyAsync_WithSecretClassification_RejectsBeforeOutput()
    {
        var runner = RejectingRunner();
        var service = CreateService("osc52", runner, path: _temporaryDirectory!);
        var terminalCalled = false;

        var result = await service.CopyAsync(
            "credential",
            ClipboardContentClassification.Secret,
            _ => terminalCalled = true,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(terminalCalled);
        Assert.IsNull(runner.Invocation);
        StringAssert.Contains(result.Message, "Secret", StringComparison.Ordinal);
    }

    private ConfiguredClipboardService CreateService(
        string policy,
        IChildProcessRunner runner,
        string path)
    {
        var configuration = new GitConfigurationSnapshot(
        [
            new GitConfigurationEntry(
                GitConfigurationScope.Local,
                GitConfigurationOrigin.FromBytes("file:local"u8),
                GitConfigurationKey.FromBytes("gitsail.clipboard"u8),
                GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(policy))),
        ]);
        var environment = new TestProcessEnvironment(
            new Dictionary<string, string?>
            {
                ["PATH"] = path,
                ["HOME"] = _temporaryDirectory,
                ["USERPROFILE"] = _temporaryDirectory,
            });
        return new ConfiguredClipboardService(
            () => configuration,
            environment,
            runner,
            CanonicalDirectory.Create(_temporaryDirectory!));
    }

    private static StubChildProcessRunner RejectingRunner()
        => new()
        {
            Handler = (_, _) => throw new AssertFailedException(
                "The clipboard helper must not run in this test."),
        };

    private string CreateClipboardExecutable()
    {
        var name = OperatingSystem.IsWindows()
            ? "clip.exe"
            : OperatingSystem.IsMacOS()
                ? "pbcopy"
                : "wl-copy";
        var path = Path.Combine(_temporaryDirectory!, name);
        if (OperatingSystem.IsWindows())
        {
            File.Copy(Environment.ProcessPath!, path);
        }
        else
        {
            File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
