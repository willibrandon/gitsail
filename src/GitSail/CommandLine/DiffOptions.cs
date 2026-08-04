using System.Collections.Immutable;

namespace GitSail.CommandLine;

/// <summary>
/// Contains the typed operands selected for the comparison workspace.
/// </summary>
/// <param name="Cached">Whether the index is the right side of a zero-or-one-revision comparison.</param>
/// <param name="LeftRevision">The optional left revision supplied by the user.</param>
/// <param name="RightRevision">The optional right revision supplied by the user.</param>
/// <param name="Pathspecs">The managed command-line pathspec operands.</param>
/// <param name="PathspecFile">The optional pathspec input file or <c>-</c> for standard input.</param>
/// <param name="PathspecFileNul">Whether the pathspec input must contain NUL-delimited records.</param>
internal sealed record DiffOptions(
    bool Cached,
    string? LeftRevision,
    string? RightRevision,
    ImmutableArray<string> Pathspecs,
    string? PathspecFile = null,
    bool PathspecFileNul = false);
