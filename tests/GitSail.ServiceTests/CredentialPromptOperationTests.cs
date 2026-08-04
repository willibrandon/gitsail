using GitSail.Git.Execution;
using GitSail.Testing;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies authenticated one-operation helper environments and bounded response exchange.
/// </summary>
[TestClass]
public sealed class CredentialPromptOperationTests
{
    /// <summary>
    /// Verifies black-box Git launches the current application helper twice and receives exact queued credentials.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task CredentialFill_WithApplicationHelper_UsesAuthenticatedUsernameAndSecretPrompts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var helperPath = Path.Combine(
            repositoryRoot,
            "src",
            "GitSail",
            "bin",
            "Release",
            "net10.0",
            OperatingSystem.IsWindows() ? "git-tui.exe" : "git-tui");
        Assert.IsTrue(File.Exists(helperPath), helperPath);
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"gitsail-credential-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var hostileDirectoryName = OperatingSystem.IsWindows()
                ? "Gït Sail $&'[]; helper"
                : "Gït Sail $&'\"[]; helper";
            var hostileInstallDirectory = Path.Combine(temporaryDirectory, hostileDirectoryName);
            CopyApplicationOutput(Path.GetDirectoryName(helperPath)!, hostileInstallDirectory);
            var hostileHelperPath = Path.Combine(
                hostileInstallDirectory,
                Path.GetFileName(helperPath));
            var responder = new QueuedCredentialPromptResponder("alice", "correct horse");
            var broker = new CredentialPromptBroker(responder, hostileHelperPath);
            await using var operation = broker.StartOperation(
                "Git credential fill",
                TestContext.Current!.CancellationToken);
            var environmentVariables = new Dictionary<string, string>
            {
                ["HOME"] = temporaryDirectory,
                ["USERPROFILE"] = temporaryDirectory,
                ["XDG_CONFIG_HOME"] = Path.Combine(temporaryDirectory, "xdg-config"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["LANG"] = "C",
                ["LC_ALL"] = "C",
                ["GIT_PAGER"] = "cat",
            };
            foreach (var name in new[] { "TMPDIR", "TEMP", "TMP" })
            {
                if (Environment.GetEnvironmentVariable(name) is { } value)
                {
                    environmentVariables[name] = value;
                }
            }

            var baseEnvironment = ChildEnvironment.Create(environmentVariables);
            var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
            var invocation = new ProcessInvocation(
                resolver.Resolve(ProgramKind.Git),
                [
                    ProcessArgument.Literal("--no-pager"),
                    ProcessArgument.Literal("-c"),
                    ProcessArgument.Literal("credential.helper="),
                    ProcessArgument.Literal("credential"),
                    ProcessArgument.Literal("fill"),
                ],
                CanonicalDirectory.Create(repositoryRoot),
                operation.ConfigureEnvironment(baseEnvironment),
                StandardInputSource.FromBytes("protocol=https\nhost=example.invalid\n\n"u8),
                OutputPolicy.Create(64 * 1024, 64 * 1024));

            var result = await new ChildProcessRunner().RunAsync(
                invocation,
                TestContext.Current.CancellationToken);

            Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
            var output = Encoding.UTF8.GetString(result.StandardOutput.Span);
            StringAssert.Contains(output, "username=alice");
            StringAssert.Contains(output, "password=correct horse");
            TestSeq.AreEqual(
                [CredentialPromptKind.Text, CredentialPromptKind.Secret],
                responder.Kinds);
        }
        finally
        {
            TestDirectory.Delete(temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies a helper authenticates to its exact parent operation and receives only response bytes.
    /// </summary>
    [TestMethod]
    public async Task RequestAsync_WithExactOperationEnvironment_AuthenticatesAndReturnsResponse()
    {
        var responder = new RecordingCredentialPromptResponder("correct horse"u8);
        var broker = new CredentialPromptBroker(responder);
        await using var operation = broker.StartOperation(
            "Fetch origin",
            TestContext.Current!.CancellationToken);
        var environment = operation.ConfigureEnvironment(ChildEnvironment.Create([]));
        var variables = new Dictionary<string, string?>();
        foreach (var name in GetHelperVariableNames())
        {
            Assert.IsTrue(environment.TryGetValue(name, out var value), name);
            variables[name] = value;
        }

        using var invocation = CredentialPromptHelperInvocation.Create(
            ["Password for 'https://example.invalid':"],
            new TestProcessEnvironment(variables));
        var response = await CredentialPromptHelperClient.RequestAsync(
            invocation,
            TestContext.Current.CancellationToken);

        Assert.IsNotNull(response);
        try
        {
            Assert.AreEqual("correct horse", Encoding.UTF8.GetString(response));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }

        Assert.AreEqual("Fetch origin", responder.Operation);
        Assert.AreEqual("Password for 'https://example.invalid':", responder.Prompt);
        Assert.AreEqual(CredentialPromptKind.Secret, responder.Kind);
    }

    /// <summary>
    /// Verifies helper configuration supplies the exact current executable and disables ordinary terminal prompts.
    /// </summary>
    [TestMethod]
    public async Task ConfigureEnvironment_WithFreshOperation_InstallsOnlyAuthenticatedAskpassBridge()
    {
        var broker = new CredentialPromptBroker(new TestCredentialPromptResponder());
        await using var operation = broker.StartOperation(
            "Push origin",
            TestContext.Current!.CancellationToken);

        var environment = operation.ConfigureEnvironment(ChildEnvironment.Create(
            new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
            }));

        Assert.IsTrue(environment.TryGetValue("GIT_ASKPASS", out var gitAskPass));
        Assert.IsTrue(environment.TryGetValue("SSH_ASKPASS", out var sshAskPass));
        Assert.AreEqual(gitAskPass, sshAskPass);
        Assert.AreEqual(Path.GetFullPath(Environment.ProcessPath!), gitAskPass);
        Assert.IsTrue(environment.TryGetValue("SSH_ASKPASS_REQUIRE", out var sshRequirement));
        Assert.AreEqual("force", sshRequirement);
        Assert.IsTrue(environment.TryGetValue("GIT_TERMINAL_PROMPT", out var terminalPrompt));
        Assert.AreEqual("0", terminalPrompt);
    }

    private static string[] GetHelperVariableNames()
        =>
        [
            CredentialPromptProtocol.ProtocolVariable,
            CredentialPromptProtocol.KindVariable,
            CredentialPromptProtocol.EndpointVariable,
            CredentialPromptProtocol.SessionVariable,
            CredentialPromptProtocol.NonceVariable,
            CredentialPromptProtocol.ParentProcessVariable,
        ];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GitSail.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("The GitSail repository root was not found from the test output.");
    }

    private static void CopyApplicationOutput(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
            }
        }
    }
}
