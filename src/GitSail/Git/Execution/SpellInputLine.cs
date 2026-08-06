namespace GitSail.Git.Execution;

/// <summary>
/// Maps one nonempty pipe-protocol input line back to its commit-message offset.
/// </summary>
/// <param name="DocumentOffset">The zero-based UTF-16 offset of the line in the complete message.</param>
/// <param name="Text">The exact line text without its line ending.</param>
internal sealed record SpellInputLine(int DocumentOffset, string Text);
