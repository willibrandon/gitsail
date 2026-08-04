namespace GitSail.Domain;

/// <summary>
/// Validates cross-line constraints in an edited interactive-rebase todo document.
/// </summary>
internal static class RebaseTodoValidator
{
    /// <summary>
    /// Validates combination commands and requires at least one executable todo command.
    /// </summary>
    /// <param name="document">The edited todo document.</param>
    internal static void Validate(RebaseTodoDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var hasPriorCommit = false;
        var hasCommand = false;
        foreach (var entry in document.Entries)
        {
            if (entry.Action is not { } action)
            {
                continue;
            }

            hasCommand = true;
            if (action is RebaseTodoAction.Squash or RebaseTodoAction.Fixup && !hasPriorCommit)
            {
                throw new InvalidDataException(
                    $"'{RebaseTodoParser.GetCommandName(action)}' requires an earlier commit to combine with.");
            }

            if (action is RebaseTodoAction.Pick or
                RebaseTodoAction.Reword or
                RebaseTodoAction.Edit or
                RebaseTodoAction.Squash or
                RebaseTodoAction.Fixup or
                RebaseTodoAction.Merge)
            {
                hasPriorCommit = true;
            }
        }

        if (!hasCommand)
        {
            throw new InvalidDataException("The interactive-rebase plan does not contain a command.");
        }
    }
}
