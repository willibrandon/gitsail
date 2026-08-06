using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies trusted executable resolution and replacement detection.
/// </summary>
[TestClass]
public sealed class ExecutableResolverTests
{
    private string? _temporaryDirectory;

    /// <summary>
    /// Creates an isolated executable directory for each test.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>
    /// Removes the isolated executable directory after each test.
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
    /// Verifies that resolution returns the canonical executable from an absolute path entry.
    /// </summary>
    [TestMethod]
    public void Resolve_WithAbsoluteExecutable_ReturnsCanonicalExecutable()
    {
        var executablePath = CreateGitExecutable();
        var resolver = CreateResolver(_temporaryDirectory!);

        var executable = resolver.Resolve(ProgramKind.Git);

        Assert.AreEqual(Path.GetFullPath(executablePath), executable.Path);
        Assert.AreEqual(ProgramKind.Git, executable.Kind);
        Assert.IsTrue(ExecutableResolver.IsUnchanged(executable));
    }

    /// <summary>
    /// Verifies that empty and relative path entries cannot select a current-directory executable.
    /// </summary>
    [TestMethod]
    public void Resolve_WithOnlyUnsafePathEntries_RejectsCurrentDirectoryExecutable()
    {
        CreateGitExecutable();
        var searchPath = string.Join(Path.PathSeparator, string.Empty, ".", "relative/bin");
        var resolver = CreateResolver(searchPath);

        var exception = Assert.ThrowsExactly<ExecutableResolutionException>(
            () => resolver.Resolve(ProgramKind.Git));

        StringAssert.Contains(exception.Message, "absolute executable directory", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that changing a resolved executable invalidates its captured fingerprint.
    /// </summary>
    [TestMethod]
    public void IsUnchanged_AfterExecutableReplacement_ReturnsFalse()
    {
        var executablePath = CreateGitExecutable();
        var resolver = CreateResolver(_temporaryDirectory!);
        var executable = resolver.Resolve(ProgramKind.Git);

        File.AppendAllText(executablePath, Environment.NewLine + "replacement");

        Assert.IsFalse(ExecutableResolver.IsUnchanged(executable));
    }

    /// <summary>
    /// Verifies the optional spell checker uses only its exact platform executable name.
    /// </summary>
    [TestMethod]
    public void Resolve_WithAspellExecutable_ReturnsTrustedOptionalTool()
    {
        var executablePath = CreateExecutable("aspell");
        var resolver = CreateResolver(_temporaryDirectory!);

        var executable = resolver.Resolve(ProgramKind.Aspell);

        Assert.AreEqual(Path.GetFullPath(executablePath), executable.Path);
        Assert.AreEqual(ProgramKind.Aspell, executable.Kind);
    }

    /// <summary>
    /// Verifies clipboard integration resolves only a supported platform helper.
    /// </summary>
    [TestMethod]
    public void Resolve_WithClipboardExecutable_ReturnsTrustedOptionalTool()
    {
        var helperName = OperatingSystem.IsWindows()
            ? "clip"
            : OperatingSystem.IsMacOS()
                ? "pbcopy"
                : "wl-copy";
        var executablePath = CreateExecutable(helperName);
        var resolver = CreateResolver(_temporaryDirectory!);

        var executable = resolver.Resolve(ProgramKind.Clipboard);

        Assert.AreEqual(Path.GetFullPath(executablePath), executable.Path);
        Assert.AreEqual(ProgramKind.Clipboard, executable.Kind);
    }

    /// <summary>
    /// Verifies SSH key creation resolves only the exact platform executable name from an absolute path entry.
    /// </summary>
    [TestMethod]
    public void Resolve_WithSshKeygenExecutable_ReturnsTrustedOptionalTool()
    {
        var executablePath = CreateExecutable("ssh-keygen");
        var resolver = CreateResolver(_temporaryDirectory!);

        var executable = resolver.Resolve(ProgramKind.SshKeygen);

        Assert.AreEqual(Path.GetFullPath(executablePath), executable.Path);
        Assert.AreEqual(ProgramKind.SshKeygen, executable.Kind);
    }

    /// <summary>
    /// Verifies configured commands use the fixed operating-system shell instead of PATH selection.
    /// </summary>
    [TestMethod]
    public void Resolve_WithPlatformShell_IgnoresSearchPathShells()
    {
        if (OperatingSystem.IsWindows())
        {
            var systemDirectory = Path.Combine(_temporaryDirectory!, "System32");
            Directory.CreateDirectory(systemDirectory);
            File.Copy(Environment.ProcessPath!, Path.Combine(systemDirectory, "cmd.exe"));
            var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PATH"] = _temporaryDirectory,
                ["SystemRoot"] = _temporaryDirectory,
            };
            var executable = new ExecutableResolver(
                new TestProcessEnvironment(variables)).Resolve(ProgramKind.Shell);

            Assert.AreEqual(Path.Combine(systemDirectory, "cmd.exe"), executable.Path);
            Assert.AreEqual(ProgramKind.Shell, executable.Kind);
            return;
        }

        _ = CreateExecutable("sh");
        var resolver = CreateResolver(_temporaryDirectory!);

        var shell = resolver.Resolve(ProgramKind.Shell);
        var shellInformation = new FileInfo("/bin/sh");
        var shellTarget = shellInformation.ResolveLinkTarget(returnFinalTarget: true);

        Assert.AreEqual(
            Path.GetFullPath(shellTarget?.FullName ?? shellInformation.FullName),
            shell.Path);
        Assert.AreEqual(ProgramKind.Shell, shell.Kind);
    }

    private static ExecutableResolver CreateResolver(string searchPath)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = searchPath,
        };
        return new ExecutableResolver(new TestProcessEnvironment(variables));
    }

    private string CreateGitExecutable()
        => CreateExecutable("git");

    private string CreateExecutable(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        var path = Path.Combine(_temporaryDirectory!, fileName);
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
