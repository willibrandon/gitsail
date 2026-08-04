using GitSail.Domain;
using Hex1b.Documents;
using Hex1b.Widgets;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Owns one editable exact-byte conflict result with lifted chunk choices and file mode.
/// </summary>
internal sealed class ConflictResolutionState
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private ConflictResolutionChoice?[] _choices = [];
    private ConflictResolutionChoice?[] _lastChoices = [];
    private int[] _startOffsets = [];
    private int[] _endOffsets = [];
    private string[] _openingMarkers = [];
    private string[] _allMarkers = [];
    private bool _rangesValid;

    /// <summary>
    /// Gets the exact unmerged status entry driving the current merge, or null when inactive.
    /// </summary>
    internal RepositoryStatusEntry? Entry { get; private set; }

    /// <summary>
    /// Gets the immutable exact merge source and original chunk slices, or null when inactive.
    /// </summary>
    internal ConflictMergeDocument? Document { get; private set; }

    /// <summary>
    /// Gets the writable result editor whose UTF-8 byte buffer is staged after resolution.
    /// </summary>
    internal EditorState? Editor { get; private set; }

    /// <summary>
    /// Gets the repository generation whose unchanged stages own the current result buffer.
    /// </summary>
    internal OperationGeneration Generation { get; private set; }

    /// <summary>
    /// Gets the selected regular or executable mode for the staged result.
    /// </summary>
    internal GitFileMode ResultMode { get; private set; } = GitFileMode.RegularFile;

    /// <summary>
    /// Gets whether one generation-matched editable text-conflict result is active.
    /// </summary>
    internal bool IsActive => Entry is not null && Document is not null && Editor is not null;

    /// <summary>
    /// Gets the number of exact conflict chunks in the original three-way merge.
    /// </summary>
    internal int ChunkCount => _choices.Length;

    /// <summary>
    /// Gets the number of chunks whose generated opening marker is absent from the result region.
    /// </summary>
    internal int ResolvedChunkCount
    {
        get
        {
            if (!IsActive)
            {
                return 0;
            }

            if (!_rangesValid)
            {
                return IsComplete ? ChunkCount : _choices.Count(static choice => choice is not null);
            }

            var resolved = 0;
            for (var index = 0; index < ChunkCount; index++)
            {
                if (!RangeContainsOpeningMarker(index))
                {
                    resolved++;
                }
            }

            return resolved;
        }
    }

    /// <summary>
    /// Gets whether every collision-checked generated conflict marker is absent from the result.
    /// </summary>
    internal bool IsComplete
    {
        get
        {
            if (!IsActive || Editor is null)
            {
                return false;
            }

            var text = Editor.Document.GetText();
            return _allMarkers.All(marker => !text.Contains(marker, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Gets whether the current conflict can produce a regular or executable file result.
    /// </summary>
    internal bool CanToggleExecutable => IsActive && GetAvailableModes().Any();

    /// <summary>
    /// Loads a merge result while retaining edits when the exact path and stages remain unchanged.
    /// </summary>
    /// <param name="entry">The exact current unmerged status entry.</param>
    /// <param name="document">The exact raw merge document and conflict chunks.</param>
    /// <param name="generation">The repository generation that produced the entry.</param>
    internal void SetDocument(
        RepositoryStatusEntry entry,
        ConflictMergeDocument document,
        OperationGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(document);
        if (entry.Kind != RepositoryStatusEntryKind.Unmerged || entry.ConflictStages is null)
        {
            throw new ArgumentException("Conflict state requires an exact unmerged entry.", nameof(entry));
        }

        var retainResult = Entry is not null &&
            Entry.Path.Equals(entry.Path) &&
            Entry.ConflictStages == entry.ConflictStages &&
            _choices.Length == document.Chunks.Length &&
            Editor is not null;
        if (retainResult)
        {
            Entry = entry;
            Document = document;
            Generation = generation;
            return;
        }

        ValidateUtf8(document.Content.Span);
        var bytes = document.Content.ToArray();
        var editor = new EditorState(new Hex1bDocument(bytes));
        var ranges = BuildCharacterRanges(document, editor.Document.GetByteMap());
        var markers = ExtractMarkers(document);
        ClearEditor();
        Entry = entry;
        Document = document;
        Generation = generation;
        Editor = editor;
        Editor.Document.Changed += HandleDocumentChanged;
        _choices = new ConflictResolutionChoice?[document.Chunks.Length];
        _lastChoices = new ConflictResolutionChoice?[document.Chunks.Length];
        (_startOffsets, _endOffsets) = ranges;
        (_openingMarkers, _allMarkers) = markers;
        _rangesValid = true;
        ResultMode = GetDefaultMode(entry.ConflictStages);
    }

    /// <summary>
    /// Clears the editable result when focus or immutable conflict-stage identities no longer match.
    /// </summary>
    internal void Clear()
    {
        ClearEditor();
        Entry = null;
        Document = null;
        Generation = default;
        ResultMode = GitFileMode.RegularFile;
        _choices = [];
        _lastChoices = [];
        _startOffsets = [];
        _endOffsets = [];
        _openingMarkers = [];
        _allMarkers = [];
        _rangesValid = false;
    }

    /// <summary>
    /// Gets the unresolved conflict chunk containing one zero-based editor presentation line.
    /// </summary>
    /// <param name="line">The zero-based result-editor line.</param>
    /// <returns>The containing unresolved chunk index, or negative one outside a marker block.</returns>
    internal int FindChunkAtLine(int line)
    {
        if (!_rangesValid || Editor is null || line < 0)
        {
            return -1;
        }

        for (var index = 0; index < _startOffsets.Length; index++)
        {
            if (!RangeContainsOpeningMarker(index))
            {
                continue;
            }

            var startLine = Editor.Document.OffsetToPosition(new DocumentOffset(_startOffsets[index])).Line - 1;
            var endLine = Editor.Document.OffsetToPosition(new DocumentOffset(_endOffsets[index])).Line - 1;
            var endsAtLineStart = _endOffsets[index] > _startOffsets[index] &&
                Editor.Document.GetText(new DocumentRange(
                    new DocumentOffset(_endOffsets[index] - 1),
                    new DocumentOffset(_endOffsets[index]))) == "\n";
            var endExclusiveLine = endsAtLineStart ? endLine : endLine + 1;
            if (line >= startLine && line < endExclusiveLine)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Replaces one unresolved marker block through the writable editor's normal undo history.
    /// </summary>
    /// <param name="chunkIndex">The zero-based current chunk index.</param>
    /// <param name="choice">The exact base, ours, theirs, or both choice.</param>
    internal void SetChoice(int chunkIndex, ConflictResolutionChoice choice)
    {
        if ((uint)chunkIndex >= (uint)_choices.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        if (!_rangesValid || Editor is null || Document is null || !RangeContainsOpeningMarker(chunkIndex))
        {
            throw new InvalidOperationException("The selected conflict chunk is no longer available for a quick choice.");
        }

        var replacement = GetChoiceText(Document, chunkIndex, choice);
        Editor.CollapseToSingleCursor();
        Editor.Cursor.SelectionAnchor = new DocumentOffset(_startOffsets[chunkIndex]);
        Editor.Cursor.Position = new DocumentOffset(_endOffsets[chunkIndex]);
        Editor.InsertText(replacement);
        _choices[chunkIndex] = choice;
        _lastChoices[chunkIndex] = choice;
    }

    /// <summary>
    /// Gets the next unresolved chunk after one index, wrapping to the first unresolved chunk.
    /// </summary>
    /// <param name="afterChunkIndex">The most recently visited chunk index.</param>
    /// <returns>The next unresolved index, or negative one when no mapped marker block remains.</returns>
    internal int FindNextUnresolvedChunk(int afterChunkIndex)
    {
        if (!_rangesValid)
        {
            return -1;
        }

        for (var offset = 1; offset <= _choices.Length; offset++)
        {
            var index = (afterChunkIndex + offset) % _choices.Length;
            if (RangeContainsOpeningMarker(index))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Gets the zero-based presentation line at the start of one currently mapped chunk region.
    /// </summary>
    /// <param name="chunkIndex">The validated zero-based chunk index.</param>
    /// <returns>The zero-based editor line containing the chunk region's first character.</returns>
    internal int GetStartLine(int chunkIndex)
    {
        if (!_rangesValid || Editor is null || (uint)chunkIndex >= (uint)_startOffsets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        return Editor.Document.OffsetToPosition(new DocumentOffset(_startOffsets[chunkIndex])).Line - 1;
    }

    /// <summary>
    /// Toggles the selected regular-file executable bit for a blob-backed file conflict.
    /// </summary>
    internal void ToggleExecutable()
    {
        if (!CanToggleExecutable)
        {
            throw new InvalidOperationException("The current conflict has no regular-file result mode.");
        }

        ResultMode = ResultMode == GitFileMode.ExecutableFile
            ? GitFileMode.RegularFile
            : GitFileMode.ExecutableFile;
    }

    /// <summary>
    /// Returns exact current editor bytes after verifying generated conflict markers are gone.
    /// </summary>
    /// <returns>The exact clean UTF-8 result buffer ready for rollback-capable staging.</returns>
    internal byte[] BuildResolvedContent()
    {
        if (!IsComplete || Editor is null)
        {
            throw new InvalidOperationException("Every conflict marker must be resolved before staging.");
        }

        return Editor.Document.GetBytes().ToArray();
    }

    private void HandleDocumentChanged(object? sender, DocumentChangedEventArgs eventArgs)
    {
        foreach (var operation in eventArgs.Operations)
        {
            if (!TryApplyRangeChange(operation))
            {
                _rangesValid = false;
                break;
            }
        }

        if (!_rangesValid || Editor is null || Document is null)
        {
            return;
        }

        for (var index = 0; index < ChunkCount; index++)
        {
            if (RangeContainsOpeningMarker(index))
            {
                _choices[index] = null;
                continue;
            }

            var lastChoice = _lastChoices[index];
            if (eventArgs.Source == "redo" &&
                lastChoice is not null &&
                GetRangeText(index) == GetChoiceText(Document, index, lastChoice.Value))
            {
                _choices[index] = lastChoice;
            }
            else if (_choices[index] is not null &&
                GetRangeText(index) != GetChoiceText(Document, index, _choices[index]!.Value))
            {
                _choices[index] = null;
            }
        }
    }

    private bool TryApplyRangeChange(EditOperation operation)
    {
        var (editStart, editEnd, replacementLength) = operation switch
        {
            InsertOperation insert => (insert.Offset.Value, insert.Offset.Value, insert.Text.Length),
            DeleteOperation delete => (delete.Range.Start.Value, delete.Range.End.Value, 0),
            ReplaceOperation replace => (replace.Range.Start.Value, replace.Range.End.Value, replace.NewText.Length),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var delta = replacementLength - (editEnd - editStart);
        for (var index = 0; index < _startOffsets.Length; index++)
        {
            var rangeStart = _startOffsets[index];
            var rangeEnd = _endOffsets[index];
            if (editEnd <= rangeStart && editStart < rangeStart)
            {
                _startOffsets[index] += delta;
                _endOffsets[index] += delta;
                continue;
            }

            if (editStart >= rangeEnd)
            {
                continue;
            }

            if (editStart < rangeStart || editEnd > rangeEnd)
            {
                return false;
            }

            _endOffsets[index] += delta;
        }

        return true;
    }

    private bool RangeContainsOpeningMarker(int chunkIndex)
        => GetRangeText(chunkIndex).Contains(_openingMarkers[chunkIndex], StringComparison.Ordinal);

    private string GetRangeText(int chunkIndex)
    {
        if (Editor is null)
        {
            return string.Empty;
        }

        return Editor.Document.GetText(new DocumentRange(
            new DocumentOffset(_startOffsets[chunkIndex]),
            new DocumentOffset(_endOffsets[chunkIndex])));
    }

    private IEnumerable<GitFileMode> GetAvailableModes()
    {
        var stages = Entry?.ConflictStages;
        if (stages is null)
        {
            yield break;
        }

        foreach (var stage in new[] { stages.Ours, stages.Theirs, stages.Base })
        {
            if (stage?.Mode is GitFileMode.RegularFile or GitFileMode.ExecutableFile)
            {
                yield return stage.Mode;
            }
        }
    }

    private void ClearEditor()
    {
        Editor?.Document.Changed -= HandleDocumentChanged;
        Editor = null;
    }

    private static void ValidateUtf8(ReadOnlySpan<byte> content)
    {
        try
        {
            _ = s_strictUtf8.GetCharCount(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Built-in conflict editing requires valid UTF-8; use an approved external mergetool for this file.",
                exception);
        }
    }

    private static GitFileMode GetDefaultMode(ConflictStages stages)
        => new[] { stages.Ours, stages.Theirs, stages.Base }
            .Where(static stage => stage?.Mode is GitFileMode.RegularFile or GitFileMode.ExecutableFile)
            .Select(static stage => stage!.Mode)
            .FirstOrDefault(GitFileMode.RegularFile);

    private static (int[] StartOffsets, int[] EndOffsets) BuildCharacterRanges(
        ConflictMergeDocument document,
        Utf8ByteMap map)
    {
        var starts = new int[document.Chunks.Length];
        var ends = new int[document.Chunks.Length];
        for (var index = 0; index < document.Chunks.Length; index++)
        {
            starts[index] = ByteToCharacterOffset(map, document.Chunks[index].StartOffset);
            ends[index] = ByteToCharacterOffset(map, document.Chunks[index].EndOffset);
        }

        return (starts, ends);
    }

    private static int ByteToCharacterOffset(Utf8ByteMap map, int byteOffset)
        => byteOffset == map.TotalBytes ? map.CharCount : map.ByteToChar(byteOffset).charIndex;

    private static (string[] OpeningMarkers, string[] AllMarkers) ExtractMarkers(
        ConflictMergeDocument document)
    {
        var openingMarkers = new string[document.Chunks.Length];
        var markers = new HashSet<string>(StringComparer.Ordinal);
        var content = document.Content.Span;
        for (var index = 0; index < document.Chunks.Length; index++)
        {
            var chunk = document.Chunks[index];
            openingMarkers[index] = DecodeMarker(content[chunk.StartOffset..chunk.OursOffset]);
            markers.Add(openingMarkers[index]);
            markers.Add(DecodeMarker(content[(chunk.OursOffset + chunk.OursLength)..chunk.BaseOffset]));
            markers.Add(DecodeMarker(content[(chunk.BaseOffset + chunk.BaseLength)..chunk.TheirsOffset]));
            markers.Add(DecodeMarker(content[(chunk.TheirsOffset + chunk.TheirsLength)..chunk.EndOffset]));
        }

        markers.Remove(string.Empty);
        return (openingMarkers, [.. markers]);
    }

    private static string DecodeMarker(ReadOnlySpan<byte> marker)
        => s_strictUtf8.GetString(marker).TrimEnd('\r', '\n');

    private static string GetChoiceText(
        ConflictMergeDocument document,
        int chunkIndex,
        ConflictResolutionChoice choice)
    {
        var chunk = document.Chunks[chunkIndex];
        var content = document.Content.Span;
        return choice switch
        {
            ConflictResolutionChoice.Ours => s_strictUtf8.GetString(
                content.Slice(chunk.OursOffset, chunk.OursLength)),
            ConflictResolutionChoice.Theirs => s_strictUtf8.GetString(
                content.Slice(chunk.TheirsOffset, chunk.TheirsLength)),
            ConflictResolutionChoice.Base => s_strictUtf8.GetString(
                content.Slice(chunk.BaseOffset, chunk.BaseLength)),
            ConflictResolutionChoice.Both => s_strictUtf8.GetString(
                content.Slice(chunk.OursOffset, chunk.OursLength)) +
                s_strictUtf8.GetString(content.Slice(chunk.TheirsOffset, chunk.TheirsLength)),
            _ => throw new ArgumentOutOfRangeException(nameof(choice)),
        };
    }
}
