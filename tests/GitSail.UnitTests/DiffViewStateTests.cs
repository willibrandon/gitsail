using GitSail.Domain;
using GitSail.Ui;
using Hex1b.Documents;
using Hex1b.Widgets;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies generation-stamped diff editor replacement and immutable presentation behavior.
/// </summary>
[TestClass]
public sealed class DiffViewStateTests
{
    /// <summary>
    /// Verifies typing through the editor state cannot change a read-only patch presentation.
    /// </summary>
    [TestMethod]
    public void SetContent_WithPatch_CreatesReadOnlyEditorState()
    {
        var state = new DiffViewState();
        state.SetContent("Unstaged: file.txt", "-old\n+new", new OperationGeneration(9));

        state.Editor.InsertText("changed");

        Assert.AreEqual("Unstaged: file.txt", state.Title);
        Assert.AreEqual(9L, state.Generation.Value);
        Assert.IsTrue(state.Editor.IsReadOnly);
        Assert.AreEqual("-old\n+new", state.Editor.Document.GetText());
    }

    /// <summary>
    /// Verifies each replacement document receives a fresh decoration cache with its own line spans.
    /// </summary>
    [TestMethod]
    public void SetContent_WithReplacementDocument_ReplacesDecorationProvider()
    {
        var state = new DiffViewState();
        state.SetContent("First", "+short", new OperationGeneration(1));
        var firstProvider = state.DecorationProvider;

        state.SetContent(
            "Second",
            "+a much longer added line",
            new OperationGeneration(2));

        Assert.IsNotNull(firstProvider);
        Assert.IsNotNull(state.DecorationProvider);
        Assert.AreNotSame(firstProvider, state.DecorationProvider);
    }

    /// <summary>
    /// Verifies a same-path refresh retains logical cursor and selection coordinates in changed text.
    /// </summary>
    [TestMethod]
    public void SetContent_WithCursorPreservation_RetainsLogicalPositions()
    {
        var state = new DiffViewState();
        state.SetContent("First", "one\ntwo longer\nthree", new OperationGeneration(1));
        state.Editor.SetCursorPosition(
            state.Editor.Document.PositionToOffset(new DocumentPosition(2, 4)));
        state.Editor.SetCursorPosition(
            state.Editor.Document.PositionToOffset(new DocumentPosition(3, 3)),
            extend: true);

        state.SetContent(
            "Second",
            "changed first\ntwo changed\nthree changed",
            new OperationGeneration(2),
            preserveCursor: true);

        Assert.AreEqual(
            new DocumentPosition(3, 3),
            state.Editor.Document.OffsetToPosition(state.Editor.Cursor.Position));
        Assert.AreEqual(
            new DocumentPosition(2, 4),
            state.Editor.Document.OffsetToPosition(state.Editor.Cursor.SelectionAnchor!.Value));
    }

    /// <summary>
    /// Verifies case-insensitive diff search selects matches in both directions with wraparound.
    /// </summary>
    [TestMethod]
    public void Find_WithRepeatedText_TraversesAndWrapsMatches()
    {
        var state = new DiffViewState();
        state.SetContent("Diff", "Alpha beta ALPHA", new OperationGeneration(1));
        state.SetSearch("alpha");

        Assert.IsTrue(state.Find(reverse: false));
        Assert.AreEqual("Alpha", state.Editor.Document.GetText(state.Editor.Cursor.SelectionRange));
        Assert.AreEqual("1/2", state.SearchStatus);

        Assert.IsTrue(state.Find(reverse: false));
        Assert.AreEqual("ALPHA", state.Editor.Document.GetText(state.Editor.Cursor.SelectionRange));
        Assert.AreEqual("2/2", state.SearchStatus);

        Assert.IsTrue(state.Find(reverse: false));
        Assert.AreEqual("Alpha", state.Editor.Document.GetText(state.Editor.Cursor.SelectionRange));
        Assert.AreEqual("1/2", state.SearchStatus);

        Assert.IsTrue(state.Find(reverse: true));
        Assert.AreEqual("ALPHA", state.Editor.Document.GetText(state.Editor.Cursor.SelectionRange));
        Assert.AreEqual("2/2", state.SearchStatus);

        state.Search.Text = "beta";
        state.SetSearch("beta");
        Assert.AreEqual(string.Empty, state.SearchStatus);
        Assert.IsTrue(state.Find(reverse: false));
        Assert.AreEqual("beta", state.Editor.Document.GetText(state.Editor.Cursor.SelectionRange));
        Assert.AreEqual("1/1", state.SearchStatus);
    }

    /// <summary>
    /// Verifies missing and empty diff searches report their state without moving the cursor.
    /// </summary>
    [TestMethod]
    public void Find_WithNoMatch_ReportsStatusWithoutMovingCursor()
    {
        var state = new DiffViewState();
        state.SetContent("Diff", "patch text", new OperationGeneration(1));
        state.Editor.SetCursorPosition(new DocumentOffset(3));
        state.SetSearch("missing");

        Assert.IsFalse(state.Find(reverse: false));
        Assert.AreEqual(new DocumentOffset(3), state.Editor.Cursor.Position);
        Assert.AreEqual("No matches", state.SearchStatus);

        state.SetSearch(string.Empty);
        Assert.IsFalse(state.Find(reverse: false));
        Assert.AreEqual("Enter text", state.SearchStatus);
    }

    /// <summary>
    /// Verifies an editable lifted result preserves its editor identity and writable behavior.
    /// </summary>
    [TestMethod]
    public void SetEditor_WithConflictResult_PreservesWritableEditorState()
    {
        var state = new DiffViewState();
        var editor = new EditorState(new Hex1bDocument("result\n"));

        state.SetEditor("Conflict: file.txt", editor, new OperationGeneration(10));

        Assert.AreSame(editor, state.Editor);
        Assert.IsFalse(state.Editor.IsReadOnly);
        Assert.IsNull(state.DecorationProvider);
        Assert.AreEqual("Conflict: file.txt", state.Title);
        Assert.AreEqual(10L, state.Generation.Value);
    }
}
