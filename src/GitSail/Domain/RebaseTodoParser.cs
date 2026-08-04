using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Parses bounded Git interactive-rebase todo bytes into validated typed commands.
/// </summary>
internal static class RebaseTodoParser
{
    /// <summary>
    /// Parses a complete todo file without interpreting comment or subject text.
    /// </summary>
    /// <param name="contents">The bounded bytes read from Git's exact todo path.</param>
    /// <returns>The typed todo document retaining exact source bytes and line endings.</returns>
    internal static RebaseTodoDocument Parse(ReadOnlySpan<byte> contents)
    {
        if (contents.Contains((byte)0))
        {
            throw new InvalidDataException("The interactive-rebase todo contains NUL.");
        }

        var entries = ImmutableArray.CreateBuilder<RebaseTodoEntry>();
        var endings = ImmutableArray.CreateBuilder<byte[]>();
        var offset = 0;
        while (offset < contents.Length)
        {
            var remaining = contents[offset..];
            var lineFeed = remaining.IndexOf((byte)'\n');
            if (lineFeed < 0)
            {
                entries.Add(ParseLine(remaining));
                endings.Add([]);
                offset = contents.Length;
                continue;
            }

            var contentLength = lineFeed;
            var ending = new byte[] { (byte)'\n' };
            if (contentLength > 0 && remaining[contentLength - 1] == (byte)'\r')
            {
                contentLength--;
                ending = [(byte)'\r', (byte)'\n'];
            }

            entries.Add(ParseLine(remaining[..contentLength]));
            endings.Add(ending);
            offset += lineFeed + 1;
        }

        if (contents.IsEmpty)
        {
            return new RebaseTodoDocument([], []);
        }

        return new RebaseTodoDocument(entries.ToImmutable(), endings.ToImmutable());
    }

