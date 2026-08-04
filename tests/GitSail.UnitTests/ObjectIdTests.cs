using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies exact SHA-1 and SHA-256 Git object identifier behavior.
/// </summary>
[TestClass]
public sealed class ObjectIdTests
{
    /// <summary>
    /// Verifies lowercase normalization and SHA-1 format detection.
    /// </summary>
    [TestMethod]
    public void TryParseHex_WithUppercaseSha1_ReturnsNormalizedObjectId()
    {
        var parsed = ObjectId.TryParseHex("0123456789ABCDEF0123456789ABCDEF01234567"u8, out var objectId);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(objectId);
        Assert.AreEqual(RepositoryObjectFormat.Sha1, objectId.Format);
        Assert.AreEqual("0123456789abcdef0123456789abcdef01234567", objectId.ToString());
    }

    /// <summary>
    /// Verifies SHA-256 format detection.
    /// </summary>
    [TestMethod]
    public void TryParseHex_WithSha256_ReturnsSha256ObjectId()
    {
        var parsed = ObjectId.TryParseHex(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"u8,
            out var objectId);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(objectId);
        Assert.AreEqual(RepositoryObjectFormat.Sha256, objectId.Format);
    }

    /// <summary>
    /// Verifies invalid width and non-hexadecimal input rejection.
    /// </summary>
    /// <param name="value">The invalid object identifier text.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("0123")]
    [DataRow("g123456789abcdef0123456789abcdef01234567")]
    public void TryParseHex_WithInvalidValue_ReturnsFalse(string value)
    {
        Assert.IsFalse(ObjectId.TryParseHex(System.Text.Encoding.ASCII.GetBytes(value), out _));
    }
}
