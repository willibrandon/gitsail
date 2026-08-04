using GitSail.Domain;
using Hex1b.Documents;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Maps one or more presentation cursor ranges back to exact changed raw-patch line identities.
/// </summary>
internal static class DiffLineSelectionMapper
{
    /// <summary>
    /// Returns every addition or deletion intersected by the editor's discontiguous cursor set.
    /// </summary>
    /// <param name="editor">The read-only editor state presenting this exact patch.</param>
    /// <param name="patchIndex">The raw line index corresponding to the presentation document.</param>
    /// <returns>The selected one-based presentation line numbers.</returns>
    internal static HashSet<int> GetChangedLineNumbers(
        EditorState editor,
        RawPatchIndex patchIndex)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(patchIndex);
        var selectedRanges = new List<(int Start, int End)>();
        for (var cursorIndex = 0; cursorIndex < editor.Cursors.Count; cursorIndex++)
        {
            var cursor = editor.Cursors[cursorIndex];
            var positionOffset = cursor.Position.Value;
            if (cursor.SelectionAnchor is { } anchor && anchor.Value != positionOffset)
            {
                var startOffset = Math.Min(anchor.Value, positionOffset);
                var endOffset = Math.Max(anchor.Value, positionOffset) - 1;
                selectedRanges.Add((
                    editor.Document.OffsetToPosition((DocumentOffset)startOffset).Line,
                    editor.Document.OffsetToPosition((DocumentOffset)endOffset).Line));
            }
            else
            {
                var line = editor.Document.OffsetToPosition(cursor.Position).Line;
                selectedRanges.Add((line, line));
            }
        }

        var selectedLineNumbers = new HashSet<int>();
        foreach (var line in patchIndex.Hunks.SelectMany(static hunk => hunk.Lines))
        {
            if (line.Kind is not RawPatchLineKind.Addition and not RawPatchLineKind.Deletion)
            {
                continue;
            }

            if (selectedRanges.Any(range => line.LineNumber >= range.Start && line.LineNumber <= range.End))
            {
                selectedLineNumbers.Add(line.LineNumber);
            }
        }

        return selectedLineNumbers;
    }
}
