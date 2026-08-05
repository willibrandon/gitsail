using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.SecurityTests;

/// <summary>
/// Verifies exact cross-platform NUL-delimited pathspec input construction.
/// </summary>
[TestClass]
public sealed class PathspecInputBuilderTests
{
    /// <summary>
    /// Verifies that spaces, option prefixes, and record boundaries are retained literally.
    /// </summary>
    [TestMethod]
    public void Build_WithPlatformPaths_ReturnsExactNulDelimitedBytes()
    {
        GitPath[] paths = OperatingSystem.IsWindows()
            ? [GitPath.FromWindowsPath("file with spaces.txt"), GitPath.FromWindowsPath("--option.txt")]
            : [GitPath.FromUnixBytes("file with spaces.txt"u8), GitPath.FromUnixBytes("--option.txt"u8)];

        var result = PathspecInputBuilder.Build(paths);

        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("file with spaces.txt\0--option.txt\0"),
            result);
    }

    /// <summary>
    /// Verifies that invalid UTF-8 Unix filename bytes remain unchanged.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX | OperatingSystems.FreeBSD)]
    public void Build_WithUnixInvalidUtf8_RetainsExactBytes()
    {
        var result = PathspecInputBuilder.Build([GitPath.FromUnixBytes([0x61, 0xff, 0x62])]);

        CollectionAssert.AreEqual(new byte[] { 0x61, 0xff, 0x62, 0x00 }, result);
    }

    /// <summary>
    /// Verifies that an empty mutation selection is rejected before Git starts.
    /// </summary>
    [TestMethod]
    public void Build_WithNoPaths_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PathspecInputBuilder.Build([]));
    }
}
