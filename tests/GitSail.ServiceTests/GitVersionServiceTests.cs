using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies installed Git discovery through the typed child-process boundary.
/// </summary>
[TestClass]
public sealed class GitVersionServiceTests
{
    /// <summary>
    /// Verifies that the installed Git executable is canonical, unchanged, and versioned.
    /// </summary>
    [TestMethod]
    public async Task GetAsync_WithInstalledGit_ReturnsInstallation()
    {
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        var service = new GitVersionService(resolver, new ChildProcessRunner());

        var installation = await service.GetAsync(
            CanonicalDirectory.Create(Path.GetTempPath()),
            TestContext.Current!.CancellationToken);

        Assert.IsTrue(Path.IsPathFullyQualified(installation.Executable.Path));
        Assert.IsTrue(ExecutableResolver.IsUnchanged(installation.Executable));
        Assert.IsGreaterThanOrEqualTo(2, installation.Version.Major);
    }

    /// <summary>
    /// Verifies ordinary discovery rejects Git below 2.36 before any workflow starts.
    /// Reports both the installed version and the exact supported floor.
    /// </summary>
    [TestMethod]
    public async Task GetAsync_WithUnsupportedGitVersion_ThrowsActionableError()
    {
        var runner = CreateVersionRunner("git version 2.35.8\n");
        var service = new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            runner);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            service.GetAsync(
                CanonicalDirectory.Create(Path.GetTempPath()),
                TestContext.Current!.CancellationToken));

        StringAssert.Contains(exception.Message, "Git 2.35.8 is installed");
        StringAssert.Contains(exception.Message, "Git 2.36.0 or newer");
    }

    /// <summary>
    /// Verifies Doctor can report an installed Git below the supported floor precisely.
    /// Keeps diagnostics available without authorizing ordinary workflows on that version.
    /// </summary>
    [TestMethod]
    public async Task GetForDiagnosticsAsync_WithUnsupportedGitVersion_ReturnsInstallation()
    {
        var runner = CreateVersionRunner("git version 2.35.8\n");
        var service = new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            runner);

        var installation = await service.GetForDiagnosticsAsync(
            CanonicalDirectory.Create(Path.GetTempPath()),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual("2.35.8", installation.Version.ToString());
        Assert.IsLessThan(0, installation.Version.CompareTo(GitVersion.MinimumSupported));
    }

    private static StubChildProcessRunner CreateVersionRunner(string output)
        => new()
        {
            Handler = (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ProcessResult(
                    0,
                    System.Text.Encoding.UTF8.GetBytes(output),
                    ReadOnlyMemory<byte>.Empty,
                    TimeSpan.Zero));
            },
        };
}
