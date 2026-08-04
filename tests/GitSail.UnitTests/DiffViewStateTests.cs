using GitSail.Domain;
using GitSail.Ui;

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
}
