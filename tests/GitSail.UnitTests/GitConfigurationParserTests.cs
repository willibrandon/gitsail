using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded byte parsing of Git configuration source records.
/// </summary>
[TestClass]
public sealed class GitConfigurationParserTests
{
    /// <summary>
    /// Verifies exact scope, origin, key, embedded newline, and explicit-empty preservation.
    /// </summary>
    [TestMethod]
    public void Parse_WithCompleteRecords_ReturnsLosslessEntries()
    {
        var output = new List<byte>();
        AddField(output, "global"u8);
        AddField(output, "file:/home/user/.gitconfig"u8);
        AddField(output, "branch.MyBranch.description\nfirst\nsecond"u8);
        AddField(output, "local"u8);
        AddField(output, "file:.git/config"u8);
        AddField(output, "gitsail.theme\n"u8);

        var entries = new GitConfigurationParser().Parse(output.ToArray());

        Assert.HasCount(2, entries);
        Assert.AreEqual(GitConfigurationScope.Global, entries[0].Scope);
        CollectionAssert.AreEqual(
            "file:/home/user/.gitconfig"u8.ToArray(),
            entries[0].Origin.GetBytes().ToArray());
        Assert.AreEqual("branch.MyBranch.description", entries[0].Key.DisplayText);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("first\nsecond"),
            entries[0].Value.GetBytes().ToArray());
        Assert.AreEqual(GitConfigurationScope.Local, entries[1].Scope);
        Assert.IsTrue(entries[1].Value.IsEmpty);
    }

    /// <summary>
    /// Verifies that subsection bytes not representable as UTF-8 remain exact and visibly escaped.
    /// </summary>
    [TestMethod]
    public void Parse_WithNonUtf8Subsection_PreservesExactKeyBytes()
    {
        var output = new List<byte>();
        AddField(output, "local"u8);
        AddField(output, "file:.git/config"u8);
        AddField(output, [.. "branch.topic"u8.ToArray(), 0xff, .. ".remote\norigin"u8.ToArray()]);

        var entry = new GitConfigurationParser().Parse(output.ToArray()).Single();

        Assert.AreEqual("branch.topic<0xFF>.remote", entry.Key.DisplayText);
        CollectionAssert.Contains(entry.Key.GetBytes().ToArray(), (byte)0xff);
    }

    /// <summary>
    /// Verifies that a truncated scope, source, and value record fails closed.
    /// </summary>
    [TestMethod]
    public void Parse_WithTruncatedTriple_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddField(output, "global"u8);
        AddField(output, "file:/home/user/.gitconfig"u8);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new GitConfigurationParser().Parse(output.ToArray()));
    }

    /// <summary>
    /// Verifies that a record without Git's key-value separator fails closed.
    /// </summary>
    [TestMethod]
    public void Parse_WithMissingKeyValueSeparator_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddField(output, "global"u8);
        AddField(output, "file:/home/user/.gitconfig"u8);
        AddField(output, "user.name"u8);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new GitConfigurationParser().Parse(output.ToArray()));
    }

    private static void AddField(List<byte> destination, ReadOnlySpan<byte> field)
    {
        destination.AddRange(field.ToArray());
        destination.Add(0);
    }
}
