using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies exact path recovery from the NUL-delimited metadata preceding raw patches.
/// </summary>
[TestClass]
public sealed class RawDiffMetadataParserTests
{
    /// <summary>
    /// Verifies that exact metadata disambiguates spaces and side-prefix text inside rename paths.
    /// </summary>
    [TestMethod]
    public async Task Parse_WithAmbiguousRenameHeader_UsesExactMetadataPaths()
    {
        var bytes = new List<byte>();
        AddField(bytes, ":100644 100644 1111111 2222222 R100"u8);
        AddField(bytes, "old b/name.txt"u8);
        AddField(bytes, "new b/name.txt"u8);
        bytes.Add(0);
        var patchOffset = bytes.Count;
        bytes.AddRange("diff --git a/old b/name.txt b/new b/name.txt\nsimilarity index 100%\n"u8.ToArray());
        using var spool = RawByteSpool.Create(16);
        await spool.AppendAsync(bytes.ToArray(), CancellationToken.None);

        var index = RawDiffParser.Parse(spool, new OperationGeneration(4));

        Assert.HasCount(1, index.Files);
        Assert.AreEqual(patchOffset, index.Files[0].Offset);
        AssertPathEquals("old b/name.txt", index.Files[0].OldPath);
        AssertPathEquals("new b/name.txt", index.Files[0].NewPath);
    }

    /// <summary>
    /// Verifies that non-UTF-8 Unix path bytes survive the metadata protocol unchanged.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX | OperatingSystems.FreeBSD)]
    public async Task Parse_WithNonUtf8UnixPath_RetainsExactBytes()
    {
        var bytes = new List<byte>();
        AddField(bytes, ":100644 100644 1111111 2222222 M"u8);
        AddField(bytes, [.. "invalid-"u8, 0xff, .. ".txt"u8]);
        bytes.Add(0);
        bytes.AddRange("diff --git placeholder\n@@ -1 +1 @@\n-old\n+new\n"u8.ToArray());
        using var spool = RawByteSpool.Create(16);
        await spool.AppendAsync(bytes.ToArray(), CancellationToken.None);

        var index = RawDiffParser.Parse(spool, new OperationGeneration(5));

        byte[] expected = [.. "invalid-"u8, 0xff, .. ".txt"u8];
        CollectionAssert.AreEqual(expected, index.Files[0].OldPath.GetUnixBytes().ToArray());
        CollectionAssert.AreEqual(expected, index.Files[0].NewPath.GetUnixBytes().ToArray());
    }

    private static void AddField(List<byte> output, ReadOnlySpan<byte> field)
    {
        output.AddRange(field.ToArray());
        output.Add(0);
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
