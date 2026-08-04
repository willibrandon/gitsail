using GitSail.Domain;
using GitSail.Git.Parsing;
using GitSail.Testing;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded literal line- and NUL-delimited pathspec input parsing.
/// </summary>
[TestClass]
public sealed class PathspecFileReaderTests
{
    /// <summary>
    /// Verifies native trailing operands replace managed fallback text before optional file records are appended.
    /// </summary>
    [TestMethod]
    public async Task ResolveAsync_WithNativePaths_PrefersExactNativeOperands()
    {
        var nativePath = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("native.txt")
            : GitPath.FromUnixBytes(new byte[] { (byte)'n', (byte)'a', (byte)'t', (byte)'i', (byte)'v', (byte)'e', 0xff });

        var paths = await CommandPathspecResolver.ResolveAsync(
            ["managed.txt"],
            [nativePath],
            pathspecFile: null,
            pathspecFileNul: false,
            TestContext.Current!.CancellationToken);

        Assert.HasCount(1, paths);
        Assert.AreSame(nativePath, paths[0]);
    }

    /// <summary>
    /// Verifies NUL records retain spaces and exact non-UTF-8 bytes on Unix.
    /// </summary>
    [TestMethod]
    public void Parse_WithNulRecords_ReturnsExactNativePaths()
    {
        byte[] input = OperatingSystem.IsWindows()
            ? "first file.txt\0second.txt\0"u8.ToArray()
            : [(byte)'f', (byte)'i', (byte)'r', (byte)'s', (byte)'t', (byte)' ', 0xff, 0, (byte)'b', 0];

        var paths = PathspecFileReader.Parse(input, nulDelimited: true);

        Assert.HasCount(2, paths);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("first file.txt", paths[0].GetWindowsPath());
            Assert.AreEqual("second.txt", paths[1].GetWindowsPath());
        }
        else
        {
            TestSeq.AreEqual(
                new byte[] { (byte)'f', (byte)'i', (byte)'r', (byte)'s', (byte)'t', (byte)' ', 0xff },
                paths[0].GetUnixBytes().ToArray());
            TestSeq.AreEqual(new byte[] { (byte)'b' }, paths[1].GetUnixBytes().ToArray());
        }
    }

    /// <summary>
    /// Verifies line records accept CRLF and a final record without a line terminator.
    /// </summary>
    [TestMethod]
    public void Parse_WithLineRecords_ReturnsLiteralPaths()
    {
        var paths = PathspecFileReader.Parse(
            "folder/first file.txt\r\nsecond.txt"u8,
            nulDelimited: false);

        Assert.HasCount(2, paths);
        Assert.AreEqual("folder/first file.txt", paths[0].DisplayText);
        Assert.AreEqual("second.txt", paths[1].DisplayText);
    }

    /// <summary>
    /// Verifies NUL mode requires a complete final record boundary.
    /// </summary>
    [TestMethod]
    public void Parse_WithoutFinalNul_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PathspecFileReader.Parse("file.txt"u8, nulDelimited: true));
    }

    /// <summary>
    /// Verifies empty pathspec records are rejected in both delimiter modes.
    /// </summary>
    /// <param name="nulDelimited">Whether to parse NUL-delimited input.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Parse_WithEmptyRecord_ThrowsInvalidDataException(bool nulDelimited)
    {
        var input = nulDelimited ? new byte[] { 0 } : "first\n\nsecond"u8.ToArray();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            PathspecFileReader.Parse(input, nulDelimited));
    }
}
