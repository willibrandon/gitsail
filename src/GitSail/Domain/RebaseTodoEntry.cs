using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Retains one validated interactive-rebase todo line without losing its original bytes.
/// </summary>
internal sealed class RebaseTodoEntry
{
    private readonly byte[] _content;
    private readonly int _commandLength;
    private readonly RebaseTodoAction? _originalAction;

    /// <summary>
    /// Initializes one parsed todo line.
    /// </summary>
    /// <param name="kind">The structural line kind.</param>
    /// <param name="action">The parsed command, or <see langword="null"/> for a blank or comment line.</param>
    /// <param name="content">The exact line bytes without its line ending.</param>
    /// <param name="commandLength">The byte length of the original command token.</param>
    internal RebaseTodoEntry(
        RebaseTodoLineKind kind,
        RebaseTodoAction? action,
        ReadOnlySpan<byte> content,
        int commandLength)
    {
        if (commandLength < 0 || commandLength > content.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(commandLength));
        }

        if (kind == RebaseTodoLineKind.Command != action.HasValue)
        {
            throw new ArgumentException("Only a command line can have a todo action.", nameof(action));
        }

        Kind = kind;
        Action = action;
        _originalAction = action;
        _content = content.ToArray();
        _commandLength = commandLength;
    }

    /// <summary>
    /// Gets the structural kind of this line.
    /// </summary>
    internal RebaseTodoLineKind Kind { get; }

    /// <summary>
    /// Gets the selected sequencer action for a command line.
    /// </summary>
    internal RebaseTodoAction? Action { get; private set; }

    /// <summary>
    /// Gets a control-safe representation of the complete line.
    /// </summary>
    internal string DisplayText
    {
        get
        {
            var bytes = RenderContent();
            var text = Encoding.UTF8.GetString(bytes.Span);
            var builder = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                builder.Append(Rune.IsControl(rune) ? '�' : rune.ToString());
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Changes a commit command while retaining its exact object and subject bytes.
    /// </summary>
    /// <param name="action">The replacement commit action.</param>
    internal void ChangeCommitAction(RebaseTodoAction action)
    {
        if (Action is not (RebaseTodoAction.Pick or
            RebaseTodoAction.Reword or
            RebaseTodoAction.Edit or
            RebaseTodoAction.Squash or
            RebaseTodoAction.Fixup or
            RebaseTodoAction.Drop))
        {
            throw new InvalidOperationException("Only a commit todo line can change its action.");
        }

        if (action is not (RebaseTodoAction.Pick or
            RebaseTodoAction.Reword or
            RebaseTodoAction.Edit or
            RebaseTodoAction.Squash or
            RebaseTodoAction.Fixup or
            RebaseTodoAction.Drop))
        {
            throw new ArgumentOutOfRangeException(nameof(action), "The replacement must be a commit action.");
        }

        Action = action;
    }

    /// <summary>
    /// Renders this line with its selected command and exact retained payload.
    /// </summary>
    /// <returns>The line bytes without a line ending.</returns>
    internal ReadOnlyMemory<byte> RenderContent()
    {
        if (Action is null)
        {
            return _content;
        }

        if (Action == _originalAction)
        {
            return _content;
        }

        var command = Encoding.ASCII.GetBytes(RebaseTodoParser.GetCommandName(Action.Value));
        var retainedPayload = _content.AsSpan(_commandLength);
        byte[]? normalizedPayload = null;
        if (_originalAction == RebaseTodoAction.Fixup && Action != RebaseTodoAction.Fixup)
        {
            normalizedPayload = RemoveFixupMessageOption(retainedPayload);
            retainedPayload = normalizedPayload;
        }

        var result = new byte[checked(command.Length + retainedPayload.Length)];
        command.CopyTo(result, 0);
        retainedPayload.CopyTo(result.AsSpan(command.Length));
        return result;
    }

    /// <summary>
    /// Returns the control-safe line representation used by the terminal list.
    /// </summary>
    /// <returns>The control-safe complete todo line.</returns>
    public override string ToString()
        => DisplayText;

    private static byte[] RemoveFixupMessageOption(ReadOnlySpan<byte> payload)
    {
        var optionStart = 0;
        while (optionStart < payload.Length && payload[optionStart] is (byte)' ' or (byte)'\t')
        {
            optionStart++;
        }

        if (optionStart + 2 > payload.Length ||
            payload[optionStart] != (byte)'-' ||
            payload[optionStart + 1] is not ((byte)'C' or (byte)'c'))
        {
            return payload.ToArray();
        }

        var targetStart = optionStart + 2;
        while (targetStart < payload.Length && payload[targetStart] is (byte)' ' or (byte)'\t')
        {
            targetStart++;
        }

        var result = new byte[checked(
            optionStart + (targetStart < payload.Length ? payload.Length - targetStart : 0))];
        payload[..optionStart].CopyTo(result);
        if (targetStart < payload.Length)
        {
            payload[targetStart..].CopyTo(result.AsSpan(optionStart));
        }

        return result;
    }
}
