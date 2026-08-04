using GitSail.Git.Parsing;
using GitSail.Ui;
using Hex1b.Documents;
using Hex1b.Widgets;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies exact raw-line mapping from contiguous and discontiguous editor cursor selections.
/// </summary>
[TestClass]
public sealed class DiffLineSelectionMapperTests
{
    /// <summary>
    /// Verifies multiple cursor ranges select changed lines but exclude context and an exclusive end line.
    /// </summary>
    [TestMethod]
    public void GetChangedLineNumbers_WithMultipleCursorRanges_ReturnsExactChangedLines()
    {
        const string patch = "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n" +
            "@@ -1,3 +1,3 @@\n context\n-old one\n+new one\n-old two\n+new two\n";
        var patchIndex = RawPatchParser.Parse(Encoding.UTF8.GetBytes(patch));
        var editor = new EditorState(new Hex1bDocument(patch));
        var firstStart = patch.IndexOf("-old one", StringComparison.Ordinal);
        var firstEnd = patch.IndexOf("-old two", StringComparison.Ordinal);
        editor.Cursor.SelectionAnchor = (DocumentOffset)firstStart;
        editor.Cursor.Position = (DocumentOffset)firstEnd;
        var secondStart = patch.IndexOf("+new two", StringComparison.Ordinal);
        _ = editor.Cursors.Add(
            (DocumentOffset)(secondStart + "+new two".Length),
            (DocumentOffset)secondStart);

        var selected = DiffLineSelectionMapper.GetChangedLineNumbers(editor, patchIndex);

        Assert.HasCount(3, selected);
        Assert.Contains(6, selected);
        Assert.Contains(7, selected);
        Assert.Contains(9, selected);
    }

    /// <summary>
    /// Verifies a caret without a range selects its changed line for a single-line action.
    /// </summary>
    [TestMethod]
    public void GetChangedLineNumbers_WithCaretOnAddition_ReturnsCurrentChangedLine()
    {
        const string patch = "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n" +
            "@@ -1 +1 @@\n-old\n+new\n";
        var patchIndex = RawPatchParser.Parse(Encoding.UTF8.GetBytes(patch));
        var editor = new EditorState(new Hex1bDocument(patch));
        editor.Cursor.Position = (DocumentOffset)patch.IndexOf("+new", StringComparison.Ordinal);

        var selected = DiffLineSelectionMapper.GetChangedLineNumbers(editor, patchIndex);

        Assert.HasCount(1, selected);
        Assert.Contains(6, selected);
    }
}
