using GitSail.Domain;
using GitSail.Ui;
using Hex1b.Documents;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies history preview state remains renderable while its commit changes.
/// </summary>
[TestClass]
public sealed class HistoryWorkspaceStateTests
{
    /// <summary>
    /// Verifies replacement patches invalidate the existing editor and clamp its cursor.
    /// </summary>
    [TestMethod]
    public void SetPreview_WithShorterReplacement_PreservesEditorAndInvalidatesDocument()
    {
        var state = new HistoryWorkspaceState();
        var commit = CreateCommit();
        state.SetPreview(commit, "+a long preview tail that must be erased");
        var editor = state.Preview;
        var document = state.Preview.Document;
        var provider = state.PreviewDecorationProvider;
        var version = document.Version;
        editor.Cursor.Position = new DocumentOffset(document.Length);

        state.SetPreview(commit, "+short");

        Assert.AreSame(editor, state.Preview);
        Assert.AreSame(document, state.Preview.Document);
        Assert.AreSame(provider, state.PreviewDecorationProvider);
        Assert.IsGreaterThan(version, document.Version);
        Assert.AreEqual("+short", document.GetText());
        Assert.AreEqual(document.Length, editor.Cursor.Position.Value);
    }

    /// <summary>
    /// Verifies refresh preparation retains the selected commit and its current preview.
    /// </summary>
    [TestMethod]
    public void BeginReload_WithSelectedCommit_PreservesSelectionAndPreview()
    {
        var state = new HistoryWorkspaceState();
        var commit = CreateCommit();
        state.ApplyCatalog(new HistoryCatalog([commit]));
        state.SetPreview(commit, "+current preview");

        state.BeginReload();

        Assert.IsNull(state.Catalog);
        Assert.IsEmpty(state.VisibleItems);
        Assert.AreEqual("+current preview", state.Preview.Document.GetText());

        state.ApplyCatalog(new HistoryCatalog([commit]));

        Assert.AreSame(commit, state.FocusedItem?.Commit);
    }

    private static HistoryCommit CreateCommit()
    {
        Assert.IsTrue(ObjectId.TryParseHex(
            "1111111111111111111111111111111111111111"u8,
            out var objectId));
        return new HistoryCommit(
            objectId!,
            [],
            "Developer"u8,
            "developer@example.com"u8,
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            [],
            CommitSignatureStatus.None,
            "Subject"u8,
            []);
    }
}
