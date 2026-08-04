using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies platform user configuration and cache path precedence without ambient environment reads.
/// </summary>
[TestClass]
public sealed class UserDirectoryPathServiceTests
{
    /// <summary>
    /// Verifies explicit platform roots produce fully qualified application-owned directories.
    /// </summary>
    [TestMethod]
    public void GetDirectories_WithPlatformRoots_ReturnsExpectedApplicationPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"gitsail-user-paths-{Guid.NewGuid():N}");
        var environment = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = Path.Combine(root, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "xdg-config"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "xdg-cache"),
            ["APPDATA"] = Path.Combine(root, "roaming"),
            ["LOCALAPPDATA"] = Path.Combine(root, "local"),
        });
        var service = new UserDirectoryPathService(environment);

        var configuration = service.GetConfigurationDirectory();
        var cache = service.GetCacheDirectory();

        Assert.IsTrue(Path.IsPathFullyQualified(configuration));
        Assert.IsTrue(Path.IsPathFullyQualified(cache));
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(Path.Combine(root, "roaming", "gitsail"), configuration);
            Assert.AreEqual(Path.Combine(root, "local", "gitsail", "cache"), cache);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.AreEqual(
                Path.Combine(root, "home", "Library", "Application Support", "gitsail"),
                configuration);
            Assert.AreEqual(
                Path.Combine(root, "home", "Library", "Caches", "gitsail"),
                cache);
        }
        else
        {
            Assert.AreEqual(Path.Combine(root, "xdg-config", "gitsail"), configuration);
            Assert.AreEqual(Path.Combine(root, "xdg-cache", "gitsail"), cache);
        }
    }

    /// <summary>
    /// Verifies relative XDG overrides are ignored in favor of absolute HOME fallbacks.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void GetDirectories_WithRelativeXdgRoots_UsesHomeFallbacks()
    {
        var home = Path.Combine(Path.GetTempPath(), $"gitsail-user-home-{Guid.NewGuid():N}");
        var environment = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = home,
            ["XDG_CONFIG_HOME"] = "relative-config",
            ["XDG_CACHE_HOME"] = "relative-cache",
        });
        var service = new UserDirectoryPathService(environment);

        Assert.AreEqual(Path.Combine(home, ".config", "gitsail"), service.GetConfigurationDirectory());
        Assert.AreEqual(Path.Combine(home, ".cache", "gitsail"), service.GetCacheDirectory());
    }
}
