using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.CommandLine;

/// <summary>
/// Contains the typed operands selected for the structured history workflow.
/// </summary>
/// <param name="RevisionRange">The optional revision range supplied by the user.</param>
/// <param name="Pathspecs">The managed command-line pathspec operands.</param>
/// <param name="PathspecFile">The optional pathspec input file or <c>-</c> for standard input.</param>
/// <param name="PathspecFileNul">Whether the pathspec input must contain NUL-delimited records.</param>
/// <param name="NativePathspecs">The exact native operands following <c>--</c>, when present.</param>
internal sealed record HistoryOptions(
    string? RevisionRange,
    ImmutableArray<string> Pathspecs,
    string? PathspecFile = null,
    bool PathspecFileNul = false,
    ImmutableArray<GitPath>? NativePathspecs = null);
