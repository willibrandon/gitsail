using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies typed untrusted revision candidate validation.
/// </summary>
[TestClass]
public sealed class RevisionTests
{
    /// <summary>
    /// Verifies that option-looking text remains an ordinary revision value.
    /// </summary>
    [TestMethod]
    public void Create_WithOptionLookingText_RetainsLiteralValue()
    {
        var revision = Revision.Create("--help");

        Assert.AreEqual("--help", revision.Value);
    }

    /// <summary>
    /// Verifies that embedded NUL is rejected before Git starts.
    /// </summary>
    [TestMethod]
    public void Create_WithNul_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Revision.Create("HEAD\0--help"));
    }
}
