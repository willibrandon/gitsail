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
            Directory.Delete(_temporaryDirectory, recursive: true);
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

    private static ExecutableResolver CreateResolver(string searchPath)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = searchPath,
        };
        return new ExecutableResolver(new TestProcessEnvironment(variables));
    }

    private string CreateGitExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";
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
