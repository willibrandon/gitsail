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
}
