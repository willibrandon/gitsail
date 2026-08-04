using GitSail.Domain;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies lossless parsing and typed editing of Git interactive-rebase todo files.
/// </summary>
[TestClass]
public sealed class RebaseTodoParserTests
{
    /// <summary>
    /// Verifies every sequencer command and alias is classified without changing source bytes.
    /// </summary>
    [TestMethod]
    public void Parse_WithCompleteCommandSet_RoundTripsExactBytes()
    {
        var source = Encoding.UTF8.GetBytes(
            "p 1234 first\r\n" +
            "reword abcdef second\n" +
            "e fedcba edit\n" +
            "s 1111 squash\n" +
            "f -C 2222 fixup\n" +
            "d 3333 drop\n" +
            "x dotnet test\n" +
            "b\n" +
            "l onto\n" +
            "t onto\n" +
            "m -C 4444 branch # merge\n" +
            "u refs/heads/topic\n" +
            "noop\n" +
            "# retained comment\n" +
            "\n");

        var document = RebaseTodoParser.Parse(source);

        CollectionAssert.AreEqual(source, document.Render());
        Assert.AreEqual(RebaseTodoAction.Pick, document.Entries[0].Action);
        Assert.AreEqual(RebaseTodoAction.UpdateRef, document.Entries[11].Action);
        Assert.AreEqual(RebaseTodoLineKind.Comment, document.Entries[13].Kind);
        Assert.AreEqual(RebaseTodoLineKind.Blank, document.Entries[14].Kind);
    }

    /// <summary>
    /// Verifies changing an action retains the exact object, subject, spacing, and line ending.
    /// </summary>
    [TestMethod]
    public void ChangeCommitAction_WithRawSubject_ChangesOnlyCommandBytes()
    {
        byte[] source = [
            .. "p\t1234  subject "u8.ToArray(),
            0xff,
            (byte)'\r',
            (byte)'\n',
        ];
        var document = RebaseTodoParser.Parse(source);

        document.Entries[0].ChangeCommitAction(RebaseTodoAction.Reword);

        byte[] expected = [
            .. "reword\t1234  subject "u8.ToArray(),
            0xff,
            (byte)'\r',
            (byte)'\n',
        ];
        CollectionAssert.AreEqual(expected, document.Render());
    }

    /// <summary>
    /// Verifies changing an autosquash fixup removes its fixup-only message option.
    /// </summary>
    [TestMethod]
    public void ChangeCommitAction_FromFixupMessageMode_RemovesInapplicableOption()
    {
        var document = RebaseTodoParser.Parse("fixup\t-C  1234 subject\n"u8);

        document.Entries[0].ChangeCommitAction(RebaseTodoAction.Pick);

        CollectionAssert.AreEqual(
            "pick\t1234 subject\n"u8.ToArray(),
            document.Render());
    }

    /// <summary>
    /// Verifies moving command rows skips comments and stops at both document bounds.
    /// </summary>
    [TestMethod]
    public void MoveCommand_AcrossComments_StopsAtCommandBounds()
    {
        var document = RebaseTodoParser.Parse(
            "pick 1111 one\n# divider\npick 2222 two\n"u8);

        Assert.AreEqual(0, document.MoveCommand(0, -1));
        Assert.AreEqual(2, document.MoveCommand(0, 1));
        Assert.AreEqual("pick 2222 two", document.Entries[0].DisplayText);
        Assert.AreEqual(RebaseTodoLineKind.Comment, document.Entries[1].Kind);
        Assert.AreEqual("pick 1111 one", document.Entries[2].DisplayText);
        Assert.AreEqual(2, document.MoveCommand(2, 1));
    }

    /// <summary>
    /// Verifies invalid commands, NUL, indentation, and missing payloads are rejected.
    /// </summary>
    /// <param name="source">The invalid todo source.</param>
    [TestMethod]
    [DataRow("unknown 1234 subject\n")]
    [DataRow(" pick 1234 subject\n")]
    [DataRow("pick\n")]
    [DataRow("break unexpected\n")]
    public void Parse_WithInvalidTodo_ThrowsInvalidDataException(string source)
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RebaseTodoParser.Parse(Encoding.UTF8.GetBytes(source)));
    }

    /// <summary>
    /// Verifies an embedded NUL is rejected before any line is exposed to the editor.
    /// </summary>
    [TestMethod]
    public void Parse_WithNul_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RebaseTodoParser.Parse([(byte)'#', 0, (byte)'x']));
    }
}
