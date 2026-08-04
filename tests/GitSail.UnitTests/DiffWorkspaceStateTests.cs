using GitSail.Domain;
using GitSail.Git.Parsing;
using GitSail.Ui;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies comparison content search and one-based presentation-line navigation.
/// </summary>
[TestClass]
public sealed class DiffWorkspaceStateTests
{
    /// <summary>
    /// Verifies side-by-side search selects matches in display order and wraps in both directions.
    /// </summary>
    [TestMethod]
    public void FindText_WithAlignedDocuments_SelectsAndWrapsMatches()
    {
        var state = CreateState();
        state.Search.Text = "new";

        Assert.IsTrue(state.FindText(reverse: false));
        Assert.AreEqual("new", GetSelectedText(state.RightEditor));
        var firstOffset = state.RightEditor.Cursor.SelectionStart.Value;

        Assert.IsTrue(state.FindText(reverse: false));
        Assert.AreEqual("new", GetSelectedText(state.RightEditor));
        Assert.IsGreaterThan(firstOffset, state.RightEditor.Cursor.SelectionStart.Value);

        Assert.IsTrue(state.FindText(reverse: true));
        Assert.AreEqual(firstOffset, state.RightEditor.Cursor.SelectionStart.Value);
    }

    /// <summary>
    /// Verifies go-to-line moves both aligned cursors and rejects lines outside the presentation.
    /// </summary>
    [TestMethod]
    public void GoToPresentationLine_WithAlignedDocuments_MovesBothCursors()
    {
        var state = CreateState();

        Assert.IsTrue(state.GoToPresentationLine(3));
        Assert.AreEqual(3, state.LeftEditor.Document.OffsetToPosition(state.LeftEditor.Cursor.Position).Line);
        Assert.AreEqual(3, state.RightEditor.Document.OffsetToPosition(state.RightEditor.Cursor.Position).Line);
        Assert.IsFalse(state.GoToPresentationLine(20));
    }

    /// <summary>
    /// Verifies unified search uses the complete unified document after changing layouts.
    /// </summary>
    [TestMethod]
    public void FindText_AfterUnifiedToggle_SelectsUnifiedMetadata()
    {
        var state = CreateState();
        state.ToggleLayout();
        state.Search.Text = "diff --git";

        Assert.IsTrue(state.FindText(reverse: false));

        Assert.AreEqual("diff --git", GetSelectedText(state.UnifiedEditor));
    }

    private static DiffWorkspaceState CreateState()
    {
        const string patch = "diff --git a/file.txt b/file.txt\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,3 +1,3 @@\n" +
            "-old one\n" +
            "+new one\n" +
            " context\n" +
            "-old two\n" +
            "+new two\n";
        var bytes = Encoding.UTF8.GetBytes(patch);
        var path = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("file.txt")
            : GitPath.FromUnixBytes("file.txt"u8);
        var file = new RawDiffFile(
            path,
            path,
            Offset: 0,
            bytes.Length,
            RawPatchParser.Parse(bytes),
            IsBinary: false);
        var state = new DiffWorkspaceState();
        state.ApplyFiles([file]);
        state.SetPresentation(
            file,
            new ComparisonPresentation(
                patch,
                "@@ -1,3 +1,3 @@\n-old one\n context\n-old two\n",
                "@@ -1,3 +1,3 @@\n+new one\n context\n+new two\n",
                ImmutableArray.Create(4),
                ImmutableArray.Create(1),
                [],
                [],
                [],
                [],
                []),
            "Left",
            "Right");
        return state;
    }

    private static string GetSelectedText(Hex1b.Widgets.EditorState editor)
        => editor.Document.GetText(editor.Cursor.SelectionRange);
}
