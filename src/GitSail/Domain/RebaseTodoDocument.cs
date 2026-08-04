using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Owns a bounded interactive-rebase todo document and its exact line endings.
/// </summary>
internal sealed class RebaseTodoDocument
{
    private ImmutableArray<RebaseTodoEntry> _entries;
    private ImmutableArray<byte[]> _lineEndings;

    /// <summary>
    /// Initializes a parsed todo document.
    /// </summary>
    /// <param name="entries">The parsed lines in file order.</param>
    /// <param name="lineEndings">The exact line ending following each corresponding line.</param>
    internal RebaseTodoDocument(
        ImmutableArray<RebaseTodoEntry> entries,
        ImmutableArray<byte[]> lineEndings)
    {
        if (entries.Length != lineEndings.Length)
        {
            throw new ArgumentException("Every todo line must have one retained line-ending slot.", nameof(lineEndings));
        }

        _entries = entries;
        _lineEndings = lineEndings;
    }

    /// <summary>
    /// Gets the todo lines in their current order.
    /// </summary>
    internal ImmutableArray<RebaseTodoEntry> Entries => _entries;

    /// <summary>
    /// Moves one command across the nearest preceding or following command line.
    /// </summary>
    /// <param name="index">The absolute selected line index.</param>
    /// <param name="offset">The requested direction, which must be negative or positive.</param>
    /// <returns>The selected line's new index, or its existing index at a boundary.</returns>
    internal int MoveCommand(int index, int offset)
    {
        if (index < 0 || index >= _entries.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (offset == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (_entries[index].Kind != RebaseTodoLineKind.Command)
        {
            return index;
        }

        var step = Math.Sign(offset);
        for (var candidate = index + step;
             candidate >= 0 && candidate < _entries.Length;
             candidate += step)
        {
            if (_entries[candidate].Kind != RebaseTodoLineKind.Command)
            {
                continue;
            }

            var builder = _entries.ToBuilder();
            (builder[index], builder[candidate]) = (builder[candidate], builder[index]);
            _entries = builder.ToImmutable();
            return candidate;
        }

        return index;
    }

    /// <summary>
    /// Inserts an explicitly trusted shell command after the selected line.
    /// </summary>
    /// <param name="index">The absolute line after which the command is inserted.</param>
    /// <param name="command">The nonempty command text passed to Git's sequencer.</param>
    /// <returns>The absolute index of the inserted command.</returns>
    internal int InsertExec(int index, string command)
    {
        if (index < -1 || index >= _entries.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Contains('\0', StringComparison.Ordinal) ||
            command.Contains('\r', StringComparison.Ordinal) ||
            command.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("An exec command must occupy exactly one non-NUL line.", nameof(command));
        }

        var generated = RebaseTodoParser.Parse(
            System.Text.Encoding.UTF8.GetBytes($"exec {command}"));
        var entry = generated.Entries[0];
        var insertionIndex = index + 1;
        var entries = _entries.ToBuilder();
        entries.Insert(insertionIndex, entry);
        var endings = _lineEndings.ToBuilder();
        var preferredEnding = _lineEndings.FirstOrDefault(static ending => ending.Length > 0) ?? [(byte)'\n'];
        if (insertionIndex == _entries.Length && _entries.Length > 0 && _lineEndings[^1].Length == 0)
        {
            endings[^1] = preferredEnding;
            endings.Add([]);
        }
        else
        {
            endings.Insert(insertionIndex, preferredEnding);
        }

        _entries = entries.ToImmutable();
        _lineEndings = endings.ToImmutable();
        return insertionIndex;
    }

    /// <summary>
    /// Removes one selected exec line while retaining all surrounding source bytes.
    /// </summary>
    /// <param name="index">The absolute selected line index.</param>
    /// <returns>The nearest remaining line index, or zero for an empty document.</returns>
    internal int RemoveExec(int index)
    {
        if (index < 0 || index >= _entries.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (_entries[index].Action != RebaseTodoAction.Exec)
        {
            throw new InvalidOperationException("Only an exec todo line can be removed directly.");
        }

        var entries = _entries.ToBuilder();
        entries.RemoveAt(index);
        var endings = _lineEndings.ToBuilder();
        endings.RemoveAt(index);
        _entries = entries.ToImmutable();
        _lineEndings = endings.ToImmutable();
        return _entries.IsEmpty ? 0 : Math.Min(index, _entries.Length - 1);
    }

    /// <summary>
    /// Renders the current todo while retaining every original line-ending position.
    /// </summary>
    /// <returns>The complete bytes to return to Git.</returns>
    internal byte[] Render()
    {
        var length = 0;
        for (var index = 0; index < _entries.Length; index++)
        {
            length = checked(length + _entries[index].RenderContent().Length + _lineEndings[index].Length);
        }

        var result = new byte[length];
        var offset = 0;
        for (var index = 0; index < _entries.Length; index++)
        {
            var content = _entries[index].RenderContent().Span;
            content.CopyTo(result.AsSpan(offset));
            offset += content.Length;
            _lineEndings[index].CopyTo(result, offset);
            offset += _lineEndings[index].Length;
        }

        return result;
    }
}
