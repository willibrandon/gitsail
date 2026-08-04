using GitSail.Git.Parsing;
using GitSail.Testing;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded parsing of Git's incremental blame protocol and exact origin paths.
/// </summary>
[TestClass]
public sealed class BlameIncrementalParserTests
{
    private static readonly int[] s_expectedResultLines = [2, 3, 7];
    private static readonly int[] s_expectedSourceLines = [4, 5, 9];

    /// <summary>
    /// Verifies commit metadata caching, group expansion, boundaries, and renamed previous paths.
    /// </summary>
    [TestMethod]
    public void Parse_WithCachedCommitGroups_ReturnsOrderedLineAttributions()
    {
        const string objectId = "1111111111111111111111111111111111111111";
        const string previousId = "2222222222222222222222222222222222222222";
        var output = Encoding.UTF8.GetBytes(
            $"{objectId} 4 2 2\n" +
            "author Line Author\n" +
            "author-mail <line@example.invalid>\n" +
            "author-time 946684800\n" +
            "author-tz +0130\n" +
            "committer Line Committer\n" +
            "committer-mail <commit@example.invalid>\n" +
            "committer-time 946684800\n" +
            "committer-tz +0130\n" +
            "summary Move two lines\n" +
            "boundary\n" +
            $"previous {previousId} \"old\\tname.cs\"\n" +
            "filename \"new name.cs\"\n" +
            $"{objectId} 9 7 1\n" +
            $"previous {previousId} \"older name.cs\"\n" +
            "filename \"new name.cs\"\n");

        var attributions = new BlameIncrementalParser().Parse(output);

        Assert.HasCount(3, attributions);
        TestSeq.AreEqual(s_expectedResultLines, attributions.Select(static item => item.ResultLineNumber));
        TestSeq.AreEqual(s_expectedSourceLines, attributions.Select(static item => item.SourceLineNumber));
        Assert.AreSame(attributions[0].Commit, attributions[2].Commit);
        Assert.IsTrue(attributions.All(static item => item.IsBoundary));
        Assert.AreEqual("Line Author", Encoding.UTF8.GetString(attributions[0].Commit.AuthorName.Span));
        Assert.AreEqual("+0130", attributions[0].Commit.AuthorTimeZone);
        Assert.AreEqual("old<0x09>name.cs", attributions[0].Previous!.Path.DisplayText);
        Assert.AreEqual("older name.cs", attributions[2].Previous!.Path.DisplayText);
        Assert.AreEqual("new name.cs", attributions[1].SourcePath.DisplayText);
    }

    /// <summary>
    /// Verifies SHA-256 identities and an all-zero worktree identity are accepted without width assumptions.
    /// </summary>
    [TestMethod]
    public void Parse_WithSha256AndWorktreeIdentities_RetainsExactWidths()
    {
        var sha256 = new string('a', 64);
        var zeros = new string('0', 64);
        var output = Encoding.UTF8.GetBytes(
            CreateCompleteGroup(sha256, 1, 1, "first.txt") +
            CreateCompleteGroup(zeros, 2, 2, "first.txt"));

        var attributions = new BlameIncrementalParser().Parse(output);

        Assert.HasCount(2, attributions);
        Assert.AreEqual(64, attributions[0].Commit.ObjectId.ToString().Length);
        Assert.IsFalse(attributions[0].Commit.IsUncommitted);
        Assert.IsTrue(attributions[1].Commit.IsUncommitted);
    }

    /// <summary>
    /// Verifies a new object without its required metadata fails closed.
    /// </summary>
    [TestMethod]
    public void Parse_WithMissingNewCommitMetadata_ThrowsInvalidDataException()
    {
        var output = Encoding.UTF8.GetBytes(
            "1111111111111111111111111111111111111111 1 1 1\nfilename file.txt\n");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new BlameIncrementalParser().Parse(output));
    }

    /// <summary>
    /// Verifies unterminated records fail instead of accepting partial attribution state.
    /// </summary>
    [TestMethod]
    public void Parse_WithoutFinalLineTerminator_ThrowsInvalidDataException()
    {
        var output = Encoding.UTF8.GetBytes(
            CreateCompleteGroup("1111111111111111111111111111111111111111", 1, 1, "file.txt").TrimEnd('\n'));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new BlameIncrementalParser().Parse(output));
    }

    private static string CreateCompleteGroup(string objectId, int sourceLine, int resultLine, string path)
        => $"{objectId} {sourceLine} {resultLine} 1\n" +
            "author Test Author\n" +
            "author-mail <test@example.invalid>\n" +
            "author-time 946684800\n" +
            "author-tz +0000\n" +
            "committer Test Author\n" +
            "committer-mail <test@example.invalid>\n" +
            "committer-time 946684800\n" +
            "committer-tz +0000\n" +
            "summary Subject\n" +
            $"filename {path}\n";
}
