using GitSail.Git.Execution;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies byte-oriented Git version parsing and comparison.
/// </summary>
[TestClass]
public sealed class GitVersionTests
{
    /// <summary>
    /// Verifies parsing of an ordinary three-component Git version.
    /// </summary>
    [TestMethod]
    public void TryParse_WithStandardVersion_ReturnsComponents()
    {
        var parsed = GitVersion.TryParse("git version 2.55.1\n"u8, out var version);

        Assert.IsTrue(parsed);
        Assert.AreEqual(2, version.Major);
        Assert.AreEqual(55, version.Minor);
        Assert.AreEqual(1, version.Patch);
        Assert.AreEqual(string.Empty, version.Suffix);
        Assert.AreEqual("2.55.1", version.ToString());
    }

    /// <summary>
    /// Verifies parsing of a vendor-suffixed Git version.
    /// </summary>
    [TestMethod]
    public void TryParse_WithVendorSuffix_PreservesSuffix()
    {
        var output = Encoding.UTF8.GetBytes("git version 2.39.5 (Apple Git-154)\r\n");

        var parsed = GitVersion.TryParse(output, out var version);

        Assert.IsTrue(parsed);
        Assert.AreEqual("(Apple Git-154)", version.Suffix);
        Assert.AreEqual("2.39.5 (Apple Git-154)", version.ToString());
    }

    /// <summary>
    /// Verifies a dot-prefixed Windows build suffix remains attached to the numeric version.
    /// </summary>
    [TestMethod]
    public void TryParse_WithWindowsBuildSuffix_PreservesOriginalSeparator()
    {
        var output = Encoding.UTF8.GetBytes("git version 2.51.1.windows.1\r\n");

        var parsed = GitVersion.TryParse(output, out var version);

        Assert.IsTrue(parsed);
        Assert.AreEqual(".windows.1", version.Suffix);
        Assert.AreEqual("2.51.1.windows.1", version.ToString());
    }

    /// <summary>
    /// Verifies that malformed and non-Git responses are rejected.
    /// </summary>
    /// <param name="output">The malformed output text.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("2.55.0")]
    [DataRow("git version x.55.0")]
    [DataRow("git version 2.x.0")]
    [DataRow("git version 2.55.x")]
    public void TryParse_WithMalformedResponse_ReturnsFalse(string output)
    {
        Assert.IsFalse(GitVersion.TryParse(Encoding.UTF8.GetBytes(output), out _));
    }

    /// <summary>
    /// Verifies that numeric components define version ordering independently from vendor text.
    /// </summary>
    [TestMethod]
    public void CompareTo_WithDifferentNumericVersions_UsesSemanticOrder()
    {
        _ = GitVersion.TryParse("git version 2.36.0"u8, out var baseline);
        _ = GitVersion.TryParse("git version 2.55.0 vendor"u8, out var current);

        Assert.IsLessThan(0, baseline.CompareTo(current));
    }
}
