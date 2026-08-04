using GitSail.Git.Parsing;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded exact parsing of Git-resolved default push mappings.
/// </summary>
[TestClass]
public sealed class PushPorcelainParserTests
{
    /// <summary>
    /// Verifies full refs, deletion, duplicate destination sections, and automatic upstream intent.
    /// </summary>
    [TestMethod]
    public void Parse_WithCompletePorcelainResponse_ReturnsExactDeduplicatedMappings()
    {
        var result = PushPorcelainParser.Parse(
            "To first\n*\trefs/heads/main:refs/heads/main\t[new branch]\n"u8.ToArray()
                .Concat("-\t:refs/heads/obsolete\t[deleted]\nDone\n"u8.ToArray())
                .Concat("To second\n*\trefs/heads/main:refs/heads/main\t[new branch]\n"u8.ToArray())
                .Concat("Would set upstream of 'main' to 'main' of 'origin'\nDone\n"u8.ToArray())
                .ToArray());

        Assert.HasCount(2, result.RefSpecs);
        Assert.AreEqual("refs/heads/main:refs/heads/main", result.RefSpecs[0].ToString());
        Assert.AreEqual(":refs/heads/obsolete", result.RefSpecs[1].ToString());
        Assert.IsTrue(result.WouldSetUpstream);
    }

    /// <summary>
    /// Verifies an abbreviated ambiguous porcelain mapping is rejected instead of guessed.
    /// </summary>
    [TestMethod]
    public void Parse_WithUnqualifiedRefs_ThrowsInvalidDataException()
    {
        _ = Assert.ThrowsExactly<InvalidDataException>(() => PushPorcelainParser.Parse(
            " \tmain:main\t0123456..89abcde\n"u8));
    }

    /// <summary>
    /// Verifies a truncated response is rejected rather than partially planned.
    /// </summary>
    [TestMethod]
    public void Parse_WithoutFinalLineTerminator_ThrowsInvalidDataException()
    {
        _ = Assert.ThrowsExactly<InvalidDataException>(() => PushPorcelainParser.Parse(
            "*\trefs/heads/main:refs/heads/main\t[new branch]"u8));
    }
}
