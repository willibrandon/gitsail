using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies strict reconstruction of exact paths from Git C-quoted patch headers.
/// </summary>
[TestClass]
public sealed class GitQuotedPathParserTests
{
    /// <summary>
    /// Verifies that ordinary unquoted side paths are parsed without their diff prefixes.
    /// </summary>
    [TestMethod]
    public void ParseDiffHeader_WithPlainPaths_ReturnsExactPaths()
    {
        var (oldPath, newPath) = GitQuotedPathParser.ParseDiffHeader(
            "diff --git a/source.txt b/destination.txt"u8);

        AssertPathEquals("source.txt", oldPath);
        AssertPathEquals("destination.txt", newPath);
    }

    /// <summary>
    /// Verifies that quotes, controls, and octal UTF-8 bytes are decoded exactly.
    /// </summary>
    [TestMethod]
    public void ParseDiffHeader_WithCQuotedPaths_DecodesEscapesExactly()
    {
        var header = Encoding.ASCII.GetBytes(
            "diff --git \"a/line\\n\\\"\\303\\251.txt\" \"b/line\\n\\\"\\303\\251.txt\"");

        var (oldPath, newPath) = GitQuotedPathParser.ParseDiffHeader(header);

        AssertPathEquals("line\n\"é.txt", oldPath);
        AssertPathEquals("line\n\"é.txt", newPath);
    }

    /// <summary>
    /// Verifies that invalid C escapes fail closed instead of changing path identity.
    /// </summary>
    [TestMethod]
    public void ParseDiffHeader_WithUnknownEscape_ThrowsInvalidDataException()
    {
        var header = Encoding.ASCII.GetBytes("diff --git \"a/file\\x.txt\" b/file.txt");

        Assert.ThrowsExactly<InvalidDataException>(
            () => GitQuotedPathParser.ParseDiffHeader(header));
    }

    /// <summary>
    /// Verifies that missing configured side prefixes fail closed.
    /// </summary>
    [TestMethod]
    public void ParseDiffHeader_WithoutSidePrefix_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(
            () => GitQuotedPathParser.ParseDiffHeader("diff --git source.txt b/source.txt"u8));
    }

    private static void AssertPathEquals(string expected, GitPath actual)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(expected, actual.GetWindowsPath());
            return;
        }

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), actual.GetUnixBytes().ToArray());
    }
}
