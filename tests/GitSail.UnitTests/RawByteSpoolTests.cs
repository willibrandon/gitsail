using GitSail.Git.Execution;
using System.Security.Cryptography;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies exact in-memory and file-backed raw-byte spool behavior.
/// </summary>
[TestClass]
public sealed class RawByteSpoolTests
{
    /// <summary>
    /// Verifies that a small exact byte sequence remains in memory and supports slices and hashing.
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_BelowThreshold_RetainsExactMemoryBytes()
    {
        using var spool = RawByteSpool.Create(32);
        var expected = new byte[] { 0, 1, 0xff, (byte)'\n', 4 };

        await spool.AppendAsync(expected, CancellationToken.None);
        var actual = await spool.ReadSliceAsync(0, expected.Length, CancellationToken.None);
        var fingerprint = await spool.ComputeSha256Async(CancellationToken.None);

        Assert.IsFalse(spool.IsFileBacked);
        Assert.AreEqual(expected.Length, spool.Length);
        CollectionAssert.AreEqual(expected, actual);
        CollectionAssert.AreEqual(SHA256.HashData(expected), fingerprint);
    }

    /// <summary>
    /// Verifies that crossing the threshold spills, slices, and hashes every byte without alteration.
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_AboveThreshold_SpillsWithoutChangingBytes()
    {
        using var spool = RawByteSpool.Create(4);
        var first = new byte[] { 0, 1, 2 };
        var second = new byte[] { 3, 0xff, 5 };

        await spool.AppendAsync(first, CancellationToken.None);
        await spool.AppendAsync(second, CancellationToken.None);
        var actual = await spool.ReadSliceAsync(1, 4, CancellationToken.None);
        var fingerprint = await spool.ComputeSha256Async(CancellationToken.None);
        byte[] expected = [1, 2, 3, 0xff];
        byte[] complete = [0, 1, 2, 3, 0xff, 5];

        Assert.IsTrue(spool.IsFileBacked);
        Assert.AreEqual(6L, spool.Length);
        CollectionAssert.AreEqual(expected, actual);
        CollectionAssert.AreEqual(SHA256.HashData(complete), fingerprint);
    }
}
