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
}
