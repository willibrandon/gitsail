using GitSail.Domain;
using GitSail.Ui;
using Hex1b.Documents;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies lifted commit-editor state change notification, versioning, and successful reset behavior.
/// </summary>
[TestClass]
public sealed class CommitMessageStateTests
{
    /// <summary>
    /// Verifies a document mutation publishes the complete changed message and advances its version.
    /// </summary>
    [TestMethod]
    public void DocumentApply_WithInsertedText_NotifiesChangedMessage()
    {
        var state = new CommitMessageState("subject");
        var changedCount = 0;
        state.Changed += () => changedCount++;
        var initialVersion = state.Version;

        _ = state.Editor.Document.Apply(
            new InsertOperation((DocumentOffset)7, " line"),
            "test");

        Assert.AreEqual(1, changedCount);
        Assert.AreEqual("subject line", state.Message);
        Assert.IsGreaterThan(initialVersion, state.Version);
    }

    /// <summary>
    /// Verifies clearing detaches the replaced document and tracks changes only on the new editor state.
    /// </summary>
    [TestMethod]
    public void Clear_AfterSuccessfulCommit_ReplacesTrackedDocument()
    {
        var state = new CommitMessageState("committed");
        var replacedDocument = state.Editor.Document;
        var changedCount = 0;
        state.Changed += () => changedCount++;

        state.Clear();
        _ = replacedDocument.Apply(
            new InsertOperation((DocumentOffset)replacedDocument.Length, " stale"),
            "test");
        _ = state.Editor.Document.Apply(
            new InsertOperation(DocumentOffset.Zero, "new draft"),
            "test");

        Assert.AreEqual(1, changedCount);
        Assert.AreEqual("new draft", state.Message);
    }

    /// <summary>
    /// Verifies an exact configured template must change and becomes blocked again when restored byte-for-byte.
    /// </summary>
    [TestMethod]
    public void IsInitialTemplateUnchanged_AfterEditAndRestore_TracksExactContent()
    {
        const string template = "Subject\n\nDetails\n";
        var state = new CommitMessageState(
            template,
            CommitMessageInitializationKind.Template);

        Assert.IsTrue(state.IsInitialTemplateUnchanged);
        _ = state.Editor.Document.Apply(
            new InsertOperation((DocumentOffset)state.Editor.Document.Length, "edited"),
            "test");
        Assert.IsFalse(state.IsInitialTemplateUnchanged);

        _ = state.Editor.Document.Apply(
            new ReplaceOperation(
                new DocumentRange(DocumentOffset.Zero, (DocumentOffset)state.Editor.Document.Length),
                template),
            "test");

        Assert.IsTrue(state.IsInitialTemplateUnchanged);
        state.Clear();
        Assert.IsFalse(state.IsInitialTemplateUnchanged);
    }
}