    /// <summary>
    /// Gets Git's canonical command spelling for one typed todo action.
    /// </summary>
    /// <param name="action">The typed todo action.</param>
    /// <returns>The canonical command token accepted by Git.</returns>
    internal static string GetCommandName(RebaseTodoAction action)
        => action switch
        {
            RebaseTodoAction.Pick => "pick",
            RebaseTodoAction.Reword => "reword",
            RebaseTodoAction.Edit => "edit",
            RebaseTodoAction.Squash => "squash",
            RebaseTodoAction.Fixup => "fixup",
            RebaseTodoAction.Drop => "drop",
            RebaseTodoAction.Exec => "exec",
            RebaseTodoAction.Break => "break",
            RebaseTodoAction.Label => "label",
            RebaseTodoAction.Reset => "reset",
            RebaseTodoAction.Merge => "merge",
            RebaseTodoAction.UpdateRef => "update-ref",
            RebaseTodoAction.Noop => "noop",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private static RebaseTodoEntry ParseLine(ReadOnlySpan<byte> content)
    {
        var tokenStart = 0;
        while (tokenStart < content.Length && IsHorizontalSpace(content[tokenStart]))
        {
            tokenStart++;
        }

        if (tokenStart == content.Length)
        {
            return new RebaseTodoEntry(RebaseTodoLineKind.Blank, null, content, commandLength: 0);
        }

        if (content[tokenStart] == (byte)'#')
        {
            return new RebaseTodoEntry(RebaseTodoLineKind.Comment, null, content, commandLength: 0);
        }

        if (tokenStart != 0)
        {
            throw new InvalidDataException("An interactive-rebase command cannot be indented.");
        }

        var tokenEnd = 0;
        while (tokenEnd < content.Length && !IsHorizontalSpace(content[tokenEnd]))
        {
            if (content[tokenEnd] > 0x7f)
            {
                throw new InvalidDataException("An interactive-rebase command token must be ASCII.");
            }

            tokenEnd++;
        }

        var action = ParseAction(content[..tokenEnd]);
        var payload = content[tokenEnd..];
        ValidatePayload(action, payload);
        return new RebaseTodoEntry(RebaseTodoLineKind.Command, action, content, tokenEnd);
    }

    private static RebaseTodoAction ParseAction(ReadOnlySpan<byte> command)
    {
        if (command.SequenceEqual("pick"u8) || command.SequenceEqual("p"u8))
        {
            return RebaseTodoAction.Pick;
        }

        if (command.SequenceEqual("reword"u8) || command.SequenceEqual("r"u8))
        {
            return RebaseTodoAction.Reword;
        }

        if (command.SequenceEqual("edit"u8) || command.SequenceEqual("e"u8))
        {
            return RebaseTodoAction.Edit;
        }

        if (command.SequenceEqual("squash"u8) || command.SequenceEqual("s"u8))
        {
            return RebaseTodoAction.Squash;
        }

        if (command.SequenceEqual("fixup"u8) || command.SequenceEqual("f"u8))
        {
            return RebaseTodoAction.Fixup;
        }

        if (command.SequenceEqual("drop"u8) || command.SequenceEqual("d"u8))
        {
            return RebaseTodoAction.Drop;
        }

        if (command.SequenceEqual("exec"u8) || command.SequenceEqual("x"u8))
        {
            return RebaseTodoAction.Exec;
        }

        if (command.SequenceEqual("break"u8) || command.SequenceEqual("b"u8))
        {
            return RebaseTodoAction.Break;
        }

        if (command.SequenceEqual("label"u8) || command.SequenceEqual("l"u8))
        {
            return RebaseTodoAction.Label;
        }

        if (command.SequenceEqual("reset"u8) || command.SequenceEqual("t"u8))
        {
            return RebaseTodoAction.Reset;
        }

        if (command.SequenceEqual("merge"u8) || command.SequenceEqual("m"u8))
        {
            return RebaseTodoAction.Merge;
        }

        if (command.SequenceEqual("update-ref"u8) || command.SequenceEqual("u"u8))
        {
            return RebaseTodoAction.UpdateRef;
        }

        if (command.SequenceEqual("noop"u8))
        {
            return RebaseTodoAction.Noop;
        }

        throw new InvalidDataException("The interactive-rebase todo contains an unsupported command.");
    }

    private static void ValidatePayload(RebaseTodoAction action, ReadOnlySpan<byte> payload)
    {
        payload = TrimHorizontalSpace(payload);
        var valid = action switch
        {
            RebaseTodoAction.Pick or
            RebaseTodoAction.Reword or
            RebaseTodoAction.Edit or
            RebaseTodoAction.Squash or
            RebaseTodoAction.Drop => StartsWithHexObject(payload),
            RebaseTodoAction.Fixup => StartsWithFixupTarget(payload),
            RebaseTodoAction.Exec or
            RebaseTodoAction.Label or
            RebaseTodoAction.Reset or
            RebaseTodoAction.Merge or
            RebaseTodoAction.UpdateRef => !payload.IsEmpty,
            RebaseTodoAction.Break or RebaseTodoAction.Noop => payload.IsEmpty,
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"The '{GetCommandName(action)}' todo command has an invalid or missing argument.");
        }
    }

    private static bool StartsWithFixupTarget(ReadOnlySpan<byte> payload)
    {
        if (payload.StartsWith("-C"u8) || payload.StartsWith("-c"u8))
        {
            if (payload.Length == 2 || !IsHorizontalSpace(payload[2]))
            {
                return false;
            }

            payload = TrimHorizontalSpace(payload[2..]);
        }

        return StartsWithHexObject(payload);
    }

    private static bool StartsWithHexObject(ReadOnlySpan<byte> payload)
    {
        var length = 0;
        while (length < payload.Length && !IsHorizontalSpace(payload[length]))
        {
            var value = payload[length];
            if (!((value >= (byte)'0' && value <= (byte)'9') ||
                (value >= (byte)'a' && value <= (byte)'f') ||
                (value >= (byte)'A' && value <= (byte)'F')))
            {
                return false;
            }

            length++;
        }

        return length is >= 4 and <= 64;
    }

    private static ReadOnlySpan<byte> TrimHorizontalSpace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsHorizontalSpace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsHorizontalSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsHorizontalSpace(byte value)
        => value is (byte)' ' or (byte)'\t';
}
