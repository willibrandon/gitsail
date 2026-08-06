using GitSail.Domain;
using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies SSH key creation keeps secrets at the terminal and protects existing output.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SshKeyCreationServiceTests
{
    private string? _temporaryDirectory;

    /// <summary>
    /// Creates one isolated user profile for each SSH key creation test.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"gitsail-ssh-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>
    /// Removes the isolated user profile and any generated directory after each test.
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
    /// Verifies Ed25519 defaults create the private directory and pass no secret argument.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithEd25519Default_CreatesPrivateDirectoryAndUsesTerminalPrompts()
    {
        var environment = CreateEnvironment();
        var runner = new RecordingTerminalChildProcessRunner();
        var service = CreateService(environment, runner);
        var path = SshKeyCreationService.GetDefaultKeyPath(environment, SshKeyAlgorithm.Ed25519);
        Assert.IsTrue(SshKeyCreationService.TryValidateRequest(
            SshKeyAlgorithm.Ed25519,
            path,
            "developer@example.invalid",
            replaceExisting: false,
            out var request,
            out var error), error);

        var exitCode = await service.RunAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            request,
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.IsNotNull(runner.Invocation);
        Assert.AreEqual(ProgramKind.SshKeygen, runner.Invocation.Executable.Kind);
        CollectionAssert.AreEqual(
            new[]
            {
                "-t",
                "ed25519",
                "-a",
                "100",
                "-f",
                path,
                "-C",
                "developer@example.invalid",
            },
            runner.Invocation.Arguments.Select(static argument => argument.ToString()).ToArray());
        Assert.IsFalse(runner.Invocation.Arguments.Any(static argument => argument.IsLiteral("-N")));
        Assert.IsTrue(runner.Invocation.StandardInput.GetBytes().IsEmpty);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode privateMode = UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute;
            Assert.AreEqual(privateMode, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
    }

    /// <summary>
    /// Verifies existing private output cannot reach ssh-keygen without explicit replacement review.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithExistingOutputAndNoConfirmation_RefusesToLaunch()
    {
        var environment = CreateEnvironment();
        var runner = new RecordingTerminalChildProcessRunner();
        var service = CreateService(environment, runner);
        var path = SshKeyCreationService.GetDefaultKeyPath(environment, SshKeyAlgorithm.Ed25519);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "existing", TestContext.Current!.CancellationToken);
        var request = new SshKeyCreationRequest(
            SshKeyAlgorithm.Ed25519,
            path,
            string.Empty,
            ReplaceExisting: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            request,
            TestContext.Current.CancellationToken));

        Assert.Contains("replacement was not confirmed", exception.Message);
        Assert.IsNull(runner.Invocation);
        Assert.AreEqual("existing", await File.ReadAllTextAsync(
            path,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies confirmed RSA output retains the explicit strength and still leaves overwrite to ssh-keygen.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithConfirmedRsaReplacement_PreservesStrengthAndTerminalReview()
    {
        var environment = CreateEnvironment();
        var runner = new RecordingTerminalChildProcessRunner();
        var service = CreateService(environment, runner);
        var path = SshKeyCreationService.GetDefaultKeyPath(environment, SshKeyAlgorithm.Rsa4096);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync($"{path}.pub", "existing public", TestContext.Current!.CancellationToken);
        var request = new SshKeyCreationRequest(
            SshKeyAlgorithm.Rsa4096,
            path,
            string.Empty,
            ReplaceExisting: true);

        _ = await service.RunAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            request,
            TestContext.Current.CancellationToken);

        Assert.IsNotNull(runner.Invocation);
        CollectionAssert.AreEqual(
            new[] { "-t", "rsa", "-b", "4096", "-a", "100", "-f", path },
            runner.Invocation.Arguments.Select(static argument => argument.ToString()).ToArray());
        Assert.AreEqual(
            "existing public",
            await File.ReadAllTextAsync($"{path}.pub", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies relative paths and multiline public-key comments are rejected before review.
    /// </summary>
    [TestMethod]
    public void TryValidateRequest_WithUnsafeInputs_ReturnsActionableErrors()
    {
        Assert.IsFalse(SshKeyCreationService.TryValidateRequest(
            SshKeyAlgorithm.Ed25519,
            "relative-key",
            string.Empty,
            replaceExisting: false,
            out _,
            out var pathError));
        Assert.IsNotNull(pathError);
        Assert.Contains("fully qualified", pathError);

        Assert.IsFalse(SshKeyCreationService.TryValidateRequest(
            SshKeyAlgorithm.Ed25519,
            Path.Combine(_temporaryDirectory!, "id_ed25519"),
            "line one\nline two",
            replaceExisting: false,
            out _,
            out var commentError));
        Assert.IsNotNull(commentError);
        Assert.Contains("one line", commentError);
    }

    private TestProcessEnvironment CreateEnvironment()
        => new(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME"] = _temporaryDirectory,
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        });

    private static SshKeyCreationService CreateService(
        TestProcessEnvironment environment,
        RecordingTerminalChildProcessRunner runner)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current test process path is unavailable.");
        return new SshKeyCreationService(
            new ResolvedExecutable(
                ProgramKind.SshKeygen,
                executablePath,
                ExecutableFingerprint.Capture(executablePath)),
            runner,
            new GitChildEnvironmentFactory(environment),
            environment);
    }
}
