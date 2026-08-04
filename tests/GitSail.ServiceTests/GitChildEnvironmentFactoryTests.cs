using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies classified and bounded environment inheritance for Git configuration queries.
/// </summary>
[TestClass]
public sealed class GitChildEnvironmentFactoryTests
{
    /// <summary>
    /// Verifies required configuration values are retained while unrelated secrets are excluded.
    /// </summary>
    [TestMethod]
    public void CreateConfigurationReadEnvironment_WithClassifiedValues_ReturnsIsolatedEnvironment()
    {
        var source = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = "/isolated/home",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "gitsail.theme",
            ["GIT_CONFIG_VALUE_0"] = "dark",
            ["LANG"] = "user-locale",
            ["SECRET_TOKEN"] = "must-not-leak",
        });

        var environment = new GitChildEnvironmentFactory(source).CreateConfigurationReadEnvironment();

        Assert.IsTrue(environment.TryGetValue("HOME", out var home));
        Assert.AreEqual("/isolated/home", home);
        Assert.IsTrue(environment.TryGetValue("GIT_CONFIG_KEY_0", out var key));
        Assert.AreEqual("gitsail.theme", key);
        Assert.IsTrue(environment.TryGetValue("LANG", out var locale));
        Assert.AreEqual("C", locale);
        Assert.IsFalse(environment.TryGetValue("SECRET_TOKEN", out _));
    }

    /// <summary>
    /// Verifies hostile command-configuration counts cannot cause unbounded environment reads.
    /// </summary>
    [TestMethod]
    public void CreateConfigurationReadEnvironment_WithExcessiveCommandCount_ThrowsInvalidDataException()
    {
        var source = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "999999999",
        });

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new GitChildEnvironmentFactory(source).CreateConfigurationReadEnvironment());
    }

    /// <summary>
    /// Verifies startup repository overrides apply only during initial repository discovery.
    /// </summary>
    [TestMethod]
    public void CreateRepositoryEnvironments_WithStartupOverride_PreventsCrossRepositoryBleed()
    {
        var source = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["GIT_DIR"] = "/selected/repository/.git",
            ["GIT_WORK_TREE"] = "/selected/repository",
        });
        var factory = new GitChildEnvironmentFactory(source);

        var discovery = factory.CreateRepositoryDiscoveryEnvironment();
        var repositoryRead = factory.CreateRepositoryReadEnvironment();

        Assert.IsTrue(discovery.TryGetValue("GIT_DIR", out _));
        Assert.IsTrue(discovery.TryGetValue("GIT_WORK_TREE", out _));
        Assert.IsFalse(repositoryRead.TryGetValue("GIT_DIR", out _));
        Assert.IsFalse(repositoryRead.TryGetValue("GIT_WORK_TREE", out _));
    }

    /// <summary>
    /// Verifies commit children retain classified hook and identity inputs without unrelated secrets.
    /// </summary>
    [TestMethod]
    public void CreateCommitEnvironment_WithClassifiedValues_RetainsRequiredInputsOnly()
    {
        var source = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["PATH"] = "/isolated/bin",
            ["TMPDIR"] = "/isolated/temp",
            ["LANG"] = "fr_FR.UTF-8",
            ["GIT_AUTHOR_NAME"] = "Author",
            ["GIT_COMMITTER_EMAIL"] = "committer@example.invalid",
            ["SSH_AUTH_SOCK"] = "/isolated/agent.sock",
            ["SECRET_TOKEN"] = "must-not-leak",
        });

        var environment = new GitChildEnvironmentFactory(source).CreateCommitEnvironment();

        Assert.IsTrue(environment.TryGetValue("PATH", out var path));
        Assert.AreEqual("/isolated/bin", path);
        Assert.IsTrue(environment.TryGetValue("LANG", out var locale));
        Assert.AreEqual("fr_FR.UTF-8", locale);
        Assert.IsTrue(environment.TryGetValue("GIT_AUTHOR_NAME", out _));
        Assert.IsTrue(environment.TryGetValue("GIT_COMMITTER_EMAIL", out _));
        Assert.IsTrue(environment.TryGetValue("SSH_AUTH_SOCK", out _));
        Assert.IsFalse(environment.TryGetValue("SECRET_TOKEN", out _));
    }
}
