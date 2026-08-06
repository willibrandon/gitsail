using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains the validated result of checking one exact commit-message version.
/// </summary>
/// <param name="DocumentVersion">The editor document version supplied to the checker.</param>
/// <param name="Dictionary">The configured dictionary name, or an empty value for the checker default.</param>
/// <param name="CheckerVersion">The validated checker version banner without control characters.</param>
/// <param name="Issues">The ordered misspelled ranges in the checked document.</param>
internal sealed record SpellCheckResult(
    long DocumentVersion,
    string Dictionary,
    string CheckerVersion,
    ImmutableArray<SpellingIssue> Issues);
