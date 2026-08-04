using GitSail.CommandLine;
using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies lossless native process argument parsing for direct path operands.
/// </summary>
[TestClass]
public sealed class NativeArgumentReaderTests
{
    /// <summary>
    /// Verifies Linux-style argument parsing retains non-UTF-8 filename bytes after <c>--</c>.
    /// </summary>
    [TestMethod]
    public void ParseUnixPathsAfterDoubleDash_WithNonUtf8Path_RetainsExactBytes()
    {
        byte[] commandLine =
        [
            (byte)'/', (byte)'g', (byte)'i', (byte)'t', (byte)'-', (byte)'t', (byte)'u', (byte)'i', 0,
            (byte)'m', (byte)'e', (byte)'r', (byte)'g', (byte)'e', 0,
            (byte)'-', (byte)'-', 0,
            (byte)'b', (byte)'a', (byte)'d', 0xff, (byte)'.', (byte)'t', (byte)'x', (byte)'t', 0,
        ];

        var paths = NativeArgumentReader.ParseUnixPathsAfterDoubleDash(
            commandLine,
            expectedManagedArgumentCount: 3,
            delimiterIndex: 1);

        Assert.HasCount(1, paths);
        Assert.AreEqual(NativePathKind.UnixBytes, paths[0].Kind);
        CollectionAssert.AreEqual(
            new byte[] { (byte)'b', (byte)'a', (byte)'d', 0xff, (byte)'.', (byte)'t', (byte)'x', (byte)'t' },
            paths[0].GetUnixBytes().ToArray());
    }

    /// <summary>
    /// Verifies a native argument vector shorter than the managed command is rejected instead of guessing path identity.
    /// </summary>
    [TestMethod]
    public void ParseUnixPathsAfterDoubleDash_WithShortNativeVector_ThrowsInvalidDataException()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            NativeArgumentReader.ParseUnixPathsAfterDoubleDash(
                "/git-tui\0--\0"u8,
                expectedManagedArgumentCount: 4,
                delimiterIndex: 0));

        StringAssert.Contains(exception.Message, "shorter", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a framework-dependent host prefix does not shift exact trailing path extraction.
    /// </summary>
    [TestMethod]
    public void ParseUnixPathsAfterDoubleDash_WithDotnetHostPrefix_UsesManagedCommandSuffix()
    {
        var paths = NativeArgumentReader.ParseUnixPathsAfterDoubleDash(
            "/usr/bin/dotnet\0/app/git-tui.dll\0merge\0--\0file.txt\0"u8,
            expectedManagedArgumentCount: 3,
            delimiterIndex: 1);

        Assert.HasCount(1, paths);
        CollectionAssert.AreEqual("file.txt"u8.ToArray(), paths[0].GetUnixBytes().ToArray());
    }

    /// <summary>
    /// Verifies an incomplete native command line is rejected before any path can reach Git.
    /// </summary>
    [TestMethod]
    public void ParseUnixPathsAfterDoubleDash_WithoutFinalNul_ThrowsInvalidDataException()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            NativeArgumentReader.ParseUnixPathsAfterDoubleDash(
                "/git-tui\0merge\0--\0file.txt"u8,
                expectedManagedArgumentCount: 3,
                delimiterIndex: 1));

        StringAssert.Contains(exception.Message, "incomplete", StringComparison.Ordinal);
    }
}
