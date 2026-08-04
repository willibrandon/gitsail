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
