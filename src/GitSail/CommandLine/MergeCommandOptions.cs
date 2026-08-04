using System.Collections.Immutable;

namespace GitSail.CommandLine;

/// <summary>
/// Contains the typed path operands selected for conflict-resolution mode.
/// </summary>
/// <param name="Paths">The managed command-line path operands.</param>
/// <param name="PathspecFile">The optional pathspec input file or <c>-</c> for standard input.</param>
/// <param name="PathspecFileNul">Whether the pathspec input must contain NUL-delimited records.</param>
internal sealed record MergeCommandOptions(
    ImmutableArray<string> Paths,
    string? PathspecFile = null,
    bool PathspecFileNul = false);
