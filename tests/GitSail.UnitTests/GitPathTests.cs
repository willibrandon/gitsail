using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies lossless Git path storage, comparison, and display sanitization.
/// </summary>
[TestClass]
public sealed class GitPathTests
{
    /// <summary>
    /// Verifies that a Unix path owns an independent copy of its source bytes.
    /// </summary>
    [TestMethod]
    public void FromUnixBytes_AfterSourceChanges_RetainsOriginalBytes()
    {
        byte[] source = [0x66, 0x6f, 0x6f];
        var path = GitPath.FromUnixBytes(source);

        source[0] = 0x62;

        CollectionAssert.AreEqual(new byte[] { 0x66, 0x6f, 0x6f }, path.GetUnixBytes().ToArray());
    }

    /// <summary>
    /// Verifies that embedded NUL is rejected from a Unix path.
    /// </summary>
    [TestMethod]
    public void FromUnixBytes_WithNul_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GitPath.FromUnixBytes([0x61, 0x00, 0x62]));
    }

    /// <summary>
    /// Verifies that embedded NUL is rejected from a Windows path.
    /// </summary>
    [TestMethod]
    public void FromWindowsPath_WithNul_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GitPath.FromWindowsPath("a\0b"));
    }

    /// <summary>
    /// Verifies that invalid UTF-8 bytes stay visible without being decoded and re-encoded.
    /// </summary>
    [TestMethod]
    public void DisplayText_WithInvalidUtf8_RendersExactByteToken()
    {
        var path = GitPath.FromUnixBytes([0x61, 0xff, 0x62]);

        Assert.AreEqual("a<0xFF>b", path.DisplayText);
        CollectionAssert.AreEqual(new byte[] { 0x61, 0xff, 0x62 }, path.GetUnixBytes().ToArray());
    }

    /// <summary>
    /// Verifies that terminal controls are rendered as visible tokens.
    /// </summary>
    [TestMethod]
    public void DisplayText_WithTerminalControl_RendersVisibleToken()
    {
        var path = GitPath.FromUnixBytes([0x61, 0x1b, 0x62]);

        Assert.AreEqual("a<0x1B>b", path.DisplayText);
    }

    /// <summary>
    /// Verifies that bidirectional formatting characters are rendered as visible tokens.
    /// </summary>
    [TestMethod]
    public void DisplayText_WithBidirectionalControl_RendersVisibleToken()
    {
        var path = GitPath.FromWindowsPath("a\u202Eb");

        Assert.AreEqual("a<U+202E>b", path.DisplayText);
    }

    /// <summary>
    /// Verifies that equality and ordering use exact native Unix bytes.
    /// </summary>
    [TestMethod]
    public void CompareTo_WithUnixPaths_UsesNativeByteOrder()
    {
        var first = GitPath.FromUnixBytes([0x61, 0x7f]);
        var equal = GitPath.FromUnixBytes([0x61, 0x7f]);
        var second = GitPath.FromUnixBytes([0x61, 0x80]);

        Assert.AreEqual(first, equal);
        Assert.AreEqual(first.GetHashCode(), equal.GetHashCode());
        Assert.IsLessThan(0, first.CompareTo(second));
    }
}
