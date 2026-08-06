using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Identifies one misspelled range and its bounded replacement suggestions.
/// </summary>
/// <param name="Offset">The zero-based UTF-16 document offset of the misspelled word.</param>
/// <param name="Length">The positive UTF-16 length of the misspelled word.</param>
/// <param name="Word">The exact misspelled text returned by the checker.</param>
/// <param name="Suggestions">The ordered bounded replacement suggestions.</param>
internal sealed record SpellingIssue(
    int Offset,
    int Length,
    string Word,
    ImmutableArray<string> Suggestions);
